# RocksDB binding

> **Status: as built (spec `001-2`).** This document describes Crystal's internal RocksDB layer as it is
> implemented today. Where this document and the code disagree, one of them has a defect, and it is fixed or
> recorded, never left silent. The requirements and the two dated addenda in
> [`../product/specs/001-2-rocksdb-binding/`](../product/specs/001-2-rocksdb-binding/) are the authority for *why*;
> this document is *what and where*.

## 1. The one place Crystal touches RocksDB

Crystal stores everything in RocksDB through its .NET binding — the `RocksDB` NuGet package, assembly
`RocksDbSharp`. Every type that touches the binding is `internal` and lives in the `SystemD.Crystal.Storage`
namespace. No RocksDB type appears on any public signature. The architecture tests (spec `001` R3) enforce both
rules, and the binding surface test (§ 7) enforces that every member Crystal depends on still exists.

```
src/SystemD.Crystal/Storage/        (internal only)
  RocksException.cs                 a native error → a typed exception, freeing the native string
  NativeRocks.cs                    every native call the managed binding does not wrap (§ 3)
  BoxProfile.cs                     General | DeleteHeavy
  ProfileOptions.cs                 the column-family options per profile (§ 4)
  StoreResources.cs                 the store's shared cache, environment, write-buffer budget (§ 5)
  RocksInstance.cs                  one database: open with families, Get, Write, snapshots, scans (§ 2)
  ReadOptionsPool.cs                reused ReadOptions (trap § 6, R4.2)
  BoundedIterator.cs                an iterator with mandatory bounds + limit, counting its steps
  WalAccess.cs                      latest sequence, updates since, guarded tailing, checkpoints (§ 6, R4.1)
  StorageGauges.cs                  per-family metrics read from RocksDB properties (§ 8)
  OfflineCompaction.cs              compact every family of a database end to end (§ 9)
```

## 2. The pin and the upgrade discipline

- **The binding is pinned to exactly `11.8.1.5032`** in `Directory.Packages.props`. A RocksDB upgrade is a Crystal
  release, done with the checklist in the spec (R8): the API difference against the pin (the surface test lists
  what Crystal uses), on-disk compatibility **in both directions** so a rollback stays possible, WAL batch bytes
  identical across the two versions with a mixed-version primary/standby both ways, and the full suite, crash
  harness and replication tests green. Both products built on Crystal run this same version, so a replica pair can
  briefly run one of each.

## 3. Native calls — `NativeRocks`

The managed binding does not wrap every C-API call Crystal needs. Those live in one class, `NativeRocks`. Nothing
else calls `Native.Instance` or declares a native import. Each method follows one pattern (R2.2):

1. check the native error output and throw a typed `RocksException` carrying RocksDB's own message;
2. keep the managed owner of every handle alive across the call (`GC.KeepAlive`);
3. free every native object it creates, even on failure.

| Method | Native call(s) | Use |
|---|---|---|
| `CompactOnDeletions` | `rocksdb_options_add_compact_on_deletion_collector_factory_del_ratio` | compact SST files full of deletions (the `DeleteHeavy` profile) |
| `MemtableScanFlushTriggers` | `..._set_memtable_op_scan_flush_trigger`, `..._set_memtable_avg_op_scan_flush_trigger` | flush the memtable when a scan steps over too many deleted entries |
| `ApproximateSizes` | `rocksdb_size_approximation_options_create` / `_set_include_memtables` / `_set_include_files` / `_destroy`, `rocksdb_approximate_sizes_cf_with_options` | cheap size estimates of key ranges, including memtable data; public through `CrystalDatabase.ApproximateBytes` (`012`) |
| `FlushWal` | `rocksdb_flush_wal` (synced) | the interval durability mode's timed WAL sync |
| `CreateWriteBufferManager` / `SetWriteBufferManager` / `DestroyWriteBufferManager` | `rocksdb_write_buffer_manager_create_with_cache`, `rocksdb_options_set_write_buffer_manager`, `rocksdb_write_buffer_manager_destroy` | one write-buffer budget across the store's databases (§ 5) |

**The write-buffer manager (amended from R2.4).** The spec expected the constructor to be absent from the binding's
`Native` class and resolved from the loaded native library through a function pointer. Against the pinned package it
is **present** as a managed delegate (`rocksdb_write_buffer_manager_create_with_cache`), so Crystal calls it
directly — no `NativeLibrary.GetExport`, no unmanaged function pointer. The surface test still asserts the export,
so a package change that removes it fails the build. See the dated addendum in the spec's requirements.

**The auto-refresh-iterator option is not used** (R2.5): Crystal's iterators are always bounded and limited and its
snapshots last one operation, so it would add refresh overhead and remove nothing. A later spec with a long-running
scan revisits this.

## 4. Profiles

A box's workload profile is a column family, because compaction settings are a property of a family. The values come
from the products' measurements, where they took a queue's poll cost from growing with uptime to flat.

| Option | `General` | `DeleteHeavy` |
|---|---|---|
| compaction on deletions (window, trigger, ratio) | — | 1024, 512, 0.5 |
| memtable scan-flush triggers (per operation, average) | — | 1000, 16 |
| everything else | RocksDB defaults | RocksDB defaults |

## 5. Shared resources — `StoreResources`

Every database of one store shares (R5):

- **one LRU block cache** (`Cache.CreateLru`);
- **one background-thread environment** (`Env.CreateDefaultEnv` with background threads set);
- **one write-buffer budget** across all databases — a native write-buffer manager, costed against the block cache,
  applied to every database's options. `allow_stall = 0`, so a full budget slows writers through back-pressure, it
  never blocks forever.

The sizes are store options (set by spec `005-store-and-databases`). `StoreResources` holds the live native objects
and frees them when the store closes.

## 6. One database — `RocksInstance`

- **Opens** with the fixed column-family order `default`, `general`, `delete_heavy`, checked on open (box-layout
  § 3.1). The order never changes; a family may only be appended.
- **WAL:** time-to-live and total size are set; recovery mode is `TolerateCorruptedTailRecords`, so a torn tail
  after a crash is dropped rather than refusing the whole log; **WAL compression is off** (trap R4.5), so the log can
  be decoded for replication's tail, trimming and reconciliation reports.
- **Reads:** `Get` on the shared point-read options; bounded, limited, snapshot-pinned scans through
  `BoundedIterator`, forward (`Seek`, then `Next`) or backward (`012`: `SeekForPrev` to the upper bound, then `Prev`).
  The iterate bounds on the read options keep a backward scan inside `[from, to)`: RocksDB places a `SeekForPrev` at or
  above the upper bound on the last key below it. Crystal's own checks (a key at or above `to`, below `from`, of
  another box) stay as a second guard; with the bounds set they never fire. Every step is counted. Each
  open iterator is counted on the store's `StoreResources` until it is disposed, never by a finalizer
  (`crystal.scan.open`).
- **Writes:** write batches with a sync choice. `WriteOptions` is not disposable (freed by its finalizer), so it is
  not wrapped in `using`.
- **Snapshots:** one per operation; disposed when the read is done.

**`WalAccess`** carries the replication-support surface (used by spec `003`): the latest sequence, updates since a
sequence, guarded tailing, checkpoints, and file-deletion holds.

### The fork's replication toolkit, and why Crystal does not use it

The `RocksDB` package Crystal pins (`11.8.1.5032`, the Curiosity fork) ships extras beyond the core engine. Two are
adjacent to replication, and Crystal uses **neither**:

- A **WAL replication toolkit** in the `RocksDbSharp` namespace (`ReplicationSource`, `ReplicationConsumer`,
  `AdaptiveCommitDelayController`, `ReplicationSession`, `RocksDbWalInspector`) — convenience wrappers over the
  engine's `GetUpdatesSince`: a WAL-batch stream, a checkpoint for bootstrap, and an `IngestBatch` apply. (The
  fork's docs: [WAL Replication](https://docs.curiosity.ai/rocksdb-sharp/advanced/replication).)
- **Read-only / secondary instances** (`TryCatchUpWithPrimary`) — a follower that shares the primary's data
  directory on a filesystem and polls it. (The fork's docs:
  [Read-only & Secondary](https://docs.curiosity.ai/rocksdb-sharp/advanced/read-only-secondary).)

Crystal builds on the **same primitive the toolkit wraps** — `GetUpdatesSince` plus `TransactionLogIterator`
(`GetBatch`/`Valid`/`Next`), reached only through `WalAccess` — and re-derives the thin wrapper itself. The reasons,
on record for the independent review:

1. **The architecture gate forbids the toolkit types on Crystal's surface.** They live in the `RocksDbSharp`
   namespace; leaning on them would spread a RocksDB-package type across the replication layer, which spec `001`
   R3.2 keeps inside `Storage/` (enforced by `GateTests` and `ArchitectureTests.R31_NoRocksDbTypeOnAnyPublicSurface`).
   Crystal has its own `SystemD.Crystal.Storage.ReplicationSource` — a different type, over `WalAccess`, that the
   gate allows.
2. **Spec `003` § 11 commits to Crystal's own reader and writer both sides**, and spec `001` R3.3 forbids carrying a
   client-library dependency. Porting the *behaviour* (from Highway's code, itself over the same primitive) rather
   than binding to the fork's extras keeps the dependency surface exactly the pinned package's core engine.
3. **The toolkit models one primary DB → one follower DB.** Crystal replicates a *store* of many databases over one
   connection, named by UID, with set reconciliation, per-database incarnation/base and divergence detection —
   richer than `ReplicationConsumer.IngestBatch` (which has no store-level protocol). The secondary-instance path is
   ruled out outright: it needs a shared filesystem, while spec `003` § 1 streams the WAL over the network to a node
   with its own disk (the fork's own docs point from the secondary guide to the replication guide for exactly this).

The cost of not using the wrappers is nil by the fork's own account: `IngestBatch` is documented as "the same path
as `db.Write(new WriteBatch(bytes))`" (Crystal's `WalAccess.ApplyRaw` through the one commit point), and
`GetInitialState` as "just a normal RocksDB checkpoint" (Crystal's `WalAccess.Checkpoint`). We keep the behaviour and
keep the RocksDB types behind the `Storage/` boundary.

## 7. Known traps, as rules and tests

Each trap was found in one of the products. Each is a rule in the code and a test (`BindingTrapTests`,
`NativeRocksTests`).

| Trap | Rule in the code | Test |
|---|---|---|
| **R4.1** tailing a WAL range the log no longer holds crashes the process | `WalAccess.TryTail` / `WalHolds` check the WAL still reaches the position before tailing; a gone range returns null/false | a far-future sequence is refused, the process lives; a held range tails |
| **R4.2** `ReadOptions` is not disposable (freed by its finalizer) | `ReadOptionsPool` shares one options object for point reads and rents/returns scan options | (allocation bound: the cost harness, `004`) |
| **R4.3** ingesting a batch does not sync and ignores its sequence | Crystal writes batch bytes itself with `Write(batch, sync)` | a synced batch survives close + reopen (true process-kill: the crash harness, `004`) |
| **R4.4** each iterator step is its own native call | `BoundedIterator` requires bounds and a limit and counts steps | a scan of N keys with a limit of N reports N steps, and never crosses its upper bound |
| **R4.5** WAL compression must stay off | `RocksInstance` sets `Compression.No` on the WAL | implied by the open options (T4) |

**A note the binding taught us (R4.1):** `GetUpdatesSince` **throws** `RocksDbException` ("Requested sequence not
yet written") when asked for a sequence the WAL does not hold, rather than returning an invalid iterator. The holds
check treats both an invalid iterator and that exception as "range gone", so a gap becomes a re-bootstrap, never a
crash.

## 8. Metrics — `StorageGauges`

Per-database gauges read from RocksDB properties (R6.1): deletes in the active memtable, estimated live keys and
live data size, pending-compaction bytes, SST file count and size, block-cache usage. An unknown or unparsable
property counts as 0 and never throws (R6.3), so a metrics read can never fail a request. `BoundedIterator` counts
iterator steps and keys read per scan for cost tests (R6.2).

## 9. Offline compaction — `OfflineCompaction`

Compacts every column family of a database from first key to last, dropping every tombstone (R7). A store's offline
operation runs this over every database; it is refused while the store is open (the store lock, held by spec `005`).

## 10. Surface test — the authority for what Crystal depends on

`BindingSurfaceTests` is the gate (R1.3). It asserts, against the loaded assembly:

- the pinned version is `11.8.1.5032`;
- every native entry point Crystal calls is present on `Native` (matched by name, because some are overloaded);
- every managed member Crystal uses is present on the binding's public types;
- the write-buffer-manager create/destroy exports are present.

The lists in that test are the authority. **Adding a native call or a managed member to Crystal means adding its
name to the list** — so a package change that removes any of them is a failed build, not a crash in production.

## 11. What this layer does not do

The store, databases, boxes, batches and replication themselves are later specs (`005`, `002`, `003`). This is the
layer they call. There is no fork of the binding: everything missing is called through the binding's `Native` class
or the bundled native library.

## 12. Verification status

`dotnet build -c Release` is clean with warnings as errors. `dotnet test` passes the binding suite:
`BindingSurfaceTests`, `NativeRocksTests`, `BindingTrapTests`, `EstimateTests`, and `ProfileTests`. The profile
test asserts the `DeleteHeavy` head-of-range scan is never costlier than `General`; demonstrating the *growing*
`General` curve under sustained churn is left to the cost/benchmark harness (`004`), not a unit test — recorded in
the spec's T11 note.
