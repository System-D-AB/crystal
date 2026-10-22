# 015 — Release 1.0

> **Status:** proposed 2026-10-22. The maintainer approves the plan, settles the decisions below, and picks the tasks.
> **Depends on:** every implemented spec (`001-2` to `014`). **Closes or carries:** `001` T1, T7; `004` T15, T16;
> `006` T7; `008` T15; `012` T10, T11, T15; `013` (every requirement: taken, met, or moved to 1.x).

## Introduction

Crystal 1.0.0-rc.2 is the complete storage kernel: stores and databases, boxes, durability, observability,
self-describing databases, backup and restore, and replication with batched shipping. What it has not done is prove
itself where it counts: under a product, in production.

This spec takes Crystal from rc.2 to **1.0.0** by one route: **Highway 3.0 runs on Crystal in production, and Crystal
1.0.0 is the release candidate that held up there, unchanged but for its version.** Before that, the gaps the project's
own rules say a release must not have are closed: a flaky CI leg, untested crash recovery per durability mode, no
published numbers, no release gates, and a public API that has not been reviewed for freezing.

**What 1.0 means.** From 1.0 the public API follows semantic versioning: additions may come in 1.x, a removal or a change
only in 2.0. The on-disk format, the catalogue, the encoder and the replication protocol are what 1.0 promises to read
and speak for its whole major version. So the only work that **must** precede 1.0 is work that would otherwise break
that promise later; everything additive can follow in 1.x.

| Req | Title | Priority |
|---|---|---|
| R1 | CI is trustworthy on Windows and Linux | **Must** |
| R2 | Crash recovery is proven per durability mode | **Must** |
| R3 | The public API is reviewed and frozen | **Must** |
| R4 | Highway's production needs are in the API | **Must** |
| R5 | Numbers are published from the benchmark harness | **Must** |
| R6 | The release gates exist and pass | **Must** |
| R7 | A release candidate runs Highway in production and holds up | **Must** |
| R8 | 1.0.0 is that candidate, renamed | **Must** |
| R9 | What moves to 1.x is recorded | applies to all |

## Requirements

### R1 — CI is trustworthy on Windows and Linux

- **R1.1** The Linux failure of the `v1.0-rc2` tag run (the same commit passed on `main`) is root-caused and fixed
  (playbook § 4: a failure seen only sometimes is a finding). So is any other failure found while doing it.
- **R1.2** The full suite runs **20 times in a row** on Linux and on Windows with no failure, unit, architecture and
  process tests alike.
- **R1.3** CI is green on both legs for a push to `main` and for a tag, on the release commit. The runner images are
  pinned (`ubuntu-24.04`, `windows-2025`), not `-latest`, so an image migration cannot change a release's result, and
  the workflow's actions are on versions that run on the current Node runtime.
- **R1.4** `001` T1 and T7 are closed with this evidence.

### R2 — Crash recovery is proven per durability mode

- **R2.1** Kill tests for **Strict, Buffered and Relaxed**, each by killing a separate process mid-write with the
  existing crash host (`004` T4), 20 kills a mode: after each, the store opens, the database is consistent, and what
  survived is exactly what the mode promises (`006` R7.1–R7.3; `013` R3).
- **R2.2** A **torn WAL tail** (a partially written last record) is recovered to the last whole batch.
- **R2.3** A **multi-box batch** killed mid-commit is all there or all absent, never part.
- **R2.4** A **replicated pair** survives a kill of the primary mid-load: the standby promotes, loses no batch the
  primary reported Strict-committed to a standby-acknowledged position, and the old primary rejoins after
  re-bootstrapping if it had diverged.

### R3 — The public API is reviewed and frozen

- **R3.1** A review of every public type and member against the playbook's coding standards: names, `sealed`, records,
  nullability, typed exceptions with the way forward, `CancellationToken` on async members, XML docs that say whether
  an async member yields a thread, and nothing that leaks a RocksDB type.
- **R3.2** Anything the review would change incompatibly is changed **before** 1.0, or recorded as kept on purpose.
- **R3.3** A **public API baseline**: a file listing every public type and member, checked by an architecture test, so
  any later change to the public surface fails the build until the baseline is updated on purpose.
- **R3.4** `CHANGELOG.md` states the semantic-versioning promise from 1.0.

### R4 — Highway's production needs are in the API

`013`'s *Should* requirements are what Highway works around with its own code. Additions can come in 1.x, but Highway
goes to production on this release, so what its operators and its failover need is taken now.

- **R4.1** **Taken:** `013` R4 (`CrystalStore.Compact`, offline, for Highway's `--compact`), R5
  (`DatabaseStatus.LatestPosition`), R6 (`Promote(reason, minimumEpoch)`), R8 (`FollowedDatabase.LastReply`,
  `ReplicationStatus.FollowerConnected`), and R9 (`BoxCatalog.ListBoxes()`, small and additive).
- **R4.2** **Already met:** `013` R7 (a replaced database's handle says so, 1.0.0-rc.1) and R11 (Linux directory sync).
- **R4.3** **Moved to 1.x unless the maintainer takes it:** `013` R10 (`Unfence`: Highway keeps its own write gate);
  `012` R7 (`CrystalSequence`) and R10 (lighter batches).
- **R4.4** Each item taken follows its `013` requirement and its tests, and changes no format (`013` R12).

### R5 — Numbers are published from the benchmark harness

- **R5.1** `008` T15: an independent review of the benchmark harness, and its findings fixed.
- **R5.2** `013` R2: the `full` run on Windows and Linux, Release, with the multi-box `command` workload and the
  RocksDB baseline runner, recorded in `engineering/measurements.md`.
- **R5.3** Every performance claim in the README and the CHANGELOG cites a measurement there.

### R6 — The release gates exist and pass

- **R6.1** `004` R4.1, built as a script that a release runs: the full suite, the process tests, the concurrency and
  failover tests under a starved thread pool (`CRYSTAL_TEST_POOL_HOG`), and the long cost probes.
- **R6.2** `004` R4.2 for **Highway**, against the release candidate: its layout reference-model test, its poll-cost
  probe within 10 % of its last release, its herd failover test 20 of 20 under a starved pool, its suites on Windows and
  Linux (Highway `076` T23), and a fresh-start rehearsal of its upgrade. StowDB's gate does not apply to 1.0: StowDB
  does not run on this package (recorded, R9).
- **R6.3** `004` R4.3: any failure seen only under load is root-caused before release, never re-run until green.
- **R6.4** `004` T16: each release appends its gate results and numbers to `engineering/measurements.md`.

### R7 — A release candidate runs Highway in production and holds up

- **R7.1** **1.0.0-rc.3** carries R1–R6. Highway's 3.0 release candidate is built on it, from published packages
  only.
- **R7.2** **Staging soak** before production: a Highway pair **on each production platform, Linux and Windows**, under the load harness
  for at least **72 hours**, with a primary kill, a planned handover and a standby restart at least once a day. Pass:
  no data loss, no unexpected gap, divergence or fence, slot drops only for standbys deliberately stopped, memory and
  disk flat after warm-up, every Crystal metric within its band (design § 6).
- **R7.3** **Production canary:** one Highway deployment **on each platform, Linux and Windows** (Highway runs on both in
  production, and Crystal's own platform-sensitive code — the store lock, folder deletion, directory sync, timers, TLS —
  differs between them), each cut over by its fresh-start runbook (drain, wipe, start on
  Crystal), watched against design § 6's signals, with a planned failover drill in production.
- **R7.4** **Holds up** means: for **14 days** in production (the window is the maintainer's to set), no incident
  caused by Crystal, every signal within its band, and the failover drill passed. An incident caused by Crystal is
  fixed in a new release candidate and the window starts again.
- **R7.5** **Rollback** is defined before cut-over: Highway's previous release on its previous store, reached by its
  drain-and-wipe runbook in reverse. There is no in-place rollback of data (Highway `076` R3.2); the runbook says so.

### R8 — 1.0.0 is that candidate, renamed

- **R8.1** 1.0.0 is built from the release candidate's commit with only the version changed (`<Version>`,
  `CrystalVersion.Value`, the readmes, the CHANGELOG heading). No other change.
- **R8.2** Tagged `v1.0`, published to NuGet with symbols, and its release notes say what was proven and where.

### R9 — What moves to 1.x is recorded

- **R9.1** Every open task of an earlier spec is closed, carried here, or moved to a **1.x** list in this spec's
  tasks, with its reason.

## Out of scope

- Highway's own work (its failover policy, its runbooks, its deployment): Highway's repository and its maintainer.
  This spec states what Crystal needs from it as gates.
- StowDB on Crystal: a later step, after 1.0.
- A high-resolution timer for the gather window on Windows (`014` A3): 1.x, if `014` R3.1 is restated.

## Decisions for the maintainer

| # | Decision | Proposed |
|---|---|---|
| D1 | `014` R3.1's median lag (14–15 ms against 10 ms on Windows) | Restate as "at most one gather window plus a round trip" |
| D2 | `013` requirements taken for 1.0 | R4, R5, R6, R8, R9 (R4.1); R10 to 1.x |
| D3 | `012` R7 `CrystalSequence` and R10 lighter batches | 1.x |
| D4 | Staging soak length and production window | 72 hours; 14 days |
| D5 | Which Highway deployment is the canary, and who runs its runbook | the maintainer |
| D6 | Highway-side work (gates, runbooks) done by whom, in Highway's repository | the maintainer |
