// Spec 003 T4 / R10 — the listener/client protocol: HELLO (version/format), AUTH (HMAC challenge), the pre-AUTH
// gate, STATUS. Driven over an in-memory duplex stream pair (the ReplicationConnection server against the
// ReplicationClient standby) for determinism, plus one real TCP round trip through the listener.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class ReplicationProtocolTests
{
    private const string Secret = "a-shared-secret";

    private sealed class FakeSource : IReplicationSource
    {
        public string NodeId { get; init; } = "primary-node";
        public string Role { get; init; } = ReplicationProtocol.RolePrimary;
        public long Epoch { get; init; } = 7;
        public string Secret { get; init; } = ReplicationProtocolTests.Secret;
        public string StatusJson() => "{\"role\":\"primary\",\"epoch\":7}";

        public void ObservePeer(long peerEpoch, string peerRole, string peerNodeId, string? peerEndpoint = null) { /* no-op in the protocol tests */ }

        public System.Threading.Tasks.ValueTask<bool> TryHandleAsync(
            string command, IReadOnlyList<byte[]> args, string peerNodeId, RespWriter reply, System.Threading.CancellationToken cancellationToken)
        {
            // The protocol tests exercise the handshake/STATUS; other authenticated commands are not primary-served here.
            reply.Error($"{ReplicationProtocol.ErrNotPrimary} {Epoch} {Role}");
            return System.Threading.Tasks.ValueTask.FromResult(true);
        }
    }

    // A pair of streams wired to each other: what one writes, the other reads (an in-memory socket).
    private static (Stream Server, Stream Client) DuplexPair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = new DuplexStream(toServer.Reader, toClient.Writer);
        var client = new DuplexStream(toClient.Reader, toServer.Writer);
        return (server, client);
    }

    private static async Task<ReplicationClient> HandshakeAsync(FakeSource source, Stream serverStream, Stream clientStream, CancellationToken ct)
    {
        var connection = new ReplicationConnection(serverStream, source, "test:0", logger: null);
        _ = connection.ServeAsync(ct);
        var client = new ReplicationClient(clientStream, "standby-node", Secret);
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, epoch: 0, ct);
        return client;
    }

    [Fact]
    public async Task Handshake_WithCorrectSecret_Succeeds_AndStatusIsReadable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (serverStream, clientStream) = DuplexPair();
        var source = new FakeSource();

        var client = await HandshakeAsync(source, serverStream, clientStream, cts.Token);

        Assert.Equal("primary-node", client.PrimaryNodeId);
        Assert.Equal(ReplicationProtocol.RolePrimary, client.PrimaryRole);
        Assert.Equal(7, client.PrimaryEpoch);

        // STATUS is readable after AUTH.
        var status = await client.RequestAsync([Text.UTF8.GetBytes("STATUS")], cts.Token);
        Assert.Equal(RespType.BulkString, status.Type);
        Assert.Contains("primary", Text.UTF8.GetString(status.Bytes!));
    }

    [Fact]
    public async Task Handshake_WithWrongSecret_IsRefused_AndConnectionCloses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (serverStream, clientStream) = DuplexPair();
        var source = new FakeSource();

        var connection = new ReplicationConnection(serverStream, source, "test:0", logger: null);
        var serve = connection.ServeAsync(cts.Token);

        var client = new ReplicationClient(clientStream, "standby-node", "the-WRONG-secret");
        var ex = await Assert.ThrowsAsync<ReplicationProtocolException>(
            () => client.HandshakeAsync(ReplicationProtocol.RoleStandby, 0, cts.Token));
        Assert.Contains(ReplicationProtocol.ErrAuth, ex.Message);

        // The connection closed after the wrong HMAC: the server loop ends.
        await serve.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.True(serve.IsCompleted);
    }

    [Fact]
    public async Task APeerThatCannotProveItHoldsTheSecret_IsRefusedByTheStandby()
    {
        // A fake "primary" that hands out a nonce and answers AUTH with +OK, as anyone posing at the endpoint could:
        // the standby must refuse it before taking anything from it (003 should-fix 4).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = DuplexPair();
        var fake = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            _ = await server.ReadAsync(buffer, cts.Token); // HELLO
            var hello = new RespWriter().ArrayHeader(6).Integer(ReplicationProtocol.Version).BulkString("impostor")
                .BulkString(ReplicationProtocol.RolePrimary).Integer(1).BulkString(ReplicationProtocol.Format)
                .BulkString(Convert.ToHexStringLower(ReplicationProtocol.NewNonce()));
            await server.WriteAsync(hello.ToArray(), cts.Token);
            _ = await server.ReadAsync(buffer, cts.Token); // AUTH
            await server.WriteAsync(new RespWriter().SimpleString("OK").ToArray(), cts.Token);
        });

        var standby = new ReplicationClient(client, "standby-node", Secret);
        var refused = await Assert.ThrowsAsync<ReplicationProtocolException>(
            () => standby.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, cts.Token));
        Assert.Contains("did not prove", refused.Message);
        await fake;
    }

    [Fact]
    public async Task APeerThatKeepsSayingHello_IsClosedWhenTheDeadlineFromConnectRunsOut()
    {
        // The pre-AUTH deadline counts from the connection, not from each request (003 should-fix 8).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = DuplexPair();
        var conn = new ReplicationConnection(server, new FakeSource(), "test:0", logger: null, preAuthTimeout: TimeSpan.FromMilliseconds(600));
        var serve = conn.ServeAsync(cts.Token);

        // Close the server's side when serving ends, as the listener does with the socket. Without it, a HELLO sent just
        // as the deadline fires waits for a reply that never comes and no end of stream either (spec 011 T14: the cause
        // of this test's failure under load, reproduced 240 times in 300 tight runs, 0 with the close).
        _ = serve.ContinueWith(_ => server.Dispose(), TaskScheduler.Default);
        var raw = new RawClient(client);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!serve.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                await raw.HelloAsync(cts.Token); // a new request every 200 ms, never AUTH
            }
            catch (Exception)
            {
                break; // the server closed
            }

            await Task.Delay(200, cts.Token);
        }

        await serve.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"closed after {sw.Elapsed.TotalMilliseconds:F0} ms of HELLOs");
    }

    [Fact]
    public async Task ReplayedResponse_OnANewConnection_IsRefused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new FakeSource();

        // First connection: capture the HMAC the standby sent (by driving HELLO/AUTH manually).
        var (server1, client1) = DuplexPair();
        var conn1 = new ReplicationConnection(server1, source, "test:0", logger: null);
        _ = conn1.ServeAsync(cts.Token);

        var raw1 = new RawClient(client1);
        var hello1 = await raw1.HelloAsync(cts.Token);
        var clientNonce = ReplicationProtocol.NewNonce();
        var response = ReplicationProtocol.ClientAnswer(Secret, hello1.Nonce, clientNonce, "standby-node", "primary-node");
        var auth1 = await raw1.AuthAsync(response, clientNonce, cts.Token);
        Assert.Equal(RespType.BulkString, auth1.Type);
        // (First connection authenticated with a valid response.)

        // Second connection: replay the SAME response against a fresh nonce. It must fail: the nonce differs.
        var (server2, client2) = DuplexPair();
        var conn2 = new ReplicationConnection(server2, source, "test:0", logger: null);
        var serve2 = conn2.ServeAsync(cts.Token);

        var raw2 = new RawClient(client2);
        _ = await raw2.HelloAsync(cts.Token);              // a new, different nonce
        var auth2 = await raw2.AuthAsync(response, clientNonce, cts.Token);  // replay the old response and nonce
        Assert.Equal(RespType.Error, auth2.Type);
        Assert.Contains(ReplicationProtocol.ErrAuth, auth2.Text!);
        await serve2.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.True(serve2.IsCompleted);
    }

    // ------------------------------------------------------------------ spec 011 R1: no reflection

    [Fact]
    public async Task AHelloStatingTheListenersOwnId_IsRefused()
    {
        // Spec 011 R1.3, the listener's side: a peer naming this node can only be a relay reflecting its own follower.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = DuplexPair();
        var serve = new ReplicationConnection(server, new FakeSource(), "test:0", logger: null).ServeAsync(cts.Token);
        var raw = new RawClient(client);

        var reply = await raw.HelloRawAsync(ReplicationProtocol.Version, cts.Token, nodeId: "primary-node");

        Assert.Equal(RespType.Error, reply.Type);
        Assert.Contains("own id", reply.Text!);
        await serve.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task AHelloReplyStatingTheClientsOwnId_IsRefused_BeforeAuth()
    {
        // Spec 011 R1.3, the client's side: a "primary" naming the standby itself is refused before any AUTH is sent.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = DuplexPair();
        var sawAuth = false;
        var fake = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            _ = await server.ReadAsync(buffer, cts.Token); // HELLO
            var hello = new RespWriter().ArrayHeader(6).Integer(ReplicationProtocol.Version).BulkString("standby-node")
                .BulkString(ReplicationProtocol.RolePrimary).Integer(1).BulkString(ReplicationProtocol.Format)
                .BulkString(Convert.ToHexStringLower(ReplicationProtocol.NewNonce()));
            await server.WriteAsync(hello.ToArray(), cts.Token);
            try
            {
                sawAuth = await server.ReadAsync(buffer, cts.Token) > 0;
            }
            catch (Exception)
            {
                // the client closed
            }
        });

        var standby = new ReplicationClient(client, "standby-node", Secret);
        var refused = await Assert.ThrowsAsync<ReplicationProtocolException>(
            () => standby.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, cts.Token));
        Assert.Contains("own id", refused.Message);
        client.Dispose();
        await fake.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.False(sawAuth);
    }

    [Fact]
    public void TheClientAnswerAndTheServerProof_DifferForTheSameInputs()
    {
        // Spec 011 R1.1: labelled by direction, so a proof can never be replayed as an answer, nor an answer as a proof.
        var serverNonce = ReplicationProtocol.NewNonce();
        var clientNonce = ReplicationProtocol.NewNonce();
        var answer = ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "a", "b");
        var proof = ReplicationProtocol.ServerProof(Secret, serverNonce, clientNonce, "a", "b");

        Assert.NotEqual(answer, proof);
        Assert.Equal(64, answer.Length); // HMAC-SHA256, hex
    }

    [Fact]
    public void AnAnswer_IsBoundToBothNoncesBothIdsAndTheSecret()
    {
        // Spec 011 R1.2: changing any one input changes the MAC; ids are length-prefixed, so they cannot run together.
        var serverNonce = ReplicationProtocol.NewNonce();
        var clientNonce = ReplicationProtocol.NewNonce();
        var baseline = ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "node-a", "node-b");

        Assert.Equal(baseline, ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "node-a", "node-b"));
        Assert.NotEqual(baseline, ReplicationProtocol.ClientAnswer("another", serverNonce, clientNonce, "node-a", "node-b"));
        Assert.NotEqual(baseline, ReplicationProtocol.ClientAnswer(Secret, ReplicationProtocol.NewNonce(), clientNonce, "node-a", "node-b"));
        Assert.NotEqual(baseline, ReplicationProtocol.ClientAnswer(Secret, serverNonce, ReplicationProtocol.NewNonce(), "node-a", "node-b"));
        Assert.NotEqual(baseline, ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "node-x", "node-b"));
        Assert.NotEqual(baseline, ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "node-a", "node-x"));
        Assert.NotEqual(baseline, ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "node-b", "node-a"));
        Assert.NotEqual(
            ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "ab", "c"),
            ReplicationProtocol.ClientAnswer(Secret, serverNonce, clientNonce, "a", "bc"));
    }

    [Fact]
    public async Task AVersion1Peer_IsRefused()
    {
        // Spec 011 R1.4: version 2 changed what AUTH covers; a version-1 peer cannot authenticate, so it is refused at HELLO.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = DuplexPair();
        _ = new ReplicationConnection(server, new FakeSource(), "test:0", logger: null).ServeAsync(cts.Token);
        var reply = await new RawClient(client).HelloRawAsync(protocolVersion: 1, cts.Token);
        Assert.Equal(RespType.Error, reply.Type);
        Assert.Contains(ReplicationProtocol.ErrVersion, reply.Text!);
    }

    [Fact]
    public async Task CommandBeforeAuth_IsRefused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (serverStream, clientStream) = DuplexPair();
        var source = new FakeSource();
        var connection = new ReplicationConnection(serverStream, source, "test:0", logger: null);
        _ = connection.ServeAsync(cts.Token);

        var raw = new RawClient(clientStream);
        _ = await raw.HelloAsync(cts.Token);
        // Send STATUS before AUTH: refused with CRYSTAL_NOAUTH, connection stays open.
        var reply = await raw.CommandAsync([Text.UTF8.GetBytes("STATUS")], cts.Token);
        Assert.Equal(RespType.Error, reply.Type);
        Assert.Contains(ReplicationProtocol.ErrNoAuth, reply.Text!);
    }

    [Fact]
    public async Task VersionMismatch_IsNamed_AndConnectionCloses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (serverStream, clientStream) = DuplexPair();
        var source = new FakeSource();
        var connection = new ReplicationConnection(serverStream, source, "test:0", logger: null);
        var serve = connection.ServeAsync(cts.Token);

        var raw = new RawClient(clientStream);
        var reply = await raw.HelloRawAsync(protocolVersion: 999, cts.Token);
        Assert.Equal(RespType.Error, reply.Type);
        Assert.Contains(ReplicationProtocol.ErrVersion, reply.Text!);
        Assert.Contains($"ours={ReplicationProtocol.Version}", reply.Text!);
        Assert.Contains("theirs=999", reply.Text!);
        await serve.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.True(serve.IsCompleted);
    }

    [Fact]
    public async Task RealTcp_RoundTrip_ThroughListener()
    {
        var source = new FakeSource();
        var options = new ReplicationOptions { Secret = Secret, ListenEndpoint = "127.0.0.1:0" };
        using var listener = ReplicationListener.Start(source, options, logger: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(listener.Endpoint.Address, listener.Endpoint.Port, cts.Token);
        var client = new ReplicationClient(tcp.GetStream(), "standby-node", Secret);
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 0, cts.Token);

        Assert.Equal("primary-node", client.PrimaryNodeId);
        var status = await client.RequestAsync([Text.UTF8.GetBytes("STATUS")], cts.Token);
        Assert.Equal(RespType.BulkString, status.Type);
    }

    // A thin manual client for the gate tests: it does not auto-handshake, so a test can send raw frames.
    private sealed class RawClient(Stream stream)
    {
        private byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public async Task<(long Epoch, byte[] Nonce)> HelloAsync(CancellationToken ct)
        {
            var reply = await HelloRawAsync(ReplicationProtocol.Version, ct);
            var fields = reply.Items!;
            var nonceHex = Text.UTF8.GetString(fields[5].Bytes!);
            return (fields[3].Integer, Convert.FromHexString(nonceHex));
        }

        public async Task<RespValue> HelloRawAsync(long protocolVersion, CancellationToken ct, string nodeId = "standby-node")
        {
            await SendAsync(
                [
                    Text.UTF8.GetBytes("HELLO"),
                    Text.UTF8.GetBytes(protocolVersion.ToString()),
                    Text.UTF8.GetBytes(nodeId),
                    Text.UTF8.GetBytes("standby"),
                    Text.UTF8.GetBytes("0"),
                    Text.UTF8.GetBytes(ReplicationProtocol.Format),
                    Text.UTF8.GetBytes(ReplicationProtocol.Layout),
                ],
                ct);
            return await ReadAsync(ct);
        }

        public async Task<RespValue> AuthAsync(string response, byte[] clientNonce, CancellationToken ct)
        {
            // AUTH <response> <ownNonce>: the client's own challenge to the server rides along (mutual authentication).
            await SendAsync([Text.UTF8.GetBytes("AUTH"), Text.UTF8.GetBytes(response),
                Text.UTF8.GetBytes(Convert.ToHexStringLower(clientNonce))], ct);
            return await ReadAsync(ct);
        }

        public async Task<RespValue> CommandAsync(IReadOnlyList<byte[]> args, CancellationToken ct)
        {
            await SendAsync(args, ct);
            return await ReadAsync(ct);
        }

        private async Task SendAsync(IReadOnlyList<byte[]> args, CancellationToken ct)
        {
            var frame = new RespWriter().Request(args).ToArray();
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }

        private async Task<RespValue> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_end > _start)
                {
                    var seq = new System.Buffers.ReadOnlySequence<byte>(_buffer, _start, _end - _start);
                    var status = RespReader.TryReadReply(seq, int.MaxValue, out var value, out var consumed, out _);
                    if (status == RespReadStatus.Complete)
                    {
                        _start += (int)seq.Slice(seq.Start, consumed).Length;
                        if (_start == _end) { _start = _end = 0; }
                        return value!;
                    }
                }

                if (_start > 0) { Array.Copy(_buffer, _start, _buffer, 0, _end - _start); _end -= _start; _start = 0; }
                if (_end == _buffer.Length) { Array.Resize(ref _buffer, _buffer.Length * 2); }
                var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
                if (read == 0) { throw new IOException("closed"); }
                _end += read;
            }
        }
    }
}
