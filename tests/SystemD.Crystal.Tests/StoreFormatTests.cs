using System;
using System.IO;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class StoreFormatTests
{
    [Fact]
    public void Open_MissingDirectory_CreatesAndStampsStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(Directory.Exists(dir));

            using var store = CrystalStore.Open(dir);
            Assert.True(Directory.Exists(dir));

            var formatPath = Path.Combine(dir, "crystal.format");
            var nodeIdPath = Path.Combine(dir, "node.id");

            Assert.True(File.Exists(formatPath));
            Assert.True(File.Exists(nodeIdPath));

            var format = File.ReadAllText(formatPath).Trim();
            Assert.Equal("crystal 1", format);

            var nodeId = File.ReadAllText(nodeIdPath).Trim();
            Assert.True(Guid.TryParse(nodeId, out _));
            Assert.Equal(nodeId, store.NodeId);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Open_ReopenExistingStore_PreservesNodeId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string originalNodeId;
            using (var store1 = CrystalStore.Open(dir))
            {
                originalNodeId = store1.NodeId;
            }

            using (var store2 = CrystalStore.Open(dir))
            {
                Assert.Equal(originalNodeId, store2.NodeId);
            }
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Open_ContentWithoutFormatStamp_ThrowsCrystalFormatException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "foreign.txt"), "hello");

        try
        {
            var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir));
            Assert.Null(ex.FoundFormat);
            Assert.Equal("crystal 1", ex.SupportedFormat);
            Assert.Contains("crystal.format", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Open_ForeignFormatStamp_ThrowsCrystalFormatException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "crystal.format"), "crystal 2\n");

        try
        {
            var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir));
            Assert.Equal("crystal 2", ex.FoundFormat);
            Assert.Equal("crystal 1", ex.SupportedFormat);
            Assert.Contains("crystal 2", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ex.WayForward);
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
