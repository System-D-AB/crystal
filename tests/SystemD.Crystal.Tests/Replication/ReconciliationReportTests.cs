// Spec 003 T13 / R9.2 — a demote writes a reconciliation report: the batches committed beyond the highest position a
// standby acknowledged, decoded per box, so an operator can see what a failover may have lost. Driven end to end over
// a real socket: a standby follows and catches up (its slot's acked position), stops, the primary writes more to
// known boxes, then demotes — the report names exactly those boxes with the right put/delete counts.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class ReconciliationReportTests
{
    private static CrystalOptions Options(string? listen) => new()
    {
        Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = listen, LongPollMax = TimeSpan.FromMilliseconds(300) },
    };

    private static void Put(CrystalDatabase db, Box box, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    private static void Delete(CrystalDatabase db, Box box, string key)
    {
        using var batch = db.NewBatch();
        batch.Delete(box, Text.UTF8.GetBytes(key));
        batch.Commit(DurabilityMode.Strict);
    }

    [Fact]
    public void Demote_WritesAReconciliationReport_NamingTheUnreplicatedBoxesAndCounts()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();

        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        var orders = db.Boxes.Create("orders");
        var users = db.Boxes.Create("users");
        Put(db, orders, "o0", "seed");
        Put(db, users, "u0", "seed");

        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;

        // A standby follows and catches up, so its slot's acked position is the primary's latest-so-far.
        using var standby = CrystalStore.Open(sdir.Path, Options(listen: null));
        standby.Replication.Follow(endpoint);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db")
            && standby.OpenDatabase("db").Slot.LatestSequence >= db.Slot.LatestSequence, TimeSpan.FromSeconds(30));

        // Give the slot a moment to reflect the caught-up pull, then stop following so acked freezes.
        Thread.Sleep(300);
        standby.Replication.StopFollowing();

        // The primary now writes MORE than the standby acked: 3 puts to 'orders', 1 delete to 'users'.
        Put(db, orders, "o1", "v1");
        Put(db, orders, "o2", "v2");
        Put(db, orders, "o3", "value-three");
        Delete(db, users, "u0");

        // Demote: the report names exactly the unreplicated boxes with their counts.
        primary.Replication.Demote();
        var reportPath = primary.Replication.LastReconciliationReport;
        Assert.NotNull(reportPath);
        Assert.True(File.Exists(reportPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath!));
        var dbs = doc.RootElement.GetProperty("databases");
        Assert.Equal(1, dbs.GetArrayLength());
        var boxes = dbs[0].GetProperty("boxes");

        var byName = new Dictionary<string, (int Puts, int Deletes)>(StringComparer.Ordinal);
        foreach (var b in boxes.EnumerateArray())
        {
            byName[b.GetProperty("name").GetString()!] = (b.GetProperty("puts").GetInt32(), b.GetProperty("deletes").GetInt32());
        }

        Assert.True(byName.TryGetValue("orders", out var o), "the report must name the 'orders' box");
        Assert.Equal(3, o.Puts);
        Assert.Equal(0, o.Deletes);

        Assert.True(byName.TryGetValue("users", out var u), "the report must name the 'users' box");
        Assert.Equal(0, u.Puts);
        Assert.Equal(1, u.Deletes);
    }

    [Fact]
    public void Demote_WithNothingUnreplicated_WritesNoReport()
    {
        // Rewritten for spec 011 R8.1: this test used to demote a store no standby had ever pulled from and expect no
        // report, which was the defect (its box creation is a write nobody acknowledged). Nothing unreplicated now
        // means what it says: a standby holds every write when the primary demotes.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var store = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = store.CreateDatabase("db", replicated: true);
        Put(db, db.Boxes.Create("box"), "k", "v");
        store.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options(listen: null));
        standby.Replication.Follow(store.Replication.ListenerEndpoint!);
        WaitUntil(() => store.Replication.Status().Slots.Any(s => s.Acknowledged >= (long)db.Slot.LatestSequence), TimeSpan.FromSeconds(30));
        standby.Replication.StopFollowing();

        store.Replication.Demote();
        Assert.Null(store.Replication.LastReconciliationReport);
    }

    // ------------------------------------------------------------------ spec 011 R8

    [Fact]
    public void ADatabaseNoStandbyPulled_IsInTheReport_FromPositionZero()
    {
        // R8.1: the report covers every replicated database, not only those a standby happened to pull.
        using var pdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: null));
        var unpulled = primary.CreateDatabase("unpulled", replicated: true);
        var box = unpulled.Boxes.Create("box");
        Put(unpulled, box, "k", "v");

        primary.Replication.Demote();

        var reportPath = primary.Replication.LastReconciliationReport;
        Assert.NotNull(reportPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath!));
        var entry = Assert.Single(doc.RootElement.GetProperty("databases").EnumerateArray(), d => d.GetProperty("uid").GetString() == unpulled.Uid);
        Assert.Equal(0, entry.GetProperty("lastReplicatedSeq").GetInt64());
        Assert.Contains(entry.GetProperty("boxes").EnumerateArray(), b => b.GetProperty("name").GetString() == "box" && b.GetProperty("puts").GetInt32() == 1);
    }

    [Fact]
    public async System.Threading.Tasks.Task ASlot_HoldsThePositionItsStandbyLastStated_NotTheHighest()
    {
        // R8.2: a standby that re-bootstraps lower holds no more than it says; its slot follows it down.
        using var pdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        Put(db, box, "a", "1");
        Put(db, box, "b", "2");
        primary.Replication.Listen();

        var endpoint = primary.Replication.ListenerEndpoint!;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        var client = new SystemD.Crystal.Replication.ReplicationClient(tcp.GetStream(), "standby-1", "test-secret");
        await client.HandshakeAsync(SystemD.Crystal.Replication.ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        var latest = (long)db.Slot.LatestSequence;
        await Pull(client, db.Uid, latest);
        Assert.Equal(latest, primary.Replication.Status().Slots.Single().Acknowledged);
        await Pull(client, db.Uid, latest - 1);
        Assert.Equal(latest - 1, primary.Replication.Status().Slots.Single().Acknowledged);
    }

    [Fact]
    public async System.Threading.Tasks.Task ADemotionByAHigherEpochFromAPeer_WritesTheReportToo()
    {
        // R8.3: every demotion writes the report, including one a peer's higher epoch causes.
        using var pdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        Put(db, db.Boxes.Create("box"), "k", "v");
        primary.Replication.Listen();
        var events = new List<ReplicationEvent>();
        primary.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };

        var endpoint = primary.Replication.ListenerEndpoint!;
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture)))
        {
            var client = new SystemD.Crystal.Replication.ReplicationClient(tcp.GetStream(), "peer", "test-secret");
            await client.HandshakeAsync(SystemD.Crystal.Replication.ReplicationProtocol.RoleStandby, primary.Replication.Epoch + 5, CancellationToken.None);
        }

        WaitUntil(() => primary.Replication.Role == ReplicationRole.Standby, TimeSpan.FromSeconds(10));
        EventDelivery.Drain(primary);
        var reportPath = primary.Replication.LastReconciliationReport;
        Assert.NotNull(reportPath);
        Assert.True(File.Exists(reportPath));
        lock (events)
        {
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.Demoted && e.Detail.Contains(reportPath!, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ADemotionByAPeer_ReportsFromWhatTheStandbyAcknowledged_NotFromZero()
    {
        // Review 2026-10-13, S1: the report was written after the slots were cleared, so its floor was always 0.
        using var pdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        Put(db, box, "k", "v");
        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;
        var port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        // A standby acknowledges everything so far.
        var acked = (long)db.Slot.LatestSequence;
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port))
        {
            var standby = new SystemD.Crystal.Replication.ReplicationClient(tcp.GetStream(), "standby-1", "test-secret");
            await standby.HandshakeAsync(SystemD.Crystal.Replication.ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
            await Pull(standby, db.Uid, acked);
        }

        Put(db, box, "unreplicated", "x");

        // A peer at a higher epoch demotes the primary.
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port))
        {
            var peer = new SystemD.Crystal.Replication.ReplicationClient(tcp.GetStream(), "peer", "test-secret");
            await peer.HandshakeAsync(SystemD.Crystal.Replication.ReplicationProtocol.RoleStandby, primary.Replication.Epoch + 5, CancellationToken.None);
        }

        WaitUntil(() => primary.Replication.LastReconciliationReport is not null, TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(File.ReadAllText(primary.Replication.LastReconciliationReport!));
        var entry = Assert.Single(doc.RootElement.GetProperty("databases").EnumerateArray());
        Assert.Equal(acked, entry.GetProperty("lastReplicatedSeq").GetInt64());
    }

    [Fact]
    public async System.Threading.Tasks.Task AStandbyToldItDiverged_HoldsNothing_SoTheReportCountsFromZero()
    {
        // Review 2026-10-13, S2 (R8.2): a standby stating a position past this primary's latest diverged; its slot must
        // not stand at that position, or the report would think everything replicated and write nothing.
        using var pdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        Put(db, db.Boxes.Create("box"), "k", "v");
        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture)))
        {
            var standby = new SystemD.Crystal.Replication.ReplicationClient(tcp.GetStream(), "standby-1", "test-secret");
            await standby.HandshakeAsync(SystemD.Crystal.Replication.ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
            await Pull(standby, db.Uid, (long)db.Slot.LatestSequence + 50);
        }

        Assert.Equal(0, primary.Replication.Status().Slots.Single().Acknowledged);
        primary.Replication.Demote();
        Assert.NotNull(primary.Replication.LastReconciliationReport);
    }

    private static System.Threading.Tasks.Task<SystemD.Crystal.Replication.Resp.RespValue> Pull(SystemD.Crystal.Replication.ReplicationClient client, string uid, long from) =>
        client.RequestAsync(
            [Text.UTF8.GetBytes("PULL"), Text.UTF8.GetBytes("1048576"), Text.UTF8.GetBytes("0"), Text.UTF8.GetBytes(uid),
             Text.UTF8.GetBytes(from.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            CancellationToken.None);

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
            catch (DatabaseNotFoundException)
            {
                // still bootstrapping
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("the standby did not catch up in time");
    }
}
