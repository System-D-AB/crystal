using System;
using System.Threading;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Encapsulates a RocksDB snapshot inside the internal storage layer (spec 002 R6.4).
/// Ensures no RocksDB types leak to the public surface or IL bodies outside SystemD.Crystal.Storage.
/// </summary>
internal sealed class StorageSnapshot : IDisposable
{
    private RocksDbSharp.Snapshot? _snapshot;

    internal StorageSnapshot(RocksDbSharp.Snapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>Whether the native snapshot has been released.</summary>
    internal bool IsReleased => Volatile.Read(ref _snapshot) is null;

    internal RocksDbSharp.Snapshot Value =>
        _snapshot ?? throw new ObjectDisposedException(nameof(StorageSnapshot));

    public void Dispose()
    {
        Interlocked.Exchange(ref _snapshot, null)?.Dispose();
    }
}
