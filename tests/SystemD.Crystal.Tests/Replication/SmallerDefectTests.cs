// Spec 011 R11 — the smaller defects of the 2026-10-12 review, one test each where it can be tested: a change to the
// replicated set wakes a waiting pull (R11.1); every new connection reconciles (R11.3); Follow refuses a bad endpoint at
// once (R11.5); a standby with nothing to follow long-polls rather than asking DATABASES five times a second (R11.6); the
// RESP reader refuses an overflowing integer and a negative length other than -1 (R11.7).

using System;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
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
public sealed class SmallerDefectTests
{
    private const string Secret = "test-secret";

    private static CrystalOptions Options(string? listen = null, TimeSpan? longPoll = null) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = Secret,
            ListenEndpoint = listen,
            LongPollMax = longPoll ?? TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public async Task ASetChange_WakesAWaitingPull()
    {
        // R11.1: a PULL naming no database waits up to 10 s; creating a replicated database ends the wait at once.
        using var dir = new TempStoreDirectory();
        using var waiting = new ManualResetEventSlim(false);
        var options = Options(listen: "127.0.0.1:0", longPoll: TimeSpan.FromSeconds(10));
        using var primary = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = options.Replication,
            Hook = step =>
            {
                if (step == "pull.waiting")
                {
                    waiting.Set();
                }
            },
        });
        primary.Replication.Listen();
        var client = await Connect(primary);
        var before = primary.Engine.ReplicatedSetVersion;

        var sw = Stopwatch.StartNew();
        var pull = client.RequestAsync([B(ReplicationProtocol.Pull), B("1048576"), B("10000")], CancellationToken.None);
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)), "the pull did not wait"); // review 2026-10-13, M9: no sleep
        primary.CreateDatabase("new", replicated: true);
        var reply = await pull.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the pull waited {sw.Elapsed.TotalSeconds:F1} s after the set changed");
        Assert.True(reply.Items![1].Integer > before, "the reply states the new set version");
    }

    [Fact]
    public void ANewConnection_AlwaysReconciles_EvenAtTheSameSetVersion()
    {
        // R11.3: the follower forgot nothing between connections, so a different primary at the same endpoint whose
        // registry generation equals the last one reconciled was never reconciled. Two primaries with the same history of
        // registry changes (one create each) but different databases, served one after the other on the same port.
        using var adir = new TempStoreDirectory();
        using var bdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        string endpoint;
        var a = CrystalStore.Open(adir.Path, Options(listen: "127.0.0.1:0"));
        Seeded(a, "first");
        a.Replication.Listen();
        endpoint = a.Replication.ListenerEndpoint!;

        using var standby = CrystalStore.Open(sdir.Path, Options());
        standby.Replication.Follow(endpoint);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "first"));
        Assert.Contains(standby.ListDatabases(), d => d.Name == "first");
        var version = a.Engine.ReplicatedSetVersion;
        a.Dispose();

        using var b = CrystalStore.Open(bdir.Path, Options(listen: endpoint));
        b.Replication.Promote("the successor"); // a newer epoch than A's, or the standby rightly sees two primaries (R7)
        Seeded(b, "second");
        Assert.Equal(version, b.Engine.ReplicatedSetVersion); // the same set version, a different set
        b.Replication.Listen();

        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "second"));
        Assert.Contains(standby.ListDatabases(), d => d.Name == "second");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:port")]
    public void Follow_ABadEndpoint_IsRefusedAtOnce(string endpoint)
    {
        // R11.5: refused with the expected form named, before anything starts; the node does not become a standby.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var role = store.Replication.Role;

        var refused = Assert.Throws<ArgumentException>(() => store.Replication.Follow(endpoint));
        Assert.Contains("host:port", refused.Message, StringComparison.Ordinal);
        Assert.Equal(role, store.Replication.Role);
        Assert.Null(store.Replication.Status().FollowedPrimary);
    }

    [Fact]
    public void AScheduledRejoin_FollowsItsPrimaryAtTheNextStart_WithoutAFollowCall()
    {
        // R11.4: ScheduleRejoin(endpoint) records the endpoint; the next open follows it and re-bootstraps every
        // replicated database from it (the old content, written while this node was a primary, is replaced).
        using var pdir = new TempStoreDirectory();
        using var ndir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        primary.Replication.Promote("the new primary");
        Seeded(primary, "db");
        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;

        using (var old = CrystalStore.Open(ndir.Path, Options()))
        {
            Seeded(old, "db"); // its own database of that name, written as a primary
            old.Replication.ScheduleRejoin(endpoint);
        }

        using var rejoined = CrystalStore.Open(ndir.Path, Options());
        Assert.Equal(endpoint, rejoined.Replication.Status().FollowedPrimary);
        Assert.Equal(ReplicationRole.Standby, rejoined.Replication.Role);
        var primaryUid = primary.OpenDatabase("db").Uid;
        WaitUntil(() => rejoined.ListDatabases().Any(d => d.Name == "db" && d.Uid == primaryUid));
        Assert.Contains(rejoined.ListDatabases(), d => d.Name == "db" && d.Uid == primaryUid);
    }

    [Fact]
    public void AStandbyWithNothingToFollow_LongPollsInsteadOfAskingDatabasesRepeatedly()
    {
        // R11.6: a primary with no replicated database; over three seconds with a one-second long poll, the standby asks
        // DATABASES once (its first reconcile) and then only pulls, each pull waiting on the primary.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0", longPoll: TimeSpan.FromSeconds(1)));
        primary.Replication.Listen();
        using var standby = CrystalStore.Open(sdir.Path, Options(longPoll: TimeSpan.FromSeconds(1)));
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        Thread.Sleep(3000); // the span measured, not a wait for an outcome
        standby.Replication.StopFollowing();

        var asked = primary.Replication.Source!.DatabasesServed;
        Assert.InRange(asked, 1, 2); // before: one every 200 ms, about 15
    }

    [Theory]
    [InlineData(":99999999999999999999\r\n")]
    [InlineData(":-99999999999999999999\r\n")]
    [InlineData(":9223372036854775808\r\n")]
    [InlineData(":-9223372036854775809\r\n")]
    [InlineData("$-2\r\n")]
    [InlineData("*-5\r\n")]
    [InlineData("$99999999999999999999\r\n")]
    public void TheReplyReader_RefusesAnOverflowingIntegerAndANegativeLengthOtherThanMinusOne(string frame)
    {
        // R11.7: a malformed number is a protocol error, never a wrapped value or a null.
        var status = RespReader.TryReadReply(new ReadOnlySequence<byte>(Text.ASCII.GetBytes(frame)), 1 << 20, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("$-1\r\n", "NullBulkString")]
    [InlineData("*-1\r\n", "NullArray")]
    [InlineData(":9223372036854775807\r\n", "Integer")]
    [InlineData(":-9223372036854775807\r\n", "Integer")]
    [InlineData(":-9223372036854775808\r\n", "Integer")]
    public void TheReplyReader_StillReadsNullsAndTheLargestIntegers(string frame, string type)
    {
        var status = RespReader.TryReadReply(new ReadOnlySequence<byte>(Text.ASCII.GetBytes(frame)), 1 << 20, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(type, value!.Type.ToString());
    }

    private static async Task<ReplicationClient> Connect(CrystalStore primary)
    {
        var endpoint = primary.Replication.ListenerEndpoint!;
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        var client = new ReplicationClient(tcp.GetStream(), "standby-1", Secret);
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
        return client;
    }

    private static void Seeded(CrystalStore store, string name)
    {
        var db = store.CreateDatabase(name, replicated: true);
        var box = db.Boxes.Create("box");
        using var batch = db.NewBatch();
        batch.Put(box, B("k"), B("v"));
        batch.Commit(DurabilityMode.Strict);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && !condition())
        {
            Thread.Sleep(50);
        }
    }

    private static byte[] B(string text) => Text.UTF8.GetBytes(text);
}
