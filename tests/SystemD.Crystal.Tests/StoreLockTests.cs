using System;
using System.IO;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class StoreLockTests
{
    [Fact]
    public void Open_SecondOpenInSameProcess_ThrowsStoreLockedException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store1 = CrystalStore.Open(dir);

            var ex = Assert.Throws<StoreLockedException>(() => CrystalStore.Open(dir));
            Assert.Equal(Path.GetFullPath(dir), ex.DataDirectory);
            Assert.Contains("already open", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Open_DisposedStore_AllowsReopen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store1 = CrystalStore.Open(dir);
            store1.Dispose();

            // After dispose, opening again succeeds without StoreLockedException
            using var store2 = CrystalStore.Open(dir);
            Assert.NotNull(store2);
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
