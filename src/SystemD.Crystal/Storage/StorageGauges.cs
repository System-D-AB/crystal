using System;
using System.Globalization;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Storage gauges per database, read from RocksDB properties (spec 001-2 R6.1). An unknown or
/// unparsable property counts as 0 and never throws (R6.3), so a metrics read can never fail a request.
/// </summary>
internal readonly record struct StorageGauges(
    long DeletesInActiveMemtable,
    long EstimatedLiveKeys,
    long EstimatedLiveDataSize,
    long PendingCompactionBytes,
    long SstFileCount,
    long SstFileSize,
    long BlockCacheUsage)
{
    private const string NumDeletesActiveMemtable = "rocksdb.num-deletes-active-mem-table";
    private const string EstimateNumKeys = "rocksdb.estimate-num-keys";
    private const string EstimateLiveDataSizeProp = "rocksdb.estimate-live-data-size";
    private const string EstimatePendingCompactionBytes = "rocksdb.estimate-pending-compaction-bytes";
    private const string NumFilesAtLevelPrefix = "rocksdb.num-files-at-level";
    private const string TotalSstFilesSize = "rocksdb.total-sst-files-size";
    private const string BlockCacheUsageProp = "rocksdb.block-cache-usage";

    /// <summary>Reads every gauge for one column family of <paramref name="instance"/>.</summary>
    internal static StorageGauges Read(RocksInstance instance, string family)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var db = instance.Db;
        var cf = instance.Family(family);

        return new StorageGauges(
            DeletesInActiveMemtable: ReadLong(db, NumDeletesActiveMemtable, cf),
            EstimatedLiveKeys: ReadLong(db, EstimateNumKeys, cf),
            EstimatedLiveDataSize: ReadLong(db, EstimateLiveDataSizeProp, cf),
            PendingCompactionBytes: ReadLong(db, EstimatePendingCompactionBytes, cf),
            SstFileCount: SstFileCountOf(db, cf),
            SstFileSize: ReadLong(db, TotalSstFilesSize, cf),
            BlockCacheUsage: ReadLong(db, BlockCacheUsageProp, cf));
    }

    /// <summary>A property summed over every column family of <paramref name="instance"/> (spec 007 R2.5).</summary>
    internal static long Sum(RocksInstance instance, string property)
    {
        long total = 0;
        foreach (var family in RocksInstance.FamilyOrder)
        {
            total += ReadLong(instance.Db, property, instance.Family(family));
        }

        return total;
    }

    /// <summary>SST files over every level and column family of <paramref name="instance"/>.</summary>
    internal static long SstFiles(RocksInstance instance)
    {
        long total = 0;
        foreach (var family in RocksInstance.FamilyOrder)
        {
            total += SstFileCountOf(instance.Db, instance.Family(family));
        }

        return total;
    }

    /// <summary>A database-wide property (stalls, the shared cache), read once, 0 if unknown (R6.3).</summary>
    internal static long Db(RocksInstance instance, string property)
    {
        try
        {
            var raw = instance.Db.GetProperty(property);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static long ReadLong(RocksDb db, string property, ColumnFamilyHandle cf)
    {
        try
        {
            var raw = db.GetProperty(property, cf);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        catch (Exception)
        {
            // R6.3: an unknown or unparsable property counts as 0, never throws.
            return 0;
        }
    }

    private static long SstFileCountOf(RocksDb db, ColumnFamilyHandle cf)
    {
        // RocksDB exposes a per-level file count; sum the levels. Seven levels is the default maximum,
        // and a missing level reads as 0 (R6.3), so summing a fixed range is safe.
        long total = 0;
        for (var level = 0; level < 7; level++)
        {
            total += ReadLong(db, NumFilesAtLevelPrefix + level.ToString(CultureInfo.InvariantCulture), cf);
        }

        return total;
    }
}
