// Spec 015 R4.5 (decision D2) — the replication features of spec 013 taken for 1.0: promoting past a floor (013 R6),
// a standby's contact with its primary (013 R8) and a fence that lifts at the same epoch (013 R10).

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class HighwayReplicationFeatureTests
{
    private static CrystalOptions Options() => new()
    {
        Replication = new ReplicationOptions { Secret = "test-secret" },
    };

    [Fact]
    public void Promote_PastAFloor_LandsOnItInOneStep_AndTheEpochIsDurable()
    {
        // 013 R6: Highway promoted in a loop until it passed the highest epoch it had heard of.
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            Assert.Equal(100, store.Replication.Promote("past an announced epoch", minimumEpoch: 100));
            Assert.Equal(100, store.Replication.Epoch);
            Assert.Equal("Primary", store.Replication.Role.ToString());

            // A floor below the next epoch is the plain promotion: epoch + 1.
            Assert.Equal(101, store.Replication.Promote("again", minimumEpoch: 50));
        }

        using var reopened = CrystalStore.Open(dir.Path, Options());
        Assert.Equal(101, reopened.Replication.Epoch); // persisted, in node.epoch
    }

    [Fact]
    public void AStandby_ReportsItsLinkToThePrimary_AndItsLoss()
    {
        // 013 R8: a follower parked in a long poll and one whose primary vanished must look different. Highway sent its
        // own heartbeat every 200 ms to tell them apart.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        static ReplicationOptions Replication(string? listen) => new()
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300), // an idle link still replies this often
        };
        var primary = CrystalStore.Open(pdir.Path, new CrystalOptions { Replication = Replication("127.0.0.1:0") });
        using var standby = CrystalStore.Open(sdir.Path, new CrystalOptions { Replication = Replication(null) });
        try
        {
            primary.Replication.Listen();
            primary.CreateDatabase("shop", replicated: true);
            Assert.False(standby.Replication.Status().ConnectedToPrimary); // not following yet

            standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
            Assert.True(Eventually(() => standby.Replication.Status() is { ConnectedToPrimary: true } s && s.Followed.Count == 1),
                "the standby never reported a connection to its primary");

            // Idle and healthy: the long poll keeps answering, so the last reply keeps moving.
            var first = standby.Replication.Status().LastReplyFromPrimary;
            Assert.NotNull(first);
            Assert.True(Eventually(() => standby.Replication.Status().LastReplyFromPrimary > first),
                "the last reply did not move on an idle, healthy link");

            // The primary vanishes: the follower loses its connection, and the last reply stops.
            primary.Dispose();
            Assert.True(Eventually(() => !standby.Replication.Status().ConnectedToPrimary),
                "the standby still reported a connection after its primary closed");
            var last = standby.Replication.Status().LastReplyFromPrimary;
            Thread.Sleep(1000); // three long polls' worth: a live link would have replied
            Assert.Equal(last, standby.Replication.Status().LastReplyFromPrimary);
        }
        finally
        {
            primary.Dispose();
        }
    }

    [Fact]
    public void Unfence_AFencedPrimary_ServesAgainAtTheSameEpoch_AndSaysSo()
    {
        // 013 R10: Highway's fence is a backstop that lifts at the same epoch when contact returns; with Crystal's fence
        // lifting only by a new epoch, Highway kept its own write gate and left Crystal's primary serving.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var epoch = store.Replication.Promote("start");
        var db = store.CreateDatabase("shop", replicated: true);
        var box = db.Boxes.Create("orders");
        var events = new ConcurrentQueue<ReplicationEvent>();
        store.Replication.Events += (_, e) => events.Enqueue(e);

        store.Replication.Fence("lost every peer");
        Assert.Throws<NotPrimaryException>(() => Put(db, box, "while-fenced"));

        store.Replication.Unfence("contact returned");

        Assert.Equal("Primary", store.Replication.Role.ToString());
        Assert.Equal(epoch, store.Replication.Epoch); // no new epoch: its standbys continue where they were
        Put(db, box, "after"); // writes are accepted again
        Assert.True(store.Replication.DrainEventsForTests(TimeSpan.FromSeconds(10)));
        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Unfenced && e.Epoch == epoch);
    }

    [Fact]
    public void Unfence_IsRefused_UnlessAPrimaryFencedHere_AtTheSameEpoch()
    {
        using var dir = new TempStoreDirectory();

        // Not fenced.
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            store.Replication.Promote("start");
            Assert.Contains("not fenced", Assert.Throws<InvalidOperationException>(() => store.Replication.Unfence("x")).Message, StringComparison.Ordinal);

            // Fenced, then restarted: a restart lifts a fence by failing closed, to a standby (spec 003 R6.5), which has
            // no fence to lift; promotion, with a new epoch, is its way back.
            store.Replication.Fence("backstop");
        }

        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            Assert.Equal("Standby", store.Replication.Role.ToString());
            Assert.Contains("not fenced", Assert.Throws<InvalidOperationException>(() => store.Replication.Unfence("x")).Message, StringComparison.Ordinal);
            store.Replication.Promote("the way back after a restart");
            Assert.Equal("Primary", store.Replication.Role.ToString());
        }

        // A standby fenced here: lifting it to primary at the same epoch would make two primaries at one epoch.
        using var sdir = new TempStoreDirectory();
        using var standby = CrystalStore.Open(sdir.Path, Options());
        standby.Replication.Demote();
        standby.Replication.Fence("standby backstop");
        Assert.Throws<InvalidOperationException>(() => standby.Replication.Unfence("x"));
        Assert.Equal("Fenced", standby.Replication.Role.ToString());
    }

    private static void Put(CrystalDatabase db, Box box, string key)
    {
        using var batch = db.NewBatch();
        batch.Put(box, System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes("v"));
        batch.Commit(DurabilityMode.Strict);
    }

    private static bool Eventually(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData((1L << 62) + 1)]
    [InlineData(long.MaxValue)]
    public void Promote_PastAFloorThatNoHistoryReaches_IsRefused_AndNothingChanges(long floor)
    {
        // A garbled or hostile floor is refused before anything changes: no epoch write, no role change.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var epoch = store.Replication.Epoch;
        var role = store.Replication.Role;

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Replication.Promote("hostile", floor));

        Assert.Equal(epoch, store.Replication.Epoch);
        Assert.Equal(role, store.Replication.Role);
    }
}
