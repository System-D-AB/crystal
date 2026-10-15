// Spec 003 R2.4, R12.1 — lag in time: the age of the oldest batch a standby lacks, which is what a failover would lose.
// Sequences are not comparable across databases, so each served database keeps a small timeline of when its sequences
// were committed, sampled at most every 100 ms, and a slot's lag time is read from it.

using System;
using System.Buffers.Binary;
using System.Threading;

namespace SystemD.Crystal.Replication;

/// <summary>
/// When a database's sequences were committed, sampled (spec 003 R2.4). A bounded ring of (last sequence, time) pairs:
/// one sample per <see cref="SampleInterval"/> at most, <see cref="Capacity"/> samples, so it covers at least
/// <c>Capacity × SampleInterval</c> (about 3.4 minutes) of constant writing and longer when writes are sparse. Thread-safe.
/// </summary>
internal sealed class CommitTimeline
{
    /// <summary>The finest resolution of a lag time.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Samples kept.</summary>
    public const int Capacity = 2048;

    private readonly object _sync = new();
    private readonly long[] _sequences = new long[Capacity];
    private readonly long[] _ticks = new long[Capacity];
    private int _start;
    private int _count;

    // Spec 014 R1.1 — the commit path takes the lock only when a sample is due (about ten times a second). Between
    // samples a commit raises _pending, the newest sequence committed since the newest sample, without a lock, and the
    // next sample or read folds it into the newest sample: the same samples as before, no lock per commit.
    private const long NoSample = long.MinValue;
    private long _newestTicks = NoSample;
    private long _pending;

    /// <summary>
    /// Records a committed batch (its WAL bytes: an 8-byte sequence and a 4-byte count lead every batch) at
    /// <paramref name="nowTicks"/>. Within <see cref="SampleInterval"/> of the last sample, only that sample's sequence
    /// moves forward.
    /// </summary>
    public void Committed(ReadOnlySpan<byte> batch, long nowTicks)
    {
        if (batch.Length < 12)
        {
            return;
        }

        var first = (long)BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(batch[8..]);
        var last = first + Math.Max(count, 1u) - 1;
        var newestTicks = Volatile.Read(ref _newestTicks);
        if (newestTicks != NoSample && nowTicks - newestTicks < SampleInterval.Ticks)
        {
            RaisePending(last);
            return;
        }

        lock (_sync)
        {
            if (_count > 0)
            {
                var newest = (_start + _count - 1) % Capacity;
                if (nowTicks - _ticks[newest] < SampleInterval.Ticks)
                {
                    RaisePending(last);
                    return;
                }

                // A new sample is due: what was committed since the newest one belongs to it.
                FoldPending(newest);
            }

            var at = (_start + _count) % Capacity;
            if (_count == Capacity)
            {
                _start = (_start + 1) % Capacity; // the oldest sample gives way
            }
            else
            {
                _count++;
            }

            _sequences[at] = last;
            _ticks[at] = nowTicks;
            Volatile.Write(ref _newestTicks, nowTicks);
        }
    }

    private void RaisePending(long last)
    {
        var seen = Volatile.Read(ref _pending);
        while (last > seen)
        {
            var was = Interlocked.CompareExchange(ref _pending, last, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>Moves what was committed since the newest sample into it. Called under <c>_sync</c>.</summary>
    private void FoldPending(int newest)
    {
        var pending = Interlocked.Exchange(ref _pending, 0);
        _sequences[newest] = Math.Max(_sequences[newest], pending);
    }

    /// <summary>
    /// How long ago the oldest batch after <paramref name="acknowledged"/> was committed, at <paramref name="nowTicks"/>:
    /// zero when nothing recorded lies after it. When the oldest sample is already after it, the batch is older than the
    /// timeline, and the age of the oldest sample is returned: a lower bound.
    /// </summary>
    public TimeSpan AgeAfter(long acknowledged, long nowTicks)
    {
        lock (_sync)
        {
            if (_count > 0)
            {
                FoldPending((_start + _count - 1) % Capacity);
            }

            for (var i = 0; i < _count; i++)
            {
                var at = (_start + i) % Capacity;
                if (_sequences[at] > acknowledged)
                {
                    return TimeSpan.FromTicks(Math.Max(0, nowTicks - _ticks[at]));
                }
            }

            return TimeSpan.Zero;
        }
    }
}
