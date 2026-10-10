// Spec 007: the meter, the instruments, the log events, Status() and the write-stall transition (R1–R6).

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Observability;
using SystemD.Crystal.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class ObservabilityTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempStoreDirectory _dir = new();
    private readonly ManualClock _clock = new();
    private readonly MetricsCapture _metrics = new();
    private readonly CapturingLoggerFactory _logs = new();

    public void Dispose()
    {
        _metrics.Dispose();
        _dir.Dispose();
    }

    private CrystalStore Open(DurabilityMode durability = DurabilityMode.Strict, bool tagDatabase = false, TimeProvider? clock = null, TimeSpan? slowCommit = null) =>
        CrystalStore.Open(_dir.Path, new CrystalOptions
        {
            Durability = durability,
            SyncInterval = Interval,
            Clock = clock ?? _clock,
            MeterFactory = _metrics,
            LoggerFactory = _logs,
            TagMetricsWithDatabase = tagDatabase,
            SlowCommitThreshold = slowCommit,
            BlockCacheBytes = 64L << 20,
        });

    private static void Put(CrystalDatabase db, Box box, string key, DurabilityMode durability = DurabilityMode.Strict)
    {
        using var batch = db.NewBatch();
        batch.Put(box, System.Text.Encoding.UTF8.GetBytes(key), "v"u8);
        batch.Commit(durability);
    }

    // ------------------------------------------------------------------ R1, R2.1: the meter and commits

    [Fact]
    public void Commits_AreCounted_ByDurabilityAndOutcome_WithDurationAndOperations()
    {
        using var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        using (var batch = db.NewBatch())
        {
            batch.Put(box, "a"u8, "v"u8);
            batch.Put(box, "b"u8, "v"u8);
            batch.Commit();
        }

        Put(db, box, "c", DurabilityMode.Buffered);
        using (var empty = db.NewBatch())
        {
            empty.Commit();   // an empty batch is not a commit
        }

        var stale = db.Boxes.Create("stale");
        db.Boxes.Drop(stale);
        using (var refused = db.NewBatch())
        {
            refused.Put(stale, "k"u8, "v"u8);
            Assert.Throws<BoxDroppedException>(() => refused.Commit());
        }

        Assert.Single(_metrics.Of("crystal.commit.count", ("durability", "strict"), ("outcome", "ok")));
        Assert.Single(_metrics.Of("crystal.commit.count", ("durability", "buffered"), ("outcome", "ok")));
        Assert.Single(_metrics.Of("crystal.commit.count", ("outcome", "refused")));
        Assert.Equal(3, _metrics.Of("crystal.commit.count").Count);
        Assert.Equal(2, _metrics.Of("crystal.commit.duration").Count);   // refused commits have no duration
        Assert.Equal([2.0, 1.0], _metrics.Of("crystal.commit.operations").Select(m => m.Value));
        Assert.All(_metrics.All, m => Assert.DoesNotContain(m.Tags.Values, v => v is "orders" or "stale" or "shop"));
    }

    [Fact]
    public void AFailedCommit_IsCountedAsFailed_AndAsASyncFailure()
    {
        var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        db.Slot.Instance.OnBeforeWrite = _ => throw new IOException("injected");

        Assert.Throws<DatabaseFailedException>(() => Put(db, box, "k"));

        Assert.Single(_metrics.Of("crystal.commit.count", ("outcome", "failed")));
        Assert.Single(_metrics.Of("crystal.wal.sync.failures", ("reason", "commit")));
        Assert.Equal([1.0], _metrics.Collect("crystal.store.databases.failed").Select(m => m.Value));
        Assert.Throws<AggregateException>(() => store.Dispose());
    }

    // ------------------------------------------------------------------ R2.2: WAL syncs

    [Fact]
    public void WalSyncs_AreCounted_ByReason()
    {
        var store = Open(DurabilityMode.Interval);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        Put(db, box, "a", DurabilityMode.Interval);
        _clock.Advance(Interval);
        Put(db, box, "b", DurabilityMode.Buffered);
        store.SyncAll();
        Put(db, box, "c", DurabilityMode.Buffered);
        store.Dispose();

        Assert.Single(_metrics.Of("crystal.wal.sync.count", ("reason", "interval")));
        Assert.Single(_metrics.Of("crystal.wal.sync.count", ("reason", "sync_all")));
        Assert.Single(_metrics.Of("crystal.wal.sync.count", ("reason", "close")));
        Assert.Equal(3, _metrics.Of("crystal.wal.sync.duration").Count);
        Assert.Empty(_metrics.Of("crystal.wal.sync.failures"));
    }

    // ------------------------------------------------------------------ R2.3, R2.4: locks and scans

    [Fact]
    public void ALockWait_IsRecordedOnlyWhenContended_AndLockAllIsCounted()
    {
        using var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        using (db.Locks.Lock(box))
        {
        }

        Assert.Empty(_metrics.Of("crystal.lock.wait.duration"));

        using var contended = new ManualResetEventSlim();
        var held = db.Locks.Lock(box);
        db.Locks.OnStripeContended = _ => contended.Set();   // fires only after the non-blocking try failed
        var other = DedicatedThread.Run(() => db.Locks.Lock(box).Dispose());
        Assert.True(contended.Wait(Patience));
        held.Dispose();
        other.Complete(Patience);

        Assert.Single(_metrics.Of("crystal.lock.wait.duration"));
        db.Locks.OnStripeContended = null;
        db.Locks.LockAll().Dispose();
        Assert.Single(_metrics.Of("crystal.lock.all.count"));
    }

    [Fact]
    public void AScan_RecordsItsNativeSteps_EvenWhenTheCallerStopsEarly()
    {
        using var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        for (var i = 0; i < 5; i++)
        {
            Put(db, box, $"k{i}");
        }

        using (var snapshot = db.Snapshot())
        {
            Assert.Equal(5, snapshot.Scan(box, [], [], 100).Count());
            Assert.Equal("k0", System.Text.Encoding.UTF8.GetString(snapshot.Scan(box, [], [], 100).First().Key));
        }

        var steps = _metrics.Of("crystal.scan.steps").Select(m => m.Value).ToList();
        Assert.Equal(2, steps.Count);
        Assert.True(steps[0] >= 5, $"A scan of 5 keys took {steps[0]} steps.");
        Assert.True(steps[1] >= 1 && steps[1] < steps[0], "The early stop is recorded with the steps it took.");
    }

    // ------------------------------------------------------------------ R2.5–R2.7: gauges

    [Fact]
    public void Gauges_ReadTheOpenDatabases_SummedOrPerDatabase()
    {
        using (var store = Open())
        {
            var shop = store.CreateDatabase("shop");
            var users = store.CreateDatabase("users");
            var orders = shop.Boxes.Create("orders");
            shop.Boxes.Create("archive");
            var accounts = users.Boxes.Create("accounts");
            for (var i = 0; i < 20; i++)
            {
                Put(shop, orders, $"k{i}");
            }

            using (var batch = shop.NewBatch())
            {
                for (var i = 0; i < 10; i++)
                {
                    batch.Delete(orders, System.Text.Encoding.UTF8.GetBytes($"k{i}"));
                }

                batch.Commit();
            }

            Put(users, accounts, "a");

            Assert.Equal([2.0], _metrics.Collect("crystal.store.databases.open").Select(m => m.Value));
            Assert.Equal([3.0], _metrics.Collect("crystal.store.boxes").Select(m => m.Value));
            Assert.True(_metrics.Collect("crystal.storage.memtable.deletes").Single().Value >= 10);
            Assert.True(_metrics.Collect("crystal.storage.keys.estimated").Single().Value > 0);
            Assert.Equal([64.0 * (1 << 20)], _metrics.Collect("crystal.storage.block_cache.capacity").Select(m => m.Value));
            Assert.Equal([0.0], _metrics.Collect("crystal.storage.write.stopped").Select(m => m.Value));
            Assert.Equal([0.0], _metrics.Collect("crystal.store.databases.failed").Select(m => m.Value));
        }

        using var tagged = Open(tagDatabase: true);
        tagged.OpenDatabase("shop");
        tagged.OpenDatabase("users");
        var perDatabase = _metrics.Collect("crystal.store.boxes");
        Assert.Equal(2.0, perDatabase.Single(m => m.Tags["database"] == "shop").Value);
        Assert.Equal(1.0, perDatabase.Single(m => m.Tags["database"] == "users").Value);
    }

    [Fact]
    public void Gauges_ReadNothing_OnceTheStoreIsClosed()
    {
        var store = Open();
        store.CreateDatabase("shop").Boxes.Create("orders");
        Assert.NotEmpty(_metrics.Collect("crystal.store.boxes"));

        store.Dispose();

        // The meter belongs to the factory and outlives the store; its gauges must not touch a closed instance.
        Assert.Empty(_metrics.Collect("crystal.store.boxes"));
        Assert.Empty(_metrics.Collect("crystal.storage.block_cache.usage"));
        Assert.Empty(_metrics.Collect("crystal.store.databases.open"));
    }

    /// <summary>
    /// R2.7: while the store closes, after the gauges stop and before the meter is disposed, a collection reads nothing,
    /// not databases being closed or a registry about to be disposed.
    /// </summary>
    [Fact]
    public void Gauges_ReadNothing_WhileTheStoreIsClosing()
    {
        var gate = new StepGate();
        var store = CrystalStore.Open(_dir.Path, new CrystalOptions { MeterFactory = _metrics, Clock = _clock, Hook = gate.Options().Hook });
        store.CreateDatabase("shop").Boxes.Create("orders");
        Assert.NotEmpty(_metrics.Collect("crystal.store.boxes"));

        gate.HoldAt("dispose.closing");
        var closing = DedicatedThread.Run(store.Dispose);
        Assert.True(gate.WaitReached("dispose.closing", Patience));
        var boxes = _metrics.Collect("crystal.store.boxes");
        var cache = _metrics.Collect("crystal.storage.block_cache.usage");
        var capacity = _metrics.Collect("crystal.storage.block_cache.capacity");
        var open = _metrics.Collect("crystal.store.databases.open");
        gate.Release("dispose.closing");
        closing.Complete(Patience);

        Assert.Empty(boxes);
        Assert.Empty(cache);
        Assert.Empty(capacity);
        Assert.Empty(open);
    }

    // ------------------------------------------------------------------ R3: log events

    [Fact]
    public void EveryLogMethod_UsesItsIdFromTheTable()
    {
        var table = typeof(CrystalLogEvents).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (EventId)f.GetValue(null)!)
            .ToDictionary(e => e.Name!, e => e.Id);
        Assert.Equal(table.Count, table.Values.Distinct().Count());   // ids are never shared

        var methods = typeof(CrystalLog).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<LoggerMessageAttribute>())
            .Where(a => a is not null)
            .ToList();
        Assert.Equal(table.Count, methods.Count);
        Assert.All(methods, a => Assert.Equal(table[a!.EventName!], a.EventId));
    }

    [Fact]
    public void TheLifecycle_IsLoggedWithItsEventIds_AndNoKeyOrValue()
    {
        using (var store = Open())
        {
            var db = store.CreateDatabase("shop");
            var box = db.Boxes.Create("orders");
            using (var batch = db.NewBatch())
            {
                batch.Put(box, "secret-key"u8, "secret-value"u8);
                batch.Commit();
            }

            box = db.Boxes.Rename(box, "sales");
            db.Boxes.Drop(box);
            store.RenameDatabase(db, "market");
            store.DropDatabase(db);
        }

        Assert.Single(_logs.Of(CrystalLogEvents.StoreOpened));
        Assert.Single(_logs.Of(CrystalLogEvents.StoreClosed));
        Assert.Equal(LogLevel.Information, _logs.Of(CrystalLogEvents.DatabaseCreated).Single().Level);
        Assert.Single(_logs.Of(CrystalLogEvents.DatabaseRenamed));
        Assert.Single(_logs.Of(CrystalLogEvents.DatabaseDropped));
        Assert.Equal(LogLevel.Debug, _logs.Of(CrystalLogEvents.BoxCreated).Single().Level);
        Assert.Single(_logs.Of(CrystalLogEvents.BoxRenamed));
        Assert.Single(_logs.Of(CrystalLogEvents.BoxDropped));
        Assert.All(_logs.Entries, e => Assert.DoesNotContain("secret", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void AFormatRefusal_IsLoggedAsAnError_WithTheWayForward()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "crystal.format"), "crystal 0\n");

        Assert.Throws<CrystalFormatException>(() => Open());

        var refused = Assert.Single(_logs.Of(CrystalLogEvents.FormatRefused));
        Assert.Equal(LogLevel.Error, refused.Level);
        Assert.Contains("Way forward", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedDatabase_IsLoggedCritical_WithItsEventId()
    {
        var store = Open(DurabilityMode.Interval);
        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a", DurabilityMode.Interval);
        db.Slot.Instance.OnBeforeSync = () => throw new IOException("injected");

        _clock.Advance(Interval);

        var failed = Assert.Single(_logs.Of(CrystalLogEvents.DatabaseFailed));
        Assert.Equal(LogLevel.Critical, failed.Level);
        Assert.Single(_metrics.Of("crystal.wal.sync.failures", ("reason", "interval")));
        Assert.Throws<AggregateException>(() => store.Dispose());
    }

    [Fact]
    public void ASlowCommit_IsLogged_OnlyWhenAThresholdIsSet()
    {
        using (var store = Open(clock: TimeProvider.System))
        {
            var db = store.CreateDatabase("shop");
            Put(db, db.Boxes.Create("orders"), "a");
        }

        Assert.Empty(_logs.Of(CrystalLogEvents.SlowCommit));

        using var slow = Open(clock: TimeProvider.System, slowCommit: TimeSpan.FromTicks(1));
        var again = slow.OpenDatabase("shop");
        Put(again, again.Boxes.Find("orders")!.Value, "b");
        Assert.Equal(LogLevel.Warning, _logs.Of(CrystalLogEvents.SlowCommit).Single().Level);
    }

    // ------------------------------------------------------------------ R5: write stalls

    [Fact]
    public void AWriteStall_IsLoggedWhenItStartsAndWhenItEnds()
    {
        using var store = Open();
        var db = store.CreateDatabase("shop");
        string? state = null;
        store.Engine.StallProbe = _ => state;

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_logs.Of(CrystalLogEvents.WriteStallStarted));

        state = "slowed";
        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var slowed = Assert.Single(_logs.Of(CrystalLogEvents.WriteStallStarted));   // once, not per check
        Assert.Equal(LogLevel.Warning, slowed.Level);
        Assert.Contains("slowed", slowed.Message, StringComparison.Ordinal);

        state = "stopped";
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains("stopped", _logs.Of(CrystalLogEvents.WriteStallStarted)[1].Message, StringComparison.Ordinal);

        state = null;
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LogLevel.Information, _logs.Of(CrystalLogEvents.WriteStallEnded).Single().Level);
    }

    // ------------------------------------------------------------------ the independent review's findings

    /// <summary>
    /// Review finding 1: with no meter factory the store's own meter must outlive its databases' final syncs. Listens
    /// to the process-wide meter, keeping only this store's measurements (by its node id).
    /// </summary>
    [Fact]
    public void WithTheDefaultMeter_TheFinalSyncAtClose_IsMeasured()
    {
        var store = CrystalStore.Open(_dir.Path, new CrystalOptions { Durability = DurabilityMode.Buffered });
        var node = store.NodeId;
        var closeSyncs = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CrystalMetrics.MeterName && instrument.Name == "crystal.wal.sync.count"
                && instrument.Meter.Tags?.Any(t => t.Key == "crystal.node" && (string?)t.Value == node) == true)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && (string?)tag.Value == "close")
                {
                    Interlocked.Increment(ref closeSyncs);
                }
            }
        });
        listener.Start();

        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a", DurabilityMode.Buffered);
        store.Dispose();

        Assert.Equal(1, Volatile.Read(ref closeSyncs));
    }

    /// <summary>Review finding 2: two stores on one factory are two meters, told apart by node; a closed store reports nothing.</summary>
    [Fact]
    public void TwoStoresOnOneFactory_ReportApart_AndAClosedStoreReportsNothing()
    {
        using var otherDir = new TempStoreDirectory();
        var first = Open();
        using var second = CrystalStore.Open(otherDir.Path, new CrystalOptions { MeterFactory = _metrics });
        first.CreateDatabase("shop");
        second.CreateDatabase("users");
        second.CreateDatabase("billing");

        var open = _metrics.Collect("crystal.store.databases.open");
        Assert.Equal(1.0, open.Single(m => m.Tags["crystal.node"] == first.NodeId).Value);
        Assert.Equal(2.0, open.Single(m => m.Tags["crystal.node"] == second.NodeId).Value);

        first.Dispose();

        foreach (var gauge in new[] { "crystal.store.databases.open", "crystal.storage.block_cache.capacity", "crystal.storage.block_cache.usage", "crystal.store.boxes" })
        {
            Assert.All(_metrics.Collect(gauge), m => Assert.Equal(second.NodeId, m.Tags["crystal.node"]));
        }
    }

    /// <summary>Review finding 4: one failed sync is one failure, however many callers meet it.</summary>
    [Fact]
    public void OneFailedSync_IsCountedOnce_ThoughASyncAllWaitedForIt()
    {
        var store = Open(DurabilityMode.Interval);
        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a", DurabilityMode.Interval);

        using var tickInside = new ManualResetEventSlim();
        using var releaseTick = new ManualResetEventSlim();
        db.Slot.Instance.OnBeforeSync = () =>
        {
            tickInside.Set();
            releaseTick.Wait(Patience);
            throw new IOException("injected");
        };
        var tick = DedicatedThread.Run(() => _clock.Advance(Interval));
        Assert.True(tickInside.Wait(Patience));
        var syncAll = DedicatedThread.Run(() => Assert.Throws<DatabaseFailedException>(() => store.SyncAll()));
        Assert.False(syncAll.Join(TimeSpan.FromMilliseconds(200)));
        releaseTick.Set();
        tick.Complete(Patience);
        syncAll.Complete(Patience);

        Assert.Single(_metrics.Of("crystal.wal.sync.failures"));
        Assert.Single(_logs.Of(CrystalLogEvents.DatabaseFailed));
        Assert.Throws<AggregateException>(() => store.Dispose());
    }

    /// <summary>Review finding 8: a name file repaired at open is logged.</summary>
    [Fact]
    public void ARepairedNameFile_IsLogged()
    {
        uint id;
        using (var store = Open())
        {
            id = store.CreateDatabase("shop").Id;
        }

        File.WriteAllText(Path.Combine(_dir.DatabaseFolder(id), "name"), "something else");
        using (var store = Open())
        {
            store.OpenDatabase("shop");
        }

        Assert.Equal(LogLevel.Warning, _logs.Of(CrystalLogEvents.NameFileRepaired).Single().Level);
    }

    /// <summary>Review finding 11: swaps logged; the SST gauges move with a flush; the last sync is the strict commit's.</summary>
    [Fact]
    public void Swaps_AreLogged_AndSstGaugesMoveWithAFlush_AndLastSyncIsTheCommits()
    {
        using var store = Open();
        var shop = store.CreateDatabase("shop");
        var users = store.CreateDatabase("users");
        var a = shop.Boxes.Create("a");
        var b = shop.Boxes.Create("b");
        shop.Boxes.Swap(a, b);
        store.SwapDatabases(shop, users);
        Assert.Single(_logs.Of(CrystalLogEvents.BoxesSwapped));
        Assert.Single(_logs.Of(CrystalLogEvents.DatabasesSwapped));

        Assert.Equal([0.0], _metrics.Collect("crystal.storage.sst.files").Select(m => m.Value));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Put(shop, a, "k");
        shop.Slot.Instance.Db.Flush(new RocksDbSharp.FlushOptions().SetWaitForFlush(true));

        Assert.True(_metrics.Collect("crystal.storage.sst.files").Single().Value >= 1);
        Assert.True(_metrics.Collect("crystal.storage.sst.size").Single().Value > 0);
        var status = store.Status().Databases.Single(d => d.Id == shop.Id);
        Assert.Equal(_clock.GetUtcNow(), status.LastWalSync);   // the commit's, five seconds after the catalogue's
        Assert.True(status.Storage.SstFiles >= 1);
    }

    // ------------------------------------------------------------------ R4: Status()

    [Fact]
    public void Status_ReportsTheStoreAndEachOpenDatabase()
    {
        using var store = Open(DurabilityMode.Interval);
        var shop = store.CreateDatabase("shop");
        var gone = store.CreateDatabase("gone");
        var orders = shop.Boxes.Create("orders");
        shop.Boxes.Create("archive");
        Put(shop, orders, "a", DurabilityMode.Strict);
        store.DropDatabase(gone);

        var status = store.Status();

        Assert.Equal(store.NodeId, status.NodeId);
        Assert.Equal("crystal 1", status.Format);
        Assert.Equal(DurabilityMode.Interval, status.Durability);
        Assert.Equal(Interval, status.SyncInterval);
        Assert.Equal(64L << 20, status.BlockCacheCapacity);
        var database = Assert.Single(status.Databases);
        Assert.Equal(("shop", 2, false), (database.Name, database.Boxes, database.Failed));
        Assert.Equal(_clock.GetUtcNow(), database.LastWalSync);
        Assert.True(database.Storage.EstimatedKeys > 0);
        Assert.False(database.Storage.WriteStopped);
    }

    [Fact]
    public void Status_ShowsAFailedDatabase_AndItsCause()
    {
        var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        db.Slot.Instance.OnBeforeWrite = _ => throw new IOException("injected: disk full");
        Assert.Throws<DatabaseFailedException>(() => Put(db, box, "k"));

        var database = Assert.Single(store.Status().Databases);
        Assert.True(database.Failed);
        Assert.Equal("injected: disk full", database.FailedCause);
        Assert.Throws<AggregateException>(() => store.Dispose());
    }
}
