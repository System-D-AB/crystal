// Ported from Highway: src/Highway.Server/Storage/Rocks/RocksDbSnapshot.cs
// Adapted for Crystal: box-relative point reads, bounded scans, no RocksDB types exposed (spec 002 R6.4, R6.5, design § 2).

using System;
using System.Collections.Generic;
using System.Threading;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>
/// A point-in-time snapshot of one database (spec 002 R6.4).
/// Provides consistent point reads and bounded box scans isolated from subsequent writes.
/// </summary>
public sealed class Snapshot : IDisposable
{
    private readonly CrystalDatabase _database;
    private readonly DatabaseContext _context;
    private readonly DatabaseSlot _slot;
    private readonly StorageSnapshot _storageSnapshot;
    private int _disposed;

    internal Snapshot(CrystalDatabase database, DatabaseContext context, DatabaseSlot slot)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _storageSnapshot = slot.OpenSnapshot(database.Name);
    }

    /// <summary>
    /// Reads the value for <paramref name="key"/> within <paramref name="box"/> at this snapshot (spec 002 R6.4).
    /// Returns <c>null</c> if the key does not exist.
    /// Throws <see cref="BoxDroppedException"/> if the box was dropped (R4.3).
    /// </summary>
    public byte[]? Get(Box box, ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        using var _ = _database.EnterOperation();
        _context.EnsureOwned(box);
        _context.EnsureBoxesLive([box.Id]);
        var family = _context.FamilyNameOf(box.Id);
        var prefixedKey = BoxKeys.BuildKey(box.Id, key);
        return _context.DirectGet(family, prefixedKey, _storageSnapshot);
    }

    /// <summary>
    /// Scans a bounded range of keys within <paramref name="box"/> at this snapshot (spec 002 R6.4, R6.5).
    /// Never returns keys of another box.
    /// <para>Dispose the enumeration (a <c>foreach</c> does): until it ends, a drop of this database or the store's
    /// <c>Dispose</c> waits for it, and calling either from inside the loop waits forever (spec 005 design A3, A9).</para>
    /// </summary>
    public IEnumerable<KeyValue> Scan(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, int limit)
    {
        ThrowIfDisposed();
        IEnumerable<KeyValue> scan;
        using (_database.EnterOperation())
        {
            _context.EnsureOwned(box);
            if (limit <= 0)
            {
                return [];
            }

            scan = _context.ScanBox(box.Id, _storageSnapshot, from, to, limit);
        }

        return Enumerate(scan);
    }

    private IEnumerable<KeyValue> Enumerate(IEnumerable<KeyValue> scan)
    {
        using var _ = _database.EnterOperation();
        ThrowIfDisposed();
        foreach (var item in scan)
        {
            yield return item;
        }
    }

    internal StorageSnapshot Storage => _storageSnapshot;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>Disposes this snapshot and releases its read resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _slot.ReleaseSnapshot(_storageSnapshot);
    }
}
