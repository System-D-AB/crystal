// Spec 011 R1.5 — the reflection attack of the 2026-10-12 review, against a real listener and a real follower. A node S
// listens and follows an endpoint an attacker M controls. M opens S's own listener claiming to be S, then answers S's
// follower as S, hoping to relay S's answers between S's two halves: authenticated on S's listener, M could push S's
// epoch; driving S's follower, M could drop S's databases and install its own. Both halves now refuse a peer that
// states their own node id, so neither step gets anywhere.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class AuthReflectionTests
{
    private const string Secret = "test-secret";

    [Fact]
    public async Task TheReflectionAttack_Fails_AndTheNodeTakesNothing()
    {
        using var dir = new TempStoreDirectory();
        using var s = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = Secret, ListenEndpoint = "127.0.0.1:0" },
        });
        s.Replication.Listen();
        var listenerPort = Port(s.Replication.ListenerEndpoint!);
        var epochBefore = s.Replication.Epoch;

        var attacker = new TcpListener(IPAddress.Loopback, 0);
        attacker.Start();
        try
        {
            s.Replication.Follow($"127.0.0.1:{((IPEndPoint)attacker.LocalEndpoint).Port}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // S's follower connects to M and says HELLO as S.
            using var follower = await attacker.AcceptTcpClientAsync(cts.Token);
            var fromFollower = new FrameStream(follower.GetStream());
            var followerHello = await fromFollower.ReadRequestAsync(cts.Token);
            Assert.Equal(s.NodeId, Text.UTF8.GetString(followerHello[2]));

            // Step 1: M opens S's own listener claiming to be S, at a much higher epoch, to get a nonce.
            using var toListener = new TcpClient();
            await toListener.ConnectAsync(IPAddress.Loopback, listenerPort, cts.Token);
            var listener = new FrameStream(toListener.GetStream());
            await listener.WriteAsync(
                Request("HELLO", ReplicationProtocol.Version.ToString(), s.NodeId, ReplicationProtocol.RolePrimary,
                    (epochBefore + 100).ToString(), ReplicationProtocol.Format, ReplicationProtocol.Layout),
                cts.Token);
            var listenerHello = await listener.ReadReplyAsync(cts.Token);
            Assert.Equal(RespType.Error, listenerHello.Type);
            Assert.Contains("own id", listenerHello.Text!);

            // Step 2: M answers S's follower as S anyway, with a nonce of its own (it has no nonce from the listener).
            var reply = new RespWriter().ArrayHeader(6).Integer(ReplicationProtocol.Version).BulkString(s.NodeId)
                .BulkString(ReplicationProtocol.RolePrimary).Integer(epochBefore + 100).BulkString(ReplicationProtocol.Format)
                .BulkString(Convert.ToHexStringLower(ReplicationProtocol.NewNonce()));
            await fromFollower.WriteAsync(reply.ToArray(), cts.Token);

            // The follower refuses before sending AUTH: its connection ends with nothing more said.
            var next = await fromFollower.TryReadRequestOrEndAsync(cts.Token);
            Assert.Null(next);
        }
        finally
        {
            s.Replication.StopFollowing();
            attacker.Stop();
        }

        // S's epoch is unchanged and it took nothing: no database appeared.
        Assert.Equal(epochBefore, s.Replication.Epoch);
        Assert.Empty(s.ListDatabases());
    }

    private static int Port(string endpoint) =>
        int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Request(params string[] args)
    {
        var writer = new RespWriter().ArrayHeader(args.Length);
        foreach (var arg in args)
        {
            writer.BulkString(Text.UTF8.GetBytes(arg));
        }

        return writer.ToArray();
    }

    /// <summary>Reads whole RESP frames from a stream (the attacker's side of each connection).</summary>
    private sealed class FrameStream(Stream stream)
    {
        private readonly List<byte> _pending = [];

        public Task WriteAsync(byte[] bytes, CancellationToken ct) => stream.WriteAsync(bytes, ct).AsTask();

        public async Task<IReadOnlyList<byte[]>> ReadRequestAsync(CancellationToken ct) =>
            await TryReadRequestOrEndAsync(ct) ?? throw new EndOfStreamException("the peer closed before a request");

        public async Task<IReadOnlyList<byte[]>?> TryReadRequestOrEndAsync(CancellationToken ct)
        {
            while (true)
            {
                var sequence = new ReadOnlySequence<byte>(_pending.ToArray());
                if (RespReader.TryReadRequest(sequence, 1 << 20, out var args, out var consumed, out _) == RespReadStatus.Complete)
                {
                    _pending.RemoveRange(0, (int)sequence.Slice(0, consumed).Length);
                    return args;
                }

                if (!await FillAsync(ct))
                {
                    return null;
                }
            }
        }

        public async Task<RespValue> ReadReplyAsync(CancellationToken ct)
        {
            while (true)
            {
                var sequence = new ReadOnlySequence<byte>(_pending.ToArray());
                if (RespReader.TryReadReply(sequence, 1 << 20, out var value, out var consumed, out _) == RespReadStatus.Complete)
                {
                    _pending.RemoveRange(0, (int)sequence.Slice(0, consumed).Length);
                    return value!;
                }

                if (!await FillAsync(ct))
                {
                    throw new EndOfStreamException("the peer closed before a reply");
                }
            }
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct);
            }
            catch (IOException)
            {
                return false;
            }

            if (read == 0)
            {
                return false;
            }

            _pending.AddRange(buffer.AsSpan(0, read).ToArray());
            return true;
        }
    }
}
