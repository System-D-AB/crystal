using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseDropTests
{
    [Fact]
    public void DropDatabase_RemovesAliasDeletesFolderIncrementsGeneration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var db = store.CreateDatabase("shop");
            var dbId = db.Id;
            var dbFolder = Path.Combine(dir, $"db-{dbId:x8}");
            Assert.True(Directory.Exists(dbFolder));

            var genBefore = store.RegistryGeneration;
            store.DropDatabase(db);

            Assert.Equal(genBefore + 1, store.RegistryGeneration);
            Assert.False(Directory.Exists(dbFolder));

            // Name is not in list
            Assert.DoesNotContain(store.ListDatabases(), d => d.Id == dbId);

            // OpenDatabase throws
            Assert.Throws<DatabaseNotFoundException>(() => store.OpenDatabase("shop"));

            // Operations through old handle are refused
            Assert.Throws<DatabaseNotFoundException>(() => db.EnterOperation());
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void CreateDatabase_AfterDrop_CanReuseNameWithNewId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            var db1 = store.CreateDatabase("shop");
            var id1 = db1.Id;
            store.DropDatabase(db1);

            // Create again with same name (R5.8)
            var db2 = store.CreateDatabase("shop");
            Assert.NotEqual(id1, db2.Id);
            Assert.True(db2.Id > id1);

            // New folder exists
            var db2Folder = Path.Combine(dir, $"db-{db2.Id:x8}");
            Assert.True(Directory.Exists(db2Folder));

            // Old folder still does not exist
            var db1Folder = Path.Combine(dir, $"db-{id1:x8}");
            Assert.False(Directory.Exists(db1Folder));
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
