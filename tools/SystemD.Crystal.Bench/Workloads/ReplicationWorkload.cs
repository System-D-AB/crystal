using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `replication` (spec 008 / 003 R14.7) — the cost of replication over a real socket (two stores in one process):
/// an idle store of 50 databases a standby stays caught up with; a caught-up follow's steady-state cost; and the
/// catch-up of a backlog of batches, which should stay at page cost rather than growing with the WAL. Numbers go to
/// engineering/measurements.md. Verified by the standby ending byte-identical in count on every database.
/// </summary>
internal sealed class ReplicationWorkload : IWorkload
{
    private const int Databases = 50;
    private const string Secret = "bench-secret";

    public string Name => "replication";

    public WorkloadResult Run(WorkloadContext context)
    {
        var backlog = context.Size == BenchSize.Quick ? 20_000 : 300_000;
        var rows = new List<IReadOnlyList<string>>();
        var json = new List<IReadOnlyDictionary<string, object>>();
        var allPassed = true;

        using var primaryStore = TempStore.CreateReplicated(context.DataParent, Listen());
        using var standbyStore = TempStore.CreateReplicated(context.DataParent, Listen());

        // An idle store of 50 databases, each a box and a seed row.
        var dbNames = Enumerable.Range(0, Databases).Select(i => $"db{i:D2}").ToArray();
        foreach (var name in dbNames)
        {
            var db = primaryStore.Store.CreateDatabase(name, replicated: true);
            db.Boxes.Create("box");
            Put(db, "box", "seed", "0");
        }

        primaryStore.Store.Replication.Listen();
        var endpoint = primaryStore.Store.Replication.ListenerEndpoint!;

        // 1. First follow: a blank standby bootstraps all 50 databases and catches up. Time it.
        var bootstrapSw = Stopwatch.StartNew();
        standbyStore.Store.Replication.Follow(endpoint);
        WaitCaughtUp(primaryStore.Store, standbyStore.Store, dbNames, TimeSpan.FromMinutes(5));
        bootstrapSw.Stop();

        rows.Add(["bootstrap 50 dbs (blank standby)", $"{bootstrapSw.Elapsed.TotalMilliseconds:N0} ms"]);
        json.Add(new Dictionary<string, object> { ["phase"] = "bootstrap", ["databases"] = Databases, ["ms"] = bootstrapSw.Elapsed.TotalMilliseconds });

        // 2. A catch-up of `backlog` batches spread across the databases: time to apply them all.
        var writeSw = Stopwatch.StartNew();
        for (var i = 0; i < backlog; i++)
        {
            var db = primaryStore.Store.OpenDatabase(dbNames[i % Databases]);
            Put(db, "box", $"k{i}", $"v{i}");
        }

        primaryStore.Store.SyncAll();
        writeSw.Stop();

        var catchUpSw = Stopwatch.StartNew();
        WaitCaughtUp(primaryStore.Store, standbyStore.Store, dbNames, TimeSpan.FromMinutes(10));
        catchUpSw.Stop();

        var perBatchUs = catchUpSw.Elapsed.TotalMilliseconds * 1000 / backlog;
        rows.Add([$"catch-up of {backlog:N0} batches", $"{catchUpSw.Elapsed.TotalMilliseconds:N0} ms ({perBatchUs:N1} µs/batch)"]);
        json.Add(new Dictionary<string, object>
        {
            ["phase"] = "catchup",
            ["batches"] = backlog,
            ["catchUpMs"] = catchUpSw.Elapsed.TotalMilliseconds,
            ["perBatchUs"] = perBatchUs,
        });

        // 3. Idle steady-state: a caught-up standby pulling with nothing new should cost almost nothing. Sample the
        // primary's and standby's position stability over a short idle window.
        var idleStart = primaryStore.Store.OpenDatabase(dbNames[0]);
        var before = CountAll(standbyStore.Store, dbNames);
        Thread.Sleep(1_000);
        var after = CountAll(standbyStore.Store, dbNames);
        var idleStable = before == after;
        rows.Add(["idle 1 s (caught up)", idleStable ? "stable" : "unexpected change"]);
        json.Add(new Dictionary<string, object> { ["phase"] = "idle", ["stable"] = idleStable });
        _ = idleStart;

        // Verify: the standby holds the same count as the primary on every database.
        foreach (var name in dbNames)
        {
            var p = Count(primaryStore.Store.OpenDatabase(name));
            var s = Count(standbyStore.Store.OpenDatabase(name));
            allPassed &= p == s;
        }

        standbyStore.Store.Replication.StopFollowing();

        var table = new BenchTable(
            $"replication over a socket — 50 databases, {backlog:N0}-batch catch-up",
            ["phase", "result"],
            rows,
            [],
            json);

        var summary = allPassed
            ? $"standby byte-identical in count across {Databases} databases after a {backlog:N0}-batch catch-up"
            : "FAILED: the standby's counts did not match the primary on some database";

        return new WorkloadResult(Name, [table], new VerificationOutcome(allPassed, summary));
    }

    private static ReplicationOptions Listen() => new() { Secret = Secret, ListenEndpoint = "127.0.0.1:0" };

    private static void Put(CrystalDatabase db, string box, string key, string value)
    {
        var handle = db.Boxes.Find(box)!.Value;
        using var batch = db.NewBatch();
        batch.Put(handle, System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Buffered);
    }

    private static int Count(CrystalDatabase db)
    {
        using var snapshot = db.Snapshot();
        return snapshot.Scan(db.Boxes.Find("box")!.Value, [], [], int.MaxValue).Count();
    }

    private static long CountAll(CrystalStore store, string[] names)
    {
        long total = 0;
        foreach (var name in names)
        {
            total += Count(store.OpenDatabase(name));
        }

        return total;
    }

    private static void WaitCaughtUp(CrystalStore primary, CrystalStore standby, string[] names, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (names.All(Equal))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("the standby did not catch up within the benchmark window");

        bool Equal(string name)
        {
            try
            {
                return Count(standby.OpenDatabase(name)) == Count(primary.OpenDatabase(name));
            }
            catch (DatabaseNotFoundException)
            {
                return false;
            }
        }
    }
}
