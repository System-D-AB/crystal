# Benchmarks

> How to run Crystal's benchmark harness, what each workload measures, and how to read a result (spec 008 R6.4).
> Written for a contributor who has never seen the harness.

Crystal's performance claims come from one place: the harness in `tools/SystemD.Crystal.Bench`. The playbook allows
no number from anywhere else, and every number is stated beside the durability mode and the conditions it was
measured under. The harness is a console program, not a test — `dotnet test` never runs it — and it uses only
Crystal's public API, as a product would.

## Running it

```
dotnet run -c Release --project tools/SystemD.Crystal.Bench -- <command> [options]
```

Always build **Release**: a Debug build refuses to run a workload unless you pass `--allow-debug`, and then every
number is marked "DEBUG — not representative".

| Command | What it does |
|---|---|
| `env` | print the machine conditions and exit |
| `selftest` | prove the read-back verification can fail (writes, tampers behind the model's back, expects the tamper caught) |
| `commit` | durability: commits/s and latency for Strict, Interval, Buffered at 1/4/16/64 writers |
| `metrics-cost` | the cost of observability: commit with and without a `MeterListener` |
| `boxes` | catalogue cost: Create/Find/Rename/Swap/Drop/List at growing box counts, and drop/rename vs box size |
| `churn` | profiles under constant insert/delete: General vs DeleteHeavy, head-scan steps over time |
| `soak` | sustained writes with `Status()` storage figures over time, and a stall watch |
| `quick` | every workload in its quick size (minutes) |
| `full` | every workload in its full size (longer; before a release) |

| Option | Meaning |
|---|---|
| `--size quick\|full` | measured durations (default quick; `quick`/`full` set it) |
| `--dir <path>` | the data directory parent — the disk under test (default: system temp) |
| `--out <path>` | the results log (default: `<repo>/engineering/measurements.md`) |
| `--no-record` | print only, do not append (use for exploratory runs) |
| `--allow-debug` | run from a Debug build, marked as such |

The exit code is 0 only if every workload passed its read-back verification. A workload that cannot verify what it
wrote reports FAILED, records no numbers, and makes the run non-zero.

**Which runs to record.** Only runs that close a task or come before a release are appended to
`engineering/measurements.md`. Exploratory runs use `--no-record`.

## The method, in one paragraph

Throughput is measured in **cells**: one configuration (workload, durability, writers, value size) run for a fixed
time after a warm-up — quick is 300 ms warm-up then 2 s measured, full is 1 s then 10 s — not for a fixed count.
Concurrent writers run on **dedicated threads**, started together behind a barrier, never on thread-pool tasks (a
thread-pool run measures the pool's thread injection, not the engine). Each cell runs 3 times (quick) or 5 (full);
the result reports the **median** throughput and the **range**, and latency p50/p99/max from the median run.
Latency is per operation via `Stopwatch.GetTimestamp()`. Before each cell the previous store is closed and deleted
and a full GC is forced, so cells do not inherit each other's memory or compaction debt. Allocation is measured on a
single writer thread with `GC.GetAllocatedBytesForCurrentThread()`.

## Reading a section

Each recorded run appends one section to `engineering/measurements.md`. Earlier sections are never rewritten. A
section has:

1. **A heading** with the UTC date and the command (`## 2026-10-10 14:03 UTC · ` + the command).
2. **A conditions line** — build configuration *as compiled*, OS, processor and cores, memory, the data drive and
   its file system, .NET, Crystal and RocksDB versions. A value the harness cannot read is `unknown`, never a guess.
3. **One table per workload**, with the durability mode in every throughput/latency row.
4. **A verification line** — what was checked ("verified 40 000 keys in 4 boxes"), not just "OK".
5. **A JSON block** with the same numbers and conditions, one object per cell, so a later tool can compare runs
   without parsing tables.

A number is only meaningful with its row's durability and the section's conditions. When a spec cites a number, it
cites the run: the date and command of its section.

## What each workload proves

- **`commit`** closes spec 006's durability question: how many commits per second, and at what latency, each
  durability mode sustains as writers scale. Comparing Strict across writer counts against Buffered shows whether the
  limit is Crystal's locking or RocksDB's WAL (group commit).
- **`metrics-cost`** closes spec 007's question: what observability costs. It runs the same commits with no listener
  and with a listener on every instrument, and reports the throughput and latency difference and the bytes allocated
  per commit either way.
- **`boxes`** replaces spec 002's unit-test catalogue timings with measured ones, and shows that create/find/
  rename/swap/drop are O(1) in the box count and that drop and rename do not depend on how much a box holds.
- **`churn`** is the kernel half of Highway's poll-cost probe and the evidence spec 001-2 could not produce at
  unit-test scale: under constant tail-insert/head-delete, the DeleteHeavy profile keeps head-scan steps flat where
  General grows, because delete-aware compaction and the memtable scan-flush trigger clear tombstones at the head.
- **`soak`** is StowDB's compaction soak: sustained writes for minutes, with the SST file count and size, pending
  compaction bytes, and whether a write stall was seen, reported over time — the footprint reaches a steady state
  rather than growing without bound.

## What moves to Crystal, and what stays in the products (spec 008 § 8)

When a product runs on Crystal, its measurements of mechanisms Crystal now owns retire in favour of this harness's;
its measurements of its own model stay.

| Product measurement | After the product runs on Crystal |
|---|---|
| StowDB `WriteThroughputMeasurement`, `WriterScalingMeasurement` | **move** to `commit` |
| StowDB `CompactionSoakMeasurement` | **moves** to `soak` |
| StowDB `CapacityMeasurement` | **moves** when Crystal adds a capacity workload; until then stays |
| StowDB query/document measurements (`QueryShapes`, `IntersectionCost`, `CompositionCost`, `CoveredProjection`, `SkipScanScaling`, `IndexAmplification`, `MetaFootprint`, `InlineThresholdSweep`) | **stay**: StowDB's model |
| StowDB `Stow.Server.Measurements` (HTTP end to end) | **stays** |
| Highway raw commit/sync throughput | **moves** to `commit` (Strict) |
| Highway poll-cost probe | **stays**, and becomes a release gate for Crystal; `churn` measures the kernel half |
| Highway end-to-end messages/sec, failover | **stay** |

A product's migration spec retires the moved measurements, naming the Crystal workload that replaces each.
