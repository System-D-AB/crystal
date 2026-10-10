// Spec 006: the durability modes, the interval timer, SyncAll, and failed databases (R2–R7.6). The kill tests of
// R7.1–R7.3 are deferred by the maintainer (requirements addendum 2026-10-09).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Storage;
using SystemD.Crystal.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DurabilityTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempStoreDirectory _dir = new();
    private readonly ManualClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private CrystalStore Open(DurabilityMode durability = DurabilityMode.Strict, ILoggerFactory? logs = null) =>
        CrystalStore.Open(_dir.Path, new CrystalOptions
        {
            Durability = durability,
            SyncInterval = Interval,
            Clock = _clock,
            LoggerFactory = logs,
        });

    private static void Put(CrystalDatabase db, Box box, string key, DurabilityMode? durability = null)
    {
        using var batch = db.NewBatch();
        batch.Put(box, System.Text.Encoding.UTF8.GetBytes(key), "v"u8);
        if (durability is { } mode)
        {
            batch.Commit(mode);
        }
        else
        {
            batch.Commit();
        }
    }

    /// <summary>Records whether each write handed to RocksDB was synced.</summary>
    private static ConcurrentQueue<bool> RecordWrites(CrystalDatabase db)
    {
        var writes = new ConcurrentQueue<bool>();
        db.Slot.Instance.OnBeforeWrite = writes.Enqueue;
        return writes;
    }

    // ------------------------------------------------------------------ R2: choosing a mode

    [Fact]
    public void TheDefaultMode_IsStrict()
    {
        Assert.Equal(DurabilityMode.Strict, new CrystalOptions().Durability);

        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        var writes = RecordWrites(db);

        Put(db, box, "k");

        Assert.Equal([true], writes);
    }

    [Theory]
    [InlineData(DurabilityMode.Strict, true)]
    [InlineData(DurabilityMode.Interval, false)]
    [InlineData(DurabilityMode.Buffered, false)]
    public void ACommit_UsesTheStoresDefault_OrTheModeItNames(DurabilityMode storeDefault, bool defaultSynced)
    {
        using var store = Open(storeDefault);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        var writes = RecordWrites(db);

        Put(db, box, "a");
        Put(db, box, "b", DurabilityMode.Strict);
        Put(db, box, "c", DurabilityMode.Buffered);

        Assert.Equal([defaultSynced, true, false], writes);
    }

    [Theory]
    [InlineData(DurabilityMode.Interval)]
    [InlineData(DurabilityMode.Buffered)]
    public void TheCatalogue_IsAlwaysStrict_R23(DurabilityMode storeDefault)
    {
        using var store = Open(storeDefault);
        var db = store.CreateDatabase("shop");
        var writes = RecordWrites(db);

        var box = db.Boxes.Create("orders");
        box = db.Boxes.Rename(box, "sales");
        var other = db.Boxes.Create("archive");
        db.Boxes.Swap(box, other);
        db.Boxes.Drop(other);

        Assert.Equal(5, writes.Count);
        Assert.All(writes, Assert.True);
    }

    [Fact]
    public void Options_OutOfRange_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CrystalStore.Open(_dir.Path, new CrystalOptions { SyncInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrystalStore.Open(_dir.Path, new CrystalOptions { SyncInterval = TimeSpan.FromSeconds(11) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrystalStore.Open(_dir.Path, new CrystalOptions { Durability = (DurabilityMode)7 }));

        using var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        using var batch = db.NewBatch();
        batch.Put(box, "k"u8, "v"u8);
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Commit((DurabilityMode)7));
    }

    // ------------------------------------------------------------------ R4, R7.4: the interval timer

    [Fact]
    public void Interval_SyncsOncePerElapsedInterval_OnlyDatabasesWithUnsyncedCommits()
    {
        using var store = Open(DurabilityMode.Interval);
        var shop = store.CreateDatabase("shop");
        var idle = store.CreateDatabase("idle");
        var orders = shop.Boxes.Create("orders");
        idle.Boxes.Create("nothing");

        Put(shop, orders, "a");
        _clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, shop.Slot.Instance.SyncCount);       // not before the interval

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, shop.Slot.Instance.SyncCount);       // one sync when it elapses
        Assert.Equal(0, idle.Slot.Instance.SyncCount);       // none for a database with nothing to sync

        _clock.Advance(Interval * 3);
        Assert.Equal(1, shop.Slot.Instance.SyncCount);       // none while nothing new was committed

        Put(shop, orders, "b");
        Put(shop, orders, "c");
        _clock.Advance(Interval);
        Assert.Equal(2, shop.Slot.Instance.SyncCount);       // one sync covers every commit of the interval
    }

    [Fact]
    public void Buffered_IsNeverSyncedByTheTimer_OnlyBySyncAllOrClosing()
    {
        using var store = Open(DurabilityMode.Interval);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        // The timer is running: an interval commit started it, and it synced that commit.
        Put(db, box, "start", DurabilityMode.Interval);
        _clock.Advance(Interval);
        Assert.Equal(1, db.Slot.Instance.SyncCount);

        Put(db, box, "a", DurabilityMode.Buffered);
        _clock.Advance(Interval * 4);
        Assert.Equal(1, db.Slot.Instance.SyncCount);         // the running timer leaves a buffered commit alone

        store.SyncAll();
        Assert.Equal(2, db.Slot.Instance.SyncCount);
        store.SyncAll();
        Assert.Equal(2, db.Slot.Instance.SyncCount);         // nothing new: nothing synced
    }

    [Fact]
    public void Closing_SyncsWhatWasNotSynced_ButADropDoesNot_R42()
    {
        RocksInstance kept, buffered, dropped;
        using (var store = Open(DurabilityMode.Interval))
        {
            var db = store.CreateDatabase("shop");
            var log = store.CreateDatabase("log");
            var gone = store.CreateDatabase("gone");
            Put(db, db.Boxes.Create("orders"), "a");
            Put(log, log.Boxes.Create("lines"), "a", DurabilityMode.Buffered);
            Put(gone, gone.Boxes.Create("x"), "a");
            (kept, buffered, dropped) = (db.Slot.Instance, log.Slot.Instance, gone.Slot.Instance);

            store.DropDatabase(gone);
            Assert.Equal(0, dropped.SyncCount);
            Assert.Equal(0, kept.SyncCount);
            Assert.Equal(0, buffered.SyncCount);
        }

        Assert.Equal(1, kept.SyncCount);
        Assert.Equal(1, buffered.SyncCount);   // buffered commits are synced on close too (006 review, finding 7)
    }

    // ------------------------------------------------------------------ the independent review's interleavings

    /// <summary>
    /// 006 review, finding 1: SyncAll must not return while a sync it depends on is still running. The timer's sync is
    /// held inside the native call's doorway; SyncAll, on another thread, must wait for it, and then report its failure.
    /// </summary>
    [Fact]
    public void SyncAll_WaitsForASyncInProgress_AndReportsItsFailure()
    {
        using var store = Open(DurabilityMode.Interval);
        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a");

        using var tickInside = new ManualResetEventSlim();
        using var releaseTick = new ManualResetEventSlim();
        db.Slot.Instance.OnBeforeSync = () =>
        {
            tickInside.Set();
            releaseTick.Wait(Patience);
            throw new IOException("injected: the disk refused the sync");
        };

        var tick = DedicatedThread.Run(() => _clock.Advance(Interval));
        Assert.True(tickInside.Wait(Patience));

        Exception? syncAllError = null;
        var syncAll = DedicatedThread.Run(() =>
        {
            try
            {
                store.SyncAll();
            }
            catch (Exception ex)
            {
                syncAllError = ex;
            }
        });
        Assert.False(syncAll.Join(TimeSpan.FromMilliseconds(200)), "SyncAll returned while the sync it relies on was still running.");

        releaseTick.Set();
        tick.Complete(Patience);
        syncAll.Complete(Patience);
        Assert.IsType<DatabaseFailedException>(syncAllError);
        db.Slot.Instance.OnBeforeSync = null;
        Assert.Throws<AggregateException>(() => store.Dispose());
    }

    /// <summary>
    /// 006 review, finding 3: the timer is not held up by a create holding the store gate. The create is held at a
    /// step inside the gate; a tick must still sync the other database.
    /// </summary>
    [Fact]
    public void TheTimer_IsNotHeldUpByACreateHoldingTheStoreGate()
    {
        var gate = new StepGate();
        var options = gate.Options();
        using var store = CrystalStore.Open(_dir.Path, new CrystalOptions
        {
            Durability = DurabilityMode.Interval,
            SyncInterval = Interval,
            Clock = _clock,
            Hook = options.Hook,
        });
        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a");

        gate.HoldAt("create.folder");
        var create = DedicatedThread.Run(() => store.CreateDatabase("other"));
        Assert.True(gate.WaitReached("create.folder", Patience));

        var tick = DedicatedThread.Run(() => _clock.Advance(Interval));
        var ticked = tick.Join(Patience);
        gate.Release("create.folder");
        create.Complete(Patience);
        tick.Complete(Patience);

        Assert.True(ticked, "The tick waited for the create to leave the store gate.");
        Assert.Equal(1, db.Slot.Instance.SyncCount);
    }

    /// <summary>006 review, finding 2: a failed final sync is reported by Dispose, not only logged (with no logger, not at all).</summary>
    [Fact]
    public void AFailedSyncOnClose_IsReportedByDispose()
    {
        var store = Open(DurabilityMode.Buffered);
        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a");
        db.Slot.Instance.OnBeforeSync = () => throw new IOException("injected: the disk refused the sync");

        var thrown = Assert.Throws<AggregateException>(() => store.Dispose());
        Assert.Contains(thrown.InnerExceptions, e => e is DatabaseFailedException f && f.Message.Contains("'shop'", StringComparison.Ordinal));
    }

    /// <summary>006 review, finding 5: a failed registry write fails the registry like a database: typed, logged, refusing later changes.</summary>
    [Fact]
    public void AFailedRegistryWrite_RefusesLaterChanges_AndIsReportedByDispose()
    {
        var logs = new CapturingLoggerFactory();
        var store = Open(logs: logs);
        var registry = store.Engine.RegistryInstance;
        registry.OnBeforeWrite = _ => throw new IOException("injected: the registry write failed");

        Assert.Throws<DatabaseFailedException>(() => store.CreateDatabase("shop"));
        registry.OnBeforeWrite = null;
        var refused = Assert.Throws<DatabaseFailedException>(() => store.CreateDatabase("other"));
        Assert.Contains("open the store again", refused.Message, StringComparison.Ordinal);
        Assert.Single(logs.At(LogLevel.Critical));

        var thrown = Assert.Throws<AggregateException>(() => store.Dispose());
        Assert.Contains(thrown.InnerExceptions, e => e is DatabaseFailedException);

        using var reopened = Open();
        reopened.CreateDatabase("shop");
    }

    [Fact]
    public void AStrictCommit_LeavesNothingForTheTimer()
    {
        using var store = Open(DurabilityMode.Strict);
        var db = store.CreateDatabase("shop");
        Put(db, db.Boxes.Create("orders"), "a");

        Assert.False(db.Slot.Instance.IntervalPending);
        store.SyncAll();
        Assert.Equal(0, db.Slot.Instance.SyncCount);
    }

    // ------------------------------------------------------------------ R5, R7.5: a failed sync

    [Fact]
    public void AFailedIntervalSync_FailsTheDatabase_RefusesWrites_KeepsReads_AndIsLoggedCritical()
    {
        var logs = new CapturingLoggerFactory();
        var store = Open(DurabilityMode.Interval, logs);
        var db = store.CreateDatabase("shop");
        var healthy = store.CreateDatabase("healthy");
        var box = db.Boxes.Create("orders");
        var other = healthy.Boxes.Create("orders");
        Put(db, box, "before");
        Put(healthy, other, "before");

        db.Slot.Instance.OnBeforeSync = () => throw new IOException("injected: the disk refused the sync");
        _clock.Advance(Interval);

        // Writes are refused, by name, with the way forward; the failure is never retried as a success.
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "after"u8, "v"u8);
            var refused = Assert.Throws<DatabaseFailedException>(() => batch.Commit(DurabilityMode.Strict));
            Assert.Contains("'shop'", refused.Message, StringComparison.Ordinal);
            Assert.Contains("open the store again", refused.Message, StringComparison.Ordinal);
            Assert.IsType<IOException>(refused.InnerException);
        }

        Assert.Throws<DatabaseFailedException>(() => db.Boxes.Create("more"));
        Assert.Throws<DatabaseFailedException>(() => store.SyncAll());

        // Reads still work.
        using (var snapshot = db.Snapshot())
        {
            Assert.NotNull(snapshot.Get(box, "before"u8));
        }

        // The failure is logged once, at Critical, and other databases go on syncing.
        Assert.Single(logs.At(LogLevel.Critical));
        Put(healthy, other, "after", DurabilityMode.Interval);
        _clock.Advance(Interval);
        Assert.Equal(2, healthy.Slot.Instance.SyncCount);

        // Closing the store reports the failed database (006 R5.1), and only that one.
        var closing = Assert.Throws<AggregateException>(() => store.Dispose());
        Assert.Equal("database 'shop' (id 16)", Assert.IsType<DatabaseFailedException>(Assert.Single(closing.InnerExceptions)).Database);
    }

    [Fact]
    public void AFailedStrictWrite_ThrowsToItsCaller_AndFailsTheDatabase_R52()
    {
        var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        db.Slot.Instance.OnBeforeWrite = synced =>
        {
            if (synced)
            {
                throw new IOException("injected: the synced write failed");
            }
        };

        using (var batch = db.NewBatch())
        {
            batch.Put(box, "k"u8, "v"u8);
            Assert.Throws<DatabaseFailedException>(() => batch.Commit());
        }

        db.Slot.Instance.OnBeforeWrite = null;
        Assert.Throws<DatabaseFailedException>(() => Put(db, box, "k", DurabilityMode.Buffered));

        using (var snapshot = db.Snapshot())
        {
            Assert.Null(snapshot.Get(box, "k"u8));
        }

        Assert.Throws<AggregateException>(() => store.Dispose());
    }

    [Fact]
    public void AFailedDatabase_IsHealthyAgainAfterReopening()
    {
        var store = Open();
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        Put(db, box, "kept");
        db.Slot.Instance.OnBeforeWrite = _ => throw new IOException("injected");
        Assert.Throws<DatabaseFailedException>(() => Put(db, box, "lost"));
        Assert.Throws<AggregateException>(() => store.Dispose());

        using var reopened = Open();
        var again = reopened.OpenDatabase("shop");
        var orders = again.Boxes.Find("orders")!.Value;
        Put(again, orders, "new");
        using var snapshot = again.Snapshot();
        Assert.NotNull(snapshot.Get(orders, "kept"u8));
        Assert.NotNull(snapshot.Get(orders, "new"u8));
        // Whether "lost" is present is not asserted: the injected failure comes before the write, but a real failed
        // sync can leave its batch in the WAL, to replay on reopen (006 review, finding 6; said in the message).
    }

    // ------------------------------------------------------------------ R3.1, R7.6: group commit

    /// <summary>
    /// R3.1: no lock of Crystal's is held across a synced write, so concurrent strict commits reach RocksDB together
    /// and share its group commit. Both writers must be inside the write at once; a lock around it would hold the
    /// second outside until the first finished, and this test would time out. (The binding exposes no sync counter
    /// without RocksDB's statistics, so R7.6's "fewer syncs than commits" is shown by this instead; see tasks.md.)
    /// </summary>
    [Fact]
    public void ConcurrentStrictCommits_ReachRocksDbTogether_R31()
    {
        using var store = Open();
        var db = store.CreateDatabase("shop");
        var a = db.Boxes.Create("a");
        var b = db.Boxes.Create("b");

        using var bothInside = new CountdownEvent(2);
        var together = true;
        db.Slot.Instance.OnBeforeWrite = _ =>
        {
            bothInside.Signal();
            if (!bothInside.Wait(Patience))
            {
                together = false;
            }
        };

        var first = DedicatedThread.Run(() => Put(db, a, "k"));
        var second = DedicatedThread.Run(() => Put(db, b, "k"));
        first.Complete(Patience * 2);
        second.Complete(Patience * 2);

        Assert.True(together, "Both strict commits must be inside the write at the same time.");
    }
}
