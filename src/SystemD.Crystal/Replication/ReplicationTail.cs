// Ported from Highway: src/Highway.Server/Storage/Rocks/ReplicationTail.cs — the primary's recently-committed WAL
// batches in memory, so a caught-up standby's pull is a binary search and a copy, never a WAL read from a file's
// start (spec 003 R3.5). Pure: no RocksDB type. One per replicated database, within the store's tail budget.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace SystemD.Crystal.Replication;

/// <summary>
/// The most recently committed WAL batches of one replicated database, in memory (spec 003 R3.5, design § 3).
///
/// <para><b>Same bytes as the WAL.</b> After a write returns, a batch's representation carries the sequence RocksDB
/// assigned and is byte-identical to the WAL record. RocksDB's group commit merges concurrent committers into one
/// WAL record while this holds them one by one — the same sequences and operations, finer boundaries — so a standby
/// fed from here can stand inside a WAL record; the WAL path trims such a record (<see cref="WriteBatchTrim"/>).</para>
///
/// <para><b>Never a gap.</b> Committers add after their write, so adds can arrive out of order and a batch can be
/// written but not yet added. Batches are kept sorted by sequence; <see cref="TryRead(ulong, int, List{ValueTuple{ulong, byte[]}}, out ulong?)"/> returns only a contiguous
/// run starting exactly at <c>fromSeq + 1</c>, stopping at the first hole. Anything it cannot answer is a miss, and
/// the caller falls back to the WAL path with its gap checks. Capture is off until the first pull; cleared on any
/// epoch or role change.</para>
/// </summary>
internal sealed class ReplicationTail
{
    internal const long DefaultMaxBytes = 16L * 1024 * 1024;
    internal const int DefaultMaxBatches = 131_072;

    private readonly record struct Entry(ulong Seq, ulong LastSeq, byte[] Data, long? Timestamp);

    private readonly Lock _gate = new();
    private Entry[] _ring = new Entry[1024];   // circular, sorted by Seq from _head
    private int _head;
    private int _count;
    private long _bytes;
    private volatile bool _capturing;

    public ReplicationTail(long maxBytes = DefaultMaxBytes, int maxBatches = DefaultMaxBatches)
    {
        MaxBytes = maxBytes;
        MaxBatches = maxBatches;
    }

    public long MaxBytes { get; private set; }

    /// <summary>
    /// Changes the byte limit (the store-wide tail budget's share for this database, design § 3), dropping the oldest
    /// batches at once if the tail now holds more. A batch dropped from the tail is still served from the WAL.
    /// </summary>
    public void SetMaxBytes(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        lock (_gate)
        {
            MaxBytes = maxBytes;
            TrimToLimits();
        }
    }

    public int MaxBatches { get; }

    /// <summary>Whether commits are being captured (set by the first pull).</summary>
    public bool Capturing => _capturing;

    internal int Count { get { lock (_gate) { return _count; } } }

    internal long Bytes { get { lock (_gate) { return _bytes; } } }

    /// <summary>Start capturing. Idempotent; called on every pull.</summary>
    public void Enable() => _capturing = true;

    /// <summary>Drop everything (epoch or role change). Capture stays as it was.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _bytes = 0;
        }
    }

    /// <summary>
    /// Record a batch just written. <paramref name="data"/> is the written batch's representation
    /// (<c>[8B LE sequence][4B LE count]…</c>). Ownership passes to the tail. <paramref name="timestamp"/> is when it was
    /// committed, on the store clock's timestamp scale (spec 014 R3: the delay from commit to ship), or null if unknown.
    /// </summary>
    public void Add(byte[] data, long? timestamp = null)
    {
        if (!_capturing || data.Length < 12)
        {
            return;
        }

        var seq = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (seq == 0)
        {
            return; // not a written batch
        }

        var entry = new Entry(seq, seq + Math.Max(count, 1u) - 1, data, timestamp);
        lock (_gate)
        {
            if (_count == _ring.Length)
            {
                Grow();
            }

            // Append, then move back past any later-sequenced entry (a concurrent committer that added first).
            var i = _count;
            while (i > 0 && At(i - 1).Seq > seq)
            {
                At(i) = At(i - 1);
                i--;
            }

            At(i) = entry;
            _count++;
            _bytes += data.Length;
            TrimToLimits();
        }
    }

    /// <summary>Drops the oldest batches while over a limit, always keeping the newest. Called under <c>_gate</c>.</summary>
    private void TrimToLimits()
    {
        while (_count > 1 && (_bytes > MaxBytes || _count > MaxBatches))
        {
            _bytes -= At(0).Data.Length;
            At(0) = default;
            _head = (_head + 1) % _ring.Length;
            _count--;
        }
    }

    /// <summary>
    /// The contiguous run of batches following <paramref name="fromSeq"/>, up to <paramref name="maxBytes"/> (the
    /// first batch always fits). False unless a held batch starts at exactly <c>fromSeq + 1</c> — the caller then
    /// uses the WAL. <paramref name="more"/> is the first sequence left out by the byte cap, or null.
    /// </summary>
    public bool TryRead(ulong fromSeq, int maxBytes, List<(ulong Seq, byte[] Data)> into, out ulong? more) =>
        TryRead(fromSeq, maxBytes, into, out more, out _);

    /// <summary>
    /// As <see cref="TryRead(ulong, int, List{ValueTuple{ulong, byte[]}}, out ulong?)"/>; <paramref name="firstTimestamp"/>
    /// is when the first batch returned was committed (null if unknown or nothing was returned).
    /// </summary>
    public bool TryRead(ulong fromSeq, int maxBytes, List<(ulong Seq, byte[] Data)> into, out ulong? more, out long? firstTimestamp)
    {
        more = null;
        firstTimestamp = null;
        var want = fromSeq + 1;
        lock (_gate)
        {
            // The run must start exactly at the cursor. A batch that merely contains it would be skipped whole by
            // the standby (first sequence <= its watermark); the WAL path trims such a batch.
            var i = Floor(want);
            if (i < 0 || At(i).Seq != want)
            {
                return false;
            }

            ulong pageBytes = 0;
            var expected = At(i).Seq;
            firstTimestamp = At(i).Timestamp;
            for (; i < _count; i++)
            {
                var e = At(i);
                if (e.Seq != expected)
                {
                    break; // a hole: written, not yet added — pull again
                }

                if (into.Count > 0 && pageBytes + (ulong)e.Data.Length > (ulong)maxBytes)
                {
                    more = e.Seq;
                    break;
                }

                into.Add((e.Seq, e.Data));
                pageBytes += (ulong)e.Data.Length;
                expected = e.LastSeq + 1;
            }

            return true;
        }
    }

    /// <summary>
    /// When the first batch after <paramref name="acknowledged"/> was committed, on the store clock's timestamp scale, if
    /// the tail holds that batch and its time (spec 014 R3: a lag in time finer than the commit timeline's samples);
    /// otherwise null.
    /// </summary>
    public long? CommittedAtAfter(ulong acknowledged)
    {
        var want = acknowledged + 1;
        lock (_gate)
        {
            var i = Floor(want);
            if (i >= 0 && At(i).LastSeq >= want)
            {
                return At(i).Timestamp; // the batch holding `want`
            }

            return i + 1 < _count && At(i + 1).Seq == want ? At(i + 1).Timestamp : null;
        }
    }

    /// <summary>
    /// Why <see cref="TryRead(ulong, int, List{ValueTuple{ulong, byte[]}}, out ulong?)"/> cannot answer
    /// <paramref name="fromSeq"/> (spec 014 R4.1): <see cref="TailMiss.Landing"/> when the next batch is newer than
    /// everything held — written, not yet added — and <see cref="TailMiss.Behind"/> otherwise (the cursor is older than
    /// the tail, inside a merged batch, or the tail holds nothing).
    /// </summary>
    public TailMiss ClassifyMiss(ulong fromSeq)
    {
        var want = fromSeq + 1;
        lock (_gate)
        {
            return _count > 0 && At(_count - 1).LastSeq < want ? TailMiss.Landing : TailMiss.Behind;
        }
    }

    /// <summary>Index of the last entry with Seq &lt;= <paramref name="seq"/>, or -1.</summary>
    private int Floor(ulong seq)
    {
        int lo = 0, hi = _count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (At(mid).Seq <= seq)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private ref Entry At(int logical) => ref _ring[(_head + logical) % _ring.Length];

    private void Grow()
    {
        var next = new Entry[_ring.Length * 2];
        for (var i = 0; i < _count; i++)
        {
            next[i] = At(i);
        }

        _ring = next;
        _head = 0;
    }
}

/// <summary>Why a tail read missed (spec 014 R4.1).</summary>
internal enum TailMiss
{
    /// <summary>The cursor is behind what the tail holds: a standby genuinely behind, served from the WAL.</summary>
    Behind,

    /// <summary>The next batch is newer than the tail's newest: committed, not yet added.</summary>
    Landing,
}
