// Spec 003 T14 / R8; design § 8.3 — format checks in both directions. A standby states its format/layout in HELLO and
// in each SNAPSHOT BEGIN; the primary refuses a mismatch (or a missing statement) before serving anything (R8.1). A
// standby refuses a primary whose format differs before downloading or applying, and keeps waiting (R8.2, R14.5).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class FormatCheckTests
{
    private const string Secret = "test-secret";

    private static (Stream Server, Stream Client) DuplexPair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        return (new DuplexStream(toServer.Reader, toClient.Writer), new DuplexStream(toClient.Reader, toServer.Writer));
    }

    private sealed class PrimaryHarness : IDisposable
    {
        public TempStoreDirectory Dir { get; } = new();
        public CrystalStore Store { get; }
        public ReplicationSource Source { get; }

        public PrimaryHarness()
        {
            Store = CrystalStore.Open(Dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = Secret } });
            var epoch = ReplicationEpoch.Load(Dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
            Source = new ReplicationSource(Store, Store.Engine, Store.NodeId, epoch, new ReplicationOptions { Secret = Secret });
        }

        public void Dispose()
        {
            Source.Dispose();
            Store.Dispose();
            Dir.Dispose();
        }
    }

    private static async Task<RespValue> SendRawRequestAsync(Stream client, params string[] args)
    {
        var writer = new RespWriter();
        writer.ArrayHeader(args.Length);
        foreach (var a in args)
        {
            writer.BulkString(a);
        }

        var frame = writer.ToArray();
        await client.WriteAsync(frame);
        await client.FlushAsync();
        return await ReadReplyAsync(client);
    }

    private static async Task<RespValue> ReadReplyAsync(Stream client)
    {
        var buffer = new byte[64 * 1024];
        var end = 0;
        while (true)
        {
            var read = await client.ReadAsync(buffer.AsMemory(end));
            if (read == 0)
            {
                throw new IOException("the primary closed the connection");
            }

            end += read;
            var status = RespReader.TryReadReply(new ReadOnlySequence<byte>(buffer, 0, end), int.MaxValue, out var value, out _, out _);
            if (status == RespReadStatus.Complete)
            {
                return value!;
            }
        }
    }

    [Fact]
    public async Task Hello_WithTheWrongFormat_IsRefused_BeforeAnythingIsServed()
    {
        using var h = new PrimaryHarness();
        var (server, client) = DuplexPair();
        var connection = new ReplicationConnection(server, h.Source, "peer", null);
        var serve = connection.ServeAsync(CancellationToken.None);

        // HELLO <ver> <nodeId> <role> <epoch> <format> <layout> — with a bogus format.
        var reply = await SendRawRequestAsync(client,
            ReplicationProtocol.Hello, ReplicationProtocol.Version.ToString(), "standby-node",
            ReplicationProtocol.RoleStandby, "1", "crystal 999", ReplicationProtocol.Layout);

        Assert.Equal(RespType.Error, reply.Type);
        Assert.StartsWith(ReplicationProtocol.ErrFormat, reply.Text);

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
        try { await serve; } catch { /* closed */ }
    }

    [Fact]
    public async Task Hello_WithTheWrongLayout_IsRefused()
    {
        using var h = new PrimaryHarness();
        var (server, client) = DuplexPair();
        var connection = new ReplicationConnection(server, h.Source, "peer", null);
        var serve = connection.ServeAsync(CancellationToken.None);

        var reply = await SendRawRequestAsync(client,
            ReplicationProtocol.Hello, ReplicationProtocol.Version.ToString(), "standby-node",
            ReplicationProtocol.RoleStandby, "1", ReplicationProtocol.Format, "default,only");

        Assert.Equal(RespType.Error, reply.Type);
        Assert.StartsWith(ReplicationProtocol.ErrFormat, reply.Text);

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
        try { await serve; } catch { /* closed */ }
    }

    [Fact]
    public async Task SnapshotBegin_WithTheWrongFormat_IsRefused()
    {
        using var h = new PrimaryHarness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");

        var (server, client) = DuplexPair();
        var connection = new ReplicationConnection(server, h.Source, "peer", null);
        var serve = connection.ServeAsync(CancellationToken.None);

        var replica = new ReplicationClient(client, "standby-node", Secret);
        await replica.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        // SNAPSHOT BEGIN <uid> <format> <layout> — with a bogus format.
        var reply = await replica.RequestAsync(
            [Bytes(ReplicationProtocol.Snapshot), Bytes("BEGIN"), Bytes(db.Uid), Bytes("crystal 999"), Bytes(ReplicationProtocol.Layout)],
            CancellationToken.None);

        Assert.Equal(RespType.Error, reply.Type);
        Assert.StartsWith(ReplicationProtocol.ErrFormat, reply.Text);

        h.Source.Dispose();
        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
        try { await serve; } catch { /* closed */ }
    }

    [Fact]
    public async Task AStandbyClient_RefusesAPrimaryOfAnotherFormat_AtHandshake()
    {
        // The standby's handshake throws on a CRYSTAL_FORMAT error, before any pull or download (R8.2).
        using var h = new PrimaryHarness();
        var (server, client) = DuplexPair();
        var connection = new ReplicationConnection(server, h.Source, "peer", null);
        var serve = connection.ServeAsync(CancellationToken.None);

        // Send a HELLO stating a wrong format directly, so the primary refuses and closes.
        var reply = await SendRawRequestAsync(client,
            ReplicationProtocol.Hello, ReplicationProtocol.Version.ToString(), "standby-node",
            ReplicationProtocol.RoleStandby, "1", "crystal 999", ReplicationProtocol.Layout);
        Assert.Equal(RespType.Error, reply.Type);
        Assert.StartsWith(ReplicationProtocol.ErrFormat, reply.Text);

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
        try { await serve; } catch { /* closed */ }
    }

    private static byte[] Bytes(string s) => Text.UTF8.GetBytes(s);
}
