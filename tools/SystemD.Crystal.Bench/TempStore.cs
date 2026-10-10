// Ported from StowDB: tests/Stow.Storage.Measurements/TempMeasurementDb.cs (branch rocksdb). Carried: a fresh
// database in a unique temp folder, deleted after whether the run passed or failed. Changed: a Crystal store under
// --dir, one per cell (R1.4, R3.5), with retry on the Windows delete race.

using System;
using System.IO;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench;

/// <summary>
/// A fresh Crystal store in a new folder under <c>--dir</c>, deleted when the workload ends whether it passed or
/// failed (spec 008 R1.4). Each cell gets its own, so cells never inherit each other's memory or compaction debt
/// (R3.5).
/// </summary>
internal sealed class TempStore : IDisposable
{
    private readonly string _directory;
    private bool _disposed;

    private TempStore(string directory, CrystalStore store)
    {
        _directory = directory;
        Store = store;
    }

    /// <summary>The open store.</summary>
    public CrystalStore Store { get; }

    /// <summary>The store's data directory (for reporting the drive under test).</summary>
    public string Directory => _directory;

    /// <summary>
    /// Creates a fresh store in a new unique folder under <paramref name="parent"/>.
    /// </summary>
    public static TempStore Create(string parent, CrystalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(parent);
        System.IO.Directory.CreateDirectory(parent);
        var dir = Path.Combine(parent, "crystal-bench-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var store = CrystalStore.Open(dir, options);
            return new TempStore(dir, store);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    /// <summary>Creates a fresh store configured for replication (spec 003), with the given replication options.</summary>
    public static TempStore CreateReplicated(string parent, ReplicationOptions replication) =>
        Create(parent, new CrystalOptions { Replication = replication });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Store.Dispose();
        }
        finally
        {
            TryDelete(_directory);
        }
    }

    private static void TryDelete(string dir)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(20);
            }
        }
    }
}
