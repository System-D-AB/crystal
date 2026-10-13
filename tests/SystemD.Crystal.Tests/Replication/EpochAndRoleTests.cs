// Spec 003 T12 / R6, R9.1 — epochs and roles: promote (persist-first, reincarnate, event), demote, fence (read-only),
// higher-epoch adoption (a stale primary demotes), the equal-epoch conflict event, and the epoch kept across reopen.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class EpochAndRoleTests
{
    private static CrystalOptions Options(string? listen = null) => new()
    {
        Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = listen },
    };

    [Fact]
    public void Promote_BumpsTheEpoch_TakesPrimary_AndRaisesAnEvent()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());

        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => events.Add(e);

        var before = store.Replication.Epoch;
        var newEpoch = store.Replication.Promote("test");

        Assert.Equal(before + 1, newEpoch);
        Assert.Equal(ReplicationRole.Primary, store.Replication.Role);
        EventDelivery.Drain(store);
        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Promoted && e.Epoch == newEpoch);
    }

    [Fact]
    public void Promote_WhenTheEpochCannotBeWritten_ThrowsIOException_AndLeavesTheNodeAsItWas()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        store.Replication.Demote();
        var epoch = store.Replication.Epoch;
        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => events.Add(e);

        // Hold the epoch's temporary file open, so writing the new epoch fails.
        var path = System.IO.Directory.GetFiles(dir.Path, ReplicationEpoch.FileName, System.IO.SearchOption.AllDirectories)[0];
        using (new System.IO.FileStream(path + ".tmp", System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
        {
            var ex = Assert.Throws<System.IO.IOException>(() => store.Replication.Promote("test"));
            Assert.Contains("promote again", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.InnerException);
        }

        Assert.Equal(epoch, store.Replication.Epoch);
        Assert.Equal(ReplicationRole.Standby, store.Replication.Role);
        EventDelivery.Drain(store);
        Assert.DoesNotContain(events, e => e.Kind == ReplicationEventKind.Promoted);

        // With the disk fixed, promotion succeeds.
        Assert.Equal(epoch + 1, store.Replication.Promote("test"));
    }

    [Fact]
    public void TheEpoch_IsKeptAcrossReopen()
    {
        using var dir = new TempStoreDirectory();
        long promoted;
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            promoted = store.Replication.Promote("first");
            Assert.True(promoted >= 2);
        }

        // Reopen: the persisted node.epoch is read back; the node does not come back at epoch 1 (R6.1, R14.8).
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            Assert.Equal(promoted, store.Replication.Epoch);
        }
    }

    [Fact]
    public void Fence_MakesTheNodeReadOnly_RefusingPullsAndKeepingDatabasesReadable()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");

        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => events.Add(e);

        store.Replication.Fence("test");
        Assert.Equal(ReplicationRole.Fenced, store.Replication.Role);
        EventDelivery.Drain(store);
        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Fenced);
    }

    [Fact]
    public void Demote_FromPrimary_TakesStandby_AndRaisesAnEvent()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        store.Replication.Promote("to primary");

        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => events.Add(e);

        store.Replication.Demote();
        Assert.Equal(ReplicationRole.Standby, store.Replication.Role);
        EventDelivery.Drain(store);
        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Demoted);
    }

    [Fact]
    public async Task AStalePrimary_IsDemotedByAHigherEpoch_OnHello()
    {
        // A primary at epoch E hears an authenticated peer at a higher epoch: it adopts the epoch and demotes (R6.3,
        // R14.8). Rewritten for spec 011 R12.1: it used to drive a separate ReplicationSource over a second
        // ReplicationEpoch loaded from the file, so the store's own role and epoch were never shown to change. It now
        // goes through the store's listener, and asserts on the store.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        store.Replication.Promote("become primary");
        store.Replication.Listen();
        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        var higher = store.Replication.Epoch + 5;

        await HandshakeWith(store, "peer-node", ReplicationProtocol.RolePrimary, higher);

        Assert.True(SpinWait.SpinUntil(() => store.Replication.Role == ReplicationRole.Standby, TimeSpan.FromSeconds(10)));
        Assert.Equal(higher, store.Replication.Epoch);
        EventDelivery.Drain(store);
        lock (events)
        {
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.Demoted);
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.EpochChanged && e.Epoch == higher);
        }
    }

    [Fact]
    public async Task TwoPrimariesAtTheSameEpoch_RaiseAConflictEvent()
    {
        // The primary-side check (R6.4): a peer stating it is a primary at this node's epoch. Through the store's own
        // listener and epoch (spec 011 R12.1); the standby-side check between real nodes is ConflictTests.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        store.Replication.Promote("become primary");
        store.Replication.Listen();
        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };

        await HandshakeWith(store, "the-other-primary", ReplicationProtocol.RolePrimary, store.Replication.Epoch);

        EventDelivery.Drain(store);
        lock (events)
        {
            var conflict = Assert.Single(events, e => e.Kind == ReplicationEventKind.Conflict);
            Assert.Equal("the-other-primary", conflict.PeerNodeId);
        }

        Assert.Equal(ReplicationRole.Primary, store.Replication.Role); // Crystal reports; the product decides
    }

    private static async Task HandshakeWith(CrystalStore store, string nodeId, string role, long epoch)
    {
        var endpoint = store.Replication.ListenerEndpoint!;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        var client = new ReplicationClient(tcp.GetStream(), nodeId, "test-secret");
        await client.HandshakeAsync(role, epoch, CancellationToken.None);
    }
}
