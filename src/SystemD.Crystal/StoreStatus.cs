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
public sealed record StoreStatus(
    string NodeId,
    string Format,
    DurabilityMode Durability,
    TimeSpan SyncInterval,
    long BlockCacheUsage,
    long BlockCacheCapacity,
    IReadOnlyList<DatabaseStatus> Databases);

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
    StorageStatus Storage);

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
    bool WriteStopped);
