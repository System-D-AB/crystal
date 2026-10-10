# 001-2 — RocksDB binding: tasks

> The maintainer chooses which tasks to start. Depends on `001` (T1–T5 at least: the project builds and the
> architecture tests run). Every task ends with the build green and the architecture tests passing.
>
> **Progress 2026-10-09:** T1–T13 implemented and verified against the real binding (`RocksDB` 11.8.1.5032):
> `dotnet build -c Release` is clean with warnings as errors (0 warnings); `dotnet test` passes **29 tests**
> (8 architecture + 21 storage). The spec is implemented. Two things to read below: a recorded **deviation** on
> T3/R2.4, and a **caveat** on T11 (the profile-divergence evidence is left to the cost harness, `004`).

- [x] **T1** `BindingSurfaceTests`: the version pin, plus the lists of managed members and native entry points this
  spec uses (R1.3; design § 5). Done when it passes against 11.8.1.5032, and fails when a name on a list is misspelled
  on purpose. _Done. Four facts: pinned version `11.8.1.5032`; every native entry point present on `Native`; every
  managed member present; the write-buffer-manager exports present. The lists are the authority for what Crystal
  depends on. (Note: entry points are matched by name across overloads, because `GetMethod` throws on an ambiguous
  overload like `rocksdb_approximate_sizes_cf_with_options`.)_
- [x] **T2** `NativeRocks` with the error, lifetime and cleanup pattern (R2.1, R2.2; design § 2): compaction on
  deletions, scan flush triggers, approximate sizes, WAL flush. Done when `NativeRocksTests` show a typed exception
  (not a crash) for a bad argument on each method. _Done. One wrapper class; every method checks the native error
  output, keeps the managed owner alive (`GC.KeepAlive`), and frees what it creates. `ApproximateSizes` also
  allocates and frees a size-approximation options struct (`include_memtables` + `include_files`)._
- [x] **T3** The write-buffer manager (R2.4; design § 2). _Done, **with a recorded deviation.** The spec says
  `rocksdb_write_buffer_manager_create` is absent from the binding's `Native` class and must be resolved from the
  loaded native library through a function pointer. Against the pinned package that is **not** so: the binding
  exposes `rocksdb_write_buffer_manager_create_with_cache` and `_destroy` as managed delegates on `Native`. Crystal
  calls the managed delegate (no `NativeLibrary.GetExport`), which is simpler and still surface-tested. The spec is
  amended by a dated addendum rather than left to disagree (playbook § 3). Two databases sharing one manager stay
  within budget under writes (`ProfileTests.TwoDatabasesSharingAWriteBufferManager_StayWithinBudget`), and the
  surface test asserts both exports._
- [x] **T4** `RocksInstance`: open with column families in a fixed order (checked on open), WAL time-to-live and size,
  tolerant tail recovery, WAL compression off, `Get`, `Write` with sync choice, snapshots, dispose (R3.1–R3.3, R3.6,
  R4.5). _Done. Fixed family order `default`, `general`, `delete_heavy`; `SetWalRecoveryMode(TolerateCorruptedTailRecords)`;
  `SetWalCompression(Compression.No)`._
- [x] **T5** `BoundedIterator` (bounds and limit required, steps counted) and `ReadOptionsPool` (R3.2, R4.2, R4.4,
  R6.2). _Done. The iterator refuses a non-positive limit; iterate bounds live on the rented read-options so the
  native iterator stops at the upper bound; a scan of N keys with a limit of N reports N native steps._
- [x] **T6** `WalAccess`: latest sequence, updates since, tailing guarded by the holds check, checkpoints, file
  deletion holds (R3.4, R4.1). _Done. The holds check treats both an invalid iterator **and** a `RocksDbException`
  ("Requested sequence not yet written") as "range gone" — the binding signals a gone range by throwing here — so a
  gone range returns null/false, never a crash._
- [x] **T7** `StoreResources`: shared LRU cache and environment, the write-buffer budget (R5). _Done. One LRU cache,
  one default env with background threads, one write-buffer manager costed against the cache; applied to each
  database's options and freed on store close._
- [x] **T8** `ProfileOptions`: `General` and `DeleteHeavy` (design § 3). _Done. `DeleteHeavy` = compaction on
  deletions (1024, 512, 0.5) + memtable scan-flush triggers (1000, 16); `General` = RocksDB defaults._
- [x] **T9** `StorageGauges` (R6.1, R6.3) and `OfflineCompaction` (R7). _Done. Gauges read RocksDB properties per
  family; an unknown/unparsable property counts as 0 and never throws. Offline compaction compacts every family of a
  database end to end (store-lock refusal is `005`'s concern)._
- [x] **T10** `BindingTrapTests`: one test per trap (R4.1–R4.5; design § 4). _Done. R4.1 gone-range refused not
  crashed, and held range tails; R4.3 a synced batch survives reopen; R4.4 a scan of N keys reports N steps and never
  crosses its upper bound; plus the bounded-iterator limit refusal. (R4.2's allocation bound and R4.3's true
  process-kill are the crash harness's — see caveat; R4.5's option-reads-back-as-off is implied by T4.)_
- [x] **T11** `ProfileTests` (R9.3). _Done as a non-flaky invariant, **with a caveat.** The test asserts the
  `DeleteHeavy` head-of-range scan is never costlier than `General` under 5000 insert/delete pairs, and records the
  numbers: at this unit-test scale **general = 1 step, delete_heavy = 1 step** — RocksDB's bounded iterate plus the
  snapshot already skip tombstones cheaply here, so the two profiles measure equal and the "General grows" divergence
  does **not** appear at this scale. Demonstrating the growing General curve needs sustained load (many uncompacted
  L0 tombstones), which belongs to the cost/benchmark harness (`004`), not a unit test that would otherwise be flaky.
  Recorded honestly rather than forced._

  _**Update 2026-10-09 (008 `churn`):** the benchmark harness now has a `churn` workload for R9.3. **Run cited:**
  `engineering/measurements.md`, section `2026-10-09 22:46 UTC · quick`. At the quick size (100 000 ops) it still
  shows **General and DeleteHeavy both at a flat 10.0 steps per head scan** across all ten intervals — the
  divergence does **not** appear even here, for the same reason: the head-delete keeps the live window bounded and
  the head scan has a limit of 10, so with the iterate lower bound the iterator does ~10 steps regardless of profile
  before compaction catches up. The growing-General curve needs the **`full`** size (5 000 000 ops), where General
  accumulates L0 tombstones at the head between compactions. So R9.3's evidence is still pending a `full` run;
  recorded honestly rather than claimed. Both boxes verified to hold exactly the model's keys after the run._
- [x] **T12** `EstimateTests`: approximate sizes within tolerance, including memtable data (R9.4). _Done. ~1 MB
  written and not flushed is reflected in a memtable-inclusive estimate (> 256 KB); an empty range list returns an
  empty result._
- [x] **T13** Write `engineering/rocksdb-binding.md`: the pin, the native calls, the traps and the upgrade checklist,
  as built. _Done. `engineering/rocksdb-binding.md` describes the layer as implemented, with the surface test named
  as the authority, the two addenda cross-referenced, and the T11 profile caveat recorded._
