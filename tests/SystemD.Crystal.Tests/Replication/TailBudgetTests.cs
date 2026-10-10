// Spec 003 design § 3 (task T6, completed 2026-10-10) — the in-memory tails of the served databases share a store-wide
// budget: each gets an equal share, at most TailBytesPerDatabase, at least 1 MiB, rebalanced whenever a database starts
// or stops being served; a tail whose share shrinks drops its oldest batches at once (they are still in the WAL).

using System;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class TailBudgetTests
{
    private const long MiB = 1024 * 1024;

    private static CrystalOptions Options(long perDatabase, long budget) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            TailBytesPerDatabase = perDatabase,
            TailBudgetBytes = budget,
        },
    };

    [Fact]
    public void ServedTails_ShareTheBudget_CappedPerDatabase_AndRebalanceWhenOneStops()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(perDatabase: 16 * MiB, budget: 24 * MiB));
        store.Replication.Promote("to serve");
        var source = store.Replication.Source!;
        var uids = Enumerable.Range(0, 3).Select(i => store.CreateDatabase($"db{i}", replicated: true).Uid).ToList();

        source.ServingForTest(uids[0]);
        Assert.Equal(16 * MiB, source.TailLimitForTest(uids[0])); // alone: its own cap, under the budget

        source.ServingForTest(uids[1]);
        Assert.Equal(12 * MiB, source.TailLimitForTest(uids[0])); // two share 24 MiB: the first is lowered at once
        Assert.Equal(12 * MiB, source.TailLimitForTest(uids[1]));

        source.ServingForTest(uids[2]);
        Assert.All(uids, u => Assert.Equal(8 * MiB, source.TailLimitForTest(u)));

        store.DropDatabase(store.OpenDatabase("db2"));
        source.ServingForTest(uids[2]); // the dropped one is let go on its next lookup
        Assert.Equal(12 * MiB, source.TailLimitForTest(uids[0]));
        Assert.Equal(0, source.TailLimitForTest(uids[2]));
    }

    [Fact]
    public void AShare_IsNeverBelowOneMiB()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(perDatabase: 16 * MiB, budget: 2 * MiB));
        store.Replication.Promote("to serve");
        var source = store.Replication.Source!;
        var uids = Enumerable.Range(0, 4).Select(i => store.CreateDatabase($"db{i}", replicated: true).Uid).ToList();

        foreach (var uid in uids)
        {
            source.ServingForTest(uid);
        }

        Assert.All(uids, u => Assert.Equal(MiB, source.TailLimitForTest(u)));
    }

    [Fact]
    public void ATailWhoseShareShrinks_DropsItsOldestBatchesAtOnce_KeepingTheNewest()
    {
        var tail = new ReplicationTail(maxBytes: 1000);
        tail.Enable();
        for (ulong seq = 1; seq <= 10; seq++)
        {
            tail.Add(Batch(seq, payload: 88)); // 100 bytes each
        }

        Assert.Equal(1000, tail.Bytes);

        tail.SetMaxBytes(350);

        Assert.Equal(3, tail.Count);
        var read = new List<(ulong Seq, byte[] Data)>();
        Assert.True(tail.TryRead(7, int.MaxValue, read, out _));
        Assert.Equal([8UL, 9UL, 10UL], read.Select(b => b.Seq));
        Assert.False(tail.TryRead(6, int.MaxValue, new List<(ulong Seq, byte[] Data)>(), out _));
    }

    private static byte[] Batch(ulong seq, int payload)
    {
        var bytes = new byte[12 + payload];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), seq);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), 1u);
        return bytes;
    }
}
