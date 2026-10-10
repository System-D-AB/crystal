using System;

namespace SystemD.Crystal;

/// <summary>
/// Options for opening a <see cref="CrystalStore"/> (spec 005 R9, design § 6).
/// </summary>
public sealed class CrystalOptions
{
    /// <summary>The store's shared block cache size in bytes (default 256 MiB).</summary>
    public long BlockCacheBytes { get; init; } = 256L << 20;

    /// <summary>The write-buffer budget shared across all open databases (default 512 MiB).</summary>
    public long WriteBufferBudgetBytes { get; init; } = 512L << 20;

    /// <summary>The maximum number of simultaneously open databases (default 256).</summary>
    public int MaxOpenDatabases { get; init; } = 256;

    /// <summary>The number of background threads for compaction and flushing (default 2).</summary>
    public int BackgroundThreads { get; init; } = 2;

    /// <summary>
    /// The durability of a commit that does not name one (spec 006 R2.1; default <see cref="DurabilityMode.Strict"/>).
    /// </summary>
    public DurabilityMode Durability { get; init; } = DurabilityMode.Strict;

    /// <summary>
    /// How often the store syncs the WAL of a database that has <see cref="DurabilityMode.Interval"/> commits not yet
    /// synced (spec 006 R2.1, R4; default 50 ms, from 1 ms to 10 s).
    /// </summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The time provider for timestamps and the sync timer (default <see cref="TimeProvider.System"/>).</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// The host's meter factory (spec 007 R1.1). Crystal's instruments are on the meter <c>SystemD.Crystal</c>; with a
    /// factory, the host owns the meter's lifetime, and without one the store creates its own.
    /// </summary>
    public System.Diagnostics.Metrics.IMeterFactory? MeterFactory { get; init; }

    /// <summary>
    /// Report storage gauges per database, tagged with its name, instead of summed over the store (spec 007 R1.3; default
    /// off). At most <see cref="MaxOpenDatabases"/> series per gauge.
    /// </summary>
    public bool TagMetricsWithDatabase { get; init; }

    /// <summary>Log a warning for a commit that takes longer than this (spec 007 R3.3; default null, never).</summary>
    public TimeSpan? SlowCommitThreshold { get; init; }

    /// <summary>Optional logger factory for operational logging (spec 005 R8.3).</summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Optional replication configuration (spec 003). When set, replicated databases retain their WAL for standbys
    /// (R11.1); the listener, follower and protocol arrive as later tasks of spec 003 land. Null means a standalone
    /// store that never replicates.
    /// </summary>
    public ReplicationOptions? Replication { get; init; }

    /// <summary>
    /// Test hook, called with the name of each step the store reaches in create, drop, rename, swap and dispose
    /// (design addendum A8). Crash and interleaving tests block or kill the process here. Not public API.
    /// </summary>
    internal Action<string>? Hook { get; init; }
}
