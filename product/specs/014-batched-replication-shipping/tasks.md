# 014 — Batched replication shipping: tasks

> Drafted 2026-10-15. **Executed 2026-10-16; see each task and design addendum A1–A6.** The maintainer picks the tasks taken; nothing is committed without the maintainer's go-ahead.
> Ordered: measure first (T1), then the commit path (T2–T4), then the smaller fixes (T5–T7), then acceptance (T8).

## Measure first

- [x] **T1** Add `crystal.replication.pull.source` {`tail`, `wal`} (R4.1) and a lag sampler for the harness (R3.1, design
  § 7); run Highway's harness against rc.1 and record the baseline, the WAL share and lag in
  `engineering/measurements.md`. Done when the baseline table matches the requirements' (±10 %) and the WAL share is known. _Done 2026-10-16: `crystal.replication.pull.source` {tail, wal} with `crystal.pull.miss` {behind, landing}, and `crystal.replication.ship_delay`. Baseline on rc.1 (two runs, published, and once packed locally): commit p50 3.52–3.55 ms, RPC p50 3.71–3.72 ms, 5 880 / 0, queue e2e 66–69 ms. **Did not match the requirements' table ±10 %:** the harness did not saturate this day (5 880 handled, 0 failed); Highway 3.0 reproduced its reference exactly (3.38 ms, 48 ms), and the hook-off variant reproduced (2.20 ms). WAL share 0 % (15 854 pages for 16 070 commits, all from the tail). Lag from `lag_time` at 100 ms resolution was too coarse, so T4's exact lag time serves the harness instead._

## The commit path (Must)

- [x] **T2** `ChangeSignal` as an awaitable, coalescing signal (design § 3; R2.3, R5.1); `HandlePull` →
  `HandlePullAsync` (design § 5). Test: 1 000 marks with one pull waiting wake it once; a wait holds no thread. Done when
  the replication suite passes unchanged. _Done: `ChangeSignal` awaitable and coalescing, with a separate urgent channel (design addendum A2); `TryHandle` → `TryHandleAsync`, `HandlePullAsync`. Waits use a timer, not the clock (A5). Tests in `BatchedShippingTests`: 1 000 commits wake a waiting pull once; 500 waits started on one thread hold no thread and all wake on one change; a change before the mark does not end a wait; a commit does not end an urgent wait. The replication suite passes unchanged in what it asserts (the source tests call the async handler through a synchronous test extension)._
- [x] **T3** The gather window: `ReplicationOptions.GatherWindow` (default 2 ms), applied by a caught-up pull after its
  wake; catch-up unchanged (R2.1, R2.2, R2.4). Done when the replication suite passes and T8's lag bound holds. _Done differently (design addendum A2, A3): the window paces a standby near the head rather than gathering after a wake, and its default is **10 ms**, not 2 ms. At 2 ms the harness showed no gain at all. Tests: a near-head pull is paced and ships the window's commits in one reply, a pull after a full page is not paced, and a role or epoch change ends the pacing at once. Lag per T8._
- [x] **T4** The commit hook reduced to `Tail.Add` + `Mark` (design § 2; R1.1, R1.3); `CommitTimeline` sampled by the
  pull and the status read (Q2). Done when lag-in-time reporting tests pass unchanged. _Done differently (design addendum A4): the commit hook is `Tail.Add` (with the commit time), the timeline's lock-free fast path, and `Signal`; the timeline is not sampled by the pull. Lag in time is now exact from the tail where it holds the batch. Lag-time tests pass unchanged; new tests for the timeline's fold and the exact lag._

## The smaller fixes

- [x] **T5** The tail's end (design § 4; R4.2) — only if T1 shows a WAL share above 1 %. Test: a pull whose next
  sequence is still being added gets an empty page, not a WAL read. Otherwise mark this task not needed, with T1's number. _Not needed: T1 measured 0 % WAL pages (0 of 15 854). Nothing built beyond the miss classification of T1 (design addendum A6)._
- [x] **T6** `DeleteHeavy` scan-flush triggers measured off (design § 6; R6.1); retuned or dropped per R6.2, or kept with
  the measurement recorded. _Measured, kept: with the scan-flush triggers off, commit p50 2.07–2.15 ms against 2.18–2.22, RPC p50 2.35–2.51 against 2.40–2.45, queue e2e 24–26 ms against 22–23, scan steps p99 unchanged: within noise, no cost shown, so `DeleteHeavy` is unchanged (R6.2)._
- [x] **T7** Check: no task changed the format, catalogue, encoder or wire protocol (R8.1, R8.2); a standby on rc.1
  follows a primary on this build and the reverse (one run of the existing cross-version test, or by hand with the
  harness). _Done: no change to `ReplicationProtocol`, `BatchApplier`, the catalogue, the encoder or any on-disk format. By hand with the harness: a 014 primary with an rc.1 standby and the reverse stayed healthy under load and ended with the standby at the primary's latest sequence, no slot dropped._

## Acceptance and documents

- [x] **T8** Highway's harness, two runs each side (design § 7): R7.1–R7.3 met; results in
  `engineering/measurements.md` (R7.4). _Done (two runs): commit p50 2.18–2.22 ms, RPC p50 2.40–2.45 ms (hook off 2.42 ms, Highway 3.0 3.38 ms: R7.1 met), 5 880 / 0 (R7.2 met), queue e2e 22–23 ms. R1.2: 2.17 ms with no standby attached, within 2 %. **R3.1 not met as defined:** the per-second `lag_time` read median 14–15 ms (limit 10 ms), p99 16 / 46 ms (limit 50 ms: met). Those reads land near the end of each 15.6 ms window; sampled at irregular moments in-process, median 9.3 ms, p99 21.7 ms, but that is not R3.1's measurement. For the maintainer (design addendum A3)._
- [x] **T9** `engineering/replication.md` (the tail, the long poll, the gather window), the `ReplicationOptions`
  reference, `CHANGELOG.md` (R8.3). _Done: `engineering/replication.md` § 4 and § 10, the `ReplicationOptions.GatherWindow` XML reference, `engineering/measurements.md`, `CHANGELOG.md` (Unreleased)._

**Verification (2026-10-16).** `dotnet build -c Release` with 0 warnings; 16 architecture, 19 process and 688 unit tests
pass. An independent review found no defect in the signal, pacing or timeline logic; its findings (an unvalidated
window, the urgent mark read late, pages counted before the oversize check, a timestamp of 0 taken as unknown, R3.1
overclaimed in the documents, wording) were fixed or stated, and R3.1 is left to the maintainer. Mutation checks, each guard broken on purpose and each caught by a named test in `BatchedShippingTests` or
`SlotTests`: N1 no pacing · N2 a standby behind paced too · N3 a waiter re-arming on its way out (a wake for nobody) ·
N4 the pacing wait ended by a commit · N5 a role or epoch change not urgent · N6 a commit completing no waiter · N7 a
late completion ending a wait whatever the counter says · N8 the timeline not folding at a new sample · N9 a read not
folding what is pending · N10 lag time ignoring the tail · N11 the tail's commit time ignoring a batch spanning the
cursor · N12 a synchronous long poll · N13 the gather window not validated.
