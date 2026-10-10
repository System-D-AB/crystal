using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The column-family options for each workload profile (spec 001-2 design § 3). The <c>DeleteHeavy</c>
/// values — compaction-on-deletions and the memtable scan-flush triggers — come from the products'
/// measurements, where they took a queue's poll cost from growing with uptime to flat (R9.3).
/// </summary>
internal static class ProfileOptions
{
    // DeleteHeavy tuning (design § 3). A window of 1024 keys, 512 deletes to trigger, ratio 0.5;
    // flush the memtable at 1000 skipped entries in one operation, or 16 on average.
    private const ulong DeletionWindow = 1024;
    private const ulong DeletionTrigger = 512;
    private const double DeletionRatio = 0.5;
    private const uint ScanFlushPerOperation = 1000;
    private const uint ScanFlushAverage = 16;

    /// <summary>Builds the column-family options for <paramref name="profile"/>. The caller owns the result.</summary>
    internal static ColumnFamilyOptions For(BoxProfile profile)
    {
        var options = new ColumnFamilyOptions();
        if (profile == BoxProfile.DeleteHeavy)
        {
            NativeRocks.CompactOnDeletions(options, DeletionWindow, DeletionTrigger, DeletionRatio);
            NativeRocks.MemtableScanFlushTriggers(options, ScanFlushPerOperation, ScanFlushAverage);
        }

        // General: RocksDB defaults, nothing to set.
        return options;
    }
}
