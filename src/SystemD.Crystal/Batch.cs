// Ported from Highway: src/Highway.Server/Storage/Rocks/RocksDbBatch.cs
// Adapted for Crystal: multi-box puts/deletes/range-deletes, read-your-writes overlay,
// empty batch skip, live-box validation at commit (spec 002 R4, R6, design § 2, § 3).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>
/// A write batch holding puts, deletes, and range deletes across any number of boxes in one database (spec 002 R6).
/// Provides a read-your-writes overlay over the committed state and commits atomically inside the catalogue gate.
/// </summary>
public sealed class Batch : IDisposable
{
    private readonly CrystalDatabase _database;
    private readonly DatabaseContext _context;

    private readonly List<uint> _touchedBoxes = [];
    private readonly HashSet<uint> _touchedBoxSet = [];
    private readonly List<(string family, byte[] key, byte[]? value, bool isDelete, bool isRange, byte[]? rangeEnd)> _operations = [];

    // Read-your-writes overlay: per-box point mutations and range deletes
    private readonly Dictionary<uint, Dictionary<byte[], byte[]?>> _pendingPoints = [];
    private readonly Dictionary<uint, List<(byte[] From, byte[] To)>> _pendingRanges = [];

    private long _bytes; // spec 012 R5.2: key, value and range-end bytes the product passed, prefix excluded
    private int _disposed;

    internal Batch(CrystalDatabase database, DatabaseContext context)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>The number of operations staged: puts, deletes and range deletes (spec 012 R5.1). Zero after a commit or dispose.</summary>
    public int Count => _operations.Count;

    /// <summary>
    /// The bytes staged: the sum of the keys, values and range ends the product passed, without Crystal's box prefix
    /// (spec 012 R5.2). Zero after a commit or dispose. A product loading in pages bounds its batches by this.
    /// </summary>
    public long Bytes => _bytes;

    /// <summary>
    /// Enqueues putting <paramref name="value"/> under <paramref name="key"/> in <paramref name="box"/> (spec 002 R6.1).
    /// </summary>
    /// <exception cref="KeyTooLargeException">The key is longer than <see cref="CrystalOptions.MaxKeyBytes"/>.</exception>
    public void Put(Box box, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        using var _ = _database.EnterOperation();
        _context.EnsureOwned(box);
        _context.EnsureKeySize(box, key.Length);
        TrackBox(box.Id);
        _bytes += key.Length + value.Length;

        var keyBytes = key.ToArray();
        var valBytes = value.ToArray();
        var prefixedKey = BoxKeys.BuildKey(box.Id, key);
        var family = _context.FamilyNameOf(box.Id);

        // Update read-your-writes overlay
        var points = GetPoints(box.Id);
        points[keyBytes] = valBytes;

        _operations.Add((family, prefixedKey, valBytes, isDelete: false, isRange: false, rangeEnd: null));
    }

    /// <summary>
    /// Enqueues deleting <paramref name="key"/> in <paramref name="box"/> (spec 002 R6.1).
    /// </summary>
    /// <exception cref="KeyTooLargeException">The key is longer than <see cref="CrystalOptions.MaxKeyBytes"/>.</exception>
    public void Delete(Box box, ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        using var _ = _database.EnterOperation();
        _context.EnsureOwned(box);
        _context.EnsureKeySize(box, key.Length);
        TrackBox(box.Id);
        _bytes += key.Length;

        var keyBytes = key.ToArray();
        var prefixedKey = BoxKeys.BuildKey(box.Id, key);
        var family = _context.FamilyNameOf(box.Id);

        // Update read-your-writes overlay
        var points = GetPoints(box.Id);
        points[keyBytes] = null; // null denotes pending delete

        _operations.Add((family, prefixedKey, value: null, isDelete: true, isRange: false, rangeEnd: null));
    }

    /// <summary>
    /// Enqueues deleting the range [<paramref name="from"/>, <paramref name="to"/>) in <paramref name="box"/> (spec 002 R6.1).
    /// An empty <paramref name="to"/> means the end of the box, as in a scan; otherwise <paramref name="from"/> must not
    /// sort after <paramref name="to"/>.
    /// </summary>
    public void DeleteRange(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        ThrowIfDisposed();
        using var _ = _database.EnterOperation();
        _context.EnsureOwned(box);
        if (!to.IsEmpty && from.SequenceCompareTo(to) > 0)
        {
            throw new ArgumentException("The range's start sorts after its end (R6.1).", nameof(from));
        }

        _context.EnsureKeySize(box, from.Length);
        _context.EnsureKeySize(box, to.Length);
        TrackBox(box.Id);
        _bytes += from.Length + to.Length;

        var fromBytes = from.ToArray();
        var toBytes = to.ToArray();
        var prefixedFrom = BoxKeys.BuildKey(box.Id, from);
        var prefixedTo = to.IsEmpty ? BoxKeys.UpperBound(box.Id) : BoxKeys.BuildKey(box.Id, to);

        var family = _context.FamilyNameOf(box.Id);

        // Clear any pending point operations that fall in [from, to)
        var points = GetPoints(box.Id);
        var toRemove = new List<byte[]>();
        foreach (var k in points.Keys)
        {
            if (InRange(k, fromBytes, toBytes))
            {
                toRemove.Add(k);
            }
        }
        foreach (var k in toRemove)
        {
            points.Remove(k);
        }

        var ranges = GetRanges(box.Id);
        ranges.Add((fromBytes, toBytes));

        _operations.Add((family, prefixedFrom, value: null, isDelete: false, isRange: true, rangeEnd: prefixedTo));
    }

    /// <summary>
    /// Reads <paramref name="key"/> within <paramref name="box"/>, seeing this batch's pending writes (spec 002 R6.2).
    /// Returns <c>null</c> if deleted or not present.
    /// </summary>
    public byte[]? Get(Box box, ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        using var _ = _database.EnterOperation();
        _context.EnsureOwned(box);

        var keyBytes = key.ToArray();

        // 1. Check pending point writes
        if (_pendingPoints.TryGetValue(box.Id, out var points) && points.TryGetValue(keyBytes, out var pendingVal))
        {
            return pendingVal is null ? null : (byte[])pendingVal.Clone();
        }

        // 2. Check pending range deletes
        if (_pendingRanges.TryGetValue(box.Id, out var ranges))
        {
            foreach (var (rFrom, rTo) in ranges)
            {
                if (InRange(keyBytes, rFrom, rTo))
                {
                    return null;
                }
            }
        }

        // 3. Fall back to committed state
        _context.EnsureBoxesLive([box.Id]);
        var family = _context.FamilyNameOf(box.Id);
        var prefixedKey = BoxKeys.BuildKey(box.Id, key);
        return _context.DirectGet(family, prefixedKey);
    }

    /// <summary>Enqueues deleting every key of <paramref name="range"/> in <paramref name="box"/> (spec 012 R4.1).</summary>
    public void DeleteRange(Box box, KeyRange range) => DeleteRange(box, range.From ?? [], range.To ?? []);

    /// <summary>Scans <paramref name="range"/> within <paramref name="box"/>, seeing this batch's pending writes (spec 012 R4.1).</summary>
    public IEnumerable<KeyValue> Scan(Box box, KeyRange range, int limit) => Scan(box, range.From ?? [], range.To ?? [], limit);

    /// <summary>
    /// Scans a range of keys within <paramref name="box"/>, seeing this batch's pending writes (spec 002 R6.2, R6.5).
    /// <para>Dispose the enumeration (a <c>foreach</c> does): until it ends, a drop of this database or the store's
    /// <c>Dispose</c> waits for it, and calling either from inside the loop waits forever (spec 005 design A3, A9).</para>
    /// </summary>
    public IEnumerable<KeyValue> Scan(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, int limit)
    {
        ThrowIfDisposed();
        using (_database.EnterOperation())
        {
            _context.EnsureOwned(box);
        }

        if (limit <= 0)
        {
            return [];
        }

        var fromBytes = from.ToArray();
        var toBytes = to.ToArray();

        return ScanWithOverlay(box, fromBytes, toBytes, limit);
    }

    private IEnumerable<KeyValue> ScanWithOverlay(Box box, byte[] from, byte[] to, int limit)
    {
        // The database cannot close while the enumeration is in progress (spec 005 design addendum A3).
        using var operation = _database.EnterOperation();

        // 1. Read committed keys using a snapshot
        using var snapshot = _context.CreateSnapshot();
        var committed = _context.ScanBox(box.Id, snapshot, from, to, int.MaxValue);

        // 2. Collect pending point puts in [from, to)
        var pendingPuts = new List<KeyValue>();
        if (_pendingPoints.TryGetValue(box.Id, out var points))
        {
            foreach (var (k, v) in points)
            {
                if (v is not null &&
                    (from.Length == 0 || ByteArrayComparer.Instance.Compare(k, from) >= 0) &&
                    (to.Length == 0 || ByteArrayComparer.Instance.Compare(k, to) < 0))
                {
                    pendingPuts.Add(new KeyValue(k, v));
                }
            }

            pendingPuts.Sort((a, b) => ByteArrayComparer.Instance.Compare(a.Key, b.Key));
        }

        // 3. Merge committed stream and pending puts
        var committedEnumerator = committed.GetEnumerator();
        var pendingIndex = 0;
        var yielded = 0;

        try
        {
            var hasCommitted = committedEnumerator.MoveNext();

            while (yielded < limit && (hasCommitted || pendingIndex < pendingPuts.Count))
            {
                if (!hasCommitted)
                {
                    yield return pendingPuts[pendingIndex++];
                    yielded++;
                }
                else if (pendingIndex >= pendingPuts.Count)
                {
                    var c = committedEnumerator.Current;
                    hasCommitted = committedEnumerator.MoveNext();
                    if (!IsDeletedInBatch(box.Id, c.Key))
                    {
                        yield return c;
                        yielded++;
                    }
                }
                else
                {
                    var c = committedEnumerator.Current;
                    var p = pendingPuts[pendingIndex];
                    var cmp = ByteArrayComparer.Instance.Compare(c.Key, p.Key);

                    if (cmp < 0)
                    {
                        hasCommitted = committedEnumerator.MoveNext();
                        if (!IsDeletedInBatch(box.Id, c.Key))
                        {
                            yield return c;
                            yielded++;
                        }
                    }
                    else if (cmp == 0)
                    {
                        // Pending put overwrites committed
                        hasCommitted = committedEnumerator.MoveNext();
                        pendingIndex++;
                        yield return p;
                        yielded++;
                    }
                    else
                    {
                        pendingIndex++;
                        yield return p;
                        yielded++;
                    }
                }
            }
        }
        finally
        {
            committedEnumerator.Dispose();
        }
    }

    private bool IsDeletedInBatch(uint boxId, byte[] key)
    {
        // 1. Point delete
        if (_pendingPoints.TryGetValue(boxId, out var points) && points.TryGetValue(key, out var val) && val is null)
        {
            return true;
        }

        // 2. Range delete
        if (_pendingRanges.TryGetValue(boxId, out var ranges))
        {
            foreach (var (rFrom, rTo) in ranges)
            {
                if (InRange(key, rFrom, rTo))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Commits all pending operations atomically (spec 002 R6.1), with the store's default durability
    /// (<see cref="CrystalOptions.Durability"/>, spec 006 R2.2).
    /// </summary>
    /// <inheritdoc cref="Commit(DurabilityMode)" path="/remarks"/>
    public void Commit()
    {
        ThrowIfDisposed();
        Commit(_context.DefaultDurability);
    }

    /// <summary>
    /// Commits all pending operations atomically (spec 002 R6.1), durable as <paramref name="durability"/> says
    /// (spec 006 R1).
    /// </summary>
    /// <remarks>
    /// Synchronous: it blocks for native work, one WAL sync (milliseconds) for <see cref="DurabilityMode.Strict"/> and
    /// a memory write (microseconds) otherwise. Concurrent strict commits share one sync. Committing an empty batch
    /// writes nothing and syncs nothing (R6.3). Throws <see cref="BoxDroppedException"/> if any touched box was dropped
    /// (R4.1), and <see cref="DatabaseFailedException"/> if the database has failed or this commit's write fails
    /// (spec 006 R5).
    /// </remarks>
    public void Commit(DurabilityMode durability)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(durability))
        {
            throw new ArgumentOutOfRangeException(nameof(durability), durability, "Not a durability mode.");
        }

        using var _ = _database.EnterOperation();

        if (_operations.Count == 0)
        {
            return; // R6.3: empty batch skips write
        }

        _context.CommitBatch(_touchedBoxes, _operations, durability);

        _bytes = 0;
        _operations.Clear();
        _touchedBoxes.Clear();
        _touchedBoxSet.Clear();
        _pendingPoints.Clear();
        _pendingRanges.Clear();
    }

    /// <summary>Whether <paramref name="key"/> is in [<paramref name="from"/>, <paramref name="to"/>); an empty end is the box's end.</summary>
    private static bool InRange(byte[] key, byte[] from, byte[] to) =>
        ByteArrayComparer.Instance.Compare(key, from) >= 0 &&
        (to.Length == 0 || ByteArrayComparer.Instance.Compare(key, to) < 0);

    private void TrackBox(uint boxId)
    {
        if (_touchedBoxSet.Add(boxId))
        {
            _touchedBoxes.Add(boxId);
        }
    }

    private Dictionary<byte[], byte[]?> GetPoints(uint boxId)
    {
        if (!_pendingPoints.TryGetValue(boxId, out var points))
        {
            points = new Dictionary<byte[], byte[]?>(ByteArrayComparer.Instance);
            _pendingPoints[boxId] = points;
        }
        return points;
    }

    private List<(byte[] From, byte[] To)> GetRanges(uint boxId)
    {
        if (!_pendingRanges.TryGetValue(boxId, out var ranges))
        {
            ranges = [];
            _pendingRanges[boxId] = ranges;
        }
        return ranges;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>Disposes this batch and releases its resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _bytes = 0;
        _operations.Clear();
        _touchedBoxes.Clear();
        _touchedBoxSet.Clear();
        _pendingPoints.Clear();
        _pendingRanges.Clear();
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y.AsSpan());
        }

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.AsSpan().SequenceEqual(y.AsSpan());
        }

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
