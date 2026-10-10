# 001-2 — RocksDB binding

> **Status:** implemented (2026-10-09). **Depends on:** `001-project-skeleton` (the project, the build, the
> architecture tests). **Comes before:** `005-store-and-databases`, `002-box-layout`, `003-replication`. They all
> build on this layer. See the dated addenda at the end for the R2.4 and R4.1 implementation notes.

## Introduction

Crystal stores everything in RocksDB through its .NET binding (the `RocksDB` NuGet package, assembly
`RocksDbSharp`). This spec defines Crystal's **internal RocksDB layer**: the one place, in the
`SystemD.Crystal.Storage` namespace, where Crystal touches the binding.

It covers:
- the pinned version and the upgrade discipline;
- every native call the managed binding does not wrap, behind one safe wrapper;
- the managed features Crystal uses;
- the binding's known traps as tested rules;
- shared resources;
- offline compaction;
- storage metrics;
- a **binding surface test** that turns a missing function into a failed build instead of a crash in production.

**Origin.** Both products built on Crystal already run RocksDB 11.8.1 through this binding, and each found pieces the
managed API lacks:
- **Highway** (a durable message broker) added native calls for compaction on deletions and memtable flush triggers,
  which keep per-operation cost flat under constant insert and delete.
- **StowDB** (a document database) added native calls for size estimates and timed WAL syncs.

Their findings about the binding's traps are carried over as rules.

## Requirements

### R1 — The pinned binding

- **R1.1** The `RocksDB` package is pinned to exactly `11.8.1.5032` in `Directory.Packages.props`.
- **R1.2** Only types in `SystemD.Crystal.Storage`, all `internal`, use the binding (as `001` R3 enforces).
- **R1.3** A **binding surface test** checks, against the loaded assembly:
  - the package version;
  - every managed type and member Crystal uses (R3);
  - every native entry point Crystal calls (R2).

  A package change that removes any of them fails this test.

### R2 — Native calls, behind one wrapper

- **R2.1** Every native call Crystal makes goes through one internal class, `NativeRocks`. Nothing else calls
  `Native.Instance` or declares its own native import.
- **R2.2** Each wrapper method:
  - checks the native error output and throws a typed exception with the RocksDB message;
  - keeps the managed owner of every handle alive for the duration of the call (`GC.KeepAlive`);
  - frees every native object it creates, even on failure.
- **R2.3** The native calls (all present in the binding's `Native` class unless stated):

  | Call | Use |
  |---|---|
  | `rocksdb_options_add_compact_on_deletion_collector_factory_del_ratio` | compact SST files full of deletions (the `DeleteHeavy` profile) |
  | `rocksdb_options_set_memtable_op_scan_flush_trigger` | flush the memtable when one `Seek` or `Next` steps over too many deleted entries |
  | `rocksdb_options_set_memtable_avg_op_scan_flush_trigger` | the same, for many small skips |
  | `rocksdb_approximate_sizes_cf_with_options` (with memtables included) | cheap size estimates of a key range |
  | `rocksdb_flush_wal` (synced) | a timed WAL sync, for the interval durability mode |
  | `rocksdb_options_set_write_buffer_manager` + `rocksdb_write_buffer_manager_create` | one write-buffer budget across all databases of a store (R5.2) |

- **R2.4** **The write-buffer manager's constructor is not in the binding's `Native` class** (only the option setter
  is), but **every native library the package ships exports it**: Windows x64, Linux x64 and ARM64 (glibc, musl and
  jemalloc builds), macOS x64 and ARM64 (checked 2026-10-09). The wrapper calls that export through a function
  pointer. The surface test asserts it is present; there is no fallback.
- **R2.5** **The auto-refresh-iterator option is not used.** It exists to stop a long-running iterator from pinning
  old memtables and files. Crystal's iterators are always bounded and limited (R3.2), and its snapshots last one
  operation, so it would add refresh overhead and remove nothing. A later spec that introduces a long-running scan
  revisits this.

### R3 — Managed features Crystal uses

- **R3.1** **Databases:** open with column families and a fixed family order checked on open; dispose.
- **R3.2** **Reads:** `Get`; iterators with **lower and upper bounds on every iterator**; `Seek`, `SeekForPrev`,
  `Next`, `Prev`; snapshots.
- **R3.3** **Writes:** write batches with put, delete and delete-range per column family; `Write` with sync on or off.
- **R3.4** **Replication support** (used by `003-replication`):
  - the latest sequence number;
  - updates since a sequence;
  - the replication source (initial state, WAL tailing);
  - write batches from raw bytes;
  - disabling and enabling file deletions;
  - checkpoints.
- **R3.5** **Maintenance and metrics:** compact a range; read properties per column family.
- **R3.6** Options: block cache (LRU), write buffer size, WAL time-to-live and maximum total size, tolerant recovery
  of a corrupted WAL tail.

### R4 — Known traps, as tested rules

Each trap below was found in one of the products. Each becomes a rule in the wrapper and a test.

- **R4.1** **WAL tailing over a deleted range crashes the process** in native code. The wrapper checks that the WAL
  still holds a sequence before tailing from it, and the test proves a gone range is refused, not crashed.
- **R4.2** **`ReadOptions` is not disposable**; it is freed by its finalizer. The wrapper reuses read options on hot
  paths instead of allocating per read. A test bounds allocations per point read.
- **R4.3** **Ingesting a batch does not sync and ignores its sequence argument.** Replication writes batch bytes
  itself, synced. A test shows a written batch survives a process kill.
- **R4.4** **Each iterator step is its own native call.** Scans are bounded and limited (R3.2), and iterator steps are
  counted (R6.2) so a cost test can see them.
- **R4.5** **WAL compression stays off**, so the log can be decoded for replication's tail, trimming and reports. A
  test asserts the option.
- **R4.6** **Native AOT and trimming are not supported:** the binding generates code at runtime (`001` R2.2).

### R5 — Shared resources

- **R5.1** One LRU block cache and one background thread environment per store, shared by every database.
- **R5.2** One write-buffer budget across the store's databases (R2.4).
- **R5.3** The sizes are store options (`005-store-and-databases` R9).

### R6 — Metrics

- **R6.1** Storage gauges per database, read from RocksDB properties:
  - deletes in the active memtable;
  - estimated live keys and live data size;
  - pending compaction bytes;
  - SST file count and size;
  - block cache usage.
- **R6.2** Iterator steps and keys read per scan are counted, for metrics and cost tests.
- **R6.3** Unknown or unparsable property values count as 0, never throw.

### R7 — Offline compaction

- **R7.1** An offline operation compacts every column family of every database of a store end to end, dropping every
  tombstone. It is refused while the store is open (the store lock).

### R8 — The upgrade discipline

A RocksDB upgrade is a Crystal release, done with this checklist, recorded in the spec that performs it:

- **R8.1** The binding's API difference against the pinned version, for every member and entry point Crystal uses
  (the surface test lists them).
- **R8.2** **On-disk compatibility in both directions:** the new version opens data written by the old one, **and the
  old version opens data written by the new one**, so a rollback stays possible. No raised table format version and
  no new on-disk feature without a Crystal format change.
- **R8.3** WAL batch bytes identical across the two versions, and a standby on one version applying a primary's batches
  from the other, in both directions.
- **R8.4** The full suite, the crash harness and the replication tests green on the new version.

### R9 — Verification

- **R9.1** The surface test (R1.3).
- **R9.2** A test per trap (R4.1–R4.5).
- **R9.3** **Profile tests:**
  - a range with constant inserts and deletes keeps a flat head-of-range scan cost (compaction on deletions plus the
    flush trigger);
  - the same range without them grows. That is the evidence the native calls work.
- **R9.4** **Estimate tests:** approximate sizes are within a stated tolerance of the real size, including data still in
  the memtable.
- **R9.5** Every native wrapper method has a failure test: a bad handle or argument produces the typed exception, not
  a crash.

## Out of scope

- The store, databases, boxes, batches and replication themselves (`005`, `002`, `003`). This spec is the layer they
  call.
- A fork of the binding. Everything missing is called natively through the binding's `Native` class or the bundled
  library.

## Decisions

| Question | Decision | Basis |
|---|---|---|
| Is the write-buffer-manager constructor available? | Yes, on every shipped platform; called directly (R2.4) | the export is present in all eight native libraries of 11.8.1.5032 |
| Use the auto-refresh-iterator option? | No (R2.5) | Crystal's iterators are bounded and short-lived by design |

## Addendum 2026-10-09 — the write-buffer-manager constructor is in the binding (amends R2.4)

Recorded while implementing T2/T3. **This amends R2.4 and design § 2; the original text stays readable above.**

R2.4 states that `rocksdb_write_buffer_manager_create` is absent from the binding's `Native` class and must be
resolved from the loaded native library through a function pointer (`NativeLibrary.GetExport`). Against the pinned
package, `RocksDB` 11.8.1.5032, that is not so. The binding's `Native` class **does** expose managed delegates for
the write-buffer manager, including:

- `rocksdb_write_buffer_manager_create_with_cache(UIntPtr buffer_size, IntPtr cache, byte allow_stall) -> IntPtr`
- `rocksdb_write_buffer_manager_destroy(IntPtr)`
- `rocksdb_options_set_write_buffer_manager(IntPtr opt, IntPtr wbm)`

**Decision:** Crystal calls the managed delegate `rocksdb_write_buffer_manager_create_with_cache` (costed against the
shared block cache, `allow_stall = 0`) and `rocksdb_write_buffer_manager_destroy`, with no `NativeLibrary.GetExport`
and no unmanaged function pointer. This is simpler, has no platform-resolution risk, and is still covered by the
surface test (R1.3), which asserts both exports exist and fails the build if a package change removes them. The
"there is no fallback" intent of R2.4 is preserved: the surface test is the single gate.

If a future RocksDB upgrade removes these managed delegates, the surface test fails, and that upgrade spec decides
whether to restore the function-pointer approach R2.4 originally described.

## Addendum 2026-10-09 — WAL holds check: a gone range throws, not just reports invalid (notes R4.1)

Implementing R4.1 (`WalAccess`), the binding was observed to **throw** `RocksDbException` ("Requested sequence not
yet written in the db") from `GetUpdatesSince` when asked for a sequence the WAL no longer holds (or has not yet
written), rather than returning an invalid iterator. The wrapper therefore treats **both** an invalid iterator and a
`RocksDbException` as "range gone": `WalAccess.TryTail` returns `null` and `WalAccess.WalHolds` returns `false`. This
is still the R4.1 guarantee — a gone range is refused, never tailed into a native crash — and the trap test proves
the process lives.
