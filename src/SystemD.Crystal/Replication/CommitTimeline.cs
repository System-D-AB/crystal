// Spec 003 R2.4, R12.1 — lag in time: the age of the oldest batch a standby lacks, which is what a failover would lose.
// Sequences are not comparable across databases, so each served database keeps a small timeline of when its sequences
// were committed, sampled at most every 100 ms, and a slot's lag time is read from it.

using System;
using System.Buffers.Binary;

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
        lock (_sync)
        {
            if (_count > 0)
            {
                var newest = (_start + _count - 1) % Capacity;
                if (nowTicks - _ticks[newest] < SampleInterval.Ticks)
                {
                    _sequences[newest] = Math.Max(_sequences[newest], last);
                    return;
                }
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
        }
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
