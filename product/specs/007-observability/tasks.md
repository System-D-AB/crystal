# 007 — Observability: tasks

> The maintainer approves the open questions and chooses which tasks to start. T1–T3 come before `006` T6.

- [x] **T1** Settle the open questions (per-database tags, `Status()`, the test helper, the slow-commit default).
  Done when the answers are recorded in `requirements.md` by dated addendum.
- [x] **T2** `CrystalMetrics` with the meter, `IMeterFactory`, tag rules and the commit, lock and scan instruments
  (R1, R2.1, R2.3, R2.4; design § 2), and the `MetricsCapture` test helper. Done when each instrument's test passes
  and recording allocates nothing (measured).
- [x] **T3** `CrystalLogEvents` and `CrystalLog`; move the existing `ILogger` calls onto them; add the events of R3.3
  that exist today (R3; design § 3). Done when each event's test passes.
- [x] **T4** Storage and store gauges (R2.5–R2.7), with the `StorageGauges` test `001-2` T9 never had (R6.3). Done
  when each gauge moves with its cause and a drop during collection is skipped.
- [x] **T5** `CrystalStore.Status()` (R4; design § 4). Done when it matches the gauges and leaves out a closing
  database.
- [x] **T6** Write-stall detection on the store timer (R5). Done when a test forces a stall (tiny write buffers, no
  compaction) and sees both transitions logged.
- [x] **T7** The sync instruments and events, once `006` T6 exists (R2.2, R3.3 `Critical`).
- [x] **T8** The architecture rule of R6.4; mutation checks of the guards; the cost with and without a listener from
  the benchmark harness (R6.5). _Done. The R6.4 rule and 11 mutations were already done; the cost is now measured by
  spec 008's `metrics-cost` workload. **Run cited:** `engineering/measurements.md`, section `2026-10-09 22:46 UTC ·
  quick`. commit, no-listener vs listener on every `SystemD.Crystal` instrument:_

  | Durability | Writers | No listener/s | Listener/s | p99 µs none→listener | alloc B/commit none→listener |
  |---|---|---|---|---|---|
  | Buffered | 1 | 225 368 | 213 993 | 10→12 | 1 640 → 1 640 |
  | Buffered | 16 | 142 813 | 137 236 | 245→616 | 1 527 → 1 472 |
  | Strict | 1 | 855 | 828 | 2 105→2 019 | 1 472 → 1 472 |
  | Strict | 16 | 6 454 | 6 522 | 5 376→3 859 | 1 472 → 1 472 |

  _The reading R6.5 wanted: **a listener allocates nothing extra per commit** (identical B/commit) and the
  throughput difference is within the run-to-run range (the Strict rows even cross over) — recording an instrument is
  a branch and a histogram add, not an allocation. These are `quick` numbers; a `full` run supersedes them._
- [x] **T9** `engineering/observability.md`: every instrument and event as built, for operators. Update `003` R13 to
  build on it.

## Progress 2026-10-09

- **T1** _Done 2026-10-09: settled as proposed (requirements addendum)._
- **T2** _Done. Allocation per recording not yet measured: that needs the benchmark harness (T8)._
- **T3** _Done: 28 events in `CrystalLogEvents`; every existing log call moved onto `CrystalLog`; a test keeps the two tables in step._
- **T4** _Done. Gauges are read without the store gate, from the open-database array (006 review fix), and stop at close: the shared-cache read takes the same lock as `Dispose`. A meter from `IMeterFactory` is not disposed by the store._
- **T5** _Done; the shared block cache is on `StoreStatus`, not per database._
- **T6** _Done differently: a stall cannot be forced reliably, so the test drives the check through an internal probe (requirements addendum, R5.2)._
- **T7** _Done: `crystal.wal.sync.*` and the failure counter; `DatabaseFailed` and `RegistryFailed` carry event ids. A strict commit's sync is counted in its commit (addendum, R2.2)._
- **T8** _Partly done: the R6.4 rule with a planted violation, and 11 mutations (below). The cost with and without a listener waits for the benchmark harness._
- **T9** _Done: `engineering/observability.md`. `003` R13 builds on it when replication is built._

**Mutation checks**, each a guard broken in the real source, rebuilt, the named test seen to fail, the file restored
byte-identical: O1 a refused commit counted as ok · O2 a failed commit not counted as failed · O3 an uncontended lock
recorded as a wait · O4 scan steps not recorded · O5 a close sync counted as an interval sync · O6 gauges reading after
close · O7 per-database tagging ignored · O8 a stall logged on every check · O9 a log id out of step with the table ·
O10 a failed database not counted · O11 a failed sync not kept under the lock (caught 3 runs of 3).

**Found along the way, in `006`:** `SyncAll` could still report success after a sync it waited for had failed: the
failure was recorded only after the sync lock was released. 007's timing exposed it as a failing test; the instance now
keeps a sync failure under the lock (mutation O11). Recorded in `006`'s requirements.

**After the independent review** (requirements addendum): five more mutations, each caught: P1 the meter disposed
before the databases close · P2 a closed store still reporting · P3 stores sharing a meter · P4 a failure counted per
caller · P5 a repaired name file not logged. O6 and P2 at first were not caught: once the meter is disposed at close,
the "closed" guards matter only while the store is closing, so a test now collects at that moment.

**Final, 2026-10-09:** `dotnet build -c Release` 0 warnings; **275 tests pass** (16 architecture, 259 unit). Open: T8's
cost measurement, which waits for a benchmark harness.
