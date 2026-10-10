using System;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Offline, full compaction (spec 001-2 R7): compact every column family of a database end to end,
/// dropping every tombstone. A store's offline operation runs this over every database. It is refused
/// while the store is open (the store lock, held by <c>005-store-and-databases</c>); this type does the
/// work on an instance the caller has already taken exclusively.
/// </summary>
internal static class OfflineCompaction
{
    /// <summary>Compacts every column family of <paramref name="instance"/> from first key to last.</summary>
    internal static void CompactEverything(RocksInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        foreach (var family in RocksInstance.FamilyOrder)
        {
            // Null bounds mean "the whole key range" to RocksDB's CompactRange.
            instance.Db.CompactRange((byte[]?)null, (byte[]?)null, instance.Family(family));
        }
    }
}
