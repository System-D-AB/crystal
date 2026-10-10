using System;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Storage;

/// <summary>
/// Spec 001-2 R2.2 / R9.5 — the native wrapper's error, lifetime and cleanup behaviour. Each native
/// call must produce a typed exception (never a crash) on a bad argument, and the happy-path calls
/// must succeed and free what they allocate.
/// </summary>
public sealed class NativeRocksTests
{
    [Fact]
    public void CompactOnDeletions_OnValidOptions_Succeeds()
    {
        // ColumnFamilyOptions is not IDisposable (freed by its finalizer), so no `using`.
        var options = new ColumnFamilyOptions();
        var exception = Record.Exception(() => NativeRocks.CompactOnDeletions(options, 1024, 512, 0.5));
        Assert.Null(exception);
    }

    [Fact]
    public void MemtableScanFlushTriggers_OnValidOptions_Succeeds()
    {
        var options = new ColumnFamilyOptions();
        var exception = Record.Exception(() => NativeRocks.MemtableScanFlushTriggers(options, 1000, 16));
        Assert.Null(exception);
    }

    [Fact]
    public void CompactOnDeletions_OnNullOptions_ThrowsArgumentNull_NotCrash()
    {
        Assert.Throws<ArgumentNullException>(() => NativeRocks.CompactOnDeletions(null!, 1, 1, 0.5));
    }

    [Fact]
    public void SetWriteBufferManager_WithNullHandle_ThrowsArgument_NotCrash()
    {
        var options = new DbOptions();
        Assert.Throws<ArgumentException>(() => NativeRocks.SetWriteBufferManager(options, IntPtr.Zero));
    }

    [Fact]
    public void FlushWal_OnAnOpenDatabase_Succeeds()
    {
        using var temp = TempDatabase.Create();
        using (var batch = new WriteBatch())
        {
            batch.Put(Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v"), temp.Instance.Family("general"));
            temp.Instance.Write(batch, sync: false);
        }

        var exception = Record.Exception(() => NativeRocks.FlushWal(temp.Instance.Db, sync: true));
        Assert.Null(exception);
    }

    [Fact]
    public void CreateWriteBufferManager_WithNullCache_ThrowsArgumentNull_NotCrash()
    {
        Assert.Throws<ArgumentNullException>(() => NativeRocks.CreateWriteBufferManager(1024, null!));
    }
}
