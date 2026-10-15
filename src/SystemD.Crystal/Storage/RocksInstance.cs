using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RocksDbSharp;
using RocksSnapshot = RocksDbSharp.Snapshot;

namespace SystemD.Crystal.Storage;

/// <summary>
/// One database: a RocksDB instance opened with Crystal's fixed column families, checked on open
/// (spec 001-2 R3.1, box-layout § 3.1). Holds the managed features Crystal uses — <c>Get</c>, write
/// batches with a sync choice, snapshots, bounded iterators — and sets the binding's known-safe
/// options (WAL time-to-live and size, tolerant tail recovery, WAL compression off per trap R4.5).
///
/// <para>Internal only. The store, databases and boxes (<c>005</c>, <c>002</c>) call this type.</para>
/// </summary>
internal sealed class RocksInstance : IDisposable
{
    /// <summary>
    /// The fixed column-family order, checked on open (box-layout § 3.1). The order never changes;
    /// a family may only be appended. <c>default</c> holds the catalogue (box 0); <c>general</c> and
    /// <c>delete_heavy</c> hold the keys of boxes by profile.
    /// </summary>
    internal static readonly string[] FamilyOrder = ["default", "general", "delete_heavy"];

    private readonly RocksDb _db;
    private readonly Dictionary<string, ColumnFamilyHandle> _families;

    // Created once and reused (spec 006 R3.2): WriteOptions, like ReadOptions, is freed by its finalizer (trap R4.2).
    private readonly WriteOptions _syncedWrite = new WriteOptions().SetSync(true);
    private readonly WriteOptions _unsyncedWrite = new WriteOptions().SetSync(false);

    // Set after an unsynced write lands, cleared just before a sync (spec 006 design § 2). Setting it after the write
    // means a sync that clears it has either seen the write or leaves the flag set for the next one.
    private int _unsynced;          // any Interval or Buffered write since the last sync
    private int _intervalPending;   // an Interval write since the last sync: what the store's timer looks for

    // Held from clearing the flags to the end of the sync, so a second caller that finds them clear has waited for the
    // sync that cleared them. Never held by a write: strict commits still share RocksDB's group commit (R3.1).
    private readonly object _syncLock = new();
    private bool _disposed;

    private readonly StoreResources _resources;

    private RocksInstance(RocksDb db, Dictionary<string, ColumnFamilyHandle> families, StoreResources resources)
    {
        _db = db;
        _families = families;
        _resources = resources;
        ReadOptions = new ReadOptionsPool();
    }

    /// <summary>The reused read-options for this database (trap R4.2).</summary>
    internal ReadOptionsPool ReadOptions { get; }

    /// <summary>The underlying instance, for the WAL/replication layer (<c>WalAccess</c>) in this namespace.</summary>
    internal RocksDb Db => _db;

    /// <summary>
    /// Opens (creating if missing) the database at <paramref name="path"/> with the fixed family order.
    /// <paramref name="resources"/> supplies the store's shared cache, environment and write-buffer
    /// budget. <paramref name="walTtlSeconds"/> and <paramref name="walSizeLimitMb"/> bound the WAL;
    /// tolerant tail recovery is set, and WAL compression is forced off (R4.5).
    /// </summary>
    internal static RocksInstance Open(
        string path,
        StoreResources resources,
        IReadOnlyDictionary<string, BoxProfile> familyProfiles,
        ulong walTtlSeconds,
        ulong walSizeLimitMb)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(familyProfiles);

        var dbOptions = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true)
            .SetWalTtlSeconds(walTtlSeconds)
            .SetWalSizeLimitMB(walSizeLimitMb)
            .SetWalRecoveryMode(Recovery.TolerateCorruptedTailRecords)
            // Trap R4.5: WAL compression off, so the log can be decoded for replication's tail,
            // trimming and reconciliation reports.
            .SetWalCompression(Compression.No);

        resources.ApplyTo(dbOptions);

        var families = new ColumnFamilies(ProfileOptions.For(ProfileOf("default", familyProfiles)));
        foreach (var name in FamilyOrder.Skip(1))
        {
            families.Add(name, ProfileOptions.For(ProfileOf(name, familyProfiles)));
        }

        var db = RocksDb.Open(dbOptions, path, families);

        // Resolve and keep the handles in the fixed order; fail loudly if one is missing (R3.1).
        var handles = new Dictionary<string, ColumnFamilyHandle>(StringComparer.Ordinal);
        foreach (var name in FamilyOrder)
        {
            handles[name] = db.GetColumnFamily(name);
        }

        return new RocksInstance(db, handles, resources);
    }

    /// <summary>
    /// Opens the database at <paramref name="path"/> read-only, with the fixed family order (spec 010 deep verify): a
    /// restored backup is checked without anything being written to it. Synchronous native work.
    /// </summary>
    internal static RocksInstance OpenReadOnly(string path, StoreResources resources, IReadOnlyDictionary<string, BoxProfile> familyProfiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(familyProfiles);

        var dbOptions = new DbOptions();
        resources.ApplyTo(dbOptions);
        var families = new ColumnFamilies(ProfileOptions.For(ProfileOf("default", familyProfiles)));
        foreach (var name in FamilyOrder.Skip(1))
        {
            families.Add(name, ProfileOptions.For(ProfileOf(name, familyProfiles)));
        }

        var db = RocksDb.OpenReadOnly(dbOptions, path, families, errIfLogFileExists: false);
        var handles = new Dictionary<string, ColumnFamilyHandle>(StringComparer.Ordinal);
        foreach (var name in FamilyOrder)
        {
            handles[name] = db.GetColumnFamily(name);
        }

        return new RocksInstance(db, handles, resources);
    }

    private static BoxProfile ProfileOf(string family, IReadOnlyDictionary<string, BoxProfile> profiles) =>
        family switch
        {
            "delete_heavy" => BoxProfile.DeleteHeavy,
            _ => profiles.TryGetValue(family, out var p) ? p : BoxProfile.General,
        };

    /// <summary>The handle for a column family by name, or throws if it is not one of the fixed families.</summary>
    internal ColumnFamilyHandle Family(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_families.TryGetValue(name, out var handle))
        {
            throw new ArgumentException($"'{name}' is not one of Crystal's column families.", nameof(name));
        }

        return handle;
    }

    /// <summary>A point read of <paramref name="key"/> in <paramref name="family"/>, or <c>null</c>. Shared read options (R4.2).</summary>
    internal byte[]? Get(string family, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _db.Get(key, Family(family), ReadOptions.PointRead);
    }

    /// <summary>
    /// Forces a memtable flush, rolling the active WAL. Used by tests (spec 003 T2) to make WAL retention observable
    /// deterministically: with retention on, a rolled WAL is archived rather than deleted. Synchronous native work.
    /// </summary>
    internal void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // FlushOptions is not disposable (freed by its finalizer), like WriteOptions/ReadOptions.
        // Every family, not only the default one the binding's Flush covers: a family left in memory would pin the
        // oldest WAL file, and a caller flushing to roll the WAL would wait for nothing.
        var options = new FlushOptions().SetWaitForFlush(true);
        NativeRocks.FlushColumnFamilies(_db, options, [.. _families.Values]);
    }

    /// <summary>
    /// Test hook (null in production, spec 006 R7.5): called just before a write is handed to RocksDB, with whether the
    /// write options it is handed sync the WAL, read back from the native object. A test throws from it to stand in for
    /// a failed write or sync.
    /// </summary>
    internal Action<bool>? OnBeforeWrite { get; set; }

    /// <summary>Test hook (null in production, spec 006 R7.5): called just before a WAL sync; a test throws from it.</summary>
    internal Action? OnBeforeSync { get; set; }

    /// <summary>The number of WAL syncs this instance issued outside a synced write (spec 006 R7.4).</summary>
    internal int SyncCount => Volatile.Read(ref _syncCount);

    private int _syncCount;

    /// <summary>Commits <paramref name="batch"/>, synced per <paramref name="sync"/> (R3.3). An empty batch is the caller's concern.</summary>
    internal void Write(WriteBatch batch, bool sync) => Write(batch, sync ? DurabilityMode.Strict : DurabilityMode.Buffered);

    /// <summary>
    /// The one commit point (spec 004 T10, 006 design § 2). <see cref="DurabilityMode.Strict"/> syncs the WAL before it
    /// returns, joining RocksDB's group commit with any concurrent synced write: no lock is held here. The other modes
    /// hand the batch to the OS and mark the WAL unsynced.
    /// </summary>
    internal void Write(WriteBatch batch, DurabilityMode durability)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var synced = durability == DurabilityMode.Strict;
        var options = synced ? _syncedWrite : _unsyncedWrite;
        if (OnBeforeWrite is { } hook)
        {
            hook(NativeRocks.IsSync(options));   // what RocksDB will be told, read back from the native object
        }

        _db.Write(batch, options);

        // After the write, the batch's representation carries the sequence RocksDB assigned and is byte-identical to
        // the WAL record. The replication source captures it into its in-memory tail (spec 003 R3.5, R12.3: shipped
        // whatever the durability). Null in a store with no replication, so it costs nothing.
        if (OnCommitted is { } captured)
        {
            captured(batch.ToBytes());
        }

        if (synced)
        {
            return;
        }

        Volatile.Write(ref _unsynced, 1);
        if (durability == DurabilityMode.Interval)
        {
            Volatile.Write(ref _intervalPending, 1);
        }
    }

    /// <summary>
    /// Called after each successful write with the committed batch's representation (<c>[8B LE seq][4B LE count]…</c>),
    /// for the replication tail to capture (spec 003 R3.5). Set by the replication source for a replicated database;
    /// null otherwise, so a non-replicated store pays nothing.
    /// </summary>
    internal Action<byte[]>? OnCommitted { get; set; }

    /// <summary>Whether an <see cref="DurabilityMode.Interval"/> write is waiting for the store's timer.</summary>
    internal bool IntervalPending => Volatile.Read(ref _intervalPending) != 0;

    /// <summary>Syncs the WAL if an <see cref="DurabilityMode.Interval"/> write is not yet synced (the timer's tick).</summary>
    internal bool SyncIfIntervalPending()
    {
        lock (_syncLock)
        {
            ThrowIfSyncFailed();
            if (Interlocked.Exchange(ref _intervalPending, 0) == 0)
            {
                return false;
            }

            Interlocked.Exchange(ref _unsynced, 0);
            SyncWal();
            return true;
        }
    }

    /// <summary>
    /// Syncs the WAL if any write since the last sync was not synced (<c>SyncAll</c>, closing). Returns only once every
    /// write flagged before the call is synced: a sync another caller started first is waited for, not assumed done
    /// (independent review of 006, finding 1). The caller checks afterwards whether that sync failed.
    /// </summary>
    internal bool SyncIfUnsynced()
    {
        lock (_syncLock)
        {
            ThrowIfSyncFailed();
            if (Interlocked.Exchange(ref _unsynced, 0) == 0)
            {
                return false;
            }

            Interlocked.Exchange(ref _intervalPending, 0);
            SyncWal();
            return true;
        }
    }

    /// <summary>
    /// Syncs the WAL. Called under <c>_syncLock</c>. A failure is kept there, so a caller who waited for this sync and
    /// finds the flags clear learns it failed (spec 006 R5; found by spec 007's timing: the failure used to be recorded
    /// by the caller only after the lock was released).
    /// </summary>
    private void SyncWal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            OnBeforeSync?.Invoke();
            NativeRocks.FlushWal(_db, sync: true);
        }
        catch (Exception ex)
        {
            _syncFailure ??= ex;
            throw;
        }

        Interlocked.Increment(ref _syncCount);
    }

    /// <summary>Throws if a sync of this WAL has failed: nothing after it can be called synced. Under <c>_syncLock</c>.</summary>
    private void ThrowIfSyncFailed()
    {
        if (_syncFailure is { } failure)
        {
            throw new IOException("An earlier WAL sync of this database failed; no later sync is trusted.", failure);
        }
    }

    private Exception? _syncFailure;

    /// <summary>Opens a point-in-time snapshot of this database (R3.2). Dispose it when the read is done.</summary>
    internal RocksSnapshot CreateSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _db.CreateSnapshot();
    }

    /// <summary>Opens a bounded, limited, snapshot-pinned forward scan of a key range (R3.2, R4.4).</summary>
    internal BoundedIterator Scan(string family, RocksSnapshot snapshot, byte[] lowerBound, byte[] upperBound, int limit) =>
        BoundedIterator.Open(_db, Family(family), ReadOptions, snapshot, lowerBound, upperBound, limit, _resources);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _db.Dispose();
    }
}
