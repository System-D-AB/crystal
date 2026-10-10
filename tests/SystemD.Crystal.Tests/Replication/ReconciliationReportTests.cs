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
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0"));
        store.CreateDatabase("db", replicated: true).Boxes.Create("box");
        store.Replication.Promote("primary");

        store.Replication.Demote();
        // No standby ever attached and no serving state was built, so there is nothing to reconcile.
        Assert.Null(store.Replication.LastReconciliationReport);
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
            catch (DatabaseNotFoundException)
            {
                // still bootstrapping
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("the standby did not catch up in time");
    }
}
