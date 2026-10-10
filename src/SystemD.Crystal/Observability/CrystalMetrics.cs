// Ported from Highway: src/Highway.Server/Observability/HighwayMetrics.cs (one meter per component, gauges sampled only
// when collected, bounded tags, the storage gauges) and StowDB: src/Stow.Storage.Crystal/CrystalEngine.cs
// ReadMetricsAsync (properties summed over open databases, the shared cache read once, stalls as the worst database).
// Adapted for Crystal: IMeterFactory, durations through TimeProvider, no gate held while collecting (spec 007).

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Observability;

/// <summary>
/// Every instrument of one store (spec 007 R1, R2), on the meter <see cref="MeterName"/>. Counters and histograms cost a
/// branch when nobody listens; observable gauges read RocksDB only when a listener collects them. No tag carries a box
/// name, a key or a value; a database name only with <c>CrystalOptions.TagMetricsWithDatabase</c> (R1.3).
/// </summary>
internal sealed class CrystalMetrics : IDisposable
{
    /// <summary>The meter applications subscribe to (<c>.AddMeter("SystemD.Crystal")</c>).</summary>
    public const string MeterName = "SystemD.Crystal";

    private static readonly KeyValuePair<string, object?> Strict = new("durability", "strict");
    private static readonly KeyValuePair<string, object?> Interval = new("durability", "interval");
    private static readonly KeyValuePair<string, object?> Buffered = new("durability", "buffered");

    private readonly Meter _meter;
    private readonly object _closing = new();
    private bool _closed;
    private readonly TimeProvider _clock;
    private readonly Func<DatabaseSlot[]> _openSlots;
    private readonly bool _tagDatabase;
    private readonly RocksInstance _registry;

    private readonly Counter<long> _commits;
    private readonly Histogram<double> _commitDuration;
    private readonly Histogram<int> _commitOperations;
    private readonly Counter<long> _syncs;
    private readonly Histogram<double> _syncDuration;
    private readonly Counter<long> _syncFailures;
    private readonly Histogram<double> _lockWait;
    private readonly Counter<long> _lockAll;
    private readonly Histogram<long> _scanSteps;

    /// <param name="factory">The host's meter factory, or null for a meter of the store's own (R1.1).</param>
    /// <param name="nodeId">The store's node id: a tag on the meter, so two stores in one process are two series.</param>
    /// <param name="clock">Every duration is measured through it (R1.5).</param>
    /// <param name="openSlots">The store's open databases, read without a lock.</param>
    /// <param name="registry">The registry instance: always open, and sharing the block cache with every database.</param>
    /// <param name="blockCacheCapacity">The shared block cache's size.</param>
    /// <param name="tagDatabase">Report gauges per database, tagged with its name (R1.3).</param>
    public CrystalMetrics(
        IMeterFactory? factory,
        string nodeId,
        TimeProvider clock,
        Func<DatabaseSlot[]> openSlots,
        RocksInstance registry,
        long blockCacheCapacity,
        bool tagDatabase)
    {
        // One meter per store (spec 007 review, finding 2). A factory caches meters by name, version, tags and scope, so
        // the store itself is the scope: two stores never share a meter or its instruments, and the store disposes its
        // own at close, so a closed store's gauges leave the factory with it. The node id tag keeps two stores' series
        // apart in an exporter.
        var options = new MeterOptions(MeterName)
        {
            Version = CrystalVersion.Value,
            Tags = [new KeyValuePair<string, object?>("crystal.node", nodeId)],
            Scope = this,
        };
        _meter = factory?.Create(options) ?? new Meter(options);
        _clock = clock;
        _openSlots = openSlots;
        _registry = registry;
        _tagDatabase = tagDatabase;

        _commits = _meter.CreateCounter<long>("crystal.commit.count", "{commit}",
            "Batches committed, tagged by durability and outcome (ok, refused, failed). An empty batch is not counted.");
        _commitDuration = _meter.CreateHistogram<double>("crystal.commit.duration", "s",
            "Time to commit a batch, its WAL sync included for a strict commit; tagged by durability.");
        _commitOperations = _meter.CreateHistogram<int>("crystal.commit.operations", "{operation}",
            "Puts, deletes and range deletes per committed batch.");
        _syncs = _meter.CreateCounter<long>("crystal.wal.sync.count", "{sync}",
            "WAL syncs outside a strict commit, tagged by reason (interval, sync_all, close).");
        _syncDuration = _meter.CreateHistogram<double>("crystal.wal.sync.duration", "s",
            "Time for one WAL sync outside a strict commit, tagged by reason.");
        _syncFailures = _meter.CreateCounter<long>("crystal.wal.sync.failures", "{failure}",
            "Writes or WAL syncs that failed, each failing its database (spec 006 R5), tagged by reason.");
        _lockWait = _meter.CreateHistogram<double>("crystal.lock.wait.duration", "s",
            "Time a box lock waited for a stripe someone else held. Uncontended locks are not recorded.");
        _lockAll = _meter.CreateCounter<long>("crystal.lock.all.count", "{lock}",
            "LockAll calls: a product re-planning under a moving lock set shows here.");
        _scanSteps = _meter.CreateHistogram<long>("crystal.scan.steps", "{step}",
            "Native iterator steps per scan: steps growing while results do not is tombstones at the head of a range.");

        _meter.CreateObservableGauge("crystal.storage.memtable.deletes",
            () => Sum(i => StorageGauges.Sum(i, "rocksdb.num-deletes-active-mem-table") + StorageGauges.Sum(i, "rocksdb.num-deletes-imm-mem-tables")),
            "{deletion}", "Deletions held in memtables, not yet flushed or compacted.");
        _meter.CreateObservableGauge("crystal.storage.keys.estimated",
            () => Sum(i => StorageGauges.Sum(i, "rocksdb.estimate-num-keys")), "{key}", "RocksDB's estimate of live keys.");
        _meter.CreateObservableGauge("crystal.storage.live_data.size",
            () => Sum(i => StorageGauges.Sum(i, "rocksdb.estimate-live-data-size")), "By", "RocksDB's estimate of live data.");
        _meter.CreateObservableGauge("crystal.storage.compaction.pending",
            () => Sum(i => StorageGauges.Sum(i, "rocksdb.estimate-pending-compaction-bytes")), "By", "Bytes compaction is behind by.");
        _meter.CreateObservableGauge("crystal.storage.sst.files",
            () => Sum(StorageGauges.SstFiles), "{file}", "SST files over every level.");
        _meter.CreateObservableGauge("crystal.storage.sst.size",
            () => Sum(i => StorageGauges.Sum(i, "rocksdb.total-sst-files-size")), "By", "Size of every SST file.");
        _meter.CreateObservableGauge("crystal.storage.write.stopped",
            () => Max(i => StorageGauges.Db(i, "rocksdb.is-write-stopped") != 0 ? 1 : 0), "{state}",
            "1 while RocksDB has stopped writes in any database (flushes or compaction are behind), else 0.");
        _meter.CreateObservableGauge("crystal.storage.write.delayed_rate",
            () => Max(i => StorageGauges.Db(i, "rocksdb.actual-delayed-write-rate")), "By/s",
            "The rate RocksDB is slowing writes to, in the most delayed database; 0 when not delayed.");

        // The block cache is shared by every database and the registry, so it is read once, from the registry.
        _meter.CreateObservableGauge("crystal.storage.block_cache.usage",
            () => Single(BlockCacheUsage()), "By", "Bytes in the store's shared block cache.");
        _meter.CreateObservableGauge("crystal.storage.block_cache.capacity",
            () => Single(blockCacheCapacity), "By", "Size of the store's shared block cache.");

        _meter.CreateObservableGauge("crystal.store.databases.open", () => Single(_openSlots().Length), "{database}", "Open databases.");
        _meter.CreateObservableGauge("crystal.store.boxes", () => Sum(_ => 0, s => s.Context.LiveBoxCount), "{box}", "Live boxes in open databases.");
        _meter.CreateObservableGauge("crystal.store.databases.failed",
            () => Sum(_ => 0, s => s.Context.Failed ? 1 : 0), "{database}", "Open databases that have failed (spec 006 R5).");
    }

    /// <summary>A timestamp to measure a duration from.</summary>
    public long Start() => _clock.GetTimestamp();

    /// <summary>Whether anyone listens to commit durations: if not, a commit reads no clock for them (R1.4).</summary>
    public bool TimesCommits => _commitDuration.Enabled;

    /// <summary>Records one commit (R2.1).</summary>
    /// <param name="durability">The commit's mode.</param>
    /// <param name="outcome"><c>ok</c>, <c>refused</c> or <c>failed</c>.</param>
    /// <param name="operations">The batch's operations.</param>
    /// <param name="start">The commit's start, or null if it was not timed (nobody listened when it began).</param>
    public void Commit(DurabilityMode durability, string outcome, int operations, long? start)
    {
        var mode = Tag(durability);
        _commits.Add(1, mode, new KeyValuePair<string, object?>("outcome", outcome));
        if (outcome == "ok")
        {
            if (start is { } began)
            {
                _commitDuration.Record(_clock.GetElapsedTime(began).TotalSeconds, mode);
            }

            _commitOperations.Record(operations);
        }
    }

    public void WalSync(string reason, long start)
    {
        var tag = new KeyValuePair<string, object?>("reason", reason);
        _syncs.Add(1, tag);
        _syncDuration.Record(_clock.GetElapsedTime(start).TotalSeconds, tag);
    }

    public void WalSyncFailed(string reason) => _syncFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void LockWait(long start) => _lockWait.Record(_clock.GetElapsedTime(start).TotalSeconds);

    public void LockAll() => _lockAll.Add(1);

    public void Scan(long steps) => _scanSteps.Record(steps);

    private static KeyValuePair<string, object?> Tag(DurabilityMode durability) => durability switch
    {
        DurabilityMode.Strict => Strict,
        DurabilityMode.Interval => Interval,
        _ => Buffered,
    };

    /// <summary>One store-wide measurement, or none once the store has closed (review finding 2).</summary>
    private Measurement<long>[] Single(long value) => Volatile.Read(ref _closed) ? [] : [new Measurement<long>(value)];

    private IEnumerable<Measurement<long>> Sum(Func<RocksInstance, long> read) => Sum(read, null);

    private IEnumerable<Measurement<long>> Sum(Func<RocksInstance, long> read, Func<DatabaseSlot, long>? fromSlot) =>
        Collect(read, fromSlot, max: false);

    private IEnumerable<Measurement<long>> Max(Func<RocksInstance, long> read) => Collect(read, null, max: true);

    /// <summary>
    /// Reads every open database (R2.5): summed, or the largest, or one measurement per database when tagging. A
    /// database that is closing refuses the entry and is skipped; a read never throws (R2.7).
    /// </summary>
    private List<Measurement<long>> Collect(Func<RocksInstance, long> read, Func<DatabaseSlot, long>? fromSlot, bool max)
    {
        var measurements = new List<Measurement<long>>();
        if (Volatile.Read(ref _closed))
        {
            return measurements;
        }

        long total = 0;
        foreach (var slot in _openSlots())
        {
            IDisposable scope;
            try
            {
                scope = slot.Enter(slot.Handle.Name);
            }
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException)
            {
                continue;
            }

            long value;
            using (scope)
            {
                try
                {
                    value = fromSlot is not null ? fromSlot(slot) : read(slot.Instance);
                }
                catch (Exception)
                {
                    value = 0;
                }
            }

            if (_tagDatabase)
            {
                measurements.Add(new Measurement<long>(value, new KeyValuePair<string, object?>("database", slot.Handle.Name)));
            }
            else
            {
                total = max ? Math.Max(total, value) : total + value;
            }
        }

        if (!_tagDatabase)
        {
            measurements.Add(new Measurement<long>(total));
        }

        return measurements;
    }

    /// <summary>
    /// Bytes in the shared block cache, read from the registry unless the store is closing: the registry is disposed
    /// after <see cref="Dispose"/>, which takes the same lock. Also <c>CrystalStore.Status</c>'s source.
    /// </summary>
    public long BlockCacheUsage()
    {
        lock (_closing)
        {
            return _closed ? 0 : StorageGauges.Db(_registry, "rocksdb.block-cache-usage");
        }
    }

    /// <summary>
    /// Stops every gauge from reading: called first when the store closes, so a listener collecting later reads nothing,
    /// not a disposed instance (R2.7). Counters and histograms go on recording until <see cref="Dispose"/>, so the
    /// databases' final syncs at close are measured (review finding 1).
    /// </summary>
    public void Close()
    {
        lock (_closing)
        {
            Volatile.Write(ref _closed, true);
        }
    }

    /// <summary>Disposes the store's meter, after its databases have closed. Also closes, if not yet closed.</summary>
    public void Dispose()
    {
        Close();
        _meter.Dispose();
    }
}
