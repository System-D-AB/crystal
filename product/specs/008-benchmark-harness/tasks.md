# 008 — Benchmark harness: tasks

> The maintainer approves the open questions and chooses which tasks to start. Read `product/playbook.md` first;
> it binds every contributor. Every task ends with `dotnet build -c Release` clean (warnings are errors) and
> `dotnet test -c Release` green; the harness itself is not a test project and must not run under `dotnet test`.
> Port the files design § 7 names rather than writing anew; each copied file begins with
> `// Ported from StowDB: <path>`. When a task is done differently from what is written, say so beside it.
>
> **Progress 2026-10-09:** T1–T14 implemented; `dotnet build -c Release` clean (0 warnings); the Bench project is
> not picked up by `dotnet test`; `selftest` passes and its mutation check fails as it must; `quick` runs every
> workload verified and records a section to `engineering/measurements.md`. **T12's `full` run and T15 (independent
> review) are the maintainer's to schedule.** Sources were reconciled against the StowDB `rocksdb` branch (see the
> requirements addendum 2026-10-10): the harness carries their method and shape with the corrections the spec
> requires (fixed-time cells, dedicated threads, verification), not a literal copy of the old anti-patterns.

- [x] **T1** Settle the open questions. _Done: adopted as proposed (results → `engineering/measurements.md` appended;
  only task-closing/pre-release runs recorded; durations quick 300 ms/2 s, full 1 s/10 s), recorded by dated addendum
  in `requirements.md`. These are the proposals; the maintainer confirms them (playbook § 2)._
- [x] **T2** The project and its command line. _Done: `tools/SystemD.Crystal.Bench` (Exe, not packable/publishable,
  references only `src/SystemD.Crystal`), added to the solution under `/tools/`; `Program.cs` with every command and
  option; `RepositoryRoot` walks up to the solution and refuses rather than falling back; `TempStore.cs` ported.
  `env` prints and exits 0; an unknown command prints usage and exits 2; `dotnet test` does not pick the project up._
- [x] **T3** Conditions. _Done: `Conditions.cs` ported from `EnvironmentReport.cs`/`MeasurementArtifact.cs` (branch
  rocksdb). `#if DEBUG` build detection; OS, cores, memory, data drive and file system, .NET/Crystal/RocksDB
  versions; "unknown" where unreadable (the processor model is not portably available, so it reports the architecture
  and "unknown"). Debug refusal + `--allow-debug` marking. `env` shows each item._
- [x] **T4** The cell runner. _Done: `Cell.cs` — warm-up, fixed measured window, dedicated `new Thread` writers
  behind a `Barrier`, per-writer `long[]` latency arrays merged after, repetitions with a fresh store each, median +
  range, p50/p99/max, allocation per op on one thread (`GC.GetAllocatedBytesForCurrentThread`)._
- [x] **T5** Verification and `selftest`. _Done: `WrittenModel` with values derived from (writer, sequence) so the
  model stores only keys; `Verify` by paged snapshot scans comparing presence, value, count and nothing-extra;
  `VerificationResult` with counts and first differences. **Mutation check recorded:** making `Verify` always pass
  (rebuild) makes `selftest` FAIL (exit 1) because the two planted differences go uncaught; restored, `selftest`
  passes (exit 0). The guard was seen to fail (R8.1)._
- [x] **T6** Results. _Done: `Results.cs` appends one markdown section per run — heading, conditions line, one table
  per workload with durability in every row, verification line, JSON block — and only ever appends (two runs append
  two sections; earlier text untouched). `--no-record` and `--out` honoured._
- [x] **T7** `commit`. _Done: ported from `WriteThroughputMeasurement.cs`/`WriterScalingMeasurement.cs` (branch
  rocksdb), changed to durability modes, fixed-time cells on dedicated threads, and verification. Runs every mode at
  1/4/16/64 writers, verifies every key, records its section. Numbers in `engineering/measurements.md`._
- [x] **T8** `metrics-cost`. _Done: reports throughput and p99 with and without a `MeterListener` on every
  `SystemD.Crystal` instrument of the store's node, and bytes allocated per commit either way (one writer). Finding:
  a listener allocates nothing extra per commit._
- [x] **T9** `boxes`. _Done (quick: 10 and 1 000 boxes; boxes of 0 and 10 000 keys), verified against the model
  (List equals the model). Full adds 100 000 boxes and a 1 000 000-key box. Replaces 002 T12's unit-test numbers
  (002 T13)._
- [x] **T10** `churn`. _Done: the two profiles side by side per 10 % of the run, scan steps read through a
  `MeterListener` filtered to the store's node, both boxes verified against the model. **Note:** at the quick size
  (100 000 ops) both profiles show a flat 10 steps/scan — the divergence R9.3 wants needs the `full` size; recorded
  honestly in 001-2 T11._
- [x] **T11** `soak`. _Done: ported from `CompactionSoakMeasurement.cs` (branch rocksdb), changed to time-based and
  to Crystal's `Status()` figures with real stall detection (`WriteStopped`) rather than the "stalls = 0" placeholder
  StowDB left. Quick (30 s) shows ten intervals with SST files/size, pending compaction and memtable deletes, and a
  verified key count and 1 000-value sample._
- [x] **T12** `quick` and `full`. _`quick` run on Windows/Release and recorded to `engineering/measurements.md`
  (section `2026-10-09 22:46 UTC · quick`), every workload verified (R8.2). **`full` not yet run** — it takes long
  enough (soak 10 min, churn 5 000 000 ops) that the maintainer should schedule it; the command is
  `dotnet run -c Release --project tools/SystemD.Crystal.Bench -- full`._
- [x] **T13** Copy the numbers into the specs they close, each citing the run. _Done: 006 T9 (commits/s and p99 per
  mode × writers), 007 T8 (metrics cost and allocations), 002 T12 (replaced the unit-test timings, old kept under a
  dated note), 001-2 T11 (the `churn` result, with the honest note that the divergence needs `full`). Each cites the
  run `2026-10-09 22:46 UTC · quick`._
- [x] **T14** `engineering/benchmarks.md`. _Done: how to run each command, what each workload means and proves, how to
  read a section, and the "what moves, what stays" table. `004` tasks T8 and T16 updated to point here._
- [ ] **T15** An independent review of the harness by a second contributor or a fresh agent (R8.4). _Pending — this is
  a human/second-agent step. The harness's numbers become every product's claims, so this review matters; its
  findings are fixed or recorded by dated addendum._
