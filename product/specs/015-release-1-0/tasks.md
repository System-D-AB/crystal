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

- [ ] **T9** The public API baseline file and its architecture test (design § 4).
- [ ] **T10** `013` R5 `DatabaseStatus.LatestPosition` and R9 `BoxCatalog.ListBoxes()` (the small ones), with `013`'s
  tests and the baseline updated.
- [ ] **T11** `013` R6 `Promote(reason, minimumEpoch)`, one persisted epoch write.
- [ ] **T12** `013` R8 `FollowedDatabase.LastReply` and `ReplicationStatus.FollowerConnected`.
- [ ] **T13** `013` R4 `CrystalStore.Compact` (offline, refused while open, per-database before/after/time, the
  no-data-change test).
- [ ] **T14** The API review (R3.1) by a fresh agent or contributor, its findings fixed or recorded (R3.2). The
  CHANGELOG's semantic-versioning line (R3.4).

## Phase D — numbers (R5)

- [ ] **T15** `008` T15: the harness's independent review, findings fixed.
- [ ] **T16** `013` T1, T2: the `command` workload, the RocksDB baseline runner, and the `full` run on Windows and
  Linux, recorded. README and CHANGELOG claims cite it (R5.3).

## Phase E — release gates (R6)

- [ ] **T17** `tools/release-gate` (design § 5) running `004` R4.1, appending to `engineering/measurements.md`.
  Closes `004` T15, T16.
- [ ] **T18** Pack **1.0.0-rc.3** to a local feed (not published) and run the gate on Windows and Linux.
- [ ] **T19** Highway's gates against rc.3 from the local feed (R6.2), in Highway's repository by whoever D6 names;
  results recorded here. Any failure caused by Crystal is fixed and T18 repeated.

## Phase F — production (R7, R8)

- [ ] **T20** Commit, tag `v1.0-rc3` and publish 1.0.0-rc.3 (the maintainer's go-ahead for each).
- [ ] **T21** Staging soak, 72 hours (R7.2; D4), one pair on Linux and one on Windows, with daily kill, handover and restart; signals per design § 6;
  results recorded.
- [ ] **T22** Production canary (R7.3), one deployment on Linux and one on Windows: cut-over by Highway's runbook, a planned failover drill, the window of D4;
  signals reviewed at least daily and recorded here.
- [ ] **T23** 1.0.0 (R8): version change only, from the candidate that held; commit, tag `v1.0`, publish, release notes
  stating what was proven and where (each step with the maintainer's go-ahead).

## Moved to 1.x (R9)

- `013` R10 `Unfence()`: Highway keeps its own write gate (D2).
- `012` T10, T11 `CrystalSequence` and its crash test; `012` T15 lighter batches (D3).
- `004` T2, T5–T8, T13, T14 (porting Highway's test helpers and suites): checked against the suite as it is; what the
  current tests already cover is closed with a note, the rest moves here.
- A high-resolution timer for the gather window on Windows (`014` A3), if D1 restates `014` R3.1.
