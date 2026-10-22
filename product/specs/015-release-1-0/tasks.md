# 015 — Release 1.0: tasks

> Proposed 2026-10-22. The maintainer settles D1–D6 (requirements) and picks the tasks; nothing is committed, tagged or
> published without the maintainer's go-ahead. In order: each phase relies on the one before it.

## Phase A — CI trustworthy (R1)

- [x] **T1** Find the failing test of the `v1.0-rc2` tag run (Linux) and of the `b80b4d0` run: from the trx artifact
  if the maintainer downloads it, else by looping the suites on Linux (WSL) until it fails. Done when the cause is
  named, with the interleaving that produces it. _Done 2026-10-22: reproduced on Linux (WSL Ubuntu 26.04, .NET 10). Not a flaky assertion but a test that outlasted the hang timeout: `BoxCostTests.Cost_At100000Boxes…` creates 100 000 boxes, each a synced catalogue write, and took 9 min 56 s on Linux (406 s on Windows). Under `--blame-hang-timeout 5m` the host was killed and the run reported "Passed!" for the 531 tests that had finished: 157 never ran, most of the replication suite. CI's unit step had no hang timeout, so the same test made the run fail or pass by the disk's speed; it also asserted wall-clock bounds (rename, swap, drop under 50 ms, each with an fsync). The `b80b4d0` failure is the same test (introduced with it)._
- [x] **T2** Fix it with a test that fails at that interleaving before the fix (hooks, not sleeps), and a mutation
  check. Same for anything else T1 turns up. _Done: `BoxCostTests` proven structurally (entries written per operation from the database's sequence number; the scan's native steps from `crystal.scan.steps`), no wall-clock assertion; the 100 000-box run kept as a long cost probe, `[Trait("Category", "LongProbe")]`. The same for `DatabaseCostTests` (175 s at 1 000 databases): the everyday test checks 10 against 100, the 1 000 version is a long probe. The everyday unit suite: 5 min 8 s → 2 min 8 s on Windows, 123 s on Linux, 687 tests, none skipped. A test-run budget of 3–4 minutes is the rule from now (the maintainer, 2026-10-22)._
- [x] **T3** Pin the CI runner images and update the actions (design § 2). Done when a push and a tag both run green on
  both legs. _Workflow changed (uncommitted): runners pinned to `windows-2025` and `ubuntu-24.04`; `actions/checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`; the unit step filters `Category!=LongProbe` with `--blame-hang-timeout 3m` and uploads blame sequences; the process step's hang timeout 3 min. Green on both legs needs a push (the maintainer's go-ahead). **Pushed as `258eddf`: green on `ubuntu-24.04` and `windows-2025`.** A tag run is confirmed at the next tag._
- [ ] **T4** 20 consecutive full runs on Linux and on Windows, no failure; record the counts here. Close `001` T1, T7. _2026-10-22, the everyday unit suite (`Category!=LongProbe`, `--blame-hang-timeout 3m`): **10 of 10 on Linux** (115–122 s each) and **10 of 10 on Windows** (126–129 s each), 687 tests every run, no failure, no hang, none skipped. Process tests (19) and architecture tests (16) pass on both. Ten a side, not twenty: the maintainer decides whether ten is enough. `001` T1, T7 close once CI is green on both legs._

## Phase B — crash recovery proven (R2)

- [x] **T5** The crash host's writer mode (design § 3). _Already existed: the crash harness takes `--mode` and its runner kills after a chosen number of acknowledgements (`004` T4). Nothing built; design § 3 corrected._
- [x] **T6** Kill tests: 20 kills each for Strict, Buffered and Relaxed, the per-mode survival rule of design § 3, the
  prefix rule, and a multi-box batch whole or absent. Closes `006` T7 and `013` R3. _Done 2026-10-22: `Crash/DurabilityKillTests` — 20 kills each for Strict, Interval and Buffered (one class per mode, so they run in parallel, ~30 s each): every acknowledged batch survives, whole across its two boxes, in order, every value the stream's. Mutation: unsynced commits skipping the WAL fails the Interval and Buffered tests. Closes `006` T7 and `013` R3/T5._
- [x] **T7** The torn WAL tail. _Done: `Crash/TornTailTests` — the killed store's newest WAL is parsed (RocksDB's log format, 32 KiB blocks, legacy and recyclable headers) and cut inside its last record's payload; the store opens with exactly one batch fewer, sound. Mutation: absolute-consistency recovery fails it._
- [x] **T8** The replicated pair killed mid-load (NodeHost): promotion loses no acknowledged batch; the old primary
  rejoins. _Done: `Nodes/PrimaryKillMidLoadTests` — 5 kills of the primary mid-load (after a random 3–20 committed batches, with a write in flight), roles swapping: the promoted standby holds a whole prefix; the killed primary, holding batches that never shipped in at least one kill (asserted), rejoins byte-identical. Mutation: divergence detection off on every path (standby check, the primary's diverged answer, and the gap fallback) fails it; off on two paths only, the third still caught it (defence in depth). Linux exposed two test races, fixed: the writer's failure recognised by a flag set before the kill; every process-test deadline on a monotonic clock (one Linux failure coincided with the VM's clock jumping 97 minutes). The node host gained `replstatus` for diagnostics. Process suite: 24 tests, ~2 min on Windows and on Linux; 15 of 15 runs of this test on Linux._

## Phase C — the API (R3, R4)

- [x] **T9** The public API baseline file and its architecture test (design § 4). _Done 2026-10-22: `PublicApiTests` lists every public type and member (kind, modifiers, bases, parameters, nullability, `init`/`set`, constants, enum values) and compares with `PublicApi.txt`; a difference fails, names every line added and removed, and writes `PublicApi.received.txt` (git-ignored). It caught each addition below, and nothing removed._
- [x] **T10** `013` R5 `DatabaseStatus.LatestPosition` and R9 `BoxCatalog.ListBoxes()` (the small ones), with `013`'s
  tests and the baseline updated. _Done: `DatabaseStatus.LatestPosition` (an `init` property, the constructor unchanged) and `BoxCatalog.ListBoxes()` (handles from the issuing catalogue; another database refuses them). Tests in `Adoption/HighwayFeatureTests`._
- [x] **T11** `013` R6 `Promote(reason, minimumEpoch)`, one persisted epoch write. _Done: `Promote(reason, minimumEpoch)` lands on `max(epoch + 1, floor)` in one persisted write; a floor below 0 or above 2^62 (`MaximumEpochFloor`) is refused before anything changes. Tests in `Replication/HighwayReplicationFeatureTests`._
- [x] **T12** `013` R8 `FollowedDatabase.LastReply` and `ReplicationStatus.FollowerConnected`. _Done: `ReplicationClient` reports every reply read; the follower stamps `LastReply` and keeps `FollowerConnected` from the handshake to the end of its main connection. Test: a real pair, connected and replying while idle, then not connected and silent once the primary closes._
- [x] **T13** `013` R4 `CrystalStore.Compact` (offline, refused while open, per-database before/after/time, the
  no-data-change test). _Done: `CrystalStore.Compact` opens the store (lock, format), refuses an unknown name before compacting anything, compacts each database end to end, and reports data-file bytes (table files and WAL, not diagnostic logs) before and after. Tests: no data changes across point and range deletes in two databases and both profiles; no level-0 file left; refused while open. Mutation: compaction removed fails it (by level 0; size alone did not, as RocksDB drops deleted data on its own). The architecture rule on clocks caught a direct `TimeProvider.System`, fixed._
- [x] **T13a** `013` R10 `CrystalReplication.Unfence()` and the `Unfenced` event (R4.5, D2): a fenced node that has seen
  no higher epoch returns to primary at its epoch; refused otherwise, naming why. _Done: `Unfence` returns a primary fenced here to primary at its epoch and raises `Unfenced` (log 2015); refused when not fenced, when the node was not a primary when fenced (a second primary at one epoch), or when a higher epoch was seen. A restart lifts a fence by failing closed to a standby (003 R6.5), so Promote is the way back then._
- [x] **T14** The API review (R3.1) by a fresh agent or contributor, its findings fixed or recorded (R3.2). The
  CHANGELOG's semantic-versioning line (R3.4).
  *Done 2026-10-22.* An independent reviewer read the whole public surface (the baseline in `PublicApi.txt`).
  Fixed, each one incompatible to fix after 1.0:
  - `Box` was a positional record struct: `with` could forge a handle that passed the ownership check. Now a
    `readonly struct` with internal constructors, equal by id and owning database.
  - `CrystalVersion.Value` was a `const`, baked into callers at compile time: now a property.
  - `CrystalReplication` was `IDisposable` though the store owns it: no longer.
  - `ReplicationStatus.Role` was a string: now `ReplicationRole`.
  - `BoxLocks.Lock`, `LockMany`, `LockAll` returned `IDisposable`: now `LockLease`, like the rest.
  - `ApproximateBytes` returned `ulong`: now `long`, like every other byte count.
  - Exception constructors were public: now internal (the NodeHost test tool has its own exception).
  - `CompactOptions` was a record: now a class, like the other options.
  - The standby link moved from `FollowerConnected` and `FollowedDatabase.LastReply` to
    `ReplicationStatus.ConnectedToPrimary` and `LastReplyFromPrimary` (one primary, not one per database).
  - `BoxCatalog.ListBoxes()` was dropped for `BoxInfo.Handle` on `List()`: one listing, not two.
  - `ReplicationEventKind`, `ReplicationRole`, `ReplicationSlotState`: explicit values, append-only.
  Kept on purpose:
  - `CreateDatabase(name, bool replicated)` and the backup `Open`/`VerifyAsync` shapes: an options overload can be
    added in 1.x, since its parameter is required and doesn't collide.
  - The naming suggestions (renames for taste) and `EventArgs`-style event naming: no defect, and a rename now costs
    every adopter.
  - Typed replication exceptions in place of `InvalidOperationException`: derived types can be added in 1.x.
  - The positional records for status and results: their shape is the contract.
  Architecture 18, unit 698 (LongProbe filtered, 2 m), process 24: all pass on Windows.

## Phase D — numbers (R5)

*Moved to 1.x by D7 (R5.4), 2026-10-22: Highway running the candidate is the measurement.*

- ~~**T15** `008` T15: the harness's independent review, findings fixed.~~
- ~~**T16** `013` T1, T2: the `command` workload, the RocksDB baseline runner, and the `full` run on Windows and
  Linux, recorded. README and CHANGELOG claims cite it (R5.3).~~

## Phase E — release gates (R6)

- ~~**T17** `tools/release-gate` (design § 5) running `004` R4.1, appending to `engineering/measurements.md`.
  Closes `004` T15, T16.~~ *Moved to 1.x (D8): CI on both platforms and Highway's gates were the gate for 1.0.*
- [x] **T18** Pack **1.0.0-rc.3** to a local feed (not published) and run the gate on Windows and Linux.
  *Done 2026-10-22.* Packed to a local folder from `b0a05cf` plus the version change; CI green on `windows-2025`
  and `ubuntu-24.04` for `b0a05cf`.
- [x] **T19** Highway's gates against rc.3 from the local feed (R6.2), in Highway's repository by whoever D6 names;
  results recorded here. Any failure caused by Crystal is fixed and T18 repeated.
  *Done 2026-10-22.* Run by the maintainer in Highway's repository; Highway approved rc.3.

## Phase F — production (R7, R8)

- [x] **T20** Commit, tag `v1.0-rc3` and publish 1.0.0-rc.3 (the maintainer's go-ahead for each).
  *Done 2026-10-22: committed and tagged; not published to NuGet (D8), since 1.0.0 follows from the same commit.*
- [ ] **T21** Staging soak, 72 hours (R7.2; D4), one pair on Linux and one on Windows, with daily kill, handover and restart; signals per design § 6;
  results recorded.
- [ ] **T22** Production canary (R7.3), one deployment on Linux and one on Windows: cut-over by Highway's runbook, a planned failover drill, the window of D4;
  signals reviewed at least daily and recorded here.
- [ ] **T23** 1.0.0 (R8): version change only, from the candidate that held; commit, tag `v1.0`, publish, release notes
  stating what was proven and where (each step with the maintainer's go-ahead).

## Moved to 1.x (R9)

- `012` T10, T11 `CrystalSequence` and its crash test; `012` T15 lighter batches (D3).
- `004` T2, T5–T8, T13, T14 (porting Highway's test helpers and suites): checked against the suite as it is; what the
  current tests already cover is closed with a note, the rest moves here.
- A high-resolution timer for the gather window on Windows (`014` A3), if D1 restates `014` R3.1.
