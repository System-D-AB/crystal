// Spec 003 should-fix 8 of the 2026-10-10 review: the listener keeps at most MaxConnections open; one more is closed as
// soon as it is accepted, while those already open are served. Spec 011 R11.9: at most a quarter of them (at least one)
// may be unauthenticated at once, so peers that never authenticate cannot take the places standbys need.

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class ConnectionLimitTests
{
    [Fact]
    public async Task OneConnectionPastTheCap_IsClosedAtOnce_AndTheOthersAreServed()
    {
        // Both connections authenticate first (spec 011 R11.9 refuses a second unauthenticated one under a cap of 2, so
        // this test of the total cap no longer holds them unauthenticated).
        using var dir = new TempStoreDirectory();
        using var store = Listening(dir, maxConnections: 2, out var port);

        using var first = new TcpClient();
        using var second = new TcpClient();
        using var third = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await first.ConnectAsync("127.0.0.1", port);
        await new ReplicationClient(first.GetStream(), "standby-1", "test-secret").HandshakeAsync(ReplicationProtocol.RoleStandby, 1, cts.Token);
        await second.ConnectAsync("127.0.0.1", port);
        await new ReplicationClient(second.GetStream(), "standby-2", "test-secret").HandshakeAsync(ReplicationProtocol.RoleStandby, 1, cts.Token);
        await third.ConnectAsync("127.0.0.1", port);

        // The third is closed by the listener: a read returns end of stream.
        var read = await third.GetStream().ReadAsync(new byte[1], cts.Token);
        Assert.Equal(0, read);

        // The first is still served.
        var status = await new ReplicationClient(first.GetStream(), "standby-1", "test-secret")
            .RequestAsync([System.Text.Encoding.UTF8.GetBytes("STATUS")], cts.Token);
        Assert.NotEqual(SystemD.Crystal.Replication.Resp.RespType.Error, status.Type);
    }

    [Fact]
    public async Task UnauthenticatedConnections_TakeAtMostAQuarterOfTheCap()
    {
        // R11.9: a cap of 4 allows one unauthenticated connection; a second is closed at once, while an authenticated
        // standby connected before them is served throughout.
        using var dir = new TempStoreDirectory();
        using var store = Listening(dir, maxConnections: 4, out var port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var standby = new TcpClient();
        await standby.ConnectAsync("127.0.0.1", port);
        var client = new ReplicationClient(standby.GetStream(), "standby-1", "test-secret");
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, cts.Token);

        using var idle1 = new TcpClient();
        using var idle2 = new TcpClient();
        await idle1.ConnectAsync("127.0.0.1", port);
        await Task.Delay(200); // accepted and counted
        await idle2.ConnectAsync("127.0.0.1", port);

        Assert.Equal(0, await idle2.GetStream().ReadAsync(new byte[1], cts.Token)); // closed at once
        var status = await client.RequestAsync([System.Text.Encoding.UTF8.GetBytes("STATUS")], cts.Token);
        Assert.NotEqual(SystemD.Crystal.Replication.Resp.RespType.Error, status.Type);
    }

    private static CrystalStore Listening(TempStoreDirectory dir, int maxConnections, out int port)
    {
        var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = "127.0.0.1:0", MaxConnections = maxConnections },
        });
        store.Replication.Listen();
        var endpoint = store.Replication.ListenerEndpoint!;
        port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return store;
    }
}
