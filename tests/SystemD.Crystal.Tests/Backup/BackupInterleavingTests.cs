// Spec 010 T9 — interleavings that a guard exists for, each held at a hook: a drop that begins after the backup entered
// a database but before its copy registered; a product that opens a database the backup opened; a name taken between a
// restore's allocation and its registration; an engine folder that holds another database than the manifest says.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupInterleavingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ADropBetweenEnteringTheDatabaseAndRegisteringTheCopy_StopsTheCopyBeforeItStarts()
    {
        // The drop's stop finds no copy registered yet; the copy's registration then sees the drop and never starts.
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var holding = new BackupTests.Hold("backup.entered");
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Hook = holding.Hook });
        var db = store.CreateDatabase("db");
        BackupTests.Fill(db, db.Boxes.Create("box"), 100, 16);

        var running = store.BackupAsync(root.Path);
        holding.WaitReached();
        var dropping = Task.Run(() => store.DropDatabase(db)); // waits for the backup's call in progress
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!db.Slot.IsClosing)
        {
            Assert.True(watch.Elapsed < Bound);
            await Task.Delay(10);
        }

        holding.Release();
        await dropping.WaitAsync(Bound);
        var set = await running.WaitAsync(Bound);
        Assert.Empty(set.Databases);
        Assert.Equal("dropped", Assert.Single(set.Omitted).Reason);
    }

    [Fact]
    public async Task ADatabaseTheProductOpensDuringItsBackup_StaysOpenAfterIt()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("db").Boxes.Create("box");
        }

        using var holding = new BackupTests.Hold("backup.copying");
        using (var store = CrystalStore.Open(dir.Path, new CrystalOptions { Hook = holding.Hook }))
        {
            var running = store.BackupAsync(root.Path); // opens "db", which is closed
            holding.WaitReached();
            var db = store.OpenDatabase("db"); // the product takes its handle meanwhile
            holding.Release();
            await running.WaitAsync(Bound);

            using var batch = db.NewBatch();
            batch.Put(db.Boxes.Find("box")!.Value, "k"u8, "v"u8);
            batch.Commit(); // the handle still works: the backup did not close the database under it
            Assert.Equal(["db"], store.Status().Databases.Select(d => d.Name));
        }
    }

    [Fact]
    public async Task ANameTakenWhileARestoreIsUnderWay_IsRefusedAtRegistration_AndTheRestoreLeavesNothing()
    {
        using var source = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo set;
        using (var store = CrystalStore.Open(source.Path))
        {
            store.CreateDatabase("a");
            set = await store.BackupAsync(root.Path);
        }

        using var dir = new TempStoreDirectory();
        using var holding = new BackupTests.Hold("restore.moved");
        using var target = CrystalStore.Open(dir.Path, new CrystalOptions { Hook = holding.Hook });
        var running = target.RestoreAsync(root.Path, set.SetId);
        holding.WaitReached();
        var mine = target.CreateDatabase("a"); // the product takes the name first
        holding.Release();

        await Assert.ThrowsAsync<DatabaseNameTakenException>(() => running.WaitAsync(Bound));
        Assert.Equal(mine.Uid, Assert.Single(target.ListDatabases()).Uid);
        Assert.Equal(
            [StoreEngine.DatabaseFolderName(mine.Id)],
            Directory.EnumerateDirectories(dir.Path).Select(Path.GetFileName).Where(n => n!.StartsWith("db-", StringComparison.Ordinal) || n.StartsWith(StoreEngine.RestoreStagingPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnEngineFolderHoldingAnotherDatabase_IsRefusedBeforeAnythingMovesIn()
    {
        using var source = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo set;
        using (var store = CrystalStore.Open(source.Path))
        {
            store.CreateDatabase("x");
            store.CreateDatabase("y");
            set = await store.BackupAsync(root.Path);
        }

        // Each engine holds backup 1; swapped by hand, each now holds the other database.
        var engines = Path.Combine(root.Path, "engines");
        var (x, y) = (set.Databases[0].Uid, set.Databases[1].Uid);
        Directory.Move(Path.Combine(engines, x), Path.Combine(engines, "swap"));
        Directory.Move(Path.Combine(engines, y), Path.Combine(engines, x));
        Directory.Move(Path.Combine(engines, "swap"), Path.Combine(engines, y));

        using var dir = new TempStoreDirectory();
        using var target = CrystalStore.Open(dir.Path);
        await Assert.ThrowsAsync<CrystalFormatException>(() => target.RestoreAsync(root.Path, set.SetId));
        Assert.Empty(target.ListDatabases());
        Assert.DoesNotContain(Directory.EnumerateDirectories(dir.Path).Select(d => Path.GetFileName(d)), n => n.StartsWith("db-", StringComparison.Ordinal) || n.StartsWith(StoreEngine.RestoreStagingPrefix, StringComparison.Ordinal));
    }
}
