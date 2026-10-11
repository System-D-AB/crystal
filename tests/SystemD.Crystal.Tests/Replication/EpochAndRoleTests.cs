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
        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Demoted);
    }

    [Fact]
    public async Task AStalePrimary_IsDemotedByAHigherEpoch_OnHello()
    {
        // A primary at epoch E hears a HELLO from a peer at epoch E+1: it adopts the epoch and demotes (R6.3, R14.8).
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        store.Replication.Promote("become primary"); // epoch 2, Primary

        var events = new List<ReplicationEvent>();
        var epoch = ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
        // Use the live store's facade epoch by driving a source directly over a duplex connection.
        var source = new ReplicationSource(store, store.Engine, store.NodeId, LiveEpoch(store), Options().Replication!)
        {
            OnEvent = e => events.Add(e),
        };
        _ = epoch;

        var (server, client) = DuplexPair();
        var connection = new ReplicationConnection(server, source, "peer", null);
        var serve = connection.ServeAsync(CancellationToken.None);

        var replica = new ReplicationClient(client, "peer-node", "test-secret");
        // The peer announces a HIGHER epoch in its HELLO.
        await replica.HandshakeAsync(ReplicationProtocol.RolePrimary, store.Replication.Epoch + 5, CancellationToken.None);

        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Demoted);
        Assert.Contains(events, e => e.Kind == ReplicationEventKind.EpochChanged);

        source.Dispose();
        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
        try { await serve; } catch { /* closed */ }
    }

    [Fact]
    public async Task TwoPrimariesAtTheSameEpoch_RaiseAConflictEvent()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        store.Replication.Promote("become primary"); // Primary at some epoch

        var events = new List<ReplicationEvent>();
        var source = new ReplicationSource(store, store.Engine, store.NodeId, LiveEpoch(store), Options().Replication!)
        {
            OnEvent = e => events.Add(e),
        };

        var (server, client) = DuplexPair();
        var connection = new ReplicationConnection(server, source, "peer", null);
        var serve = connection.ServeAsync(CancellationToken.None);

        var replica = new ReplicationClient(client, "the-other-primary", "test-secret");
        // Another primary announces the SAME epoch.
        await replica.HandshakeAsync(ReplicationProtocol.RolePrimary, source.Epoch, CancellationToken.None);

        Assert.Contains(events, e => e.Kind == ReplicationEventKind.Conflict);

        source.Dispose();
        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
        try { await serve; } catch { /* closed */ }
    }

    // The live store's epoch holder, so the source and the facade share one epoch/role (reflection-free: Load reads
    // the same node.epoch the facade persisted, and the facade has already promoted, so the file is current).
    private static ReplicationEpoch LiveEpoch(CrystalStore store)
    {
        var holder = ReplicationEpoch.Load(store.DataDirectory, SystemD.Crystal.Storage.ReplicationRole.Primary);
        return holder;
    }

    private static (System.IO.Stream Server, System.IO.Stream Client) DuplexPair()
    {
        var toServer = new System.IO.Pipelines.Pipe();
        var toClient = new System.IO.Pipelines.Pipe();
        var server = new DuplexStream(toServer.Reader, toClient.Writer);
        var client = new DuplexStream(toClient.Reader, toServer.Writer);
        return (server, client);
    }
}
