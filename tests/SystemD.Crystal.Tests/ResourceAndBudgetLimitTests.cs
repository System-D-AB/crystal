using System;
using System.IO;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class ResourceAndBudgetLimitTests
{
    [Fact]
    public void MaxOpenDatabases_ExceedingLimit_IsRefusedByName()
    {
        // R9.2, R9.3: Opening more than MaxOpenDatabases is refused by name, never degraded silently.
        var dir = Path.Combine(Path.GetTempPath(), "crystal-limit-" + Guid.NewGuid().ToString("N"));
        var options = new CrystalOptions
        {
            MaxOpenDatabases = 3,
            BlockCacheBytes = 64L << 20,
            WriteBufferBudgetBytes = 64L << 20,
            BackgroundThreads = 1,
        };

        try
        {
            using var store = CrystalStore.Open(dir, options);
            store.CreateDatabase("db1");
            store.CreateDatabase("db2");
            store.CreateDatabase("db3");

            // 4th database creation exceeds MaxOpenDatabases = 3
            var ex = Assert.Throws<DatabaseLimitException>(() => store.CreateDatabase("db4"));
            Assert.Equal("db4", ex.DatabaseName);
            Assert.Equal(3, ex.Limit);
            Assert.Contains("db4", ex.Message);
            Assert.Contains("3", ex.Message);
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
