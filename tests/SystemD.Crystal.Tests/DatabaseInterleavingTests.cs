using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SystemD.Crystal.Tests;

/// <summary>
/// Spec 005 R5.6, R5.7, R7.1, R7.2 and design addendum A3, tested at the exact interleaving through the store's
/// step hooks (tasks T22). Work that must block runs on dedicated threads, so a starved thread pool cannot change
/// the outcome.
/// </summary>
public sealed class DatabaseInterleavingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a negative check waits for a step that must not happen. The step is held back by a call in progress,
    /// so a correct store never reaches it; a store that does not wait reaches it at once.
    /// </summary>
    private static readonly TimeSpan NotYet = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void DropDatabase_CallInProgress_WaitsForItAndRefusesNewCalls()
    {
        using var dir = new TempStoreDirectory();
        var steps = new StepGate();
        using var store = CrystalStore.Open(dir.Path, steps.Options());
        var shop = store.CreateDatabase("shop");
        var orders = shop.Boxes.Create("orders");
        var heldBatch = shop.NewBatch();

        var call = shop.EnterOperation();
        var drop = DedicatedThread.Run(() => store.DropDatabase(shop));
        try
        {
            Assert.True(steps.WaitReached("drop.registered", Patience));
            Assert.False(steps.WaitReached("drop.drained", NotYet), "Drop closed the database under a call in progress.");

            // Marked dropped: every new call is refused, through the handle and through a batch taken before (R5.7).
            Assert.Throws<DatabaseNotFoundException>(() => shop.EnterOperation());
            Assert.Throws<DatabaseNotFoundException>(() => shop.NewBatch());
            Assert.Throws<DatabaseNotFoundException>(() => heldBatch.Put(orders, [1], [1]));
            Assert.Throws<DatabaseNotFoundException>(() => store.OpenDatabase("shop"));
        }
        finally
        {
            // Released even when an assertion fails, so a broken guard fails the test instead of hanging it.
            call.Dispose();
        }

        drop.Complete(Patience);
        Assert.True(steps.HasReached("drop.closed"));
        Assert.False(Directory.Exists(dir.DatabaseFolder(shop.Id)));
        heldBatch.Dispose();
    }

    [Fact]
    public void DropDatabase_ScanEnumerationInProgress_WaitsForItToEnd()
    {
        using var dir = new TempStoreDirectory();
        var steps = new StepGate();
        using var store = CrystalStore.Open(dir.Path, steps.Options());
        var shop = store.CreateDatabase("shop");
        var orders = shop.Boxes.Create("orders");
        using (var batch = shop.NewBatch())
        {
            batch.Put(orders, [1], [1]);
            batch.Put(orders, [2], [2]);
            batch.Commit();
        }

        using var snapshot = shop.Snapshot();
        var enumerator = snapshot.Scan(orders, [], [], 10).GetEnumerator();
        Assert.True(enumerator.MoveNext());

        var drop = DedicatedThread.Run(() => store.DropDatabase(shop));
        try
        {
            Assert.True(steps.WaitReached("drop.registered", Patience));
            Assert.False(steps.WaitReached("drop.drained", NotYet), "Drop closed the database under a scan in progress.");

            Assert.True(enumerator.MoveNext());
            Assert.Equal([2], enumerator.Current.Key);
        }
        finally
        {
            enumerator.Dispose();
        }

        drop.Complete(Patience);
    }

    [Fact]
    public void DropDatabase_SnapshotAndBatchHeldAcross_DoNotHoldItUpAndAreRefusedAfter()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var shop = store.CreateDatabase("shop");
        var orders = shop.Boxes.Create("orders");
        var snapshot = shop.Snapshot();
        var batch = shop.NewBatch();
        batch.Put(orders, [1], [1]);

        store.DropDatabase(shop); // returns: held objects are not calls in progress (design addendum A3)
        Assert.True(snapshot.Storage.IsReleased, "The drop closed the instance under a native snapshot it did not release.");

        Assert.Throws<DatabaseNotFoundException>(() => snapshot.Get(orders, [1]));
        Assert.Throws<DatabaseNotFoundException>(() => snapshot.Scan(orders, [], [], 10).ToList());
        Assert.Throws<DatabaseNotFoundException>(() => batch.Commit());
        Assert.Throws<DatabaseNotFoundException>(() => shop.Snapshot());

        // Releasing them after the close is safe: the drop released the native snapshot already.
        snapshot.Dispose();
        batch.Dispose();
    }

    [Fact]
    public void OpenDatabase_AfterDropIsRegistered_IsRefused()
    {
        // R7.2, the open that comes second: from the drop's registry batch on, the name is gone.
        using var dir = new TempStoreDirectory();
        var steps = new StepGate();
        steps.HoldAt("drop.registered");
        using var store = CrystalStore.Open(dir.Path, steps.Options());
        var shop = store.CreateDatabase("shop");

        var drop = DedicatedThread.Run(() => store.DropDatabase(shop));
        try
        {
            Assert.True(steps.WaitReached("drop.registered", Patience));
            Assert.Throws<DatabaseNotFoundException>(() => store.OpenDatabase("shop"));
            Assert.DoesNotContain(store.ListDatabases(), d => d.Name == "shop");
        }
        finally
        {
            steps.Release("drop.registered");
        }

        drop.Complete(Patience);
    }

    [Fact]
    public void OpenDatabase_BeforeDrop_ReturnsTheOneHandleWhichIsRefusedAfter()
    {
        // R7.2, the open that comes first: it gets the database's one handle, which the drop then refuses.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var created = store.CreateDatabase("shop");

        var opened = store.OpenDatabase("shop");
        Assert.Same(created, opened);

        store.DropDatabase(created);
        Assert.Throws<DatabaseNotFoundException>(() => opened.NewBatch());
        Assert.Throws<DatabaseNotFoundException>(() => store.RenameDatabase(opened, "other"));
    }

    [Fact]
    public void DataOperations_WhileStoreGateIsHeld_Proceed()
    {
        // R7.1: a create holds the store gate across its folder, instance and registry writes; batches and
        // snapshots of other databases never wait for it.
        using var dir = new TempStoreDirectory();
        var steps = new StepGate();
        using var store = CrystalStore.Open(dir.Path, steps.Options());
        var shop = store.CreateDatabase("shop");
        var orders = shop.Boxes.Create("orders");

        steps.HoldAt("create.folder");
        var create = DedicatedThread.Run(() => store.CreateDatabase("billing"));
        Assert.True(steps.WaitReached("create.folder", Patience));

        var work = DedicatedThread.Run(() =>
        {
            using (var batch = shop.NewBatch())
            {
                batch.Put(orders, [7], [7]);
                batch.Commit();
            }

            using var snapshot = shop.Snapshot();
            Assert.Equal([7], snapshot.Get(orders, [7]));
            Assert.Single(shop.Boxes.List());
        });

        try
        {
            Assert.True(work.Join(Patience), "A data operation waited for the store gate.");
            work.Complete(Patience);
        }
        finally
        {
            steps.Release("create.folder");
            create.Complete(Patience);
        }
    }

    [Fact]
    public void Dispose_CallInProgress_WaitsForItAndRefusesNewCalls()
    {
        using var dir = new TempStoreDirectory();
        var steps = new StepGate();
        var store = CrystalStore.Open(dir.Path, steps.Options());
        var shop = store.CreateDatabase("shop");

        var call = shop.EnterOperation();
        var dispose = DedicatedThread.Run(store.Dispose);
        try
        {
            Assert.True(steps.WaitReached("dispose.closing", Patience));
            Assert.False(dispose.Join(NotYet), "Dispose closed the database under a call in progress.");
            Assert.Throws<ObjectDisposedException>(() => shop.NewBatch());
        }
        finally
        {
            call.Dispose();
        }

        dispose.Complete(Patience);
        using var reopened = CrystalStore.Open(dir.Path); // the lock was released
    }

    [Fact]
    public void Dispose_DropInProgress_WaitsForTheDropToFinish()
    {
        using var dir = new TempStoreDirectory();
        var steps = new StepGate();
        steps.HoldAt("drop.registered");
        var store = CrystalStore.Open(dir.Path, steps.Options());
        var shop = store.CreateDatabase("shop");

        var drop = DedicatedThread.Run(() => store.DropDatabase(shop));
        DedicatedThread dispose;
        try
        {
            Assert.True(steps.WaitReached("drop.registered", Patience));
            dispose = DedicatedThread.Run(store.Dispose);
            Assert.False(dispose.Join(NotYet), "Dispose released the shared resources under a drop in progress.");
        }
        finally
        {
            steps.Release("drop.registered");
        }

        drop.Complete(Patience);
        dispose.Complete(Patience);
        Assert.False(Directory.Exists(dir.DatabaseFolder(shop.Id)));
    }

    [Fact]
    public void DropDatabase_FolderCannotBeDeleted_StillSucceedsAndTheNextOpenDeletesIt()
    {
        // Second review, finding 6: the drop is committed at its registry batch; a folder that will not delete yet
        // is the next open's to remove (R8.3), not an error for a drop that succeeded.
        using var dir = new TempStoreDirectory();
        uint id;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var shop = store.CreateDatabase("shop");
            id = shop.Id;
            var blocker = Path.Combine(dir.DatabaseFolder(id), "held-open");
            using (new FileStream(blocker, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                store.DropDatabase(shop);
                Assert.DoesNotContain(store.ListDatabases(), d => d.Name == "shop");
            }
        }

        using (CrystalStore.Open(dir.Path))
        {
            Assert.False(Directory.Exists(dir.DatabaseFolder(id)));
        }
    }

    [Fact]
    public void RenameDatabase_EveryHolderOfTheHandle_SeesTheNewName()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var first = store.CreateDatabase("shop");
        var second = store.OpenDatabase("shop");

        store.RenameDatabase(first, "store");

        Assert.Equal("store", second.Name);
        Assert.Same(second, store.OpenDatabase("store"));
    }

    [Fact]
    public void RenameDatabase_HandleFromAnotherStore_IsRefused()
    {
        using var dirA = new TempStoreDirectory();
        using var dirB = new TempStoreDirectory();
        using var storeA = CrystalStore.Open(dirA.Path);
        using var storeB = CrystalStore.Open(dirB.Path);
        var shopA = storeA.CreateDatabase("shop");
        storeB.CreateDatabase("shop"); // same id, 16, in another store

        Assert.Throws<DatabaseNotFoundException>(() => storeB.RenameDatabase(shopA, "other"));
        Assert.Throws<DatabaseNotFoundException>(() => storeB.DropDatabase(shopA));
        Assert.NotNull(storeB.OpenDatabase("shop"));
    }
}
