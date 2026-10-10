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
}
