using System;
using System.IO;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class StartupRecoveryTests
{
    [Fact]
    public void StartupRecovery_DroppedFolderDeletedOnOpen()
    {
        // R8.3: Startup recovery deletes the folder of every dropped record
        var dir = Path.Combine(Path.GetTempPath(), "crystal-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            uint dbId;
            string dbFolder;

            using (var store = CrystalStore.Open(dir))
            {
                var db = store.CreateDatabase("shop");
                dbId = db.Id;
                dbFolder = Path.Combine(dir, $"db-{dbId:x8}");
                store.DropDatabase(db);
            }

            // Simulate a crash right after the drop batch committed, where the folder was recreated or lingering
            Directory.CreateDirectory(dbFolder);
            File.WriteAllText(Path.Combine(dbFolder, "name"), "shop\n");
            Assert.True(Directory.Exists(dbFolder));

            // Reopen: startup recovery runs
            using (var store2 = CrystalStore.Open(dir))
            {
                Assert.False(Directory.Exists(dbFolder));
            }
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void StartupRecovery_UnregisteredFolderWithPassedId_IsRemoved()
    {
        // R8.3: removes every unregistered folder named db-{id} with an id the allocator has already passed
        // (left by a crash during create, R8.1)
        var dir = Path.Combine(Path.GetTempPath(), "crystal-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = CrystalStore.Open(dir))
            {
                store.CreateDatabase("first"); // id 16
                store.CreateDatabase("second"); // id 17
            }

            // Simulate crashed create that created db-00000010 (id 16, but assume an unregistered orphan with passed id)
            // Or create an orphan folder with id 15 (passed)
            var orphanFolder = Path.Combine(dir, "db-0000000f"); // id 15 < 18
            Directory.CreateDirectory(orphanFolder);
            File.WriteAllText(Path.Combine(orphanFolder, "name"), "orphan\n");
            Assert.True(Directory.Exists(orphanFolder));

            // Reopen: startup recovery detects id 15 is not registered and allocator has passed it
            using (var store2 = CrystalStore.Open(dir))
            {
                Assert.False(Directory.Exists(orphanFolder));
            }
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void StartupRecovery_UnrecognizedOrFutureFolder_IsUntouched()
    {
        // R8.3: reports anything else it does not recognise, and never touches it
        var dir = Path.Combine(Path.GetTempPath(), "crystal-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = CrystalStore.Open(dir))
            {
                store.CreateDatabase("first"); // id 16
            }

            var foreignFolder = Path.Combine(dir, "custom_operator_data");
            Directory.CreateDirectory(foreignFolder);
            File.WriteAllText(Path.Combine(foreignFolder, "readme.txt"), "do not touch");

            var futureFolder = Path.Combine(dir, "db-000000ff"); // id 255 > 17
            Directory.CreateDirectory(futureFolder);
            File.WriteAllText(Path.Combine(futureFolder, "name"), "future\n");

            // Reopen: both unrecognized and future folders must remain untouched
            using (var store2 = CrystalStore.Open(dir))
            {
                Assert.True(Directory.Exists(foreignFolder));
                Assert.True(Directory.Exists(futureFolder));
            }
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void StartupRecovery_DisagreeingNameFile_IsRewrittenFromRegistry()
    {
        // R8.4: A name file that disagrees with the registry is rewritten from the registry.
        var dir = Path.Combine(Path.GetTempPath(), "crystal-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            uint id;
            using (var store = CrystalStore.Open(dir))
            {
                var db = store.CreateDatabase("shop");
                id = db.Id;
            }

            // Simulate crash during rename or corruption of the disk name file
            var nameFilePath = Path.Combine(dir, $"db-{id:x8}", "name");
            File.WriteAllText(nameFilePath, "corrupt_stale_name\n");

            // Reopen: startup recovery repairs the name file
            using (var store2 = CrystalStore.Open(dir))
            {
                var restoredName = File.ReadAllText(nameFilePath).Trim();
                Assert.Equal("shop", restoredName);
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
