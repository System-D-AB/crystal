using System;
using System.Threading;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The resources every database of one store shares (spec 001-2 R5): one LRU block cache, one
/// background thread environment, and one write-buffer budget across all databases. Sizes are store
/// options (set by <c>005-store-and-databases</c>); this type holds the live native objects and frees
/// them when the store closes.
/// </summary>
internal sealed class StoreResources : IDisposable
{
    private IntPtr _writeBufferManager;
    private bool _disposed;
    private long _openIterators;

    /// <summary>
    /// The native iterators open now over every database of the store (spec 012 R8). Counted closed only when disposed,
    /// never by a finalizer, so a scan nobody disposed stays visible.
    /// </summary>
    /// <remarks>Ported from StowDB: src/Crystal/Crystal.Storage.RocksDb/IteratorAccounting.cs (the open count).</remarks>
    internal long OpenIterators => Interlocked.Read(ref _openIterators);

    internal void IteratorOpened() => Interlocked.Increment(ref _openIterators);

    internal void IteratorClosed() => Interlocked.Decrement(ref _openIterators);

    private StoreResources(Cache blockCache, Env environment, IntPtr writeBufferManager)
    {
        BlockCache = blockCache;
        Environment = environment;
        _writeBufferManager = writeBufferManager;
    }

    /// <summary>The shared LRU block cache (R5.1).</summary>
    internal Cache BlockCache { get; }

    /// <summary>The shared background-thread environment (R5.1).</summary>
    internal Env Environment { get; }

    /// <summary>The shared write-buffer manager handle, applied to every database's options (R5.2).</summary>
    internal IntPtr WriteBufferManager => _writeBufferManager;

    /// <summary>
    /// Allocates the shared cache, environment and write-buffer budget for a store.
    /// <paramref name="blockCacheBytes"/> sizes the LRU cache; <paramref name="writeBufferBytes"/> is
    /// the write-buffer budget shared across the store's databases, costed against the cache (R5.2);
    /// <paramref name="backgroundThreads"/> sets the environment's background compaction/flush threads.
    /// </summary>
    internal static StoreResources Create(ulong blockCacheBytes, ulong writeBufferBytes, int backgroundThreads)
    {
        var cache = Cache.CreateLru(blockCacheBytes);
        var env = Env.CreateDefaultEnv();
        if (backgroundThreads > 0)
        {
            env.SetBackgroundThreads(backgroundThreads);
        }

        var wbm = NativeRocks.CreateWriteBufferManager(writeBufferBytes, cache);
        return new StoreResources(cache, env, wbm);
    }

    /// <summary>Applies the shared cache, environment and write-buffer budget to a database's options.</summary>
    internal void ApplyTo(DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        options.SetEnv(Environment.Handle);
        NativeRocks.SetWriteBufferManager(options, _writeBufferManager);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Free the write-buffer manager first (it is costed against the cache), then the cache and env
        // are freed by their own finalizers / Dispose. The native WBM has no managed wrapper.
        NativeRocks.DestroyWriteBufferManager(_writeBufferManager);
        _writeBufferManager = IntPtr.Zero;
        (BlockCache as IDisposable)?.Dispose();
        (Environment as IDisposable)?.Dispose();
    }
}
