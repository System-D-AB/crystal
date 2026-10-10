# 007 — Observability

> **Status:** implemented (2026-10-09), except the cost measurement (no benchmark harness yet). **Depends on:** `001-2-rocksdb-binding` (`StorageGauges`), `005-store-and-databases`, `002-box-layout`.
> **Comes before:** `006-durability` (its sync metrics and failure events) and `003-replication` (whose R13 metrics,
> `Status()` and transition log extend what this spec defines).

## Introduction

Crystal sees things no layer above it can: how long a WAL sync takes, how many tombstones sit at the head of a box,
how far compaction is behind, how long a writer waited for a lock stripe, that a format was refused or a folder was
recovered at start. If Crystal does not report them, nobody can. That is principle 4 ("never silent") and principle 5
("measured, not assumed") applied to a running store.

**The line between Crystal and the product:**

| Crystal emits | The product or host owns |
|---|---|
| Commit, sync, lock and scan measurements; storage gauges; refusals and recoveries as log events; a status snapshot | What its keys mean (queue depth, documents, indexes) |
| Only through `System.Diagnostics.Metrics` and `Microsoft.Extensions.Logging` abstractions (playbook § 7) | The exporter (OpenTelemetry, Prometheus), dashboards, alerts, health endpoints, HTTP |

Crystal adds **no package** for this: both abstractions are part of .NET or already referenced.

**Origin.** Both products already emit storage-kernel metrics of their own, which move here: Highway's
`highway.storage.memtable_deletes` and `estimated_keys` (`Observability/HighwayMetrics.cs`), and StowDB's
`stow.engine.*` gauges and write-stall logging (`Observability/StowMeter.cs`, `EngineMetricsPoller.cs`). None of the
three measures commit or sync latency, sync failures or lock waits; those are new.

## Requirements

### R1 — One meter, bounded cardinality

- **R1.1** All instruments belong to one `Meter` named `SystemD.Crystal`, versioned with the package. It is created
  through `CrystalOptions.MeterFactory` (`IMeterFactory`) when given, so a host's dependency injection owns its
  lifetime and tests can isolate it; otherwise Crystal creates its own and disposes it with the store.
- **R1.2** Instrument names follow OpenTelemetry's conventions: `crystal.<area>.<name>`, units in UCUM (`s`, `By`,
  `{commit}`).
- **R1.3** **Tags never carry a box name, a key or a value.** A database name is a tag only when
  `CrystalOptions.TagMetricsWithDatabase` is on (off by default; at most `MaxOpenDatabases` values).
- **R1.4** Recording costs no allocation on the commit path, and an instrument nobody listens to costs a branch.
- **R1.5** Every duration is measured through `CrystalOptions.Clock` (`TimeProvider.GetTimestamp`), never a
  `Stopwatch`, so the no-clock gate rule (`004` T10) still holds.

### R2 — Instruments

- **R2.1** Commits: `crystal.commit.count` (counter; tags `durability`, `outcome` = `ok` | `refused` | `failed`),
  `crystal.commit.duration` (histogram, s; tag `durability`), `crystal.commit.operations` (histogram, operations
  per batch).
- **R2.2** WAL syncs (`006`): `crystal.wal.sync.count` (counter; tag `reason` = `strict` | `interval` | `sync_all` |
  `close`), `crystal.wal.sync.duration` (histogram, s), `crystal.wal.sync.failures` (counter).
- **R2.3** Locks: `crystal.lock.wait.duration` (histogram, s), recorded only when a stripe was contended, and
  `crystal.lock.all.count` (counter, `LockAll` calls: a product re-planning too often shows here).
- **R2.4** Scans: `crystal.scan.steps` (histogram; native iterator steps per scan, `001-2` R6.2), so tombstone
  build-up at the head of a range shows as steps growing while results do not.
- **R2.5** Storage gauges (observable, read when collected, summed over open databases, or per database under R1.3):
  `crystal.storage.memtable.deletes`, `crystal.storage.keys.estimated`, `crystal.storage.live_data.size` (By),
  `crystal.storage.compaction.pending` (By), `crystal.storage.sst.files`, `crystal.storage.sst.size` (By),
  `crystal.storage.block_cache.usage` and `.capacity` (By), `crystal.storage.write.stopped` (0/1),
  `crystal.storage.write.delayed_rate` (By/s).
- **R2.6** Store: `crystal.store.databases.open`, `crystal.store.boxes` (live boxes in open databases),
  `crystal.store.databases.failed` (`006` R5).
- **R2.7** A gauge read never throws and never blocks a drop or the store's dispose: an unreadable property reads 0
  (`001-2` R6.3), and a closing database is skipped.

### R3 — Log events

- **R3.1** Every log event has a stable `EventId` (a number and a name) in a single public table,
  `CrystalLogEvents`, so an operator can filter and alert on it across versions. Ids are never reused.
- **R3.2** Messages are templates with named values (no interpolation), emitted through source-generated
  `LoggerMessage` methods (no allocation when the level is off).
- **R3.3** The events and levels:

  | Level | Events |
  |---|---|
  | `Critical` | a database failed (`006` R5), a catalogue write failed (`002` T16) |
  | `Error` | a format refused (store, registry, database, encoder); a damaged catalogue refused |
  | `Warning` | a write stall started; a commit slower than `CrystalOptions.SlowCommitThreshold`; startup recovery found a folder it does not recognise; a `name` file repaired; dispose waiting for a call in progress |
  | `Information` | store opened and closed (with node id and mode); a database created, renamed, swapped, dropped; startup recovery removed a folder; a write stall ended |
  | `Debug` | a box created, renamed, swapped, dropped |

- **R3.4** Logs never contain a key, a value or a credential. Box and database names may appear: they are the
  product's identifiers, and appear only in events about the box or database itself.
- **R3.5** With no `LoggerFactory`, nothing is logged and nothing is allocated for it.

### R4 — Status

- **R4.1** `CrystalStore.Status()` returns an immutable snapshot: node id, store format, durability mode and interval,
  and per open database its id, name, box count, failed state and cause, last successful WAL sync time, and the
  storage gauges of R2.5.
- **R4.2** It reads only memory and RocksDB properties: no scan, no I/O beyond the properties, cost O(open databases).
- **R4.3** `003` adds a `Replication` section to the same snapshot (its R13.2); this spec leaves room for it.

### R5 — Write stalls

- **R5.1** A write stall (RocksDB has stopped or slowed writes because flushes or compaction are behind) is a
  transition, logged when it starts and when it ends (R3.3), and visible as `crystal.storage.write.stopped`.
- **R5.2** Detected by reading `rocksdb.is-write-stopped` and `rocksdb.actual-delayed-write-rate` on a timer (the same
  store timer as `006` R4, every second), since the binding does not expose RocksDB's event listener.

### R6 — Verification

- **R6.1** Each instrument is tested with a `MeterListener` (or `MetricCollector<T>` from
  `Microsoft.Extensions.Diagnostics.Testing`, if the maintainer accepts that test-only package): it is emitted with
  the right value and tags, and not emitted when nothing happened.
- **R6.2** Each log event is tested through a test logger: emitted once, at its level, with its id, without a key or
  value in it.
- **R6.3** `StorageGauges` gets the test `001-2` T9 claimed and never had: each gauge moves when its cause happens
  (deletes in the memtable, a flush, a compaction).
- **R6.4** An architecture test: no instrument or log call takes a key or value parameter, and the no-clock rule holds.
- **R6.5** The commit path's cost with a listener attached and without is measured by the benchmark harness, with the
  durability mode stated, and recorded here.

## Out of scope

- **Tracing** (`ActivitySource`). A trace span per commit has little to say that the histograms do not, and costs
  per call. Revisit when a product asks.
- **RocksDB's internal statistics** (tickers, block-cache hit ratio): they cost 5–10 % of throughput when enabled.
  A later option, off by default.
- **A slow-operation ring buffer** (StowDB's `SlowOpsProfiler`): its operations are HTTP requests, a product concern.
  Crystal logs slow commits (R3.3).
- **Exporters, dashboards, health and readiness endpoints, alert rules:** the host's.
- **Replication metrics:** `003` R13, built on this spec's conventions.

## Open questions

1. **Gauges per database by default?** Proposed: summed by default, per database behind `TagMetricsWithDatabase`
   (R1.3). A store with hundreds of databases would otherwise multiply every series.
2. **`Status()` as a method or as metrics only?** Proposed: both, because a product's health endpoint wants one call
   and one consistent snapshot (StowDB's `/stats`, Highway's `/replication`).
3. **The test-only package** `Microsoft.Extensions.Diagnostics.Testing` for `MetricCollector<T>` (R6.1), or a small
   `MeterListener` helper of Crystal's own. Proposed: the helper; no new dependency.
4. **A slow-commit threshold default.** Proposed: off unless set; a `Strict` commit on a slow disk is not news.

## Addendum 2026-10-09 — open questions settled, and what the products already have

The maintainer said to go ahead with this spec; the open questions are settled as proposed. The maintainer may
revise any of them.

1. **Gauges are summed over open databases by default;** per-database series come with `TagMetricsWithDatabase`.
2. **`Status()` is a method as well as metrics.**
3. **No test-only package:** a small `MeterListener` helper of Crystal's own.
4. **No slow-commit warning unless `SlowCommitThreshold` is set.**

**What Highway and StowDB already have** (read 2026-10-09), and what this spec takes from it:

| Source | Taken | Left behind |
|---|---|---|
| Highway `src/Highway.Server/Observability/HighwayMetrics.cs` | one meter per component; gauges sampled only when collected, so an unobserved store pays nothing; bounded tags; a meter-name override for tests (here `IMeterFactory`); `highway.storage.memtable_deletes` (active plus immutable memtables) and `estimated_keys` | queue, RPC and broker instruments (Highway's); replication instruments (`003`) |
| StowDB `src/Stow.Storage.Crystal/CrystalEngine.cs` `ReadMetricsAsync` | summing properties over every open database; the shared block cache read once; the maximum delayed-write rate; any database stopped means writes are stopped | holding the engine gate while reading (here the open databases are an immutable array, `006` review finding 3) |
| StowDB `src/Stow.Server/Observability/EngineMetricsPoller.cs` | the write-stall transition: a warning when it starts, information when it ends | a hosted service; a block-cache hit ratio that always reads 0 (RocksDB statistics are off); index progress hard-coded to 0.5 |

Neither product has log event ids, commit or sync latency, sync failures or lock waits; those are new here.

**Amended while designing:**
- **R2.2:** a `Strict` commit's sync is part of its write, so it is counted by `crystal.commit.count` with
  `durability=strict`, not by `crystal.wal.sync.count`, whose reasons are `interval`, `sync_all` and `close`.
- **R5.2:** a write stall cannot be forced reliably in a unit test (Crystal does not expose the triggers). The stall
  check reads RocksDB through an internal probe that a test replaces; the RocksDB property path is shared with the
  gauges, which are tested against real RocksDB.

## Addendum 2026-10-09 — independent review

A fresh reviewer found twelve issues; the top two reproduced by probes. What changed:

- **Fixed — close-time syncs were lost with the default meter** (finding 1): the store disposed its own meter before its
  databases' final syncs, so `crystal.wal.sync.count{reason=close}` and a failed final sync never reached a listener.
  The gauges now stop at the start of `Dispose`; the meter is disposed after the databases close. The test
  helper hid this (it owns its meters), so a test now listens to the process-wide meter.
- **Fixed — two stores on one `IMeterFactory` shared a meter** (finding 2): a factory caches meters by name, version,
  tags and scope, so both stores' gauges reported into one meter and a closed store went on reporting. **R1.1
  amended:** each store has its own meter, scoped to the store and tagged `crystal.node=<node id>`, disposed with the
  store; every gauge reports nothing once the store is closed.
- **Fixed — a failed start left gauges over a disposed registry** (finding 3).
- **Fixed — one failed sync counted once per caller who met it** (finding 4): counted once, as it is logged once.
- **Fixed — the lock-wait test could pass a lock uncontended** (finding 5): a hook now fires only after the non-blocking
  try failed.
- **Fixed — every commit read the clock** (finding 6, R1.4): only while someone listens to commit durations, or a
  slow-commit threshold is set.
- **Fixed — log closures allocated with no logger** (finding 7, R3.5); the stall check runs only with a logger, since it
  only logs.
- **Fixed — R3.3 and R5 events** (finding 8): a repaired `name` file is logged (`NameFileRepaired`, 1309); the store's
  closing event carries node and durability; a stall is "stopped" or "slowed" (`actual-delayed-write-rate`).
- **Fixed — a format refusal was logged while the store gate was held** (finding 10): a catch, not an exception filter.
- **R2.1 amended** (finding 9): `crystal.commit.duration` is the duration of commits that succeeded; a commit refused
  before it reaches the catalogue (the store closing, the database dropped) is refused by the handle and not counted.
- **R2.2 amended:** `crystal.wal.sync.failures` counts every write or sync that *failed its database*, with reasons
  `commit`, `catalogue`, `interval`, `sync_all`, `close`.
- **Not changed** (finding 10): box events are logged inside the catalogue's write lock, at `Debug`. A logging provider
  is expected not to block; moving them out would complicate every catalogue change for a debug line.
- **Design drift** (finding 12): `design.md` predates the build. Where it differs (§ 1 file list, § 3 ids, § 4
  shapes), `engineering/observability.md` and the code are current; the design stands as the record of the plan.
- **Test gaps left** (finding 11): no test by id for the recovery, reset and three repair-deferred events; no test of a
  compaction moving `compaction.pending`; no allocation measurement (waits for the benchmark harness).
