// Spec 003 T15 / R13 — replication observability: crystal.replication.* instruments on 007's meter, log events in the
// 2000s range, and the Status() replication section with its transition log. Each instrument and event has a test, as
// in 007.

using System;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class ReplicationObservabilityTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();
    private readonly MetricsCapture _metrics = new();
    private readonly CapturingLoggerFactory _logs = new();

    private CrystalStore Open() => CrystalStore.Open(_dir.Path, new CrystalOptions
    {
        MeterFactory = _metrics,
        LoggerFactory = _logs,
        Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = "127.0.0.1:0" },
    });

    public void Dispose()
    {
        _metrics.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Promote_IncrementsTheCounter_RaisesTheLogEvent_AndMovesTheRoleEpochGauges()
    {
        using var store = Open();

        store.Replication.Promote("test");

        Assert.Single(_metrics.Of("crystal.replication.promotions"));
        Assert.NotEmpty(_logs.Of(CrystalLogEvents.ReplicationPromoted));

        // Observable gauges: role 0 (primary) and the bumped epoch.
        Assert.Equal(0.0, _metrics.Collect("crystal.replication.role").Single().Value);
        Assert.True(_metrics.Collect("crystal.replication.epoch").Single().Value >= 2);
    }

    [Fact]
    public void Demote_And_Fence_IncrementTheirCountersAndLogs_AndMoveTheRoleGauge()
    {
        using var store = Open();
        store.Replication.Promote("to primary");

        store.Replication.Demote();
        Assert.Single(_metrics.Of("crystal.replication.demotions"));
        Assert.NotEmpty(_logs.Of(CrystalLogEvents.ReplicationDemoted));
        Assert.Equal(1.0, _metrics.Collect("crystal.replication.role").Single().Value); // standby

        store.Replication.Fence("test");
        Assert.Single(_metrics.Of("crystal.replication.fences"));
        Assert.NotEmpty(_logs.Of(CrystalLogEvents.ReplicationFenced));
        Assert.Equal(2.0, _metrics.Collect("crystal.replication.role").Single().Value); // fenced
    }

    [Fact]
    public void Status_ReportsRoleEpochAndABoundedTransitionLog()
    {
        using var store = Open();
        store.Replication.Listen();
        var endpoint = store.Replication.ListenerEndpoint;

        store.Replication.Promote("one");
        store.Replication.Demote();
        store.Replication.Fence("two");

        var status = store.Replication.Status();
        Assert.Equal("fenced", status.Role);
        Assert.True(status.Epoch >= 2);
        Assert.Equal(endpoint, status.ListenerEndpoint);
        Assert.Null(status.FollowedPrimary);

        // The transition log holds the recent transitions, newest last, bounded.
        Assert.Contains(status.RecentTransitions, t => t.Contains("Promoted", StringComparison.Ordinal));
        Assert.Contains(status.RecentTransitions, t => t.Contains("Demoted", StringComparison.Ordinal));
        Assert.Contains(status.RecentTransitions, t => t.Contains("Fenced", StringComparison.Ordinal));
    }

    [Fact]
    public void AFollowingStandby_AndItsPrimary_CountWhatTheyShipAndApply_LogConnections_AndReportLag()
    {
        // Spec 011 R10.1–R10.4, end to end between two stores, each with its own meter and log capture.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var pmetrics = new MetricsCapture();
        using var smetrics = new MetricsCapture();
        var plogs = new CapturingLoggerFactory();
        var slogs = new CapturingLoggerFactory();
        using var primary = CrystalStore.Open(pdir.Path, new CrystalOptions
        {
            MeterFactory = pmetrics,
            LoggerFactory = plogs,
            Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = "127.0.0.1:0", LongPollMax = TimeSpan.FromMilliseconds(300) },
        });
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        Put(db, box, "seed");
        primary.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, new CrystalOptions
        {
            MeterFactory = smetrics,
            LoggerFactory = slogs,
            Replication = new ReplicationOptions { Secret = "test-secret", LongPollMax = TimeSpan.FromMilliseconds(300) },
        });
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db"));

        // Pages after the bootstrap: shipped by the primary, applied by the standby.
        for (var i = 0; i < 5; i++)
        {
            Put(db, box, $"k{i}");
        }

        WaitUntil(() => standby.Replication.Status().Followed.Any(f => f.Position == (long)db.Slot.LatestSequence));

        // R10.4: the standby's position and lag, in Status() and as a gauge.
        var followed = Assert.Single(standby.Replication.Status().Followed);
        Assert.Equal("db", followed.Name);
        Assert.Equal(db.Uid, followed.Uid);
        Assert.Equal((long)db.Slot.LatestSequence, followed.PrimaryLatest);
        Assert.Equal(0, followed.Lag);
        // R10.1: counted on each side (before collecting the gauge: the capture's Collect clears what was recorded).
        Assert.NotEmpty(smetrics.Of("crystal.replication.bootstraps"));
        Assert.NotEmpty(smetrics.Of("crystal.replication.pages_applied"));
        Assert.True(smetrics.Of("crystal.replication.bytes_applied").Sum(r => r.Value) > 0);
        Assert.True(pmetrics.Of("crystal.replication.bytes_shipped").Sum(r => r.Value) > 0);
        Assert.Equal(0.0, smetrics.Collect("crystal.replication.standby_lag").Single().Value);

        // R10.3: a standby adopting a newer epoch from its primary says so.
        primary.Replication.Promote("again");
        Put(db, box, "after-promotion");
        // Wait for the event itself, not the epoch: the standby's epoch is visible a moment before the event is posted, so
        // a drain right after the epoch matched could find nothing queued yet (it failed so once in a full run).
        WaitUntil(() => { lock (events) { return events.Any(e => e.Kind == ReplicationEventKind.EpochChanged); } });
        Assert.Equal(primary.Replication.Epoch, standby.Replication.Epoch);
        lock (events)
        {
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.EpochChanged && e.Epoch == primary.Replication.Epoch);
        }

        // R10.2: connected on both sides; lost on the standby once it stops.
        Assert.NotEmpty(slogs.Of(CrystalLogEvents.ReplicationConnected));
        Assert.NotEmpty(plogs.Of(CrystalLogEvents.ReplicationConnected));
        standby.Replication.StopFollowing();
        Assert.NotEmpty(slogs.Of(CrystalLogEvents.ReplicationDisconnected));
        WaitUntil(() => plogs.Of(CrystalLogEvents.ReplicationDisconnected).Count > 0);
        Assert.NotEmpty(plogs.Of(CrystalLogEvents.ReplicationDisconnected));
    }

    private static void Put(CrystalDatabase db, Box box, string key)
    {
        using var batch = db.NewBatch();
        batch.Put(box, System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes("v"));
        batch.Commit(DurabilityMode.Strict);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && !condition())
        {
            System.Threading.Thread.Sleep(50);
        }
    }

    [Fact]
    public void EveryReplicationEventKind_HasALogMethodAndEventId()
    {
        // R13.3: one log event per R9.3 event plus connected/lost. Assert the ids exist and are in the 2000s range.
        var ids = new[]
        {
            CrystalLogEvents.ReplicationPromoted, CrystalLogEvents.ReplicationDemoted, CrystalLogEvents.ReplicationFenced,
            CrystalLogEvents.ReplicationEpochChanged, CrystalLogEvents.ReplicationSlotLagging, CrystalLogEvents.ReplicationSlotDropped,
            CrystalLogEvents.ReplicationGap, CrystalLogEvents.ReplicationDivergence, CrystalLogEvents.ReplicationFormatRefused,
            CrystalLogEvents.ReplicationConflict, CrystalLogEvents.ReplicationConnected, CrystalLogEvents.ReplicationDisconnected,
        };

        Assert.All(ids, id => Assert.InRange(id.Id, 2000, 2999));
        Assert.Equal(ids.Length, ids.Select(i => i.Id).Distinct().Count()); // ids are unique
    }
}
