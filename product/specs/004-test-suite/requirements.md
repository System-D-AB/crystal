# 004 — Test suite

> **Status:** drafted. **Depends on:** `001-project-skeleton`; its tests land with `002-box-layout`, `003-replication`
> and the later durability and profile specs.

## Introduction

Crystal's tests are its most important asset. A storage kernel that two products rely on must prove every guarantee it
makes, keep proving it on every change, and serve as the release gate for both products.

This spec defines:
- the **suite's structure**;
- its **test infrastructure** (temporary stores, two-process nodes, hard kills, thread starvation, exact
  interleavings, fake clocks, cost probes);
- **the inventory of tests Crystal inherits from Highway**.

Highway, a durable message broker, already runs a mature storage and replication suite against the same RocksDB
version. Many of its tests prove mechanisms that now belong to Crystal: encoding, locks, the box catalogue,
format checks, the replication tail, applying, slots, catch-up, crash replay.

**Port, don't move.** Highway's tests stay in Highway until Highway runs on Crystal. Until then they are the proof of
Highway's own storage. Crystal **rewrites** each inherited test against its own API: the scenarios and assertions
carry over; the code does not, because it targets Highway's types. When Highway later runs on Crystal, Highway retires
its copies of tests for mechanisms Crystal owns, and keeps its product tests (client herd, failover policy, queue
semantics).

## Requirements

### R1 — Structure

- **R1.1** Test projects:
  - `SystemD.Crystal.Tests`: unit and in-process integration tests, against a real RocksDB in temporary directories;
  - `SystemD.Crystal.Architecture.Tests`: the boundary rules;
  - `SystemD.Crystal.Process.Tests`: multi-process tests (two-node replication, hard kills), run on CI on both
    platforms but separately, because they are slower.
- **R1.2** No external infrastructure: every test creates and deletes its own directories, ports and processes.
- **R1.3** Test names: `Method_Scenario_ExpectedResult`, or a plain sentence of the behaviour.
- **R1.4** Every test that guards a correctness rule is **mutation-checked** once when written: the guard is broken on
  purpose, the test fails, the guard is restored. The mutation and its result are recorded in the owning spec's tasks.
  After restoring a mutated file, the build is forced to rebuild before any result is trusted.

### R2 — Test infrastructure

- **R2.1** **Temporary stores:** a helper that creates a store in a unique temporary directory and deletes it after,
  even when the test fails.
- **R2.2** **Test nodes:** a helper that starts a Crystal node in its own process (store, replication listener, a
  free port) and controls it: start, stop, hard-kill, restart, promote, read status.
- **R2.3** **The crash harness:** a small program that writes a deterministic stream of batches while the test kills
  it at random points. After each kill the store is reopened and compared with a reference run. Ported from Highway's
  crash harness.
- **R2.4** **A thread-pool hog:** an opt-in switch (an environment variable) that keeps N thread-pool threads blocked
  for a test's duration. Concurrency and failover tests are run under it before a release. (In Highway, a defect that
  produced two primaries appeared only under a starved pool.)
- **R2.5** **Interleaving hooks:** internal hooks in Crystal's locks and commit path (null in production) that let a
  test act at an exact point, such as when a thread starts waiting for a lock stripe or releases one, instead of
  hoping a sleep produces the race.
- **R2.6** **A fake clock:** Crystal reads time only through `TimeProvider`, so slot ageing, session expiry and
  retries are tested without waiting.
- **R2.7** **Cost probes:** tests that measure an operation's cost at several sizes (boxes, keys, log length, uptime)
  and assert it stays flat. The measured numbers are recorded in the owning spec. Long probes (hours) run outside the
  normal suite, before a release.
- **R2.8** **A reference-model framework:** random operation sequences, from a fixed and a random seed, applied to
  Crystal and to a plain in-memory model, compared after every step. A failure prints the seed and the step.

### R3 — The inherited tests

The inventory, test by test, is design § 2. Each inherited test:
- **R3.1** is ported into the spec that owns its mechanism, as a task of that spec;
- **R3.2** keeps its scenario and its assertion, and its name where the name still describes Crystal;
- **R3.3** is mutation-checked again in Crystal (R1.4), because a test that passed in Highway proves nothing about
  Crystal's code;
- **R3.4** records its origin in a one-line comment (`Ported from Highway: <test name>`), so the two suites can be
  compared later.

### R4 — Release gates

- **R4.1** **Every Crystal release passes:**
  - the full suite;
  - the process tests;
  - the hog run of the concurrency and failover tests;
  - the long cost probes.
- **R4.2** **Products' storage gates also run against a release candidate** before it ships:
  - **StowDB:** its conformance suites;
  - **Highway, once it runs on Crystal:** its layout reference-model test, poll-cost probe, failover test under a
    starved pool, and upgrade rehearsal.
- **R4.3** A failure that appears only under load is a finding, not noise: it is root-caused before release, never
  re-run until green.

### R5 — What is not inherited

The Highway tests that test Highway's policy or its broker behaviour stay in Highway, and are listed in design § 3 so
the boundary is explicit.

## Out of scope

Writing the inherited tests: each is a task of the spec that owns its mechanism. This spec owns the structure, the
infrastructure, the inventory and the gates.

## Decisions

| Question | Decision | Basis |
|---|---|---|
| Process tests on CI | **Every push and pull request**, on Windows and Linux | they prove replication and crash safety, the guarantees both products depend on |
| Long cost probes | A **nightly scheduled workflow** and **before every release**; each run appends its numbers to `engineering/measurements.md` | the trend is visible in the repository, and a regression shows up within a day |

## Addendum 2026-10-10 — two-process infrastructure (T1, T3, T4)

- **Where the node lives:** the node program is `tests/SystemD.Crystal.NodeHost` (a test program like `005`'s crash
  host), driven by `Process.Tests/Support/TestNode`; design § 1 placed only the helper. It holds one store and takes
  one JSON request per line on standard input, answering one JSON reply per line: a channel any test can extend
  without a network protocol of its own.
- **R2.2's replication operations** (listener, promote, follow) are reserved in the node and in `TestNode`, refusing
  with `NotSupportedException` until `003` exists. The node is given a free port now and reports it, so `003` only
  adds the listener.
- **R2.3:** the harness writes each batch to two boxes and resumes after the last batch present, so one store can be
  killed repeatedly and a batch can be seen torn; its deterministic stream is one source file shared with the tests.
