// Spec 003 addendum 2026-10-10 (second independent review) — the four blockers, each shown by a test that fails on the
// code the review read: B1 an epoch adopted before AUTH, B2 a non-primary that still accepts writes and a role lost at
// restart, B3 an incarnation base taken at first pull rather than at promotion, B4 a page cap per database and one WAL
// batch per pull.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class ReplicationBlockerTests
{
    private const string Secret = "test-secret";

    private static CrystalOptions Options(string? listen = null, int pageBytes = 1 << 20) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = Secret,
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
            PageBytes = pageBytes,
        },
    };

    // ------------------------------------------------------------------ B1: no epoch before AUTH (R10.5, R6.3)

    [Fact]
    public async Task B1_AHelloWithAHigherEpoch_WithoutAuth_ChangesNothing()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        primary.Replication.Listen();
        var epochBefore = primary.Replication.Epoch;

        using (var connection = await Connect(primary))
        {
            var reply = await connection.Client.RequestAsync(Hello(epoch: 999_999), CancellationToken.None);
            Assert.Equal(RespType.Array, reply.Type); // the HELLO itself is answered, with a nonce
        }

        Assert.Equal(epochBefore, primary.Replication.Epoch);
        Assert.Equal(ReplicationRole.Primary, primary.Replication.Role);
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(dir.Path, ReplicationEpoch.FileName)) &&
                     System.IO.File.ReadAllText(System.IO.Path.Combine(dir.Path, ReplicationEpoch.FileName)).Contains("999999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B1_AnAuthenticatedPeerWithAHigherEpoch_IsStillAdopted()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        primary.Replication.Listen();

        using (var connection = await Connect(primary))
        {
            await connection.Client.HandshakeAsync(ReplicationProtocol.RoleStandby, 50, CancellationToken.None);
        }

        WaitUntil(() => primary.Replication.Epoch == 50, TimeSpan.FromSeconds(10));
        Assert.Equal(ReplicationRole.Standby, primary.Replication.Role);
    }

    [Fact]
    public async Task B1_AnEpochTooLargeToPromotePast_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        primary.Replication.Listen();
        var epochBefore = primary.Replication.Epoch;

        using (var connection = await Connect(primary))
        {
            var reply = await connection.Client.RequestAsync(Hello(epoch: long.MaxValue), CancellationToken.None);
            Assert.Equal(RespType.Error, reply.Type);
        }

        Assert.Equal(epochBefore, primary.Replication.Epoch);
    }

    // ------------------------------------------------------------------ B2: only a primary writes (R1.6, R6.5)

    [Fact]
    public void B2_AFencedNode_RefusesWritesToReplicatedDatabases_ButReadsThem()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        Put(db, box, "before", "1");

        store.Replication.Fence("test");

        Assert.Throws<NotPrimaryException>(() => Put(db, box, "after", "2"));
        Assert.Throws<NotPrimaryException>(() => db.Boxes.Create("another"));
        using var snapshot = db.Snapshot();
        Assert.Equal("1"u8.ToArray(), snapshot.Get(box, "before"u8));
    }

    [Fact]
    public async Task B2_AFence_WaitsForAWriteAlreadyAdmitted_AndRefusesTheNext()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");

        // A write passes the role check, then pauses inside its commit; the fence starts meanwhile.
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        db.Slot.Context.OnCommitInsideGate = () =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var write = Task.Run(() => Put(db, box, "during", "1"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
        var fence = Task.Run(() => store.Replication.Fence("test"));

        // The fence does not return while the admitted write is still writing.
        await Task.Delay(300);
        Assert.False(fence.IsCompleted);

        db.Slot.Context.OnCommitInsideGate = null;
        release.Set();
        await write;
        await fence.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Throws<NotPrimaryException>(() => Put(db, box, "after", "2"));
        using var snapshot = db.Snapshot();
        Assert.Equal("1"u8.ToArray(), snapshot.Get(box, "during"u8));
        Assert.Equal(0, store.Engine.WriteGate.InFlight);
    }

    [Fact]
    public void B2_ADemotedNode_RefusesWrites_AndAPromotionAllowsThemAgain()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");

        store.Replication.Demote();
        Assert.Throws<NotPrimaryException>(() => Put(db, box, "k", "v"));

        store.Replication.Promote("back");
        Put(db, box, "k", "v");
    }

    [Fact]
    public void B2_ANotReplicatedDatabase_IsWritableOnAStandby()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var own = store.CreateDatabase("own", replicated: false);
        var box = own.Boxes.Create("box");

        store.Replication.Demote();
        Put(own, box, "k", "v");
    }

    [Theory]
    [InlineData("demote")]
    [InlineData("fence")]
    public void B2_ARestartedNonPrimary_ComesBackAStandby_AndRefusesWrites(string how)
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            store.CreateDatabase("db", replicated: true).Boxes.Create("box");
            if (how == "demote")
            {
                store.Replication.Demote();
            }
            else
            {
                store.Replication.Fence("test");
            }
        }

        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            Assert.Equal(ReplicationRole.Standby, store.Replication.Role);
            var db = store.OpenDatabase("db");
            Assert.Throws<NotPrimaryException>(() => Put(db, db.Boxes.Find("box")!.Value, "k", "v"));
        }
    }

    [Fact]
    public void B2_APromotedNode_ComesBackAPrimary()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            store.Replication.Demote();
            store.Replication.Promote("test");
        }

        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            Assert.Equal(ReplicationRole.Primary, store.Replication.Role);
        }
    }

    // ------------------------------------------------------------------ B3: the base is fixed at promotion (R5.2)

    [Fact]
    public void B3_AnOldPrimaryWithUnreplicatedWrites_RejoiningANewPrimaryThatWroteMore_IsReBootstrapped()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var oldPrimary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        using var newPrimary = CrystalStore.Open(sdir.Path, Options(listen: "127.0.0.1:0"));

        var pdb = oldPrimary.CreateDatabase("db", replicated: true);
        var pbox = pdb.Boxes.Create("box");
        for (var i = 0; i < 10; i++)
        {
            Put(pdb, pbox, $"shared-{i:D3}", "x");
        }

        oldPrimary.Replication.Listen();
        newPrimary.Replication.Listen();
        newPrimary.Replication.Follow(oldPrimary.Replication.ListenerEndpoint!);
        WaitUntil(() => newPrimary.ListDatabases().Any(d => d.Name == "db")
                        && newPrimary.OpenDatabase("db").Slot.LatestSequence == pdb.Slot.LatestSequence, TimeSpan.FromSeconds(30));

        // The old primary commits writes no standby receives, then the standby is promoted and writes MORE than that.
        newPrimary.Replication.StopFollowing();
        for (var i = 0; i < 5; i++)
        {
            Put(pdb, pbox, $"lost-{i:D3}", "only on the old primary");
        }

        newPrimary.Replication.Promote("failover");
        var sdb = newPrimary.OpenDatabase("db");
        var sbox = sdb.Boxes.Find("box")!.Value;
        for (var i = 0; i < 20; i++)
        {
            Put(sdb, sbox, $"new-{i:D3}", "y");
        }

        // The old primary rejoins as a standby of the new one. Its position (10 + 5) is below the new primary's latest
        // (10 + 20), but above the base at promotion (10): it diverged and must be bootstrapped, never continued.
        oldPrimary.Replication.Follow(newPrimary.Replication.ListenerEndpoint!);
        WaitUntil(() => Rows(oldPrimary.OpenDatabase("db")).SequenceEqual(Rows(sdb)), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(Rows(oldPrimary.OpenDatabase("db")), r => r.StartsWith("lost-", StringComparison.Ordinal));
        Assert.Equal(Rows(sdb), Rows(oldPrimary.OpenDatabase("db")));
    }

    // ------------------------------------------------------------------ B4: one page across the reply, many batches (R3.2)

    [Fact]
    public async Task B4_ThePageCap_IsAcrossTheWholeReply()
    {
        const int pageBytes = 64 * 1024;
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0", pageBytes: pageBytes));
        var uids = new List<string>();
        var value = new string('v', 1000);
        for (var d = 0; d < 6; d++)
        {
            var db = primary.CreateDatabase($"db{d}", replicated: true);
            var box = db.Boxes.Create("box");
            for (var i = 0; i < 100; i++)
            {
                Put(db, box, $"k{i:D4}", value); // about 100 KB behind per database, more than a page each
            }

            uids.Add(db.Uid);
        }

        primary.Replication.Listen();
        using var connection = await Connect(primary);
        await connection.Client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        var reply = await connection.Client.RequestAsync(Pull(pageBytes, uids.Select(u => (u, 0L))), CancellationToken.None);
        var batches = Batches(reply);
        var bytes = batches.Sum(b => b.Data.Length);

        Assert.NotEmpty(batches);
        Assert.True(bytes <= pageBytes + batches.Max(b => b.Data.Length), $"a {pageBytes}-byte page carried {bytes} bytes");
    }

    [Fact]
    public async Task B4_ACatchUpFromTheWal_SendsManyBatchesPerPull()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        for (var i = 0; i < 300; i++)
        {
            Put(db, box, $"k{i:D4}", "v"); // written before any pull: only the WAL holds them, not the tail
        }

        primary.Replication.Listen();
        using var connection = await Connect(primary);
        await connection.Client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        var reply = await connection.Client.RequestAsync(Pull(1 << 20, [(db.Uid, 0L)]), CancellationToken.None);

        Assert.True(Batches(reply).Count >= 100, $"one pull carried {Batches(reply).Count} batches");
    }

    [Fact]
    public void B4_AFollowerCatchingUp_DoesNotAskForTheDatabaseSetBeforeEveryPull()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0", pageBytes: 4 * 1024));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        primary.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options());
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db"), TimeSpan.FromSeconds(30));

        var before = primary.Replication.Source!.DatabasesServed;
        var value = new string('v', 500);
        for (var i = 0; i < 400; i++)
        {
            Put(db, box, $"k{i:D4}", value); // many small pages at a 4 KiB page size
        }

        WaitUntil(() => standby.OpenDatabase("db").Slot.LatestSequence == db.Slot.LatestSequence, TimeSpan.FromSeconds(60));
        var asked = primary.Replication.Source!.DatabasesServed - before;

        Assert.True(asked <= 5, $"the follower asked for the database set {asked} times while catching up");
    }

    // ------------------------------------------------------------------ after the review of the fixes

    [Fact]
    public void B3_TheNewPrimaryRestarting_DoesNotHideTheOldPrimarysDivergence()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var oldPrimary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var pdb = oldPrimary.CreateDatabase("db", replicated: true);
        var pbox = pdb.Boxes.Create("box");
        for (var i = 0; i < 10; i++)
        {
            Put(pdb, pbox, $"shared-{i:D3}", "x");
        }

        oldPrimary.Replication.Listen();
        using (var newPrimary = CrystalStore.Open(sdir.Path, Options(listen: "127.0.0.1:0")))
        {
            newPrimary.Replication.Follow(oldPrimary.Replication.ListenerEndpoint!);
            WaitUntil(() => newPrimary.ListDatabases().Any(d => d.Name == "db")
                            && newPrimary.OpenDatabase("db").Slot.LatestSequence == pdb.Slot.LatestSequence, TimeSpan.FromSeconds(30));
            newPrimary.Replication.StopFollowing();
            for (var i = 0; i < 5; i++)
            {
                Put(pdb, pbox, $"lost-{i:D3}", "only on the old primary");
            }

            newPrimary.Replication.Promote("failover");
            var sdb = newPrimary.OpenDatabase("db");
            for (var i = 0; i < 20; i++)
            {
                Put(sdb, sdb.Boxes.Find("box")!.Value, $"new-{i:D3}", "y");
            }
        }

        // The new primary restarts before the old one rejoins: its base must not move up to what it wrote since.
        using var restarted = CrystalStore.Open(sdir.Path, Options(listen: "127.0.0.1:0"));
        Assert.Equal(ReplicationRole.Primary, restarted.Replication.Role);
        restarted.Replication.Listen();
        var rdb = restarted.OpenDatabase("db");

        oldPrimary.Replication.Follow(restarted.Replication.ListenerEndpoint!);
        WaitUntil(() => Rows(oldPrimary.OpenDatabase("db")).SequenceEqual(Rows(rdb)), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(Rows(oldPrimary.OpenDatabase("db")), r => r.StartsWith("lost-", StringComparison.Ordinal));
        Assert.Equal(Rows(rdb), Rows(oldPrimary.OpenDatabase("db")));
    }

    [Fact]
    public void B3_AStandbyOfAPrimaryThatRestarts_Continues_WithoutReBootstrapping()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var standby = CrystalStore.Open(sdir.Path, Options());
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };

        using (var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0")))
        {
            var db = primary.CreateDatabase("db", replicated: true);
            var box = db.Boxes.Create("box");
            for (var i = 0; i < 10; i++)
            {
                Put(db, box, $"before-{i:D3}", "x");
            }

            primary.Replication.Listen();
            standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
            WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db")
                            && standby.OpenDatabase("db").Slot.LatestSequence == db.Slot.LatestSequence, TimeSpan.FromSeconds(30));
            for (var i = 0; i < 10; i++)
            {
                Put(db, box, $"tail-{i:D3}", "x"); // past the base of the first incarnation
            }

            WaitUntil(() => standby.OpenDatabase("db").Slot.LatestSequence == db.Slot.LatestSequence, TimeSpan.FromSeconds(30));
            standby.Replication.StopFollowing();
        }

        using var restarted = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        restarted.Replication.Listen();
        var rdb = restarted.OpenDatabase("db");
        for (var i = 0; i < 10; i++)
        {
            Put(rdb, rdb.Boxes.Find("box")!.Value, $"after-{i:D3}", "x");
        }

        standby.Replication.Follow(restarted.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.OpenDatabase("db").Slot.LatestSequence == rdb.Slot.LatestSequence, TimeSpan.FromSeconds(30));

        Assert.Equal(Rows(rdb), Rows(standby.OpenDatabase("db")));
        lock (events)
        {
            Assert.DoesNotContain(events, e => e.Kind is ReplicationEventKind.Divergence or ReplicationEventKind.Gap);
        }
    }

    [Fact]
    public void LogIncarnation_ContinuedAt_KeepsTheBaseAndCapsTheHistoryAtWhatSurvived()
    {
        var first = LogIncarnation.Fresh(100);
        var second = first.ContinuedAt(150);
        var third = second.ContinuedAt(120); // a restart that recovered less than was there (a power loss)

        Assert.Equal(100, second.Base);
        Assert.Equal(100, third.Base);
        Assert.Equal([new IncarnationEnd(second.Id, 120), new IncarnationEnd(first.Id, 120)], third.History);
        Assert.Equal(90, LogIncarnation.Fresh(100).ContinuedAt(90).Base);

        var many = LogIncarnation.Fresh(1);
        for (var i = 0; i < 40; i++)
        {
            many = many.ContinuedAt(1);
        }

        Assert.Equal(LogIncarnation.HistoryLimit, many.History.Count);
    }

    [Fact]
    public async Task APullForADatabaseDroppedSinceItWasServed_AnswersGone()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        Put(db, db.Boxes.Create("box"), "k", "v");
        var uid = db.Uid;
        primary.Replication.Listen();
        using var connection = await Connect(primary);
        await connection.Client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
        await connection.Client.RequestAsync(Pull(1 << 20, [(uid, 0L)]), CancellationToken.None);

        primary.DropDatabase(db);
        var reply = await connection.Client.RequestAsync(Pull(1 << 20, [(uid, 0L)]), CancellationToken.None);

        Assert.Equal(ReplicationProtocol.EntryGone, Text.UTF8.GetString(reply.Items![2].Items![0].Items![1].Bytes!));
    }

    [Fact]
    public void ANonPrimary_RefusesToChangeTheReplicatedSet_ButNotItsOwnDatabases()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var a = store.CreateDatabase("a", replicated: true);
        var b = store.CreateDatabase("b", replicated: true);
        store.Replication.Demote();

        Assert.Throws<NotPrimaryException>(() => store.CreateDatabase("c", replicated: true));
        Assert.Throws<NotPrimaryException>(() => store.RenameDatabase(a, "a2"));
        Assert.Throws<NotPrimaryException>(() => store.SwapDatabases(a, b));
        Assert.Throws<NotPrimaryException>(() => store.DropDatabase(a));

        var own = store.CreateDatabase("own", replicated: false);
        store.RenameDatabase(own, "own2");
        store.DropDatabase(own);
    }

    [Fact]
    public void AStoreWithoutReplication_OverAStandbyRole_CanPromote_AndWrite()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            store.CreateDatabase("db", replicated: true).Boxes.Create("box");
            store.Replication.Demote();
        }

        using (var store = CrystalStore.Open(dir.Path))
        {
            var db = store.OpenDatabase("db");
            Assert.Throws<NotPrimaryException>(() => Put(db, db.Boxes.Find("box")!.Value, "k", "v"));
            store.Replication.Promote("no replication here");
            Put(db, db.Boxes.Find("box")!.Value, "k", "v");
        }
    }

    [Fact]
    public void FencingAFollowingStandby_StopsItsFollower()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        primary.Replication.Listen();
        using var standby = CrystalStore.Open(sdir.Path, Options());
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        standby.Replication.Fence("test");

        Assert.Null(standby.Replication.Status().FollowedPrimary);
        Assert.Equal(ReplicationRole.Fenced, standby.Replication.Role);
    }

    [Theory]
    [InlineData("not an epoch")]
    [InlineData("")]
    public void ADamagedEpochFile_IsRefused_NotTakenAsAFreshNode(string content)
    {
        using var dir = new TempStoreDirectory();
        using (CrystalStore.Open(dir.Path, Options()))
        {
        }

        System.IO.File.WriteAllText(System.IO.Path.Combine(dir.Path, ReplicationEpoch.FileName), content);

        Assert.ThrowsAny<System.IO.IOException>(() => CrystalStore.Open(dir.Path, Options()).Dispose());
    }

    [Theory]
    [InlineData("7")]
    [InlineData("7 leader")]
    public void AnEpochFileWithoutAKnownRole_LoadsAsAStandby(string content)
    {
        using var dir = new TempStoreDirectory();
        using (CrystalStore.Open(dir.Path, Options()))
        {
        }

        System.IO.File.WriteAllText(System.IO.Path.Combine(dir.Path, ReplicationEpoch.FileName), content);
        using var store = CrystalStore.Open(dir.Path, Options());

        Assert.Equal(ReplicationRole.Standby, store.Replication.Role);
        Assert.Equal(7, store.Replication.Epoch);
    }

    [Fact]
    public async Task ASecondHelloAfterAuth_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        primary.Replication.Listen();
        using var connection = await Connect(primary);
        await connection.Client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        var reply = await connection.Client.RequestAsync(Hello(epoch: 1), CancellationToken.None);

        Assert.Equal(RespType.Error, reply.Type);
    }

    // ------------------------------------------------------------------ after the second review of the fixes

    [Fact]
    public void HasDiverged_AKeptIncarnationInTheHistory_ContinuesUpToItsEnd_AndDivergesPastIt()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        for (var i = 0; i < 5; i++)
        {
            Put(db, box, $"k{i}", "v");
        }

        var position = (long)db.Slot.LatestSequence;
        var kept = Guid.NewGuid();
        FollowRecord.Write(dir.Path, db.Uid, kept, @base: 0);
        var standby = new StandbyDatabase(db.Slot, db.Uid, dir.Path, initialEpoch: 0);
        var served = Guid.NewGuid().ToString("D");

        Assert.False(standby.HasDiverged(served, servedBase: 0, [new IncarnationEnd(kept, position)]));
        Assert.False(standby.HasDiverged(served, servedBase: 0, [new IncarnationEnd(Guid.NewGuid(), 0), new IncarnationEnd(kept, position + 3)]));
        Assert.True(standby.HasDiverged(served, servedBase: 0, [new IncarnationEnd(kept, position - 1)]));
        Assert.True(standby.HasDiverged(served, servedBase: 0, [new IncarnationEnd(Guid.NewGuid(), position + 10)]));
        standby.StopFollowing();
    }

    [Fact]
    public void ARunWithoutReplicationOptions_StillContinuesAnExistingHistory()
    {
        using var dir = new TempStoreDirectory();
        string uid;
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            uid = store.CreateDatabase("db", replicated: true).Uid;
        }

        var first = IncarnationRecord.Read(dir.Path, uid)!;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var db = store.OpenDatabase("db");
            Put(db, db.Boxes.Create("box"), "k", "written without replication");
        }

        var second = IncarnationRecord.Read(dir.Path, uid)!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Base, second.Base);
        Assert.Contains(second.History, h => h.Id == first.Id);
    }

    [Fact]
    public void AnUnreadableIncarnationRecord_RefusesTheOpen_AndOnceDeleted_TheDatabaseOpensInTheSameProcess()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        var uid = db.Uid;
        store.DropDatabase(db);
        var again = store.CreateDatabase("again", replicated: true);
        var path = System.IO.Path.Combine(IncarnationRecord.Directory(dir.Path), again.Uid + ".json");
        _ = uid;

        // Reopen the store with a damaged record: the open is refused, and nothing is left holding the database.
        store.Dispose();
        System.IO.File.WriteAllText(path, "not json");
        using var reopened = CrystalStore.Open(dir.Path, Options());
        Assert.ThrowsAny<System.IO.IOException>(() => reopened.OpenDatabase("again"));

        System.IO.File.Delete(path);
        var opened = reopened.OpenDatabase("again");
        Assert.Equal("again", opened.Name);
    }

    [Fact]
    public void ARePromotedOldPrimary_KeepsItsBaseBelowItsOwnUnreplicatedWrites()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        var before = IncarnationRecord.Read(dir.Path, db.Uid)!;
        for (var i = 0; i < 10; i++)
        {
            Put(db, box, $"own-{i}", "never replicated");
        }

        // Demoted, never applied to as a standby, then promoted again: its own writes must stay above the base.
        store.Replication.Demote();
        store.Replication.Promote("again");

        var after = IncarnationRecord.Read(dir.Path, db.Uid)!;
        Assert.NotEqual(before.Id, after.Id);
        Assert.Equal(before.Base, after.Base);
        Assert.True(after.Base < (long)db.Slot.LatestSequence);
        Assert.Equal(after.Id, db.Slot.Incarnation.Id);
    }

    [Fact]
    public void ApplyingAPageAsAStandby_ClearsTheDatabasesOwnHistory()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var pdb = primary.CreateDatabase("db", replicated: true);
        var pbox = pdb.Boxes.Create("box");
        primary.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options());
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db"), TimeSpan.FromSeconds(30));
        var uid = pdb.Uid;

        // As if this node had once been the database's primary: a history of its own it must not continue once it
        // holds data it received.
        IncarnationRecord.Write(sdir.Path, uid, LogIncarnation.Fresh(0));
        Put(pdb, pbox, "k", "v");
        WaitUntil(() => standby.OpenDatabase("db").Slot.LatestSequence == pdb.Slot.LatestSequence, TimeSpan.FromSeconds(30));

        Assert.False(IncarnationRecord.Exists(sdir.Path, uid));
    }

    // ------------------------------------------------------------------ a standby that wrote as a primary (R5.2 addendum)

    [Fact]
    public void AStandbyThatWroteAsAPrimary_RejoiningAPrimaryWhoseBaseIsAboveItsWrites_IsReBootstrapped()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options(listen: "127.0.0.1:0"));
        var pdb = primary.CreateDatabase("db", replicated: true);
        var pbox = pdb.Boxes.Create("box");
        Put(pdb, pbox, "shared", "x");
        primary.Replication.Listen();
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db")
                        && standby.OpenDatabase("db").Slot.LatestSequence == pdb.Slot.LatestSequence, TimeSpan.FromSeconds(30));

        // A split: the standby is promoted, writes, and steps down again.
        standby.Replication.StopFollowing();
        standby.Replication.Promote("by mistake");
        var sdb = standby.OpenDatabase("db");
        for (var i = 0; i < 5; i++)
        {
            Put(sdb, sdb.Boxes.Find("box")!.Value, $"own-{i}", "only on the stepped-down node");
        }

        standby.Replication.Demote();

        // The real primary goes on, is promoted past the split's epoch, and writes more than the stray writes.
        for (var i = 0; i < 20; i++)
        {
            Put(pdb, pbox, $"real-{i:D2}", "y");
        }

        primary.Replication.Promote("past the split");
        primary.Replication.Promote("past the split");

        // The stepped-down node follows the real primary: its own writes lie below the primary's base, but the primary
        // never had them, so it must be re-bootstrapped, never continued on top of them.
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => Rows(standby.OpenDatabase("db")).SequenceEqual(Rows(pdb)), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(Rows(standby.OpenDatabase("db")), r => r.StartsWith("own-", StringComparison.Ordinal));
        Assert.Equal(Rows(pdb), Rows(standby.OpenDatabase("db")));
    }

    [Fact]
    public void AStrayPrimary_RejoiningANodePromotedFromTheRealPrimaryAboveItsWrites_IsReBootstrapped()
    {
        // Three nodes, so the node the stray one rejoins was promoted from following the real primary, with a base above
        // the stray writes: the case the old "position at or below the base" rule continued (tasks addendum, R5.2 amended).
        using var ydir = new TempStoreDirectory();
        using var xdir = new TempStoreDirectory();
        using var zdir = new TempStoreDirectory();
        using var y = CrystalStore.Open(ydir.Path, Options(listen: "127.0.0.1:0"));
        using var x = CrystalStore.Open(xdir.Path, Options(listen: "127.0.0.1:0"));
        using var z = CrystalStore.Open(zdir.Path, Options(listen: "127.0.0.1:0"));
        var ydb = y.CreateDatabase("db", replicated: true);
        var ybox = ydb.Boxes.Create("box");
        Put(ydb, ybox, "shared", "x");
        y.Replication.Listen();
        x.Replication.Follow(y.Replication.ListenerEndpoint!);
        z.Replication.Follow(y.Replication.ListenerEndpoint!);
        WaitUntil(() => Rows(x.OpenDatabase("db")).SequenceEqual(Rows(ydb)) && Rows(z.OpenDatabase("db")).SequenceEqual(Rows(ydb)), TimeSpan.FromSeconds(30));

        // X splits off, writes, steps down.
        x.Replication.StopFollowing();
        x.Replication.Promote("by mistake");
        var xdb = x.OpenDatabase("db");
        for (var i = 0; i < 5; i++)
        {
            Put(xdb, xdb.Boxes.Find("box")!.Value, $"own-{i}", "stray");
        }

        x.Replication.Demote();

        // The real primary writes well past the stray writes; Z follows it, then Z is promoted (base above them).
        for (var i = 0; i < 30; i++)
        {
            Put(ydb, ybox, $"real-{i:D2}", "y");
        }

        WaitUntil(() => Rows(z.OpenDatabase("db")).SequenceEqual(Rows(ydb)), TimeSpan.FromSeconds(30));
        z.Replication.StopFollowing();
        z.Replication.Promote("failover");
        z.Replication.Promote("past the split");
        z.Replication.Listen();

        x.Replication.Follow(z.Replication.ListenerEndpoint!);
        var zdb = z.OpenDatabase("db");
        WaitUntil(() => Rows(x.OpenDatabase("db")).SequenceEqual(Rows(zdb)), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(Rows(x.OpenDatabase("db")), r => r.StartsWith("own-", StringComparison.Ordinal));
        Assert.Equal(Rows(zdb), Rows(x.OpenDatabase("db")));
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Connection(TcpClient tcp, ReplicationClient client) : IDisposable
    {
        public ReplicationClient Client { get; } = client;

        public void Dispose() => tcp.Dispose();
    }

    private static async Task<Connection> Connect(CrystalStore primary)
    {
        var endpoint = primary.Replication.ListenerEndpoint!;
        var colon = endpoint.LastIndexOf(':');
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(endpoint[..colon].Trim('[', ']'), int.Parse(endpoint[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        return new Connection(tcp, new ReplicationClient(tcp.GetStream(), "test-peer", Secret));
    }

    private static byte[][] Hello(long epoch) =>
    [
        Bytes(ReplicationProtocol.Hello), Bytes(ReplicationProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Bytes("intruder"), Bytes(ReplicationProtocol.RolePrimary), Bytes(epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Bytes(ReplicationProtocol.Format), Bytes(ReplicationProtocol.Layout),
    ];

    private static byte[][] Pull(int maxBytes, IEnumerable<(string Uid, long From)> positions)
    {
        var args = new List<byte[]> { Bytes(ReplicationProtocol.Pull), Bytes(maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)), Bytes("0") };
        foreach (var (uid, from) in positions)
        {
            args.Add(Bytes(uid));
            args.Add(Bytes(from.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return [.. args];
    }

    private static List<(long Seq, byte[] Data)> Batches(RespValue pullReply)
    {
        Assert.Equal(RespType.Array, pullReply.Type);
        var result = new List<(long, byte[])>();
        foreach (var entry in pullReply.Items![2].Items!)
        {
            if (entry.Items![5].Type != RespType.Array)
            {
                continue;
            }

            foreach (var batch in entry.Items[5].Items!)
            {
                result.Add((batch.Items![0].Integer, batch.Items[1].Bytes!));
            }
        }

        return result;
    }

    private static byte[] Bytes(string text) => Text.UTF8.GetBytes(text);

    private static void Put(CrystalDatabase db, Box box, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    private static List<string> Rows(CrystalDatabase db)
    {
        using var snapshot = db.Snapshot();
        var box = db.Boxes.Find("box")!.Value;
        return snapshot.Scan(box, [], [], int.MaxValue).Select(kv => Text.UTF8.GetString(kv.Key)).ToList();
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException or CrystalFormatException)
            {
                // Mid-install or mid-reconcile: try again.
            }

            Thread.Sleep(50);
        }
    }
}
