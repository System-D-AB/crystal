# 006 — Durability modes: tasks

> The maintainer approves the open questions and chooses which tasks to start. Needs `007` T1 (the meter and the
> logging events) for R5.3, or records those tasks as pending until `007` lands.

- [x] **T1** Settle the open questions (default mode, `CommitAsync`, the replication hazard, the harness). Done when
  the answers are recorded in `requirements.md` by dated addendum. _Done 2026-10-09: `Strict` by default; kill tests
  deferred; `CommitAsync` out; the replication hazard to `003`; the harness undecided._
- [x] **T2** `DurabilityMode`, `CrystalOptions.Durability` / `SyncInterval` with validation, `Batch.Commit(mode)`
  replacing `Commit(bool)` (R2.1, R2.2; design § 1). Done when the API compiles and existing tests are moved over.
  _Done. Options are validated at open (1 ms – 10 s; a defined mode)._
- [x] **T3** The commit path: two `WriteOptions` per database, the unsynced flag, catalogue and registry always
  `Strict` (R2.3, R3; design § 2). Done when a test shows `Strict` commits sync and `Buffered` ones do not. _Done.
  The test hook reports the sync flag read back from the native `WriteOptions` (`NativeRocks.IsSync`, a new binding
  call added to the surface test), so a test sees what RocksDB was told, not what Crystal meant. Only `Interval`
  commits mark a database for the timer; `Buffered` ones are synced only by `SyncAll`, a later strict commit or closing._
- [x] **T4** The interval timer with final syncs on close, drop and dispose (R4; design § 3), ported from StowDB's
  `CrystalEngine` loop. Done when the fake-clock tests of R7.4 pass. _Done, with a `ManualClock` test helper. The
  timer is `TimeProvider.CreateTimer`, one-shot and re-armed after each tick (never overlapping), started on the
  first `Interval` commit rather than at open (it has nothing to do before one). Ticks enter slots like any call._
- [x] **T5** `SyncAll` (R2.4). Done when it syncs every open database with unsynced commits and no other.
- [x] **T6** Failed databases (R5; design § 4): `DatabaseFailedException`, refusal of commits, reads unaffected, no
  retry. Done when R7.5 passes. _Done. `002` T16's catalogue-write failure became this one mechanism. Logged at
  `Critical` through the store's `ILogger` until `007` gives it an event id; the counter waits for `007`._
- [x] **T7** _(Deferred by the maintainer, 2026-10-09. **Done 2026-10-22 by `015` T6, T7:** `Crash/DurabilityKillTests` and `Crash/TornTailTests` in the process tests; 20 kills a mode on Windows and Linux.)_ _(Originally deferred 2026-10-09. The crash harness it needs now exists: `004` T4, 2026-10-10.)_ The crash harness and the kill tests for all three modes and the torn tail (R7.1–R7.3; design § 5),
  ported from Highway; this is also `004` T4 and part of `004` T14. Done when 20 kills per mode pass on Windows.
- [x] **T8** The group-commit test (R7.6). Done when 16 concurrent `Strict` writers sync fewer times than they commit,
  and the test fails if a lock is taken around the write. _Done differently: the binding has no WAL-sync counter
  without RocksDB's statistics, so the test proves the cause instead of counting the effect. Two strict commits must
  both be inside `RocksInstance.Write` at once; a lock around the write (mutation E12) makes it fail._
- [x] **T9** Numbers per mode and writer count from the benchmark harness (R7.7), recorded here. _Done 2026-10-09
  by spec 008's `commit` workload. **Run cited:** `engineering/measurements.md`, section `2026-10-09 22:46 UTC ·
  quick` (Release, Windows 10.0.26200, 22 logical cores, data on NTFS, .NET 10.0.7, Crystal 0.1.0-preview.1, RocksDB
  11.8.1.5032). 100 B values, 1-op batches, 2 s cells × 3 (quick). Commits/s (median), p99 µs:_

  | Durability | 1 writer | 4 | 16 | 64 |
  |---|---|---|---|---|
  | **Strict** | 910/s, p99 1 880 µs | 2 088/s, 3 289 µs | 6 938/s, 3 099 µs | 24 204/s, 3 318 µs |
  | **Interval** | 190 516/s, p99 10 µs | 279 978/s, 70 µs | 178 528/s, 179 µs | 322 586/s, 822 µs |
  | **Buffered** | 215 444/s, p99 11 µs | 301 938/s, 75 µs | 203 682/s, 167 µs | 337 558/s, 463 µs |

  _The reading R5.1 wanted: **Strict scales with writers** (910 → 24 204/s from 1 to 64) because RocksDB's group
  commit amortises the one WAL sync across concurrent writers, while Buffered/Interval already run at memory speed
  (hundreds of thousands/s, microsecond p50). Numbers are `quick`; a `full` run before a release supersedes them._
- [x] **T10** Mutation checks of every guard in the real source; an independent review (R7.8). _Done. The review
  found twelve issues (three confirmed by probes: `SyncAll` returning before a sync in progress finished, a failed
  close sync only logged, the timer waiting on the store gate); all are fixed or recorded in the requirements
  addendum of the same date. 18 mutations, listed below, each caught._
- [x] **T11** Update `engineering/box-layout.md` § 6, `002` design § 2 and `product/intro.md` to describe Crystal as it
  is now. _Done: box-layout § 6 and `002` design § 2. `intro.md` already promises the three modes; it needs no change._

**Mutation checks (T10)**, each a guard broken in the real source, the build rebuilt, the named test seen to fail, the
file restored byte-identical (checksummed): E1 strict handed unsynced options · E2 the store default ignored · E3 the
catalogue not strict · E4 an interval commit not marked · E5 a buffered commit marked for the timer (first not caught:
the test never started the timer; fixed) · E6 the timer not re-armed · E7 the tick not syncing · E8 no final sync on
close · E9 a dropped database synced · E10 a failure not recorded · E11 a failed database still writing · E12 a lock
held across the write · and after the review: F1 `SyncAll` not waiting for a sync in progress · F2 `SyncAll` not
re-checking for a failure · F3 the timer taking the store gate · F4 `Dispose` not reporting a failed database · F5 a
failed registry not latched · F6 buffered commits not synced on close.

**Final, 2026-10-09:** `dotnet build -c Release` 0 warnings; **252 tests pass** (14 architecture, 238 unit). **Open:**
T7 (kill tests, deferred by the maintainer) and T9 (numbers, waiting for a benchmark harness). The failed-sync counter
of R5.3 came with `007` (`crystal.wal.sync.failures`).
