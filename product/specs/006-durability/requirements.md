# 006 — Durability modes

> **Status:** implemented (2026-10-09), except the kill tests (deferred) and the numbers (no harness yet). **Depends on:** `001-2-rocksdb-binding` (`NativeRocks.FlushWal`, tolerant tail recovery),
> `005-store-and-databases` (open databases, the store's lifetime), `002-box-layout` (the commit path),
> `007-observability` (where its metrics and log events are defined). **Comes before:** `003-replication`, which ships
> what a commit made durable. **Uses:** `004-test-suite` T4 (the crash harness), which this spec builds.

## Introduction

Crystal promises "durability you choose" (intro): every commit is acknowledged under one of three named modes, each
with a guarantee stated plainly and proven by killing a process. Today a commit only chooses `sync: true/false`
(`002` R6.6), nothing syncs an unsynced commit later, and no test kills a process mid-stream.

The cost of the choice is large and measured in the products: on the same disk a synced write was **about 78 times**
slower than an unsynced one (StowDB v2-001: 172 163 unsynced against 2 202 synced acknowledged writes, both with
nothing lost to a process kill), and one synced writer manages about 550 commits per second, rising to about 7 800 with
64 concurrent writers sharing RocksDB's group commit (StowDB's measurements of its write path). A queue broker and a document store need
different points on that curve, so the product chooses; Crystal guarantees what each point means.

**Origin.** The modes and the interval loop come from StowDB (`Stow.Abstractions/Enums.cs`, `CrystalEngine.cs`'s
interval loop, spec 017); sync-per-commit as the safe default and the kill test come from Highway (`RocksDbStore`,
`Highway.Storage.CrashHarness`, `CrashReplayTests`).

## Requirements

### R1 — The modes and their guarantees

- **R1.1** Three modes, `DurabilityMode { Buffered, Interval, Strict }`:

  | Mode | A commit returns when | Survives a process crash | Survives power loss or an OS crash |
  |---|---|---|---|
  | `Strict` | its batch is in the WAL **and the WAL is synced** | yes | yes |
  | `Interval` | its batch is in the WAL (written to the OS, not synced) | yes | all but at most the last interval plus one sync |
  | `Buffered` | its batch is in the WAL (written to the OS, not synced) | yes | no promise: whatever the OS had flushed |

- **R1.2** These guarantees, and only these, are documented on the public API. **Power loss is not simulated by any
  test and is not claimed beyond what R1.1 says** of the sync itself.
- **R1.3** Every mode keeps a commit atomic: after any crash a batch is wholly present or wholly absent.
- **R1.4** Recovery tolerates a torn WAL tail (already set by `001-2` R3.6): a record cut off mid-write was never
  acknowledged, so dropping it loses nothing a commit claimed.

### R2 — Choosing a mode

- **R2.1** `CrystalOptions.Durability` sets the store's default mode; `CrystalOptions.SyncInterval` sets the interval
  for `Interval` (default 50 ms; 1 ms to 10 s).
- **R2.2** A commit may name its mode: `Batch.Commit(DurabilityMode)`. `Batch.Commit()` uses the store's default.
  This replaces `Commit(bool sync)` (`002` design § 2).
- **R2.3** **The catalogue and the registry are always `Strict`**, whatever the mode: a box or database that a call said
  exists, exists after any crash. (A product's data in a weaker mode may be lost; the box it was in is not.)
- **R2.4** `CrystalStore.SyncAll()` syncs every open database's WAL now, so a product can make a run of `Buffered`
  commits durable at a point it chooses (as StowDB's index builder does: unsynced writes, one sync at the end).

### R3 — Strict: one fsync shared by concurrent commits

- **R3.1** No lock of Crystal's is held across the synced write except the catalogue gate in **shared** mode, so
  concurrent `Strict` commits join RocksDB's group commit and share one fsync. (StowDB measured a commit lock held
  across the write costing 10x at 16 writers.)
- **R3.2** The write options for synced and unsynced writes are created once per database and reused, never
  allocated per commit (`ReadOptions`' finalizer trap, `001-2` R4.2, applies to `WriteOptions` too).

### R4 — Interval: one timer for the store

- **R4.1** In a store whose default mode is `Interval`, or that has had an `Interval` commit, one timer syncs the WAL
  of every open database that has had an unsynced commit since its last sync, every `SyncInterval`.
- **R4.2** A database being closed or dropped is synced before it closes, not skipped; the store's `Dispose` syncs
  every open database once more before closing it.
- **R4.3** The timer reads time only through `CrystalOptions.Clock` (`004` R2.6), so a test drives it with a fake clock.

### R5 — A failed sync is never silent

- **R5.1** A WAL sync that fails, whether in a `Strict` commit, the interval timer, `SyncAll` or a close, means writes
  that were acknowledged, or are about to be, may not be on disk. The database is then **failed**: every later commit
  is refused with a typed exception naming the database and the way forward (close the store, check the disk, open
  again; recovery replays what reached the disk).
- **R5.2** A `Strict` commit whose sync fails throws to its caller: it was not acknowledged.
- **R5.3** The failure is logged at `Critical` and counted (`007`). A sync is never retried and reported as success
  (a retried fsync can succeed after the kernel dropped the dirty pages).

### R6 — Empty and read-only work

- **R6.1** An empty batch writes nothing and syncs nothing, in every mode (`002` R6.3). Highway measured empty synced
  writes from idle pollers at 9 % of a core.

### R7 — Verification

- **R7.1** **A crash harness** (`004` R2.3, task T4): a separate program writes a deterministic stream of multi-box
  batches in a given mode and prints each acknowledged batch; the test kills the whole process tree at random points.
- **R7.2** **Kill tests per mode**, each run at least 20 kills:
  - every batch acknowledged before the kill is present after reopen, in all three modes (a process kill leaves the
    OS's page cache intact);
  - no batch is half-present (R1.3);
  - the recovered state is byte-identical to a reference run cut at the same batch.
- **R7.3** **Torn tail:** a WAL whose last record is truncated by the test opens, and loses only that record.
- **R7.4** **Interval timing** with a fake clock: no sync before the interval, one sync per elapsed interval for each
  database with unsynced commits, none for an idle database, a final sync on close and on dispose.
- **R7.5** **Failed sync:** a sync failure injected through a test hook fails the database (R5.1), throws from a
  `Strict` commit (R5.2), and logs and counts it (R5.3).
- **R7.6** **Group commit:** with 16 concurrent `Strict` writers, the number of WAL syncs is well below the number of
  commits (counted, not timed).
- **R7.7** **Numbers** for each mode (commits per second and latency percentiles at 1, 4, 16 and 64 writers) come from
  the benchmark harness, with the mode stated beside each number (playbook § 4). Recorded in this spec.
- **R7.8** Every guard above is mutation-checked in the real source, and the change has an independent review.

## Out of scope

- **Power-loss testing** (R1.2). It needs hardware or a fault-injecting filesystem.
- **Synchronous replication** and any durability that depends on a standby (`003` R12.2).
- **`CommitAsync`.** RocksDB's synced write blocks a thread; a true async commit needs a commit pipeline (write
  unsynced, then await a shared sync) that changes when other readers see a write. Deferred until the benchmark shows
  thread blocking, not the disk, is the limit (open question 2).
- **Per-database default modes.** A product that wants one names the mode on its commits.
- **Durability of the database folders themselves** (directory fsyncs on create and drop): `005`'s concern.

## Open questions

1. **The default mode.** Proposed: **`Strict`**. Safety belongs to the kernel; a product opts into speed by naming a
   weaker mode. (StowDB defaults to `Buffered`; Highway is always strict.)
2. **`CommitAsync`.** Proposed: not in this spec (see Out of scope). The alternative is an async commit that only
   moves the blocking wait off the caller's thread, which costs a thread anyway.
3. **What replication ships under a weaker mode.** A standby may receive a batch that the primary, after a power loss,
   no longer has: the standby is then *ahead* of its own primary. Proposed: `003` decides it (ship only synced
   positions, or detect and re-bootstrap); this spec states the hazard so `003` cannot miss it.
4. **The benchmark harness** (R7.7) does not exist. Proposed: its own small spec, `008-benchmark-harness`, before this
   spec's numbers are taken; or built here as the first piece of it.

## Addendum 2026-10-09 — the maintainer's decisions

- **Open question 1, the default mode:** **`Strict`**, as proposed.
- **R7.1–R7.3, the crash harness and kill tests: deferred** by the maintainer. StowDB's durability work is the
  reference for now. Until the kill tests exist, the guarantees of R1.1 rest on RocksDB's documented behaviour and on
  the in-process tests of R7.4–R7.6, and **this spec does not claim them proven by a kill.** Task T7 stays open.
- **Open question 2, `CommitAsync`:** stays out of scope, as written above.
- **Open question 3, replication under a weaker mode:** handed to `003`.
- **Open question 4, the benchmark harness:** not decided; R7.7 (task T9) waits for it.
- **`007` comes after this spec,** not before it. Until `007` defines event ids and the meter, the failed-sync event
  (R5.3) is logged through the store's existing `ILogger`, and the counting part of R5.3 waits for `007`.

## Addendum 2026-10-09 — independent review (amends R1.1, R4.2, R5, R7.6; design § 1, § 3, § 6)

A fresh reviewer (playbook § 4) found twelve issues; three were confirmed by probes. What changed:

- **Fixed — `SyncAll` could return before the sync it relied on finished** (finding 1): a timer tick that had just
  cleared the "unsynced" flag was still inside its fsync. Syncs of one database are now serialised from clearing the
  flag to the end of the fsync (never around a write, so group commit stands), and `SyncAll` re-checks for a failure
  after waiting, so a failed sync another caller ran is this caller's failure too.
- **Fixed — a failed final sync was only logged** (finding 2): `CrystalStore.Dispose` now throws an
  `AggregateException` holding a `DatabaseFailedException` for every database that failed, at close or before.
- **Fixed — the timer waited for the store gate** (finding 3): a create, open or drop held the gate across RocksDB
  opens and registry fsyncs, and no database was synced meanwhile. The open databases are now published as an
  immutable array; the timer and `SyncAll` never take the gate.
- **Fixed — registry failures bypassed R5** (finding 5): registry writes go through one helper; a failure is logged at
  `Critical` and every later registry change (create, rename, swap, drop) is refused with `DatabaseFailedException`;
  data in open databases is unaffected. `Dispose` reports it too.
- **Fixed — an exception from a logging provider could stop the timer** (finding 10): the tick always re-arms.
- **Fixed — tests:** buffered commits synced on close (finding 7); `SyncAll` against a sync in progress, and the timer
  against a create holding the gate, at exact interleavings (finding 8).
- **R1.1 amended — the interval window** (finding 4). One tick syncs the store's pending databases one after another,
  so `Interval`'s exposure is *the interval plus the time to sync every database with interval commits*, not "plus one
  sync". The timer runs on the thread pool, so a starved pool widens it further. Parallel syncs are not worth their
  complexity until a store with many interval databases shows the need.
- **R5 amended — a failed commit may reappear** (finding 6). RocksDB appends to the WAL before it syncs, so a strict
  commit whose sync failed was not acknowledged, yet its batch may replay on reopen. The refusal message says so: a
  product checks before repeating a failed commit.
- **R4.2 amended — a dropped database is not synced on close** (finding 9): its folder is deleted, and the registry's
  drop record was synced first. Syncing it would only cost an fsync.
- **R7.6 amended — group commit is shown by its cause, not counted** (finding 8): the binding exposes no WAL-sync
  counter without RocksDB's statistics (design § 6's `rocksdb.wal-file-synced` is not available). The test requires two
  strict commits inside `RocksInstance.Write` at once. Known limit: a lock taken inside `Write` *after* the test hook
  would not be seen.
- **Design § 1 amended:** `DatabaseFailedException.Database` holds the database as messages name it
  (`database 'shop' (id 16)`, or the registry), not `DatabaseName`: a database's name can change by rename, and the
  registry has none.
- **Not changed — `SyncAll` racing the store's dispose** (finding 11): databases already closing are skipped; their
  close syncs them, and `Dispose` reports a failure.

## Addendum 2026-10-09 — a second `SyncAll` race (found while building `007`)

The fix for review finding 1 made a caller wait for a sync in progress, but the *failure* of that sync was recorded
only after the sync lock was released. A `SyncAll` that got the lock in between found nothing to sync and no failure,
and reported success. `007`'s changed timing turned this into a failing test. The instance now keeps the first sync
failure under the lock, and every later sync of that database refuses with it; the database fails as before. Mutation
O11 (`007` tasks) shows the test catches its removal, three runs of three.

## Addendum 2026-10-10 — open question 3 answered by `003`

A standby that holds batches its primary lost (a power loss under `Buffered` or `Interval`) is caught by `003`'s
divergence detection (its R5.2, R12.3): each database's log on a primary has an incarnation and a base, and a standby
above the base of a new incarnation re-bootstraps that database. Batches are still shipped as soon as they are
committed, whatever their durability.
