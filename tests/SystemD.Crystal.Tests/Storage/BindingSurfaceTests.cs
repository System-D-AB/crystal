using System;
using System.Linq;
using System.Reflection;
using RocksDbSharp;
using Xunit;

namespace SystemD.Crystal.Tests.Storage;

/// <summary>
/// Spec 001-2 R1.3 — the binding surface test. It is the authority for what Crystal depends on in the
/// <c>RocksDB</c> package: the pinned version, every native entry point Crystal calls (R2), and the
/// write-buffer-manager exports (R2.4, as amended — see <c>NativeRocks</c>). A package change that
/// removes any of them fails this test, so a missing function is a failed build, not a production crash.
///
/// <para>Adding a native call to Crystal means adding its name to <see cref="NativeEntryPoints"/>.</para>
/// </summary>
public sealed class BindingSurfaceTests
{
    private const string PinnedVersion = "11.8.1.5032";

    /// <summary>Every native entry point Crystal calls through <c>NativeRocks</c> (design § 5).</summary>
    private static readonly string[] NativeEntryPoints =
    [
        "rocksdb_options_add_compact_on_deletion_collector_factory_del_ratio",
        "rocksdb_options_set_memtable_op_scan_flush_trigger",
        "rocksdb_options_set_memtable_avg_op_scan_flush_trigger",
        "rocksdb_approximate_sizes_cf_with_options",
        "rocksdb_size_approximation_options_create",
        "rocksdb_size_approximation_options_set_include_memtables",
        "rocksdb_size_approximation_options_set_include_files",
        "rocksdb_size_approximation_options_destroy",
        "rocksdb_flush_wal",
        "rocksdb_flush_cfs",
        "rocksdb_writeoptions_get_sync",
        "rocksdb_options_set_write_buffer_manager",
        "rocksdb_write_buffer_manager_create_with_cache",
        "rocksdb_write_buffer_manager_destroy",
        "rocksdb_free",
    ];

    /// <summary>Every managed member Crystal uses on the binding's public types (R1.3, R3).</summary>
    private static readonly (string Type, string Member)[] ManagedMembers =
    [
        ("RocksDbSharp.RocksDb", "Open"),
        ("RocksDbSharp.RocksDb", "Get"),
        ("RocksDbSharp.RocksDb", "Write"),
        ("RocksDbSharp.RocksDb", "CreateSnapshot"),
        ("RocksDbSharp.RocksDb", "NewIterator"),
        ("RocksDbSharp.RocksDb", "GetProperty"),
        ("RocksDbSharp.RocksDb", "CompactRange"),
        ("RocksDbSharp.RocksDb", "GetColumnFamily"),
        ("RocksDbSharp.RocksDb", "GetLatestSequenceNumber"),
        ("RocksDbSharp.RocksDb", "GetUpdatesSince"),
        ("RocksDbSharp.RocksDb", "DisableFileDeletions"),
        ("RocksDbSharp.RocksDb", "EnableFileDeletions"),
        ("RocksDbSharp.RocksDb", "Checkpoint"),
        ("RocksDbSharp.Checkpoint", "Save"),
        ("RocksDbSharp.TransactionLogIterator", "GetBatch"),
        ("RocksDbSharp.TransactionLogIterator", "Valid"),
        ("RocksDbSharp.TransactionLogIterator", "Next"),
        ("RocksDbSharp.WriteBatch", "Put"),
        ("RocksDbSharp.WriteBatch", "Delete"),
        ("RocksDbSharp.WriteBatch", "DeleteRange"),
        ("RocksDbSharp.WriteOptions", "SetSync"),
        ("RocksDbSharp.ReadOptions", "SetIterateLowerBound"),
        ("RocksDbSharp.ReadOptions", "SetIterateUpperBound"),
        ("RocksDbSharp.ReadOptions", "SetSnapshot"),
        ("RocksDbSharp.Iterator", "Seek"),
        ("RocksDbSharp.Iterator", "Next"),
        ("RocksDbSharp.Iterator", "Valid"),
        ("RocksDbSharp.Iterator", "GetKeySpan"),
        ("RocksDbSharp.Iterator", "GetValueSpan"),
        ("RocksDbSharp.Cache", "CreateLru"),
        ("RocksDbSharp.Env", "CreateDefaultEnv"),
        ("RocksDbSharp.Env", "SetBackgroundThreads"),
        ("RocksDbSharp.ColumnFamilies", "Add"),
    ];

    private static Assembly Binding => typeof(RocksDb).Assembly;

    [Fact]
    public void PinnedVersion_Is_11_8_1_5032()
    {
        var version = Binding.GetName().Version?.ToString();
        Assert.Equal(PinnedVersion, version);
    }

    [Fact]
    public void EveryNativeEntryPointCrystalCalls_Exists()
    {
        // Some entry points are overloaded (e.g. with and without the error-out parameter), so match
        // by name across all methods rather than GetMethod, which throws on an ambiguous match.
        var names = typeof(Native).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var missing = NativeEntryPoints.Where(name => !names.Contains(name)).ToList();

        Assert.True(missing.Count == 0, "Missing native entry points on RocksDbSharp.Native:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryManagedMemberCrystalUses_Exists()
    {
        var missing = ManagedMembers
            .Where(pair =>
            {
                var type = Binding.GetType(pair.Type);
                return type is null || type.GetMember(pair.Member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length == 0;
            })
            .Select(pair => pair.Type + "." + pair.Member)
            .ToList();

        Assert.True(missing.Count == 0, "Missing managed members on the RocksDB binding:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void WriteBufferManagerExports_ArePresent()
    {
        var native = typeof(Native);
        Assert.NotNull(native.GetMethod("rocksdb_write_buffer_manager_create_with_cache"));
        Assert.NotNull(native.GetMethod("rocksdb_write_buffer_manager_destroy"));
    }
}
