using System;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Every native RocksDB call the managed binding does not wrap, in one place (spec 001-2 R2.1). Nothing
/// else in Crystal calls <c>Native.Instance</c> or declares a native import. Each method here follows
/// one pattern (R2.2): check the native error output and throw a typed <see cref="RocksException"/>;
/// keep the managed owner of every handle alive across the call (<see cref="GC.KeepAlive(object?)"/>);
/// and free every native object it creates, even on failure.
///
/// <para><b>Deviation from the spec, recorded (2026-10-09).</b> Requirement R2.4 and design § 2 state
/// that <c>rocksdb_write_buffer_manager_create</c> is absent from the binding's <c>Native</c> class and
/// must be resolved from the loaded native library through a function pointer. Against the pinned
/// package (<c>RocksDB</c> 11.8.1.5032) that is not so: the binding exposes
/// <c>rocksdb_write_buffer_manager_create_with_cache</c> and <c>_destroy</c> as managed delegates on
/// <c>Native</c>. Crystal therefore calls the managed delegate, which needs no
/// <c>NativeLibrary.GetExport</c>. The surface test still asserts the member exists (R1.3). The spec
/// is being amended by a dated addendum rather than left to disagree with the code (playbook § 3).</para>
/// </summary>
internal static class NativeRocks
{
    /// <summary>The RocksDB version of the pinned binding package, recorded in each backup set (spec 010 R4.2).</summary>
    internal const string RocksDbVersion = "11.8.1";

    private static Native N => Native.Instance;

    /// <summary>
    /// Adds a compact-on-deletion collector to a column family's options, so an SST file whose window
    /// of <paramref name="windowSize"/> keys holds at least <paramref name="deletionTrigger"/> deletes,
    /// or whose deletion ratio exceeds <paramref name="deletionRatio"/>, is compacted (the
    /// <c>DeleteHeavy</c> profile). Synchronous; sets an option, allocates nothing of its own.
    /// </summary>
    internal static void CompactOnDeletions(
        ColumnFamilyOptions options,
        ulong windowSize,
        ulong deletionTrigger,
        double deletionRatio)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            N.rocksdb_options_add_compact_on_deletion_collector_factory_del_ratio(
                options.Handle,
                (UIntPtr)windowSize,
                (UIntPtr)deletionTrigger,
                deletionRatio);
        }
        finally
        {
            GC.KeepAlive(options);
        }
    }

    /// <summary>
    /// Sets the memtable scan-flush triggers on a column family's options: flush the memtable when one
    /// <c>Seek</c>/<c>Next</c> steps over <paramref name="perOperation"/> hidden entries, or when the
    /// average skip over many small reads exceeds <paramref name="average"/>. Keeps a lightly-written
    /// range's head from filling with tombstones.
    /// </summary>
    internal static void MemtableScanFlushTriggers(ColumnFamilyOptions options, uint perOperation, uint average)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            N.rocksdb_options_set_memtable_op_scan_flush_trigger(options.Handle, perOperation);
            N.rocksdb_options_set_memtable_avg_op_scan_flush_trigger(options.Handle, average);
        }
        finally
        {
            GC.KeepAlive(options);
        }
    }

    /// <summary>
    /// Cheap estimated on-disk sizes of each of <paramref name="ranges"/> within one column family,
    /// including data still in the memtable (R9.4). Allocates and frees a size-approximation options
    /// struct. Synchronous native work.
    /// </summary>
    internal static unsafe ulong[] ApproximateSizes(
        RocksDb db,
        ColumnFamilyHandle family,
        ReadOnlySpan<(byte[] From, byte[] To)> ranges)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(family);

        var count = ranges.Length;
        var sizes = new ulong[count == 0 ? 1 : count];
        if (count == 0)
        {
            return [];
        }

        // Pin every start/limit key so the native call can read them, and gather their pointers/lengths.
        var startPtrs = new IntPtr[count];
        var startLens = new UIntPtr[count];
        var limitPtrs = new IntPtr[count];
        var limitLens = new UIntPtr[count];
        var handles = new GCHandle[count * 2];

        var options = N.rocksdb_size_approximation_options_create();
        try
        {
            N.rocksdb_size_approximation_options_set_include_memtables(options, 1);
            N.rocksdb_size_approximation_options_set_include_files(options, 1);

            for (var i = 0; i < count; i++)
            {
                var from = ranges[i].From;
                var to = ranges[i].To;
                handles[(i * 2) + 0] = GCHandle.Alloc(from, GCHandleType.Pinned);
                handles[(i * 2) + 1] = GCHandle.Alloc(to, GCHandleType.Pinned);
                startPtrs[i] = handles[(i * 2) + 0].AddrOfPinnedObject();
                startLens[i] = (UIntPtr)from.Length;
                limitPtrs[i] = handles[(i * 2) + 1].AddrOfPinnedObject();
                limitLens[i] = (UIntPtr)to.Length;
            }

            fixed (IntPtr* startKeys = startPtrs)
            fixed (UIntPtr* startKeyLens = startLens)
            fixed (IntPtr* limitKeys = limitPtrs)
            fixed (UIntPtr* limitKeyLens = limitLens)
            fixed (ulong* sizesPtr = sizes)
            {
                N.rocksdb_approximate_sizes_cf_with_options(
                    db.Handle,
                    family.Handle,
                    options,
                    count,
                    (IntPtr)startKeys,
                    (IntPtr)startKeyLens,
                    (IntPtr)limitKeys,
                    (IntPtr)limitKeyLens,
                    (IntPtr)sizesPtr,
                    out var error);
                RocksException.ThrowIfError(error);
            }

            return sizes;
        }
        finally
        {
            N.rocksdb_size_approximation_options_destroy(options);
            foreach (var h in handles)
            {
                if (h.IsAllocated)
                {
                    h.Free();
                }
            }

            GC.KeepAlive(db);
            GC.KeepAlive(family);
        }
    }

    /// <summary>
    /// Whether <paramref name="options"/> syncs the WAL before a write returns, read back from the native object
    /// (spec 006 R7: lets a test see what was handed to RocksDB, not what Crystal meant to hand it).
    /// </summary>
    internal static bool IsSync(WriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return N.rocksdb_writeoptions_get_sync(options.Handle) != 0;
        }
        finally
        {
            GC.KeepAlive(options);
        }
    }

    /// <summary>
    /// Flushes the memtables of <paramref name="families"/> together (<c>rocksdb_flush_cfs</c>); the managed binding
    /// flushes only the default family. With every family flushed, no memtable pins the oldest WAL file, so it can
    /// roll and be archived. Synchronous native work; waits for the flush when <paramref name="options"/> says so.
    /// </summary>
    internal static void FlushColumnFamilies(RocksDb db, FlushOptions options, ColumnFamilyHandle[] families)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(families);
        var handles = new IntPtr[families.Length];
        for (var i = 0; i < families.Length; i++)
        {
            handles[i] = families[i].Handle;
        }

        try
        {
            N.rocksdb_flush_cfs(db.Handle, options.Handle, handles, handles.Length, out var error);
            RocksException.ThrowIfError(error);
        }
        finally
        {
            GC.KeepAlive(db);
            GC.KeepAlive(options);
            GC.KeepAlive(families);
        }
    }

    /// <summary>
    /// Flushes the write-ahead log, optionally synced (<paramref name="sync"/>) — the native call for
    /// the interval durability mode's timed WAL sync (R2.3). Synchronous native work.
    /// </summary>
    internal static void FlushWal(RocksDb db, bool sync)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            N.rocksdb_flush_wal(db.Handle, sync ? (byte)1 : (byte)0, out var error);
            RocksException.ThrowIfError(error);
        }
        finally
        {
            GC.KeepAlive(db);
        }
    }

    /// <summary>
    /// Creates a native write-buffer manager of <paramref name="bytes"/>, costed against
    /// <paramref name="cache"/>, so every database of a store shares one write-buffer budget (R5.2).
    /// The returned handle is owned by the caller and freed with <see cref="DestroyWriteBufferManager"/>.
    /// </summary>
    internal static IntPtr CreateWriteBufferManager(ulong bytes, Cache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        try
        {
            // allow_stall = 0: a full budget slows writers through back-pressure, it never blocks forever.
            return N.rocksdb_write_buffer_manager_create_with_cache((UIntPtr)bytes, cache.Handle, 0);
        }
        finally
        {
            GC.KeepAlive(cache);
        }
    }

    /// <summary>Points a store's database options at a shared write-buffer manager handle (R5.2).</summary>
    internal static void SetWriteBufferManager(DbOptions options, IntPtr manager)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (manager == IntPtr.Zero)
        {
            throw new ArgumentException("Write-buffer manager handle is null.", nameof(manager));
        }

        try
        {
            N.rocksdb_options_set_write_buffer_manager(options.Handle, manager);
        }
        finally
        {
            GC.KeepAlive(options);
        }
    }

    /// <summary>Frees a write-buffer manager created by <see cref="CreateWriteBufferManager"/>.</summary>
    internal static void DestroyWriteBufferManager(IntPtr manager)
    {
        if (manager != IntPtr.Zero)
        {
            N.rocksdb_write_buffer_manager_destroy(manager);
        }
    }

    // ------------------------------------------------------------------ BackupEngine (spec 010 design § 3)

    /// <summary>
    /// Opens a RocksDB <c>BackupEngine</c> on <paramref name="directory"/> (created if missing): table files shared and
    /// named by their checksum, every file synced, no WAL copied (the backup flushes first), at most
    /// <paramref name="backgroundOperations"/> copy threads and <paramref name="rateLimitBytesPerSecond"/> (0: none).
    /// An interrupted backup left in the directory is cleaned up here. The engine is freed with
    /// <see cref="BackupEngineClose"/>. Synchronous native work.
    /// </summary>
    internal static IntPtr BackupEngineOpen(string directory, Env env, int backgroundOperations, ulong rateLimitBytesPerSecond)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(env);
        var options = N.rocksdb_backup_engine_options_create(directory);
        try
        {
            N.rocksdb_backup_engine_options_set_share_table_files(options, 1);
            N.rocksdb_backup_engine_options_set_share_files_with_checksum(options, 1);
            N.rocksdb_backup_engine_options_set_sync(options, 1);
            N.rocksdb_backup_engine_options_set_backup_log_files(options, 0);
            N.rocksdb_backup_engine_options_set_max_background_operations(options, Math.Max(1, backgroundOperations));
            N.rocksdb_backup_engine_options_set_backup_rate_limit(options, rateLimitBytesPerSecond);
            var engine = N.rocksdb_backup_engine_open_opts(options, env.Handle, out var error);
            RocksException.ThrowIfError(error);
            return engine;
        }
        finally
        {
            N.rocksdb_backup_engine_options_destroy(options);
            GC.KeepAlive(env);
        }
    }

    /// <summary>
    /// Backs <paramref name="db"/> up into <paramref name="engine"/>, flushing its memtables first (so every
    /// acknowledged commit is in it, whatever its durability), with <paramref name="metadata"/> as the backup's
    /// application metadata. Returns the new backup's id. Synchronous native work; <see cref="BackupStop"/> from another
    /// thread makes it fail promptly.
    /// </summary>
    internal static unsafe uint BackupCreate(IntPtr engine, RocksDb db, byte[] metadata)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(metadata);
        var options = N.rocksdb_create_backup_options_create();
        try
        {
            N.rocksdb_create_backup_options_set_flush_before_backup(options, 1);
            uint id = 0;
            fixed (byte* data = metadata)
            {
                N.rocksdb_backup_engine_create_new_backup_with_metadata(
                    engine, db.Handle, options, (IntPtr)data, (UIntPtr)metadata.Length, (IntPtr)(&id), out var error);
                RocksException.ThrowIfError(error);
            }

            return id;
        }
        finally
        {
            N.rocksdb_create_backup_options_destroy(options);
            GC.KeepAlive(db);
        }
    }

    /// <summary>Asks a running backup on <paramref name="engine"/> to stop; it then fails. The engine takes no further backups.</summary>
    internal static void BackupStop(IntPtr engine) => N.rocksdb_backup_engine_stop_backup(engine);

    /// <summary>One backup an engine holds: its id, creation time (Unix seconds), size, file count and application metadata.</summary>
    internal readonly record struct BackupEntry(uint Id, long Timestamp, ulong Size, uint Files, byte[] Metadata);

    /// <summary>The backups <paramref name="engine"/> holds, oldest first. Synchronous native work.</summary>
    internal static BackupEntry[] BackupList(IntPtr engine)
    {
        var info = N.rocksdb_backup_engine_get_backup_info(engine);
        try
        {
            var count = N.rocksdb_backup_engine_info_count(info);
            var entries = new BackupEntry[count];
            for (var i = 0; i < count; i++)
            {
                var metadata = N.rocksdb_backup_engine_info_app_metadata(info, i, out var length);
                var bytes = new byte[(int)length];
                if (metadata != IntPtr.Zero && bytes.Length > 0)
                {
                    Marshal.Copy(metadata, bytes, 0, bytes.Length); // owned by the info, copied before it is freed
                }

                entries[i] = new BackupEntry(
                    N.rocksdb_backup_engine_info_backup_id(info, i),
                    N.rocksdb_backup_engine_info_timestamp(info, i),
                    N.rocksdb_backup_engine_info_size(info, i),
                    N.rocksdb_backup_engine_info_number_files(info, i),
                    bytes);
            }

            return entries;
        }
        finally
        {
            N.rocksdb_backup_engine_info_destroy(info);
        }
    }

    /// <summary>
    /// Checks that every file of backup <paramref name="backupId"/> is present at its recorded size (the C API does not
    /// read their contents). Throws <see cref="RocksException"/> otherwise. Synchronous native work.
    /// </summary>
    internal static void BackupVerify(IntPtr engine, uint backupId)
    {
        N.rocksdb_backup_engine_verify_backup(engine, backupId, out var error);
        RocksException.ThrowIfError(error);
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> backups of <paramref name="engine"/>. Synchronous native work.</summary>
    internal static void BackupPurge(IntPtr engine, uint keep)
    {
        N.rocksdb_backup_engine_purge_old_backups(engine, keep, out var error);
        RocksException.ThrowIfError(error);
    }

    /// <summary>Restores backup <paramref name="backupId"/> into <paramref name="directory"/>, its WAL folder the same. Synchronous native work.</summary>
    internal static void BackupRestore(IntPtr engine, uint backupId, string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var options = N.rocksdb_restore_options_create();
        try
        {
            N.rocksdb_restore_options_set_keep_log_files(options, 0);
            N.rocksdb_backup_engine_restore_db_from_backup(engine, directory, directory, options, backupId, out var error);
            RocksException.ThrowIfError(error);
        }
        finally
        {
            N.rocksdb_restore_options_destroy(options);
        }
    }

    /// <summary>Closes an engine opened by <see cref="BackupEngineOpen"/>.</summary>
    internal static void BackupEngineClose(IntPtr engine)
    {
        if (engine != IntPtr.Zero)
        {
            N.rocksdb_backup_engine_close(engine);
        }
    }

    /// <summary>Reads every table file of <paramref name="db"/> and checks its block checksums (deep verify). Synchronous native work.</summary>
    internal static void VerifyChecksums(RocksDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            N.rocksdb_verify_checksum(db.Handle, out var error);
            RocksException.ThrowIfError(error);
        }
        finally
        {
            GC.KeepAlive(db);
        }
    }
}
