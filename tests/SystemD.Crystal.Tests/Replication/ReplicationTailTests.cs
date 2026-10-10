// Spec 003 T6 / R3.5, R3.6, R11.2 — the primary's in-memory tail of recently committed WAL batches, the trim of a
// group-commit record to a cursor, and the WAL read path (WalAccess.ReadPage / ApplyRaw) through a real database.
// Ported from Highway's ReplicationTailTests (the pure tail and trim cases); the live-WAL cases are Crystal's own,
// since Crystal's tail and WAL path sit behind WalAccess rather than a RocksDbStore. xUnit assertions throughout.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class ReplicationTailTests
{
    // A batch representation as the tail stores it: [8B LE first seq][4B LE count], padded to `size`.
    private static byte[] Batch(ulong seq, uint count, int size = 32)
    {
        var data = new byte[Math.Max(size, 12)];
        BinaryPrimitives.WriteUInt64LittleEndian(data, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), count);
        return data;
    }

    private static ReplicationTail Capturing(long maxBytes = ReplicationTail.DefaultMaxBytes)
    {
        var tail = new ReplicationTail(maxBytes);
        tail.Enable();
        return tail;
    }

    private static List<ulong> Seqs(List<(ulong Seq, byte[] Data)> page) => page.Select(b => b.Seq).ToList();

    [Fact]
    public void Reads_TheContiguousRun_AfterTheCursor()
    {
        var tail = Capturing();
        tail.Add(Batch(1, 2));   // 1..2
        tail.Add(Batch(3, 1));   // 3
        tail.Add(Batch(4, 3));   // 4..6

        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.True(tail.TryRead(2, 1 << 20, page, out var more));
        Assert.Equal([3UL, 4UL], Seqs(page));
        Assert.Null(more);

        // seq 5 is inside the batch 4..6: served whole, the standby would skip it — the WAL path trims instead.
        page.Clear();
        Assert.False(tail.TryRead(4, 1 << 20, page, out _));
    }

    [Fact]
    public void StopsAtAHole_AndHealsWhenTheLateBatchArrives()
    {
        var tail = Capturing();
        tail.Add(Batch(1, 1));
        tail.Add(Batch(3, 1));   // 2 is written but its committer has not added it yet

        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.True(tail.TryRead(0, 1 << 20, page, out _));
        Assert.Equal([1UL], Seqs(page));

        // The batch holding seq 2 is not held — the WAL must answer.
        page.Clear();
        Assert.False(tail.TryRead(1, 1 << 20, page, out _));

        tail.Add(Batch(2, 1));
        page.Clear();
        Assert.True(tail.TryRead(0, 1 << 20, page, out _));
        Assert.Equal([1UL, 2UL, 3UL], Seqs(page));
    }

    [Fact]
    public void ByteCap_SplitsThePage_AndReportsWhereToContinue()
    {
        var tail = Capturing();
        for (ulong s = 1; s <= 5; s++)
        {
            tail.Add(Batch(s, 1, size: 100));
        }

        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.True(tail.TryRead(0, 250, page, out var more));
        Assert.Equal([1UL, 2UL], Seqs(page));
        Assert.Equal(3UL, more);

        // The first batch always fits, as on the WAL path.
        page.Clear();
        Assert.True(tail.TryRead(0, 1, page, out more));
        Assert.Single(page);
        Assert.Equal(2UL, more);
    }

    [Fact]
    public void Eviction_TurnsOldCursorsIntoMisses()
    {
        var tail = Capturing(maxBytes: 300);
        for (ulong s = 1; s <= 10; s++)
        {
            tail.Add(Batch(s, 1, size: 100));
        }

        Assert.True(tail.Bytes <= 300);
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.False(tail.TryRead(0, 1 << 20, page, out _)); // seq 1 was evicted
        Assert.True(tail.TryRead(8, 1 << 20, page, out _));
        Assert.Equal([9UL, 10UL], Seqs(page));
    }

    [Fact]
    public void NothingIsCaptured_UntilAReplicaPulls_AndClearEmptiesIt()
    {
        var tail = new ReplicationTail();
        tail.Add(Batch(1, 1));
        Assert.Equal(0, tail.Count); // a store with no standby pays nothing

        tail.Enable();
        tail.Add(Batch(2, 1));
        Assert.Equal(1, tail.Count);

        tail.Clear();
        Assert.Equal(0, tail.Count);
        Assert.Equal(0, tail.Bytes);
    }

    [Fact]
    public void OutOfOrderAdds_AreKeptSortedBySequence()
    {
        // Concurrent committers add after their write, so a later sequence can be added first. The tail must still
        // read a contiguous run in sequence order.
        var tail = Capturing();
        tail.Add(Batch(3, 1));
        tail.Add(Batch(1, 1));
        tail.Add(Batch(2, 1));

        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.True(tail.TryRead(0, 1 << 20, page, out _));
        Assert.Equal([1UL, 2UL, 3UL], Seqs(page));
    }
}
