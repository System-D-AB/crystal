# 004 — Test suite: tasks

> The maintainer chooses which tasks to start. Infrastructure (T1–T8) can start once `001` exists. Each port task (T9
> onward) belongs to its owning spec and is done together with that spec's implementation.

## Infrastructure

- [x] **T1** `SystemD.Crystal.Process.Tests` project and its CI job, on every push and pull request (R1.1). _Done
  2026-10-10: the project (xUnit, referencing the node host and the crash harness so both are built beside it), and a
  CI step of its own after the unit and architecture tests (`.github/workflows/build.yml`), with a 5-minute hang
  detector. **The CI step is written but not yet observed green:** there is no repository yet (as `001` T7)._
- [ ] **T2** `TempStore` (R2.1).
- [x] **T3** `TestNode`: an out-of-process Crystal node with start, stop, hard-kill, restart, promote, status (R2.2).
  _Done 2026-10-10, with promote reserved: `tests/SystemD.Crystal.NodeHost` (a program holding one store, driven by
  JSON lines on standard input and output) and `Process.Tests/Support/TestNode.cs` (start in a fresh directory on a
  free port, stop, hard kill of the process tree, restart on the same directory and port, status, and create, put,
  get, count, sync through the public API). `Promote`, `Follow`, `Demote` and `Fence` are refused with
  `NotSupportedException` until `003` builds replication; `003` fills them in. 7 tests, including two nodes side by
  side, a refused command reported with the node's exception, and a kill told apart from a stop by its exit code._
- [x] **T4** `tools/SystemD.Crystal.CrashHarness` and its runner (R2.3). _Done 2026-10-10, ported from Highway's
  `tests/Highway.Storage.CrashHarness/Program.cs` and the process handling of `CrashReplayTests.cs`. Changed: Crystal's
  public API; each batch writes two boxes, so the audit sees a batch whole or not at all; a durability mode argument;
  the writer resumes after the last batch in the store, so one store takes several kills. The deterministic stream
  (`CrashStream.cs`) is compiled into the tests too, so writer and audit cannot disagree. Its own proof, in Strict mode:
  three seeded kills of one store (every acknowledged batch present, whole, in order, byte-identical) and a comparison
  with a clean reference run. The kill tests for every durability mode remain `006` T7, deferred by the maintainer._
  **Mutation checks** (T1, T3, T4), each in the real source, rebuilt, restored byte-identical: a batch not whole · a
  value differing from the stream · the harness restarting from zero · a kill that is a graceful exit · replication
  commands not reserved; all caught. Full suite: 284 tests pass (16 architecture, 9 process, 259 unit), 0 warnings.
- [ ] **T5** The pool hog behind `CRYSTAL_TEST_POOL_HOG` (R2.4). Show it starves the pool: a test's duration grows
  under it.
- [ ] **T6** Interleaving hooks in locks and the commit path; null in production (R2.5).
- [ ] **T7** `TimeProvider` everywhere; a fake clock helper (R2.6).
- [ ] **T8** The cost-probe and reference-model helpers (R2.7, R2.8). _Note (008 T14): the cost-probe side of this is
  delivered by the benchmark harness (`tools/SystemD.Crystal.Bench`), not the unit-test suite; see
  [`engineering/benchmarks.md`](../../../engineering/benchmarks.md). Unit tests keep the reference-model helper._

## Ports, by owning spec (design § 2)

- [x] **T9** Encoding: 11 tests (design § 2.1) → `002`. _Done 2026-10-09 in `tests/SystemD.Crystal.Tests/Ported/KeyEncodingTests.cs`,
  one `// Ported from Highway:` line per test. Adapted: Highway's score and sequence are Crystal's signed and unsigned
  encoders, which agree in order but not in bytes; Crystal's terminator is `0x00 0x01`. The two `KeyWriter` growth
  tests **failed** against Crystal: its writer could not grow, so any key over 256 bytes through `KeyBuilder` threw.
  Fixed in `002` (T15) by porting Highway's growth. Mutation M14._
- [x] **T10** Boxes and boundaries: `BoxLayoutTests` (3 + the model pattern), `GateG1Tests` (5), the batch contract
  from `StoreContractTests` (design § 2.2) → `002` and `001`. _Done 2026-10-09. `Ported/BoxLayoutTests.cs` (3; the
  model pattern is `ReferenceModelTests`; `AnEpochReset_InvalidatesLookupsInFlight` is left for `003`, a standby's
  catalogue reload), `Ported/BatchContractTests.cs` (11), and the gate tests in the architecture project
  (`GateTests.cs`, 4; `Seam_HasNoEngineTypeOnItsSurface` is the existing `R31` rule). The gate rules walk the built
  assembly's IL rather than Highway's source text, and each has a planted violation in `ArchitectureMutationTests`.
  The single-commit-point rule **failed** on `RocksStore.Put`, the 001 skeleton's seam, which its own comment said
  002 replaces; removed, and the smoke test moved onto `RocksInstance`. **Deviation:** the ported tests live in
  `tests/SystemD.Crystal.Tests/Ported/`, one class per Highway class, rather than design § 1's per-area folders, so the
  two suites can be compared class by class._
- [x] **T11** Locks: 3 tests (design § 2.3) → `002`. _Done 2026-10-09 in `Ported/StripedLockAllTests.cs`, driven by the
  stripe hooks on dedicated threads instead of Highway's `Task.Delay`. Porting exposed that Crystal's locks used
  `Monitor` (re-entrant, and throwing when released on another thread) instead of the design's semaphores; re-ported
  from Highway in `002` (T15). Mutations M12, M13._
- [x] **T12** Format: 10 tests (design § 2.4) → `002` and `003`. _8 done 2026-10-09 in `Ported/StorageFormatTests.cs`
  (`--wipe-data` is `CrystalStore.Reset`; the unusable-directory test is adapted, as Crystal has no ephemeral mode to
  name). **Left for `003`:** `ABlankReplica_RefusesASnapshotThatDoesNotStateItsFormat`,
  `AReplica_RefusesAPrimaryThatDoesNotEchoItsFormat`._
- [ ] **T13** Replication: tail (8), applier (6), apply atomicity (3), feeder (8 generic), slots (5), catch-up (3),
  toolkit (3), rejoin marker (2), pair (2) (design § 2.5) → `003`.
- [ ] **T14** Durability and churn: crash replay (2), restarts (adapted), compaction and tombstones (4) (design § 2.6)
  → the later durability and profile specs.

## Gates

- [ ] **T15** The release-gate checklist (R4): full suite, process tests, the hog run, long probes, and the products'
  gates against the release candidate. Written as a document a release follows.
- [ ] **T16** The nightly probe workflow, and `engineering/measurements.md` appended by each run and each release.
  _Note (008 T14): the harness that produces `engineering/measurements.md` is built by spec 008
  (`tools/SystemD.Crystal.Bench`); run it with `dotnet run -c Release --project tools/SystemD.Crystal.Bench -- full`.
  See [`engineering/benchmarks.md`](../../../engineering/benchmarks.md). This task adds only the CI workflow that runs
  it (deferred until there is a repository)._
