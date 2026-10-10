using System;
using System.IO;
using System.Linq;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

/// <summary>
/// Spec 005 R13.2 and R13.5: a second Crystal process, and process kills at every step of create, drop and rename
/// (tasks T21). The crash host is a separate program that uses the built package; the test kills it at a step.
/// </summary>
public sealed class CrossProcessAndCrashSimulationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("create.allocated")]
    [InlineData("create.folder")]
    [InlineData("create.opened")]
    [InlineData("create.registered")]
    public void CreateDatabase_ProcessKilledAtStep_ReopensConsistent(string step)
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("existing"); // id 16
        }

        Assert.Equal(step, CrashHost.RunAndKillAtStep("create", dir.Path, "shop", step)); // id 17

        AssertStoreConsistent(dir.Path);
        using var reopened = CrystalStore.Open(dir.Path);
        var registered = step == "create.registered";
        Assert.Equal(registered, reopened.ListDatabases().Any(d => d.Name == "shop"));

        if (!registered)
        {
            // The interrupted id is never reused, and nothing of the interrupted create is visible.
            var shop = reopened.CreateDatabase("shop");
            Assert.Equal(18u, shop.Id);
            Assert.Empty(shop.Boxes.List());
            Assert.False(Directory.Exists(dir.DatabaseFolder(17)));
        }
    }

    [Theory]
    [InlineData("drop.registered")]
    [InlineData("drop.drained")]
    [InlineData("drop.closed")]
    public void DropDatabase_ProcessKilledAtStep_ReopensWithDatabaseGone(string step)
    {
        using var dir = new TempStoreDirectory();
        uint id;
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("existing");
            var shop = store.CreateDatabase("shop");
            id = shop.Id;
            var orders = shop.Boxes.Create("orders");
            using var batch = shop.NewBatch();
            batch.Put(orders, [1], [42]);
            batch.Commit();
        }

        Assert.Equal(step, CrashHost.RunAndKillAtStep("drop", dir.Path, "shop", step));

        AssertStoreConsistent(dir.Path);
        using var reopened = CrystalStore.Open(dir.Path);
        Assert.DoesNotContain(reopened.ListDatabases(), d => d.Name == "shop");
        Assert.Throws<DatabaseNotFoundException>(() => reopened.OpenDatabase("shop"));
        Assert.False(Directory.Exists(dir.DatabaseFolder(id)));

        var recreated = reopened.CreateDatabase("shop");
        Assert.NotEqual(id, recreated.Id);
        Assert.Null(recreated.Boxes.Find("orders"));
    }

    [Fact]
    public void RenameDatabase_ProcessKilledBeforeNameFile_ReopensWithNewNameAndNameFileRepaired()
    {
        using var dir = new TempStoreDirectory();
        uint id;
        using (var store = CrystalStore.Open(dir.Path))
        {
            id = store.CreateDatabase("shop").Id;
        }

        Assert.Equal("rename.registered", CrashHost.RunAndKillAtStep("rename", dir.Path, "shop", "store", "rename.registered"));
        var nameFile = Path.Combine(dir.DatabaseFolder(id), StoreEngine.NameFileName);
        Assert.Equal("shop\n", File.ReadAllText(nameFile)); // killed before the name file was rewritten

        AssertStoreConsistent(dir.Path);
        using var reopened = CrystalStore.Open(dir.Path);
        Assert.Equal(id, reopened.OpenDatabase("store").Id);
        Assert.Throws<DatabaseNotFoundException>(() => reopened.OpenDatabase("shop"));
        Assert.Equal("store\n", File.ReadAllText(nameFile));
    }

    [Fact]
    public void Reset_ProcessKilledAfterTheRegistryIsDeleted_IsRefusedUntilResetCompletes()
    {
        // Second review, finding 5: reset deletes the registry first, so a crash part-way leaves a store that is
        // refused with Reset as the way forward, never one that lists databases without folders.
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
            store.CreateDatabase("billing");
        }

        Assert.Equal("reset.registry-deleted", CrashHost.RunAndKillAtStep("reset", dir.Path, "reset.registry-deleted"));

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("Reset", ex.WayForward, StringComparison.Ordinal);

        CrystalStore.Reset(dir.Path);
        using var reopened = CrystalStore.Open(dir.Path);
        Assert.Empty(reopened.ListDatabases());
        Assert.Equal(16u, reopened.CreateDatabase("shop").Id);
    }

    [Fact]
    public void CreateDatabase_InSeparateProgram_RunsToCompletion()
    {
        // The playbook's real run: the built package used from a separate program.
        using var dir = new TempStoreDirectory();
        Assert.Equal("DONE", CrashHost.RunAndKillAtStep("create", dir.Path, "shop", "none"));

        using var store = CrystalStore.Open(dir.Path);
        Assert.Equal(16u, store.OpenDatabase("shop").Id);
    }

    [Fact]
    public void StoreLock_HeldByAnotherProcess_RefusesOpenAndReset()
    {
        using var dir = new TempStoreDirectory();
        CrystalStore.Open(dir.Path).Dispose();

        using (var holder = CrashHost.StartAndWaitFor("OPEN", "hold", dir.Path))
        {
            var ex = Assert.Throws<StoreLockedException>(() => CrystalStore.Open(dir.Path));
            Assert.Contains("already open", ex.Message, StringComparison.Ordinal);
            Assert.Throws<StoreLockedException>(() => CrystalStore.Reset(dir.Path));

            holder.StandardInput.Close();
            Assert.True(holder.WaitForExit(Patience));
            Assert.Equal(0, holder.ExitCode);
        }

        using var store = CrystalStore.Open(dir.Path);
    }

    [Fact]
    public void StoreLock_HeldByThisProcess_RefusesAnotherProcess()
    {
        using var dir = new TempStoreDirectory();
        using (CrystalStore.Open(dir.Path))
        {
            Assert.Equal("LOCKED", CrashHost.RunAndKillAtStep("tryopen", dir.Path));
        }

        Assert.Equal("OPENED", CrashHost.RunAndKillAtStep("tryopen", dir.Path));
    }

    /// <summary>
    /// R13.2: no unregistered folder served, no registered database without a folder, no half-dropped database.
    /// </summary>
    private static void AssertStoreConsistent(string dataDirectory)
    {
        using var store = CrystalStore.Open(dataDirectory);
        var live = store.ListDatabases();

        var folders = Directory.EnumerateDirectories(dataDirectory)
            .Select(path => Path.GetFileName(path))
            .Where(name => name.StartsWith("db-", StringComparison.Ordinal))
            .ToHashSet();
        Assert.Equal(live.Select(d => StoreEngine.DatabaseFolderName(d.Id)).ToHashSet(), folders);

        foreach (var info in live)
        {
            var database = store.OpenDatabase(info.Name);
            Assert.Equal(info.Id, database.Id);
            Assert.NotNull(database.Boxes.List());
        }
    }
}
