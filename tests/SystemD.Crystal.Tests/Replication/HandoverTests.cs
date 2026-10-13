// Spec 011 R14 — a planned handover. The product moves the primary role on purpose (its own goodbye, roster and
// priority); Crystal supplies the step it cannot do from outside: stop local writes, keep serving until the chosen
// standby holds everything, then stand down. Nothing committed before the handover is lost, and the old primary follows
// the new one without a re-bootstrap.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class HandoverTests
{
    private static CrystalOptions Options(string? listen) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public async Task AHandoverUnderContinuousWrites_LosesNothing_AndTheOldPrimaryFollowsWithoutARebootstrap()
    {
        using var adir = new TempStoreDirectory();
        using var bdir = new TempStoreDirectory();
        using var a = CrystalStore.Open(adir.Path, Options(listen: "127.0.0.1:0"));
        var orders = Created(a, "orders");
        var users = Created(a, "users");
        a.Replication.Listen();

        using var b = CrystalStore.Open(bdir.Path, Options(listen: "127.0.0.1:0"));
        b.Replication.Listen();
        b.Replication.Follow(a.Replication.ListenerEndpoint!);
        WaitUntil(() => b.ListDatabases().Count == 2);

        // A writer commits to both databases until the primary refuses: every acknowledged write must survive.
        var acknowledged = new ConcurrentBag<(string Db, string Key)>();
        var refused = 0;
        var writer = Task.Run(() =>
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    Put(i % 2 == 0 ? orders : users, $"k{i:D6}");
                    acknowledged.Add((i % 2 == 0 ? "orders" : "users", $"k{i:D6}"));
                }
                catch (NotPrimaryException)
                {
                    Interlocked.Exchange(ref refused, 1);
                    return;
                }
            }
        });
        WaitUntil(() => acknowledged.Count > 200);

        var aEvents = new List<ReplicationEvent>();
        a.Replication.Events += (_, e) => { lock (aEvents) { aEvents.Add(e); } };
        var handedOver = await a.Replication.StepDownAsync(b.NodeId, TimeSpan.FromSeconds(30));
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, refused);
        Assert.Equal(ReplicationRole.Standby, a.Replication.Role);
        Assert.Null(a.Replication.LastReconciliationReport); // nothing unreplicated: every write was acknowledged
        Assert.Equal(2, handedOver.Count);

        // The product promotes the standby and points the old primary at it.
        b.Replication.Promote("handover");
        a.Replication.Follow(b.Replication.ListenerEndpoint!);
        Put(b.OpenDatabase("orders"), "after-handover");
        WaitUntil(() => Keys(a, "orders").Contains("after-handover"));

        foreach (var (db, key) in acknowledged)
        {
            Assert.Contains(key, Keys(b, db)); // the new primary holds every acknowledged write
        }

        Assert.Equal(Keys(b, "orders"), Keys(a, "orders"));
        Assert.Equal(Keys(b, "users"), Keys(a, "users"));
        EventDelivery.Drain(a);
        lock (aEvents)
        {
            Assert.Contains(aEvents, e => e.Kind == ReplicationEventKind.Demoted && e.Detail.Contains(b.NodeId, StringComparison.Ordinal));
            Assert.DoesNotContain(aEvents, e => e.Kind is ReplicationEventKind.Gap or ReplicationEventKind.Divergence);
        }
    }

    [Fact]
    public async Task AHandover_WaitsForEveryDatabase_NotOnlySome()
    {
        // R14.2, R14.6: the standby's apply of the second database is held, so it is behind on that one alone; the
        // step-down must not complete on the strength of the first.
        using var adir = new TempStoreDirectory();
        using var bdir = new TempStoreDirectory();
        using var a = CrystalStore.Open(adir.Path, Options(listen: "127.0.0.1:0"));
        var first = Created(a, "first");
        var second = Created(a, "second");
        a.Replication.Listen();

        var hold = 0;
        var release = new ManualResetEventSlim(false); // set in finally
        var bOptions = Options(listen: null);
        using var b = CrystalStore.Open(bdir.Path, new CrystalOptions
        {
            Replication = bOptions.Replication,
            Hook = step =>
            {
                if (Volatile.Read(ref hold) == 1 && step == "standby.applying:" + second.Uid)
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            },
        });
        b.Replication.Follow(a.Replication.ListenerEndpoint!);
        WaitUntil(() => b.ListDatabases().Count == 2 && Keys(b, "second").Contains("seed"));
        try
        {
            // The first database is current and acknowledged; only then is the second held behind. (The follower applies
            // every database on one loop, so a hold also stops its acknowledgements: hence this order.)
            Put(first, "current");
            WaitUntil(() => a.Replication.Status().Slots.Any(s => s.DatabaseUid == first.Uid && s.Acknowledged == (long)first.Slot.LatestSequence));
            Volatile.Write(ref hold, 1);
            Put(second, "behind");

            await Assert.ThrowsAsync<TimeoutException>(() => a.Replication.StepDownAsync(b.NodeId, TimeSpan.FromSeconds(2)));
            Assert.Equal(ReplicationRole.Primary, a.Replication.Role);
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            release.Set();
        }
    }

    [Fact]
    public async Task AStandbyThatStoppedNamingADatabase_IsNotCaughtUpOnIt()
    {
        // Review 2026-10-13, S2: a standby re-bootstrapping a database stops naming it in its PULL; its slot must not
        // stand at the last position it stated, or a step-down would hand over to a standby that does not hold it.
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        var db = Created(primary, "db");
        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;
        using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        var standby = new SystemD.Crystal.Replication.ReplicationClient(tcp.GetStream(), "standby-1", "test-secret");
        await standby.HandshakeAsync(SystemD.Crystal.Replication.ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        await standby.RequestAsync([B("PULL"), B("1048576"), B("0"), B(db.Uid), B(((long)db.Slot.LatestSequence).ToString(System.Globalization.CultureInfo.InvariantCulture))], CancellationToken.None);
        Assert.Single(primary.Replication.Status().Slots);
        await standby.RequestAsync([B("PULL"), B("1048576"), B("0")], CancellationToken.None); // names nothing now
        Assert.Empty(primary.Replication.Status().Slots);

        await Assert.ThrowsAsync<TimeoutException>(() => primary.Replication.StepDownAsync("standby-1", TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task AHandoverThatTimesOut_AcceptsWritesAgain_AndStaysPrimary()
    {
        // R14.3: no standby named "nobody" ever acknowledges anything.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        var db = Created(store, "db");

        await Assert.ThrowsAsync<TimeoutException>(() => store.Replication.StepDownAsync("nobody", TimeSpan.FromMilliseconds(300)));

        Assert.Equal(ReplicationRole.Primary, store.Replication.Role);
        Put(db, "after-the-timeout"); // accepted again
        Assert.Contains("after-the-timeout", Keys(store, "db"));
    }

    [Fact]
    public async Task AFenceDuringAHandover_EndsIt_AndTheFenceStands()
    {
        // R14.4: a role change while the step-down waits ends it; the role the change set stands.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        Created(store, "db");

        var stepping = store.Replication.StepDownAsync("nobody", TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        store.Replication.Fence("the product changed its mind");

        await Assert.ThrowsAsync<InvalidOperationException>(() => stepping);
        Assert.Equal(ReplicationRole.Fenced, store.Replication.Role);
    }

    [Fact]
    public async Task AStepDown_IsRefusedOnANonPrimary_AndWhileAnotherRuns()
    {
        // R14.5.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        Created(store, "db");
        var first = store.Replication.StepDownAsync("nobody", TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Replication.StepDownAsync("nobody", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        store.Replication.Demote();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Replication.StepDownAsync("nobody", TimeSpan.FromSeconds(1)));
    }

    private static byte[] B(string text) => Text.UTF8.GetBytes(text);

    private static CrystalDatabase Created(CrystalStore store, string name)
    {
        var db = store.CreateDatabase(name, replicated: true);
        db.Boxes.Create("box");
        Put(db, "seed");
        return db;
    }

    private static void Put(CrystalDatabase db, string key)
    {
        using var batch = db.NewBatch();
        batch.Put(db.Boxes.Find("box")!.Value, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes("v"));
        batch.Commit(DurabilityMode.Strict);
    }

    private static List<string> Keys(CrystalStore store, string name)
    {
        var db = store.OpenDatabase(name);
        using var snapshot = db.Snapshot();
        return snapshot.Scan(db.Boxes.Find("box")!.Value, [], [], int.MaxValue).Select(kv => Text.UTF8.GetString(kv.Key)).ToList();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException or NullReferenceException)
            {
                // not there yet
            }

            Thread.Sleep(50);
        }
    }
}
