# 014 — Batched replication shipping

> **Status:** implemented 2026-10-16 (R4.2 not needed; R2.3's default is 10 ms, see design addendum A3). Drafted 2026-10-15.
> **Depends on:** `003-replication`, `011-replication-completion`, `006-durability`.
> **Supersedes** `013` R1 (Strict commit tail latency): R1 asked to find where the tail comes from; this spec is what
> was found, and what to change.

## Introduction

Highway 3.0 on Crystal 1.0.0-rc.1 handles an RPC about **25 % slower** than Highway 3.0 on its own RocksDB store
(server-side p50 3.8 ms against 3.0 ms), handles about 17 % fewer RPC calls under the same load, and under a saturated
load lets three times as many calls reach their deadline. Both engines spend about 90 % of a command in RocksDB's
native synced write; GC, the thread pool, Crystal's catalogue gate, box creation, the write-buffer manager and the
shared environment were measured and ruled out.

The cost was isolated by an A/B series against a local Crystal build (Highway's load harness, a primary and a standby,
70 s steady load, one Crystal setting off per run). The result:

| Crystal on the primary | Commit p50 | RPC p50 | RPC handled / expired | Queue end-to-end p50 |
|---|---:|---:|---:|---:|
| as shipped (2 runs) | 3.72–3.79 ms | 4.3 ms | ~4 600 / ~1 500 | 141 ms |
| per-commit replication hook off (2 runs) | 2.16–2.94 ms | 2.4–3.5 ms | 5 738–5 880 / 0–127 | 22–72 ms |
| only the wake of the waiting pull off | 2.74 ms | 3.0 ms | 5 880 / 0 | 34 ms |
| only the tail capture off | 2.34 ms | 2.7 ms | 5 880 / 0 | 36 ms |
| `General` instead of `DeleteHeavy` | 3.63 ms | 4.2 ms | 5 052 / 1 043 | 122 ms |
| Highway 3.0 (its own store), reference | — | 3.4 ms | 5 880 / 0 | 50 ms |

**What costs the time is shipping one batch per commit.** Every Strict commit runs `DatabaseReplication`'s hook on the
committing thread — copy the batch out of RocksDB, add it to the tail, record the timeline, wake the waiting pull — and
the woken pull then serves that one batch. Removing *either* the capture or the wake removes the whole gap, so neither
step is expensive alone: the per-commit pull racing the commits is. Without it Crystal is faster than Highway's own
store.

Highway's replication, which Crystal's was ported from, never shipped per commit: its standby pulled, and when a pull
applied nothing it waited 50 ms (`IdlePullDelayMs`) before the next; commits woke nothing. Batches gathered between
pulls and shipped together, and lag was bounded by that cadence. Crystal replaced the 50 ms idle poll with a long poll
woken by every commit — lower lag on an idle system, and a pull per commit on a busy one.

This spec makes Crystal ship **in batches**, as Highway did, without giving up the long poll's low lag: a commit marks
work available and returns; a pull gathers what accumulated. It also fixes two smaller costs found on the way.

| Req | Title | Priority |
|---|---|---|
| R1 | A commit does no replication work beyond recording the batch | **Must** |
| R2 | Pulls gather batches: one pull ships everything since the last | **Must** |
| R3 | Lag stays bounded and low | **Must** |
| R4 | A momentary hole in the tail is waited out, not read from the WAL | Should |
| R5 | A waiting pull holds no thread | Should |
| R6 | The `DeleteHeavy` profile's cost under a queue workload | Could |
| R7 | Acceptance: Highway's harness, as measured above | **Must** |
| R8 | No format or protocol change; documents updated | applies to all |

## Requirements

### R1 — A commit does no replication work beyond recording the batch (Must)

- **R1.1** The work a Strict commit does for replication, after RocksDB's write returns, is bounded to recording the
  committed batch where a pull can find it: no wake of a pull per commit, no timeline lock per commit, nothing that
  waits for another thread.
- **R1.2** The commit's latency with a standby attached is within 10 % of the same commit with no standby attached
  (R7 measures it).
- **R1.3** A database with no standby attached pays nothing (as today: the tail captures nothing until a pull enables it).

### R2 — Pulls gather batches (Must)

- **R2.1** A pull serves every batch committed since its cursor, up to `maxBytes`, in one reply — never one commit per
  reply because a commit woke it.
- **R2.2** Commits arriving while a pull is being served are shipped by the next pull, together.
- **R2.3** Wakes are coalesced: however many commits land, a waiting pull is woken at most once per **gather window**
  (`ReplicationOptions.GatherWindow`, default 2 ms), and serves what accumulated in it. *(Amended 2026-10-16: the
  default is 10 ms; 2 ms gathered nothing. Design addendum A3.)*
- **R2.4** Catch-up is unchanged: a standby behind pulls back-to-back, page after page, without waiting.

### R3 — Lag stays bounded and low (Must)

- **R3.1** Under Highway's harness load (R7), a standby's lag behind its primary, sampled every second, has p99 at most
  50 ms (Highway 3.0's idle-poll cadence) and median at most 10 ms.
- **R3.2** On an idle primary, a commit reaches a waiting standby within the gather window plus one round trip.
- **R3.3** `ReplicationStatus` and the slot metrics report lag as today; no new guarantee about synchronous replication.

### R4 — A momentary hole in the tail is waited out (Should)

With many commits in flight, a batch with a higher sequence can be added to the tail before a lower one still being
added; a pull that meets that hole today falls back to reading the WAL from disk, beside the commits' fsyncs. This is a
suspected contributor, not yet measured.

- **R4.1** Count, per database, the pulls served from the tail and the pulls that fell back to the WAL, as metrics
  (`crystal.replication.pull.source`, tagged `tail` / `wal`). Measure the share under R7 before changing anything.
- **R4.2** If the WAL share under R7 is more than 1 %: a pull that meets a hole at the tail's end serves the contiguous
  run before the hole and returns; the next pull ships the rest. The WAL path is kept for a standby genuinely behind the
  tail, not for a batch being added.

### R5 — A waiting pull holds no thread (Should)

A primary's `PULL` long poll waits with `Monitor.Wait` inside its async connection handler
(`ChangeSignal.WaitForChange`), pinning a thread-pool thread for up to 5 s per connected standby. Seen in Highway's test
process as part of thread-pool pressure.

- **R5.1** The long poll awaits without blocking a thread (an awaitable signal), and honours the same timeout and
  cancellation.
- **R5.2** A primary with N standbys long-polling holds no pool thread for them while idle.

### R6 — The `DeleteHeavy` profile's cost (Could)

`General` instead of `DeleteHeavy` recovered part of the gap (handled 5 052 against ~4 600; queue end-to-end 122 ms
against 141 ms). Highway's own store used only deletion-triggered compaction, not memtable scan-flush triggers.

- **R6.1** Measure under R7 with the scan-flush triggers off and deletion compaction kept; record the result in
  `engineering/measurements.md`.
- **R6.2** If the scan-flush triggers cost more than they save on a queue workload, retune or drop them from
  `DeleteHeavy`; the profile's scan guarantee in `002` is kept or restated.

### R7 — Acceptance (Must)

Highway's load harness, as used for the table above (a primary and a standby, published host, 70 s steady load, the
same laptop class), against the build of this spec:

- **R7.1** RPC handling p50 within 10 % of the "hook off" runs, and no worse than Highway 3.0's 3.4 ms.
- **R7.2** RPC calls handled within 5 % of the "hook off" runs; none expired that Highway 3.0 handled.
- **R7.3** Standby lag per R3.1.
- **R7.4** Results recorded in `engineering/measurements.md`, two runs each side.
- **R7.5** Crystal's existing replication suite passes unchanged in what it asserts. New tests only where a requirement
  has no existing test: coalescing (R2.3), the hole (R4.2) and the non-blocking wait (R5.1) — one each.

### R8 — No format or protocol change; documents updated

- **R8.1** No change to the on-disk format, the catalogue, the encoder or the replication wire protocol. A standby
  built before this spec follows a primary built with it, and the reverse.
- **R8.2** New options are additive with defaults; code built against `1.0.0-rc.1` binds unchanged.
- **R8.3** `engineering/replication.md` (§ the tail, the long poll), `engineering/measurements.md` (R7) and the
  `ReplicationOptions` reference describe what was built, in the same task.

## Out of scope

- Synchronous replication (a commit waiting for a standby): not asked for, not changed.
- A non-replicated box inside a replicated database. Highway would use it to fold its `highway.node` and
  `highway.cache` databases into `highway` — one database per broker — but it is a separate feature and has no
  performance effect (both are idle on the commit path). A spec of its own if wanted.
- The p99 investigation of `013` R1.2 beyond this cause: re-measured under R7 once this spec is built.

## Open questions

- **Q1** The gather window's default (2 ms) and whether it should adapt (wider under load, zero when idle).
- **Q2** Whether the timeline (`CommitTimeline`) needs a per-commit sample at all, or can be sampled by the pull.
