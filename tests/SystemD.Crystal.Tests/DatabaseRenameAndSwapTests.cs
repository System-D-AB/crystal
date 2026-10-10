using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseRenameAndSwapTests
{
    [Fact]
    public void RenameDatabase_MovesAliasUpdatesRecordAndNameFileAndHandle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var db = store.CreateDatabase("shop");
            var genBefore = store.RegistryGeneration;

            store.RenameDatabase(db, "store");

            Assert.Equal("store", db.Name);
            Assert.Equal(genBefore + 1, store.RegistryGeneration);

            // Verify disk name file updated (R5.4)
            var nameFile = Path.Combine(dir, $"db-{db.Id:x8}", "name");
            Assert.Equal("store", File.ReadAllText(nameFile).Trim());

            // Old name is gone, new name can be opened
            Assert.Throws<DatabaseNotFoundException>(() => store.OpenDatabase("shop"));
            var opened = store.OpenDatabase("store");
            Assert.Equal(db.Id, opened.Id);

            // List reports new name
            var list = store.ListDatabases();
            Assert.Single(list);
            Assert.Equal("store", list[0].Name);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void RenameDatabase_TakenName_ThrowsDatabaseNameTakenException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var db1 = store.CreateDatabase("shop");
            store.CreateDatabase("billing");

            var ex = Assert.Throws<DatabaseNameTakenException>(() => store.RenameDatabase(db1, "billing"));
            Assert.Equal("billing", ex.DatabaseName);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void SwapDatabases_ExchangesAliasesAndBothNameFilesAndHandles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-swap-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var db1 = store.CreateDatabase("primary-app");
            var db2 = store.CreateDatabase("staging-app");

            var id1 = db1.Id;
            var id2 = db2.Id;
            var genBefore = store.RegistryGeneration;

            store.SwapDatabases(db1, db2);

            Assert.Equal("staging-app", db1.Name);
            Assert.Equal("primary-app", db2.Name);
            Assert.Equal(genBefore + 1, store.RegistryGeneration);

            // Both disk name files updated (R5.5)
            Assert.Equal("staging-app", File.ReadAllText(Path.Combine(dir, $"db-{id1:x8}", "name")).Trim());
            Assert.Equal("primary-app", File.ReadAllText(Path.Combine(dir, $"db-{id2:x8}", "name")).Trim());

            // Resolving names points to swapped database ids
            var openedPrimary = store.OpenDatabase("primary-app");
            Assert.Equal(id2, openedPrimary.Id);

            var openedStaging = store.OpenDatabase("staging-app");
            Assert.Equal(id1, openedStaging.Id);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort in test cleanup
        }
    }
}
