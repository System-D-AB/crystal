using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseLifecycleTests
{
    [Fact]
    public void CreateDatabase_AllocatesIdsStartingAtSixteen_AndCreatesFolderAndNameFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            Assert.Equal(1, store.RegistryGeneration);

            var db1 = store.CreateDatabase("shop");
            Assert.Equal(16u, db1.Id);
            Assert.Equal("shop", db1.Name);
            Assert.Equal(2, store.RegistryGeneration);

            var db1Folder = Path.Combine(dir, "db-00000010");
            Assert.True(Directory.Exists(db1Folder));
            Assert.Equal("shop", File.ReadAllText(Path.Combine(db1Folder, "name")).Trim());

            var db2 = store.CreateDatabase("billing");
            Assert.Equal(17u, db2.Id);
            Assert.Equal("billing", db2.Name);
            Assert.Equal(3, store.RegistryGeneration);

            var db2Folder = Path.Combine(dir, "db-00000011");
            Assert.True(Directory.Exists(db2Folder));
            Assert.Equal("billing", File.ReadAllText(Path.Combine(db2Folder, "name")).Trim());
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void CreateDatabase_DuplicateName_ThrowsDatabaseNameTakenException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            store.CreateDatabase("shop");

            var ex = Assert.Throws<DatabaseNameTakenException>(() => store.CreateDatabase("shop"));
            Assert.Equal("shop", ex.DatabaseName);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void CreateDatabase_ReservedOrInvalidName_ThrowsAppropriateException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);

            // Names starting with '_' are reserved (R6.2)
            Assert.Throws<ReservedNameException>(() => store.CreateDatabase("_system"));

            // Names with slashes or control characters are rejected (R6.1)
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("invalid/name"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("invalid\\name"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("invalid\0name"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase(""));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("   "));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void OpenDatabase_ReturnsSameInstanceIfAlreadyOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var created = store.CreateDatabase("shop");

            var opened = store.OpenDatabase("shop");
            Assert.Equal(created.Id, opened.Id);
            Assert.Equal(created.Name, opened.Name);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void OpenDatabase_NonExistent_ThrowsDatabaseNotFoundException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var ex = Assert.Throws<DatabaseNotFoundException>(() => store.OpenDatabase("unknown"));
            Assert.Equal("unknown", ex.DatabaseName);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void ListDatabases_ReturnsAllLiveDatabasesFromMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = CrystalStore.Open(dir))
            {
                Assert.Empty(store.ListDatabases());

                store.CreateDatabase("shop", replicated: true);
                store.CreateDatabase("local-cache", replicated: false);

                var list = store.ListDatabases();
                Assert.Equal(2, list.Count);

                var shop = list.Single(d => d.Name == "shop");
                Assert.Equal(16u, shop.Id);
                Assert.True(shop.Replicated);

                var cache = list.Single(d => d.Name == "local-cache");
                Assert.Equal(17u, cache.Id);
                Assert.False(cache.Replicated);
            }

            // Reopen store: registry is loaded into memory (R4.4)
            using (var store2 = CrystalStore.Open(dir))
            {
                var list = store2.ListDatabases();
                Assert.Equal(2, list.Count);
                Assert.Contains(list, d => d.Name == "shop" && d.Id == 16u);
                Assert.Contains(list, d => d.Name == "local-cache" && d.Id == 17u);
            }
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
