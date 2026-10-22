using System;
using System.Collections.Generic;

namespace SystemD.Crystal;

/// <summary>What <see cref="CrystalStore.Compact"/> compacts, and how the store is opened for it (spec 013 R4).</summary>
public sealed class CompactOptions
{
    /// <summary>The databases to compact, by name; null compacts every database of the store.</summary>
    public IReadOnlyCollection<string>? Databases { get; init; }

    /// <summary>
    /// The options the store is opened with while it is compacted (a logger factory, background threads); null opens it
    /// with the defaults. They must not set <see cref="CrystalOptions.Replication"/>: a compaction serves and follows
    /// nothing.
    /// </summary>
    public CrystalOptions? Store { get; init; }
}

/// <summary>The result of <see cref="CrystalStore.Compact"/>: one entry per database compacted, in the order compacted.</summary>
/// <param name="Databases">The databases compacted.</param>
public sealed record CompactionResult(IReadOnlyList<DatabaseCompaction> Databases);

/// <summary>One database's offline compaction (spec 013 R4.2).</summary>
/// <param name="Name">The database's name.</param>
/// <param name="BytesBefore">Its data on disk before, in bytes: table files and write-ahead logs (not RocksDB's diagnostic logs).</param>
/// <param name="BytesAfter">Its data on disk after, measured once the store has closed and the replaced files are gone.</param>
/// <param name="Elapsed">How long its compaction took.</param>
public sealed record DatabaseCompaction(string Name, long BytesBefore, long BytesAfter, TimeSpan Elapsed);
