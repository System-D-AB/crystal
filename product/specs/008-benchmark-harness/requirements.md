# 008 — Benchmark harness

> **Status:** implemented (2026-10-09; T15 independent review pending, `full` run pending — see `tasks.md`).
> **Depends on:** `002-box-layout`, `005-store-and-databases`, `006-durability`, `007-observability` (all implemented). **Closes:** `006` T9 (numbers per durability mode), `007` T8 (the cost of
> metrics), `004` T8 (the cost-probe helpers) and R2.7, and replaces the unit-test timings recorded in `002` T12 and
> `005` T23 with measured numbers.

## Introduction

Crystal promises that "performance claims come from a published, self-checking benchmark harness, with the durability
mode stated beside every number" (intro, principle 5), and the playbook allows no number from anywhere else (§ 4).
No such harness exists yet, so Crystal cannot honestly state how fast it is: `006` and `007` each have a task waiting
for one, and `002` and `005` recorded rough timings from unit tests instead.

This spec builds it: a console program, separate from the tests, that runs named workloads against a real store on a
real disk, **verifies every result it measures**, and appends each run, with the conditions it ran under, to one
results log in the repository.

**Origin.** StowDB built a measurement program (13 measurements, about 3 300 lines): in the StowDB repository, on the
branch **`rocksdb`**, folder `tests/Stow.Storage.Measurements/` (last changed in commit `1b8ac7f`). On `main` that
folder holds only build output; read the files with `git show rocksdb:tests/Stow.Storage.Measurements/<file>`. Its run plumbing and its writer sweep carry over (design § 7); most of it measures StowDB's
document features and does not. Three of its lessons are requirements here:
- a Debug build once published numbers labelled "Release" (R4.3);
- thread-pool tasks blocking in native code measured the pool's thread injection, not the engine: one cell swung from
  2 060 to 16 900 writes/s between runs (R3.2);
- a published table of durability costs could not be reproduced from the repository, because no code produced it
  (StowDB spec 017) (R4, R6).

And one gap it had: **none of its measurements read back what they wrote.** A fast run that lost data looked like a
good run. Here every workload verifies (R2).

## Requirements

### R1 — The program

- **R1.1** A console project `tools/SystemD.Crystal.Bench`, in the solution, not packed, not published, referencing
  `src/SystemD.Crystal` and nothing else of Crystal's. It uses only Crystal's **public API**, as a product would.
- **R1.2** One command per workload (R5), plus `env` (print the conditions, R4.1), `selftest` (R2.4), `quick` (every
  workload in its quick size) and `full` (every workload in its full size).
- **R1.3** Options: `--dir <path>` (the data directory's parent: the disk under test; default the system temp
  folder), `--out <path>` (the results log; default `engineering/measurements.md` of the repository), `--no-record`
  (print only), `--allow-debug` (R4.3).
- **R1.4** Each workload's store is created fresh in a new folder under `--dir` and deleted when the workload ends,
  whether it passed or failed.
- **R1.5** The exit code is 0 only if every workload run passed its verification (R2).

### R2 — Self-checking

- **R2.1** Every workload verifies what it measured before its numbers count: it reads back what it wrote, through a
  snapshot, and compares it with what it meant to write (keys present, values equal, counts equal, nothing extra).
- **R2.2** A workload whose verification fails reports **FAILED** with what was wrong (the first few differences and
  the count), records no numbers, and makes the exit code non-zero.
- **R2.3** What was verified is part of the result: "verified 160 000 keys in 4 boxes", not "OK".
- **R2.4** `selftest` proves the verification works: it runs a short commit workload, deliberately deletes one key and
  changes one value behind the workload's back, and passes only if verification reports both. A harness whose check
  cannot fail protects nothing (playbook § 4).

### R3 — Method

- **R3.1** Throughput is measured in **cells**: one configuration (workload, durability, writers, value size) run for a
  fixed time after a warm-up (quick: 300 ms warm-up, 2 s measured; full: 1 s, 10 s), not for a fixed count.
- **R3.2** Concurrent writers run on **dedicated threads** (`new Thread`), started together behind a barrier, never on
  thread-pool tasks.
- **R3.3** Each cell is run **3 times** (quick) or **5 times** (full); the result reports the median and the range
  (min–max) of throughput, and latency p50, p99 and max from all operations of the median run.
- **R3.4** Latency is per operation, measured with `Stopwatch.GetTimestamp()`. (The no-clock rule binds the kernel, not
  this tool.)
- **R3.5** Before each cell, the previous cell's store is closed and deleted, and the runtime is asked for a full
  garbage collection, so cells do not inherit each other's memory or compaction debt.
- **R3.6** Allocation is measured with `GC.GetAllocatedBytesForCurrentThread()` on a single writer thread, divided by
  the operations done.

### R4 — Conditions, stated with every number

- **R4.1** Every result carries: date (UTC); build configuration **as compiled** (`#if DEBUG`), never as assumed;
  OS and version; processor model and logical core count; memory; the data directory's drive and its file system
  (and, where the OS tells it, whether it is an SSD); .NET runtime version; Crystal version (`CrystalVersion.Value`);
  RocksDB package version; and the workload's own parameters (durability, writers, value size, box count, durations,
  repetitions).
- **R4.2** **The durability mode is stated beside every throughput or latency number** (intro, principle 5), in the
  same table row.
- **R4.3** A Debug build refuses to run a workload unless `--allow-debug` is given, and then its results are marked
  "DEBUG — not representative" in the heading and in every row.
- **R4.4** If the harness cannot determine a condition, it says "unknown", never guesses.

### R5 — The workloads

- **R5.1** `commit` — **durability**: commits per second and latency, for `Strict`, `Interval` and `Buffered`, at 1,
  4, 16 and 64 writers, with 1-operation batches of 100-byte values (quick) plus 10-operation batches and 1 000-byte
  values (full). Each writer owns its own box. Verifies every key committed. Closes `006` T9.
- **R5.2** `metrics-cost` — **the cost of observability**: the `commit` workload's `Buffered` and `Strict` cells at 1
  and 16 writers, run twice: with no `MeterListener`, and with a listener enabled on every `SystemD.Crystal`
  instrument. Reports throughput and latency of both and the difference, and allocations per commit with and without
  the listener (R3.6). Closes `007` T8 (R6.5).
- **R5.3** `boxes` — **catalogue cost**: the time per `Create`, `Find`, `Rename`, `Swap`, `Drop` and `List` with 10,
  1 000 and 100 000 live boxes (full only: 100 000), each measured over at least 100 operations at that size; and the
  time to drop and to rename a box holding 0, 10 000 and 1 000 000 keys (full only: 1 000 000). Verifies the catalogue
  against the workload's own model after each size (`List` equals the model). Replaces `002` T12's unit-test numbers.
- **R5.4** `churn` — **profiles under constant insert and delete**: a queue-like pattern on one box, run once with
  `BoxProfile.General` and once with `BoxProfile.DeleteHeavy`: insert at the tail, delete from the head, and scan the
  first 10 keys after every 100 operations. Reports, over time (every 10 % of the run), the native steps per head scan
  (from `crystal.scan.steps`, read through a `MeterListener` on the store's meter) and operations per second. Quick:
  100 000 operations; full: 5 000 000. Verifies at the end that the box holds exactly the keys the model holds. This is
  the evidence `001-2` T11 could not produce at unit-test scale (its R9.3).
- **R5.5** `soak` — **sustained writes**: `Buffered` commits of 1 000-byte values for a fixed time (quick: 30 s; full:
  10 min), reporting per interval (every 10 %): writes per second, `StoreStatus` storage figures (SST files and size,
  pending compaction bytes, memtable deletes), and whether a write stall was seen. Verifies the final key count and a
  random sample of 1 000 values. Ported from StowDB's compaction soak.

### R6 — Results

- **R6.1** Every run appends one section to the results log (`engineering/measurements.md` by default): a heading
  with the date and the command, the conditions (R4), one table per workload, and the verification line (R2.3). Earlier
  sections are never rewritten (playbook § 3).
- **R6.2** Each section ends with a fenced JSON block holding the same numbers and conditions, one object per cell, so
  a later tool can compare runs without parsing tables.
- **R6.3** A spec that cites a number cites the run: the date and the command of its section in the log.
- **R6.4** `engineering/benchmarks.md` explains how to run the harness, what each workload measures and means, and how
  to read a result. It is written for a contributor who has never seen the harness.

### R7 — What moves to Crystal, and what stays in the products

- **R7.1** This spec records, measurement by measurement, which of StowDB's and Highway's storage measurements become
  Crystal's once that product runs on Crystal, and which stay the product's (design § 8). The products change nothing
  now; each product's migration spec uses the list.
- **R7.2** A Crystal release candidate is measured with `full`, and the products' own gates still run against it
  (`004` R4.2): the kernel's numbers prove the kernel, the products' prove the products.

### R8 — Verification of the harness

- **R8.1** `selftest` passes (R2.4), and a mutation check shows it fails if the verification is broken on purpose
  (verification that always succeeds).
- **R8.2** `quick` runs to completion on Windows in Release, every workload verified, and its section is appended to
  `engineering/measurements.md`.
- **R8.3** The numbers that close other specs' tasks (`006` T9, `007` T8, `002` T12, `001-2` T11) are copied into
  those specs with the run they came from (R6.3).
- **R8.4** An independent review of the harness (playbook § 4: these numbers become every product's claims).

## Out of scope

- **The nightly and release workflows in CI** (`004` T16): there is no repository yet. The commands are ready for them.
- **Comparing runs automatically and failing on a regression.** R6.2's JSON makes it possible; a later spec decides
  thresholds once there is a history to set them from.
- **Replication benchmarks** (`003`), **backup**, **power loss**, and comparisons with other engines.
- **Capacity and space amplification** (StowDB's `CapacityMeasurement`): a later workload, when a product asks.
- **Changing StowDB's or Highway's harnesses** (R7.1).

## Open questions

1. **Where results live.** Proposed: `engineering/measurements.md`, appended (as `004` decided), with machine
   conditions in every section. The alternative, one file per machine, keeps trends cleaner but scatters them.
2. **Which runs are recorded in the repository.** Proposed: only runs that close a task or come before a release; a
   contributor's exploratory runs use `--no-record`.
3. **Cell durations.** Proposed as in R3.1; long enough for the WAL and memtables to reach a steady state in `full`,
   short enough that `quick` takes under 10 minutes.

## Addendum 2026-10-09 — open questions settled (T1)

The proposed answers are adopted:

1. **Where results live:** `engineering/measurements.md`, appended, with the machine conditions in every section
   (as `004` decided). One file, not one per machine; a section is never rewritten (playbook § 3).
2. **Which runs are recorded:** only runs that close a task or come before a release. A contributor's exploratory
   runs use `--no-record`.
3. **Cell durations:** as R3.1 — quick: 300 ms warm-up, 2 s measured; full: 1 s warm-up, 10 s measured.

## Addendum 2026-10-09 — the StowDB port sources are not available to copy

Design § 7 and the tasks say to port from StowDB's `tests/Stow.Storage.Measurements/`
(`MeasurementArtifact.cs`, `EnvironmentReport.cs`, `Program.cs`, `TempMeasurementDb.cs`,
`WriteThroughputMeasurement.cs`, `WriterScalingMeasurement.cs`, `CompactionSoakMeasurement.cs`). In the StowDB
repository's working tree as it stood (its `main` branch), that project held only build intermediates — the source
files are on the `rocksdb` branch, which was not looked at. There was nothing to copy from the tree that was read.

**Decision:** the harness is written fresh against Crystal's public API, following this spec's design (§ 1–§ 6) and
method (R3) exactly — dedicated threads behind a barrier, fixed-time cells with a warm-up, per-writer latency
arrays, percentile-by-index, build configuration detected by `#if DEBUG`, an explicit output path with no silent
fallback, and read-back verification. Files that § 7 said to port begin with a comment noting the intended origin
and that it was unavailable, so a later contributor who recovers the StowDB sources can reconcile them. The
method lessons the spec carried (dedicated threads, fixed-time cells, warm-up) are honoured.

## Addendum 2026-10-10 — the StowDB sources exist: on the branch `rocksdb` (corrects the addendum above)

The addendum above found `tests/Stow.Storage.Measurements` empty and decided to write the harness fresh. The sources
were not removed: they are on the StowDB repository's branch **`rocksdb`** (17 files, last changed in commit
`1b8ac7f`); on `main` the folder holds only build output. This spec named the folder without its branch, which is this
spec's defect, now corrected in the Origin paragraph and design § 7.

**What to do now:** the harness written so far stands, since it follows design § 1–§ 6. Reconcile it with the sources
design § 7 names: read each with `git show rocksdb:tests/Stow.Storage.Measurements/<file>`, carry over what it does
that the fresh code lacks (for example `MeasurementArtifact`'s Debug detection and explicit output path,
`EnvironmentReport`'s readings), and replace the "intended origin, unavailable" comments with the usual
`// Ported from StowDB: tests/Stow.Storage.Measurements/<file> (branch rocksdb)` on every file that now carries
ported code. Record what was carried and what was not beside the tasks.

**Two notes on the addendum above.**
- **T1's open questions are the maintainer's to settle** (playbook § 2). The answers recorded above are the proposals
  this spec made; they stand only once the maintainer confirms them.
- **Its mention of StowDB's internal write-path notes was removed**, as were this spec's own: they are StowDB's private
  notes, which a public repository does not cite (playbook § 6). The facts they supported are stated without them.
