# 008 — Benchmark harness: design

## 1. Layout

```
tools/SystemD.Crystal.Bench/
  SystemD.Crystal.Bench.csproj   Exe; IsPackable false; IsPublishable false; references src/SystemD.Crystal only
  Program.cs                     command line (R1.2, R1.3): parse, run, exit code (R1.5)
  Conditions.cs                  the run's conditions (R4.1), build configuration as compiled (R4.3)
  Cell.cs                        one timed configuration: warm-up, dedicated threads, repetitions, percentiles (R3)
  Results.cs                     the markdown section and its JSON block, appended to the log (R6)
  Verification.cs                a model of what was written, and the read-back that compares (R2)
  TempStore.cs                   a fresh store under --dir, deleted after (R1.4)
  Workloads/
    CommitWorkload.cs            R5.1
    MetricsCostWorkload.cs       R5.2
    BoxesWorkload.cs             R5.3
    ChurnWorkload.cs             R5.4
    SoakWorkload.cs              R5.5
    SelfTest.cs                  R2.4
engineering/benchmarks.md        how to run and read it (R6.4)
engineering/measurements.md      the results log (R6.1), created by the first recorded run
```

Add the project to `SystemD.Crystal.slnx` under a new `/tools/` folder. It inherits `Directory.Build.props`
(warnings as errors, nullable on, C# 14). It is not a test project: `dotnet test` must not run it.

## 2. Command line

```
dotnet run -c Release --project tools/SystemD.Crystal.Bench -- <command> [options]

commands:  env | selftest | commit | metrics-cost | boxes | churn | soak | quick | full
options:   --size quick|full      (default quick; `quick` and `full` commands set it)
           --dir <path>           data directory parent (default: system temp folder)
           --out <path>           results log (default: <repo>/engineering/measurements.md)
           --no-record            print, do not append
           --allow-debug          run from a Debug build, marked as such
```

The repository root is found by walking up from `AppContext.BaseDirectory` to the folder holding
`SystemD.Crystal.slnx`; if it is not found and `--out` is not given, the run refuses with that message (never writes
somewhere unexpected — StowDB's harness once wrote every result into the wrong spec's folder through a fallback path).

## 3. A cell (R3)

```csharp
sealed record CellResult(
    string Workload, string Configuration,       // e.g. "commit", "Strict · 16 writers · 1 op · 100 B"
    DurabilityMode? Durability,                  // stated beside every number (R4.2)
    int Writers, double OpsPerSecMedian, double OpsPerSecMin, double OpsPerSecMax,
    double P50Us, double P99Us, double MaxUs, long Operations, double? AllocatedBytesPerOp);

static CellResult Run(CellSpec spec, Func<int, WriterState> prepare, Action<WriterState> oneOperation, ...)
```

- `prepare(writerIndex)` runs on the writer's thread before the barrier (its box handle, its key counter, its random).
- Writers start together behind a `Barrier`; each loops `oneOperation` until a shared `stop` flag is set by the
  timing thread after the warm-up plus the measured time. Only operations whose start falls inside the measured window
  count. Latencies go into a per-writer preallocated `long[]` of tick deltas (grown by doubling), merged after the run:
  no shared collection on the hot path (StowDB's `ConcurrentBag` serialised its writers).
- Percentiles: sort, then index `(int)(count * q)`, clamped (StowDB's `MeasurementArtifact.Percentile`).
- Repetitions (R3.3): each repetition is a fresh store (R3.5). The median repetition by throughput supplies the
  latencies.

## 4. Verification (R2)

```csharp
sealed class WrittenModel     // what the workload meant to write, per box
{
    void Put(Box box, byte[] key, byte[] valueHashOrValue);
    void Delete(Box box, byte[] key);
    VerificationResult Verify(CrystalDatabase db);   // snapshot scan of each box, compared with the model
}
sealed record VerificationResult(bool Passed, long KeysChecked, int Boxes, IReadOnlyList<string> FirstDifferences, long DifferenceCount);
```

- Writers record into their own model shard (no lock); shards merge after the run.
- Values are generated deterministically from `(writer, sequence)`, so the model may store only the key and recompute
  the value at verification time; this keeps the model's memory small at full size.
- Scans use `Snapshot.Scan` with a limit and continue from the last key until the box is exhausted.
- `selftest` (R2.4): run `commit` at `Buffered`, 4 writers, 1 s; then, through the public API and outside the model,
  delete one key and overwrite another; verify; pass only if the result lists exactly those two differences.

## 5. The workloads (R5)

| Workload | Store setup | One operation | Reported |
|---|---|---|---|
| `commit` | one database; one box per writer | a batch of N puts to the writer's box, `Commit(mode)` | ops/s (batches/s), p50/p99/max per batch, per mode × writers × batch × value |
| `metrics-cost` | as `commit` | as `commit` | both runs side by side, with the difference in %; bytes allocated per commit (one writer, R3.6) |
| `boxes` | one database, grown to 10 / 1 000 / 100 000 boxes | each catalogue call, timed singly | µs per call at each size; drop and rename time against box size |
| `churn` | one box per profile, a fresh store each | 100 ops of tail insert + head delete, then a head scan of 10 keys | per 10 % of the run: ops/s and steps per head scan, General against DeleteHeavy side by side |
| `soak` | one box, `Buffered` | a 1 000-byte put | per 10 %: writes/s and `Status()` storage figures, stall seen yes/no |

- `boxes` measures single calls, not a fixed-time cell; it reports the median and p99 of at least 100 calls at each
  size, after growing the catalogue to that size.
- `churn` reads `crystal.scan.steps` through a `MeterListener` enabled only on instruments whose meter is named
  `SystemD.Crystal` and carries the tag `crystal.node` equal to the store's `NodeId` (`007` R1.1), so a parallel store
  never pollutes it.
- `metrics-cost` with a listener: enable every instrument of the store's meter and set measurement callbacks that do
  no work beyond adding to a local counter, so the cost measured is Crystal's recording, not the listener's.
- `soak` calls `store.Status()` once per interval, on the timing thread.

## 6. Results (R6)

One appended section per run:

````markdown
## 2026-10-12 14:03 UTC · `quick`

**Conditions:** Release · Windows 11 Pro 10.0.26200 · AMD Ryzen 9 7950X, 32 logical · 64 GB ·
data on C:\ (NTFS, SSD) · .NET 10.0.x · Crystal 0.1.0-preview.1 · RocksDB 11.8.1.5032

### commit — 100 B values, 1-op batches, 2 s cells × 3

| Durability | Writers | Commits/s (median) | Range | p50 µs | p99 µs | max µs |
|---|---:|---:|---|---:|---:|---:|
| Strict | 1 | … | … | … | … | … |

Verified 1 234 567 keys in 4 boxes.

```json
[{"workload":"commit","durability":"Strict","writers":1, ... }]
```
````

The `**Conditions:**` line is built by `Conditions.cs`; a value it cannot read is written as `unknown` (R4.4).

## 7. Reuse: copy and adapt (playbook, *Reuse before writing*)

Copied files begin with `// Ported from StowDB: <path>`.

| Source (StowDB repository, branch `rocksdb`, `tests/Stow.Storage.Measurements/`) | Lines | Becomes | Keep | Change |
|---|---|---|---|---|
| `MeasurementArtifact.cs` | 142 | `Conditions.cs`, `Results.cs`, `Cell.cs` (percentiles) | build configuration detected by `#if DEBUG`; the explicit output path with no fallback; percentile indexing | one results log instead of per-spec artifact folders; JSON block; durability column |
| `EnvironmentReport.cs` | 138 | `Conditions.cs` | OS, CPU, RAM, disk, .NET, RocksDB version | Crystal version; "unknown" instead of guesses |
| `Program.cs` | 188 | `Program.cs` | one command per measurement, explicit output path | Crystal's commands and options (§ 2) |
| `TempMeasurementDb.cs` | 69 | `TempStore.cs` | a fresh database in a temp folder, deleted after | a Crystal store, under `--dir` |
| `WriteThroughputMeasurement.cs` | 272 | `Workloads/CommitWorkload.cs` | the 1/4/16/64 writer sweep, warm-up, p50/p99 | durability modes; fixed-time cells on dedicated threads instead of fixed counts on pool tasks; verification |
| `WriterScalingMeasurement.cs` | 190 | folded into `CommitWorkload.cs` | the question it asks: is the limit Crystal's locking or RocksDB's WAL? | answered by comparing `Strict` across writer counts with `Buffered`; no stripe sweep (Crystal's stripes are fixed) |
| `CompactionSoakMeasurement.cs` | 190 | `Workloads/SoakWorkload.cs` | per-interval throughput, stall and size series | through Crystal's public API and `Status()`, not raw RocksDB; verification |

Method lessons carried from StowDB's own measurements of its write path: dedicated threads, fixed-time cells, a
warm-up. (Thread-pool tasks blocking in native code measured the pool's thread injection, not the engine.) **Not copied:** `QueryShapes`, `IntersectionCost`,
`CompositionCost`, `CoveredProjection`, `SkipScanScaling`, `IndexAmplification`, `MetaFootprint`,
`InlineThresholdSweep`, `CapacityMeasurement` (StowDB's document and query features, or out of scope).

## 8. What moves to Crystal, and what stays (R7)

When a product runs on Crystal, its measurements of mechanisms Crystal now owns retire in favour of this harness's;
its measurements of its own model stay.

| Product measurement | After the product runs on Crystal |
|---|---|
| StowDB `WriteThroughputMeasurement` (raw writes per writer count) | **moves**: `commit` |
| StowDB `WriterScalingMeasurement` | **moves**: `commit` across writer counts |
| StowDB `CompactionSoakMeasurement` | **moves**: `soak` |
| StowDB `CapacityMeasurement` | **moves** when Crystal adds a capacity workload; until then stays |
| StowDB `QueryShapes`, `IntersectionCost`, `CompositionCost`, `CoveredProjection`, `SkipScanScaling`, `IndexAmplification`, `MetaFootprint`, `InlineThresholdSweep` | **stay**: StowDB's query and document model |
| StowDB `Stow.Server.Measurements` (HTTP end to end) | **stays** |
| Highway raw commit and sync throughput | **moves**: `commit` (`Strict`) |
| Highway poll-cost probe | **stays**, and becomes a release gate for Crystal (`004` R4.2); `churn` measures the kernel half of it |
| Highway end-to-end messages per second, failover behaviour | **stay** |

A product's migration spec retires the moved measurements, naming the Crystal workload that replaces each.

## 9. Risks

| Risk | Mitigation |
|---|---|
| Numbers from a noisy machine read as Crystal's | Conditions in every section; range reported with every median; specs cite the run |
| The harness measures itself (allocations, contention in its own bookkeeping) | Per-writer arrays, no shared collections on the hot path; values derived, not stored |
| `full` too long to run often | `quick` under 10 minutes; `full` before releases and nightly once CI exists |
| A verification bug passes bad data | `selftest` and its mutation check (R2.4, R8.1) |
