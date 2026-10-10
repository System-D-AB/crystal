using System;
using System.Collections.Concurrent;
using RocksDbSharp;
using RocksSnapshot = RocksDbSharp.Snapshot;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Reused <see cref="ReadOptions"/>, because of trap R4.2: <see cref="ReadOptions"/> is <b>not</b>
/// disposable — it is freed only by its finalizer. Allocating one per read turns a point read into a
/// native allocation plus a later finalizer run, so point reads share one pinned instance and scans
/// rent a bounded, snapshot-pinned one and return it. A test bounds allocations per point read (R9.5).
///
/// <para>Scans need their own options because they carry iterate bounds and a snapshot (R3.2); point
/// reads need no bounds, so one shared instance serves them all.</para>
/// </summary>
internal sealed class ReadOptionsPool
{
    // One shared, never-mutated options object for every point read on this database.
    private readonly ReadOptions _pointRead = new();

    // A small free-list of scan options, each reset (bounds + snapshot) on rent.
    private readonly ConcurrentBag<ReadOptions> _scanOptions = [];

    // A small free-list of snapshot-pinned point-read options (never have bounds set).
    private readonly ConcurrentBag<ReadOptions> _snapshotPointOptions = [];

    /// <summary>The shared point-read options. Not mutated, not returned — never give it bounds.</summary>
    internal ReadOptions PointRead => _pointRead;

    /// <summary>
    /// Rents a scan options object set to the given bounds and snapshot. The caller must
    /// <see cref="ReturnScan"/> it when the scan ends.
    /// </summary>
    internal ReadOptions RentScan(byte[] lowerBound, byte[] upperBound, RocksSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_scanOptions.TryTake(out var options))
        {
            options = new ReadOptions();
        }

        options.SetIterateLowerBound(lowerBound);
        options.SetIterateUpperBound(upperBound);
        options.SetSnapshot(snapshot);
        return options;
    }

    /// <summary>Returns scan options to the pool for reuse. Bounds are overwritten on the next rent.</summary>
    internal void ReturnScan(ReadOptions options)
    {
        if (options is not null)
        {
            _scanOptions.Add(options);
        }
    }

    /// <summary>Rents a point-read options object pinned to <paramref name="snapshot"/>.</summary>
    internal ReadOptions RentPointRead(RocksSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_snapshotPointOptions.TryTake(out var options))
        {
            options = new ReadOptions();
        }

        options.SetSnapshot(snapshot);
        return options;
    }

    /// <summary>Returns snapshot-pinned point-read options to the pool for reuse.</summary>
    internal void ReturnPointRead(ReadOptions options)
    {
        if (options is not null)
        {
            _snapshotPointOptions.Add(options);
        }
    }
}
