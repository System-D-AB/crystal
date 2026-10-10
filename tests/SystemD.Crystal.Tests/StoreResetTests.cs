using System;
using System.IO;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class StoreResetTests
{
    [Fact]
    public void Reset_WhileStoreIsOpen_ThrowsStoreLockedException()
    {
        // R10.2: Reset is refused while the store is open anywhere
        var dir = Path.Combine(Path.GetTempPath(), "crystal-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);
            store.CreateDatabase("shop");

            Assert.Throws<StoreLockedException>(() => CrystalStore.Reset(dir));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Reset_Offline_EmptiesAllDatabasesAndKeepsIdentity()
    {
        // R10.1: deletes every database folder and the registry's content, and keeps crystal.format, node.id and node.epoch
        var dir = Path.Combine(Path.GetTempPath(), "crystal-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            string nodeId;
            var formatPath = Path.Combine(dir, "crystal.format");
            var nodeIdPath = Path.Combine(dir, "node.id");
            var epochPath = Path.Combine(dir, "node.epoch");

            using (var store = CrystalStore.Open(dir))
            {
                nodeId = store.NodeId;
                store.CreateDatabase("shop");
                store.CreateDatabase("billing");
                Assert.Equal(2, store.ListDatabases().Count);
            }

            // Create a node.epoch file to simulate replication
            File.WriteAllText(epochPath, "42 primary\n");

            // Perform offline reset
            CrystalStore.Reset(dir);

            // Verify identity files are kept
            Assert.True(File.Exists(formatPath));
            Assert.Equal("crystal 1", File.ReadAllText(formatPath).Trim());

            Assert.True(File.Exists(nodeIdPath));
            Assert.Equal(nodeId, File.ReadAllText(nodeIdPath).Trim());

            Assert.True(File.Exists(epochPath));
            Assert.Equal("42 primary", File.ReadAllText(epochPath).Trim());

            // Verify database folders were deleted
            Assert.False(Directory.Exists(Path.Combine(dir, "db-00000010")));
            Assert.False(Directory.Exists(Path.Combine(dir, "db-00000011")));

            // Reopen store: store is fresh, list is empty, next database gets id 16
            using (var store2 = CrystalStore.Open(dir))
            {
                Assert.Equal(nodeId, store2.NodeId);
                Assert.Empty(store2.ListDatabases());

                var newDb = store2.CreateDatabase("new-db");
                Assert.Equal(16u, newDb.Id);
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
