// Spec 003 R3.4 (should-fix 5 of the 2026-10-10 review): a waiting pull wakes for every change it waits on — a commit,
// the epoch, the role — every waiting pull wakes, not one of several, and a pull answers for the state when it answers,
// not when it began.

using System;
using System.Diagnostics;
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
public sealed class PullWaitTests
{
    private const string Secret = "test-secret";
    private const int Wait = 4000;

    private static CrystalOptions Options() => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = Secret,
            ListenEndpoint = "127.0.0.1:0",
            LongPollMax = TimeSpan.FromMilliseconds(Wait),
        },
    };

    [Fact]
    public async Task OneCommit_WakesEveryWaitingPull()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options());
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        primary.Replication.Listen();
        var latest = (long)db.Slot.LatestSequence;

        using var a = await Connect(primary, "standby-a");
        using var b = await Connect(primary, "standby-b");
        var sw = Stopwatch.StartNew();
        var pullA = a.Pull(db.Uid, latest);
        var pullB = b.Pull(db.Uid, latest);
        await Task.Delay(300); // both are waiting now

        using (var batch = db.NewBatch())
        {
            batch.Put(box, "k"u8, "v"u8);
            batch.Commit(DurabilityMode.Strict);
        }

        await Task.WhenAll(pullA, pullB);
        Assert.True(sw.ElapsedMilliseconds < Wait / 2, $"both pulls woke on the commit, not at the end of the wait ({sw.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public async Task ADemotion_WakesAWaitingPull_WhichAnswersNotPrimary()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options());
        var db = primary.CreateDatabase("db", replicated: true);
        primary.Replication.Listen();

        using var standby = await Connect(primary, "standby-a");
        var sw = Stopwatch.StartNew();
        var pull = standby.Pull(db.Uid, (long)db.Slot.LatestSequence);
        await Task.Delay(300);
        primary.Replication.Demote();

        var reply = await pull;
        Assert.True(sw.ElapsedMilliseconds < Wait / 2, $"the pull woke on the demotion ({sw.ElapsedMilliseconds} ms)");
        Assert.Equal(RespType.Error, reply.Type);
        Assert.StartsWith(ReplicationProtocol.ErrNotPrimary, reply.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewEpochDuringTheWait_IsAnsweredAsNotPrimary_NotStampedWithIt()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options());
        var db = primary.CreateDatabase("db", replicated: true);
        primary.Replication.Listen();

        using var standby = await Connect(primary, "standby-a");
        var pull = standby.Pull(db.Uid, (long)db.Slot.LatestSequence);
        await Task.Delay(300);
        primary.Replication.Promote("a new epoch while a pull waits");

        var reply = await pull;
        Assert.Equal(RespType.Error, reply.Type);
        Assert.StartsWith(ReplicationProtocol.ErrNotPrimary, reply.Text!, StringComparison.Ordinal);
    }

    private sealed class Standby(TcpClient tcp, ReplicationClient client) : IDisposable
    {
        public Task<RespValue> Pull(string uid, long from) =>
            client.RequestAsync(
                [B(ReplicationProtocol.Pull), B("1048576"), B(Wait.ToString(System.Globalization.CultureInfo.InvariantCulture)), B(uid),
                 B(from.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                CancellationToken.None);

        public void Dispose() => tcp.Dispose();
    }

    private static async Task<Standby> Connect(CrystalStore primary, string nodeId)
    {
        var endpoint = primary.Replication.ListenerEndpoint!;
        var colon = endpoint.LastIndexOf(':');
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(endpoint[..colon], int.Parse(endpoint[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        var client = new ReplicationClient(tcp.GetStream(), nodeId, Secret);
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
        return new Standby(tcp, client);
    }

    private static byte[] B(string text) => Text.UTF8.GetBytes(text);
}
