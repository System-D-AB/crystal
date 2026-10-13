// Spec 011 R7 — two primaries at the same epoch, between real nodes (003 R6.4). The primary-side check needs a peer
// that is itself a primary, which Crystal's follower never is, so the standby detects it: it remembers the last primary
// it followed, and a different primary stating the same epoch is reported and given nothing to act on. The memory is
// persisted, so a restart between the two follows does not hide the conflict.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class ConflictTests
{
    private static CrystalOptions Options(string? listen = null) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public void AStandby_PointedAtASecondPrimaryAtTheSameEpoch_ReportsAConflict_AndTakesNothing()
    {
        using var adir = new TempStoreDirectory();
        using var bdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var a = CrystalStore.Open(adir.Path, Options(listen: "127.0.0.1:0"));
        using var b = CrystalStore.Open(bdir.Path, Options(listen: "127.0.0.1:0"));
        a.Replication.Promote("one");
        b.Replication.Promote("two");
        Assert.Equal(a.Replication.Epoch, b.Replication.Epoch);
        Seeded(a, "from-a");
        Seeded(b, "from-b");
        a.Replication.Listen();
        b.Replication.Listen();

        // The standby follows A, then restarts: what it followed is remembered across the restart.
        using (var first = CrystalStore.Open(sdir.Path, Options()))
        {
            first.Replication.Follow(a.Replication.ListenerEndpoint!);
            WaitUntil(() => first.ListDatabases().Any(d => d.Name == "from-a"), TimeSpan.FromSeconds(30));
            Assert.Contains(first.ListDatabases(), d => d.Name == "from-a");
        }

        using var standby = CrystalStore.Open(sdir.Path, Options());
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        standby.Replication.Follow(b.Replication.ListenerEndpoint!);

        WaitUntil(() => { lock (events) { return events.Any(e => e.Kind == ReplicationEventKind.Conflict); } }, TimeSpan.FromSeconds(30));
        standby.Replication.StopFollowing();
        EventDelivery.Drain(standby);

        ReplicationEvent conflict;
        lock (events)
        {
            conflict = Assert.Single(events, e => e.Kind == ReplicationEventKind.Conflict);
        }

        Assert.Equal(b.NodeId, conflict.PeerNodeId);
        Assert.Contains(a.NodeId, conflict.Detail, StringComparison.Ordinal);
        Assert.Contains(b.NodeId, conflict.Detail, StringComparison.Ordinal);

        // Nothing was taken from B, and nothing of A's was reconciled away.
        Assert.DoesNotContain(standby.ListDatabases(), d => d.Name == "from-b");
        Assert.Contains(standby.ListDatabases(), d => d.Name == "from-a");
    }

    [Fact]
    public void AStandby_PointedAtAnotherStandbyAtTheSameEpoch_IsNoConflict()
    {
        // Every node of a healthy cluster shares the epoch: only a peer stating "primary" can be a second primary.
        using var adir = new TempStoreDirectory();
        using var bdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var a = CrystalStore.Open(adir.Path, Options(listen: "127.0.0.1:0"));
        using var b = CrystalStore.Open(bdir.Path, Options(listen: "127.0.0.1:0"));
        a.Replication.Promote("one");
        b.Replication.Promote("two");
        b.Replication.Demote();
        Seeded(a, "from-a");
        a.Replication.Listen();
        b.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options());
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        standby.Replication.Follow(a.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "from-a"), TimeSpan.FromSeconds(30));

        // B is a standby at the same epoch: the follower is refused its pulls, but that is no conflict.
        standby.Replication.Follow(b.Replication.ListenerEndpoint!);
        WaitUntil(() => System.IO.File.Exists(System.IO.Path.Combine(sdir.Path, "replication", "primary.json")), TimeSpan.FromSeconds(5));
        Thread.Sleep(1000); // a bound for a negative: several reconnect attempts to B have run by now
        standby.Replication.StopFollowing();
        EventDelivery.Drain(standby);
        lock (events)
        {
            Assert.DoesNotContain(events, e => e.Kind == ReplicationEventKind.Conflict);
        }
    }

    private static void Seeded(CrystalStore store, string name)
    {
        var db = store.CreateDatabase(name, replicated: true);
        var box = db.Boxes.Create("box");
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v"));
        batch.Commit(DurabilityMode.Strict);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
        {
            Thread.Sleep(50);
        }
    }
}
