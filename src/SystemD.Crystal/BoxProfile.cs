namespace SystemD.Crystal;

/// <summary>
/// A box's workload profile, which chooses its column family (box-layout § 3.1, § 8).
/// </summary>
public enum BoxProfile
{
    /// <summary>Mixed reads and writes: RocksDB defaults.</summary>
    General = 0,

    /// <summary>
    /// Key ranges with constant insert and delete (queues, moving indexes): compaction triggered by
    /// deletions, and a memtable flush when a scan steps over too many deleted entries.
    /// </summary>
    DeleteHeavy = 1,
}
