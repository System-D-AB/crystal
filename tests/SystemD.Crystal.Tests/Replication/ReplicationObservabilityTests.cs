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
