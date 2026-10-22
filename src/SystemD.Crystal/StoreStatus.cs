using System;
using System.Collections.Generic;

namespace SystemD.Crystal;

/// <summary>
/// A snapshot of a store, from <see cref="CrystalStore.Status"/> (spec 007 R4): what a product's health or status
/// endpoint shows. Built from memory and RocksDB properties only; a database closing while it is built is left out.
/// </summary>
/// <param name="NodeId">The node's identity (<c>node.id</c>).</param>
/// <param name="Format">The store's format (<c>crystal.format</c>).</param>
/// <param name="Durability">The store's default durability (spec 006).</param>
/// <param name="SyncInterval">The interval of <see cref="DurabilityMode.Interval"/>.</param>
/// <param name="BlockCacheUsage">Bytes in the store's shared block cache.</param>
/// <param name="BlockCacheCapacity">Size of the store's shared block cache.</param>
/// <param name="Databases">Every open database.</param>
/// <param name="Backup">The backup running now, or null when none is (spec 010 R7.3).</param>
public sealed record StoreStatus(
    string NodeId,
    string Format,
    DurabilityMode Durability,
    TimeSpan SyncInterval,
    long BlockCacheUsage,
    long BlockCacheCapacity,
    IReadOnlyList<DatabaseStatus> Databases,
    BackupStatus? Backup = null);

/// <summary>A running backup, in a <see cref="StoreStatus"/> (spec 010 R7.3).</summary>
/// <param name="Destination">The backup root.</param>
/// <param name="DatabasesDone">Databases finished (copied or omitted).</param>
/// <param name="DatabasesTotal">Databases in the backup, read at its start.</param>
/// <param name="BytesCopied">Bytes copied so far, of finished databases: new files only.</param>
public sealed record BackupStatus(string Destination, int DatabasesDone, int DatabasesTotal, long BytesCopied);

/// <summary>One open database in a <see cref="StoreStatus"/>.</summary>
/// <param name="Id">The database id.</param>
/// <param name="Name">The database's current name.</param>
/// <param name="Boxes">Live boxes.</param>
/// <param name="Failed">Whether the database has failed and refuses writes (spec 006 R5).</param>
/// <param name="FailedCause">What failed, if it has.</param>
/// <param name="LastWalSync">When its WAL was last synced since it opened, or null if not yet.</param>
/// <param name="Storage">RocksDB's view of it.</param>
public sealed record DatabaseStatus(
    uint Id,
    string Name,
    int Boxes,
    bool Failed,
    string? FailedCause,
    DateTimeOffset? LastWalSync,
    StorageStatus Storage)
{
    /// <summary>
    /// The database's latest committed position: the sequence of its last committed write (spec 013 R5). It is the
    /// position replication speaks in, the same number a standby's slot acknowledges, and it is known with no standby
    /// attached. It only moves forward while the database is open; a commit that writes nothing does not move it.
    /// </summary>
    public long LatestPosition { get; init; }
}

/// <summary>RocksDB's view of one database, from its properties, summed over its column families.</summary>
/// <param name="MemtableDeletes">Deletions in memtables, not yet flushed.</param>
/// <param name="EstimatedKeys">RocksDB's estimate of live keys.</param>
/// <param name="LiveDataSize">RocksDB's estimate of live data, in bytes.</param>
/// <param name="PendingCompactionBytes">Bytes compaction is behind by.</param>
/// <param name="SstFiles">SST files over every level.</param>
/// <param name="SstSize">Size of every SST file, in bytes.</param>
/// <param name="WriteStopped">Whether RocksDB has stopped writes (flushes or compaction are behind).</param>
public sealed record StorageStatus(
    long MemtableDeletes,
    long EstimatedKeys,
    long LiveDataSize,
    long PendingCompactionBytes,
    long SstFiles,
    long SstSize,
    bool WriteStopped)
{
    /// <summary>SST files at each level, 0 to 6, summed over the database's column families (spec 012 R11).</summary>
    public IReadOnlyList<long> FilesPerLevel { get; init; } = [];

    /// <summary>Bytes in the active memtables, summed over the column families (spec 012 R11).</summary>
    public long ActiveMemtableBytes { get; init; }

    /// <summary>The rate, in bytes per second, RocksDB is slowing writes to; 0 when it is not (spec 012 R11).</summary>
    public long DelayedWriteRate { get; init; }

    /// <summary>Equal when every figure is, <see cref="FilesPerLevel"/> compared element by element.</summary>
    public bool Equals(StorageStatus? other) =>
        other is not null
        && MemtableDeletes == other.MemtableDeletes
        && EstimatedKeys == other.EstimatedKeys
        && LiveDataSize == other.LiveDataSize
        && PendingCompactionBytes == other.PendingCompactionBytes
        && SstFiles == other.SstFiles
        && SstSize == other.SstSize
        && WriteStopped == other.WriteStopped
        && ActiveMemtableBytes == other.ActiveMemtableBytes
        && DelayedWriteRate == other.DelayedWriteRate
        && System.Linq.Enumerable.SequenceEqual(FilesPerLevel, other.FilesPerLevel);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MemtableDeletes);
        hash.Add(EstimatedKeys);
        hash.Add(LiveDataSize);
        hash.Add(SstFiles);
        hash.Add(SstSize);
        hash.Add(ActiveMemtableBytes);
        foreach (var files in FilesPerLevel)
        {
            hash.Add(files);
        }

        return hash.ToHashCode();
    }
}
