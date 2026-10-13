# Observability

> **Status: implemented** by spec `007-observability` (2026-10-09). Replication's instruments and events are spec
> `003`'s and are not built yet. Where this document and the code disagree, one of them has a defect.

Crystal reports what only a storage kernel can see: how long commits and WAL syncs take, how far compaction is behind,
how many tombstones wait in memory, when RocksDB stalls writes, when a database fails. It reports through the .NET
abstractions only (`System.Diagnostics.Metrics`, `Microsoft.Extensions.Logging`) and adds no package. Exporters,
dashboards, alerts and health endpoints are the host's.

**Never in a metric or a log:** a key, a value, or a credential. No tag carries a box name. An architecture test fails
the build if a logging or metrics method takes a byte array, a byte span or a `KeyValue`.

## 1. Wiring it up

```csharp
var store = CrystalStore.Open(path, new CrystalOptions
{
    LoggerFactory = loggerFactory,           // events below; null logs nothing and allocates nothing
    MeterFactory = meterFactory,             // optional; the host's IMeterFactory owns the meter's lifetime
    TagMetricsWithDatabase = false,          // true: storage gauges per database, tagged database=<name>
    SlowCommitThreshold = null,              // e.g. TimeSpan.FromMilliseconds(100) to log slow commits
});
// OpenTelemetry: .WithMetrics(m => m.AddMeter("SystemD.Crystal"))
```

## 2. Instruments (meter `SystemD.Crystal`)

| Instrument | Kind, unit | Tags | What it shows |
|---|---|---|---|
| `crystal.commit.count` | counter, `{commit}` | `durability` (strict, interval, buffered), `outcome` (ok, refused, failed) | Batches committed. An empty batch is not a commit. `refused`: a dropped box or a failed database; `failed`: the write itself failed. |
| `crystal.commit.duration` | histogram, `s` | `durability` | Time to commit a batch that succeeded, a strict commit's WAL sync included. Recorded only while someone listens. |
| `crystal.commit.operations` | histogram, `{operation}` | — | Puts, deletes and range deletes per batch. |
| `crystal.wal.sync.count` | counter, `{sync}` | `reason` (interval, sync_all, close) | WAL syncs outside a strict commit; a strict commit's sync is in its commit. |
| `crystal.wal.sync.duration` | histogram, `s` | `reason` | One WAL sync. |
| `crystal.wal.sync.failures` | counter, `{failure}` | `reason` (commit, catalogue, interval, sync_all, close) | Writes or syncs that failed their database, each counted once. **Alert on any.** |
| `crystal.lock.wait.duration` | histogram, `s` | — | Time a box lock waited for a stripe someone held. Uncontended locks are not recorded. |
| `crystal.lock.all.count` | counter, `{lock}` | — | `LockAll` calls. |
| `crystal.scan.steps` | histogram, `{step}` | — | Native iterator steps per scan. Steps growing while results do not: tombstones at the head of a range. |
| `crystal.storage.memtable.deletes` | gauge, `{deletion}` | `database`* | Deletions in memtables, not yet flushed. |
| `crystal.storage.keys.estimated` | gauge, `{key}` | `database`* | RocksDB's estimate of live keys. |
| `crystal.storage.live_data.size` | gauge, `By` | `database`* | RocksDB's estimate of live data. |
| `crystal.storage.compaction.pending` | gauge, `By` | `database`* | Bytes compaction is behind by. |
| `crystal.storage.sst.files`, `.sst.size` | gauge, `{file}`, `By` | `database`* | SST files and their size. |
| `crystal.storage.write.stopped` | gauge, `{state}` | `database`* | 1 while RocksDB has stopped writes. **Alert on 1.** |
| `crystal.storage.write.delayed_rate` | gauge, `By/s` | `database`* | The rate RocksDB slows writes to; 0 when not. |
| `crystal.storage.block_cache.usage`, `.capacity` | gauge, `By` | — | The store's shared block cache. |
| `crystal.store.databases.open` | gauge, `{database}` | — | Open databases. |
| `crystal.store.boxes` | gauge, `{box}` | `database`* | Live boxes in open databases. |
| `crystal.store.databases.failed` | gauge, `{database}` | `database`* | Open databases that have failed. **Alert on > 0.** |
| `crystal.replication.epoch` | gauge, `{epoch}` | — | This node's replication epoch (spec 003 R6). |
| `crystal.replication.role` | gauge, `{role}` | — | 0 primary, 1 standby, 2 fenced. |
| `crystal.replication.slots` | gauge, `{slot}` | `crystal.slot.state` | Standby slots on a primary by state: active, lagging, dropped (spec 003 R2.3). **Alert on dropped.** |
| `crystal.replication.lag` | gauge, `{sequence}` | `crystal.database` when tagged | Sequences the furthest-behind standby lacks, of the slots not dropped (R2.4). |
| `crystal.replication.lag_time` | gauge, `s` | `crystal.database` when tagged | Age of the oldest batch the furthest-behind standby lacks: what a failover now would lose (R2.4, R12.1). **Alert on it.** |
| `crystal.replication.promotions`, `.demotions`, `.fences` | counter, `{…}` | — | Role transitions (R6). |
| `crystal.replication.epoch_changes`, `.conflicts` | counter, `{…}` | — | Higher epochs adopted (R6.3); equal-epoch conflicts detected (R6.4). **Alert on a conflict.** |
| `crystal.replication.gaps`, `.divergences` | counter, `{…}` | — | Databases that re-bootstrapped after a gap (R5.1) or divergence (R5.2). |
| `crystal.replication.slot_drops`, `.format_refusals` | counter, `{…}` | — | Standby slots dropped (R2.3); peers refused for a format mismatch (R8). |
| `crystal.replication.bootstraps` | counter, `{bootstrap}` | — | Snapshot bootstraps installed (R7). |
| `crystal.replication.pages_applied`, `.bytes_applied` | counter, `{page}`, `By` | — | Pull pages and WAL bytes applied on a standby (R4). |
| `crystal.backup.duration` | histogram, `s` | — | Duration of each complete backup (spec 010 R7.2). |
| `crystal.backup.bytes`, `.files` | counter, `By`, `{file}` | — | What backups copied: new files only, not those shared with earlier backups. A second backup after a small change copies little. |
| `crystal.backup.failures` | counter, `{backup}` | — | Backups that failed, were cancelled or were stopped by a close; none wrote a set. **Alert on it.** |
| `crystal.backup.last_success` | gauge, `s` | — | Unix time of the last complete set this store wrote since it opened; absent before. **Alert when it ages past the product's backup interval.** |

\* Only with `TagMetricsWithDatabase`; otherwise summed over open databases (the largest, for `write.stopped` and
`write.delayed_rate`).

Every instrument carries the meter tag `crystal.node` (the store's node id): each store has its own meter, so two
stores in one process are two series, and a closed store reports nothing.

Counters and histograms cost a branch when nothing listens. Gauges read RocksDB properties only when a listener
collects them, without holding any store lock, and stop reading when the store closes.

## 3. Log events (`CrystalLogEvents`)

| Id | Name | Level | When |
|---|---|---|---|
| 1001 | `StoreOpened` | Information | The store opened (path, node id, durability). |
| 1002 | `StoreClosed` | Information | The store closed (path, node id, durability). |
| 1003 | `FormatRefused` | Error | A store, registry or database was refused on open, with the way forward. |
| 1004 | `ResetDeletedFolder` | Information | `Reset` deleted a folder. |
| 1101 | `RegistryFailed` | Critical | A registry write failed; database changes are refused until reopened. |
| 1201–1205 | `Recovery…` | Information to Error | Startup recovery: dropped folders deleted, interrupted creates removed, unknown entries left alone, a registered folder missing. |
| 1301–1304 | `DatabaseCreated`, `Renamed`, `DatabasesSwapped`, `Dropped` | Information | Database lifecycle. |
| 1305–1308 | `NameFileNotWritten`, `DroppedFolderNotDeleted`, `FailedCreateFolderNotRemoved`, `CloseWaitingForCalls` | Warning | Repairs deferred to the next open; a close held up by an unfinished scan. |
| 1309 | `NameFileRepaired` | Warning | A database's `name` file disagreed with the registry and was rewritten. |
| 1310 | `DatabaseCreatedTimeCorrected` | Debug | The registry's creation time of a database was set to the database's own (spec 009), as on a standby after a bootstrap. |
| 1401–1404 | `BoxCreated`, `BoxRenamed`, `BoxesSwapped`, `BoxDropped` | Debug | Box lifecycle. |
| 1501 | `DatabaseFailed` | Critical | A write or WAL sync failed; the database refuses writes until the store is opened again. |
| 1502 | `SlowCommit` | Warning | A commit took longer than `SlowCommitThreshold`. |
| 1601 | `WriteStallStarted` | Warning | RocksDB stopped or slowed writes in a database (checked every second, when a logger is set). |
| 1602 | `WriteStallEnded` | Information | Writes are back to full speed. |
| 1701, 1702 | `BackupStarted`, `BackupCompleted` | Information | A backup began (root, set, databases); its set exists (databases, omitted, bytes copied, duration). |
| 1703 | `BackupFailed` | Error | A backup failed, was cancelled or was stopped by a close; no set was written, earlier sets are untouched. |
| 1704 | `BackupDatabaseOmitted` | Warning | A database live when the backup began is not in its set: it was dropped during it. |
| 1711, 1712 | `RestoreStarted`, `RestoreCompleted` | Information | A restore began; its databases are registered. |
| 1713 | `RestoreFailed` | Error | A restore failed; nothing was registered. |
| 1721 | `BackupVerifyFailed` | Error | A verify found a database's backup damaged (`CrystalBackupRoot`, through its logger factory). |
| 1731 | `BackupPurged` | Information | A purge completed: sets kept and deleted, engine folders removed. |
| 2001 | `ReplicationPromoted` | Information | This node promoted to primary at a new epoch. |
| 2002 | `ReplicationDemoted` | Information | This node demoted from primary. |
| 2003 | `ReplicationFenced` | Warning | This node fenced itself read-only. |
| 2004 | `ReplicationEpochChanged` | Information | This node adopted a higher epoch. |
| 2005 | `ReplicationSlotLagging` | Warning | A standby slot is lagging. |
| 2006 | `ReplicationSlotDropped` | Warning | A standby slot was dropped (lag cap or staleness). |
| 2007 | `ReplicationGap` | Warning | A database reported a gap; it re-bootstraps. |
| 2008 | `ReplicationDivergence` | Warning | A database diverged; it re-bootstraps. |
| 2009 | `ReplicationFormatRefused` | Error | A peer's format or layout did not match; nothing served or applied. |
| 2010 | `ReplicationConflict` | Error | Two primaries at the same epoch. **Alert.** The product resolves it. |
| 2011 | `ReplicationConnected` | Information | A replication connection was established. |
| 2012 | `ReplicationDisconnected` | Information | A replication connection was lost. |

Ids are never reused. 1000s the store, 1100s the registry, 1200s recovery, 1300s databases, 1400s boxes, 1500s
durability, 1600s storage, 1700s backup and restore (`010`), 2000s replication (`003`). No backup event names a key or
a value.

## 4. Status

`store.Replication.Status()` returns the replication section (spec 003 R13.2): role, epoch, the followed primary, the
listener endpoint and a bounded log of recent transitions; the protocol's `STATUS` command returns the same as JSON.

`CrystalStore.Status()` returns one consistent snapshot for a product's status or health endpoint: node id, format,
durability and interval, the shared cache, and per open database its id, name, box count, failed state and cause, last
successful WAL sync, and RocksDB's view (memtable deletes, keys, live data, pending compaction, SST files and size,
write stopped). It reads memory and properties only; a database closing meanwhile is left out. While a backup runs,
`Backup` names its root, the databases done and to do, and the bytes copied so far (spec 010 R7.3); it is null
otherwise.

## 5. What is not here

- **Tracing** (`ActivitySource`): a span per commit says little the histograms do not.
- **RocksDB statistics** (tickers, cache hit ratio): they cost throughput when enabled; a later option.
- **Slow-operation ring buffers, HTTP metrics, health endpoints:** the product's.
