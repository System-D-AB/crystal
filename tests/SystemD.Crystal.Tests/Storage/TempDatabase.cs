using System;
using System.Collections.Generic;
using System.IO;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Tests.Storage;

/// <summary>
/// A throwaway store and one database in a fresh temporary directory, for the binding tests. Disposing
/// it closes the database and shared resources and deletes the directory.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _dir;

    private TempDatabase(string dir, StoreResources resources, RocksInstance instance)
    {
        _dir = dir;
        Resources = resources;
        Instance = instance;
    }

    internal StoreResources Resources { get; }

    internal RocksInstance Instance { get; }

    internal static TempDatabase Create(
        ulong blockCacheBytes = 32UL * 1024 * 1024,
        ulong writeBufferBytes = 8UL * 1024 * 1024,
        int backgroundThreads = 2,
        ulong walTtlSeconds = 3600,
        ulong walSizeLimitMb = 64)
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var resources = StoreResources.Create(blockCacheBytes, writeBufferBytes, backgroundThreads);
        var instance = RocksInstance.Open(
            Path.Combine(dir, "db"),
            resources,
            new Dictionary<string, BoxProfile>(),
            walTtlSeconds,
            walSizeLimitMb);
        return new TempDatabase(dir, resources, instance);
    }

    public void Dispose()
    {
        Instance.Dispose();
        Resources.Dispose();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A background compaction thread may still hold a file briefly; a temp dir leak is harmless.
        }
    }
}
