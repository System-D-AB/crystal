// Spec 003 should-fix 8 of the 2026-10-10 review: the listener keeps at most MaxConnections open; one more is closed as
// soon as it is accepted, while those already open are served.

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
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = "127.0.0.1:0", MaxConnections = 2 },
        });
        store.Replication.Listen();
        var endpoint = store.Replication.ListenerEndpoint!;
        var colon = endpoint.LastIndexOf(':');
        var port = int.Parse(endpoint[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        using var first = new TcpClient();
        using var second = new TcpClient();
        using var third = new TcpClient();
        await first.ConnectAsync("127.0.0.1", port);
        await second.ConnectAsync("127.0.0.1", port);
        await Task.Delay(300); // both accepted and being served
        await third.ConnectAsync("127.0.0.1", port);

        // The third is closed by the listener: a read returns end of stream.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await third.GetStream().ReadAsync(new byte[1], cts.Token);
        Assert.Equal(0, read);

        // The first is still served: a handshake on it succeeds.
        var client = new ReplicationClient(first.GetStream(), "standby-1", "test-secret");
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, cts.Token);
    }
}
