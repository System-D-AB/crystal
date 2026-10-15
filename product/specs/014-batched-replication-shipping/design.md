# 014 — Batched replication shipping: design

## 1. Overview

Today, per Strict commit on a primary with a standby attached:

```
commit thread:  RocksDB write (synced) ─► OnCommitted(batch.ToBytes())
                                            ├─ Tail.Add           (lock _gate)
                                            ├─ Timeline.Committed (lock _sync)
                                            └─ Signal()           (lock _gate, PulseAll)  ──┐
pool thread (pull):  Monitor.Wait in WaitForChange ◄──────────────────────────────────────┘
                     woken ─► TryRead(cursor) ─► one batch ─► reply ─► standby PULLs again ─► waits
```

Under load that is one pull, one reply and one standby round trip **per commit**, racing the commits on the primary.
After this spec:

```
commit thread:  RocksDB write (synced) ─► OnCommitted(batch.ToBytes()) ─► Tail.Add ─► changes++ (no lock, no wake)
pull (async):   await change ─► await GatherWindow (2 ms) ─► TryRead(cursor) ─► every batch since ─► reply
```

The commit records the batch and returns. A pull wakes on the first change, waits a short gather window, and ships
everything that landed meanwhile — Highway 3.0's model (commits wake nothing; a pull gathers), with the long poll kept
for low lag when idle.

**Reuse.** The mechanism exists in Highway 3.0 (`src/Highway.Server/Storage/Rocks/`, tag `v3.0`):
- `ReplicaPuller.cs` — `IdlePullDelayMs` (50 ms): a pull that applied nothing waits before the next; catch-up pulls
  back-to-back. The gather window is the same idea on the primary's side, with a 2 ms window instead of a 50 ms poll.
- `ReplicationFeeder.Pull` — a caught-up standby (`fromSeq == latest`) gets an empty page without touching the WAL;
  anything the tail cannot answer falls to the WAL tailer. Kept, with R4's change at the tail's end.

## 2. The commit hook (R1)

`Storage/DatabaseReplication.cs` sets `RocksInstance.OnCommitted`. It becomes:

```csharp
_instance.OnCommitted = bytes =>
{
    Tail.Add(bytes);        // unchanged: records the batch, under the tail's short lock
    _changes.Mark();        // R2/R5: an Interlocked increment and, at most once per window, a TrySetResult
};
```

- **Timeline** (`Replication/CommitTimeline.cs`): `Committed` moves off the commit path. The timeline maps sequences to
  time for lag-in-time reporting; it is sampled by the pull (the latest sequence and the clock, when a pull serves) and
  by the existing status read. Its resolution was already one sample per `SampleInterval`, so per-commit calls added
  nothing but a lock (Q2).
- `batch.ToBytes()` stays: the tail needs the bytes, and the copy is a memcpy of the batch just written.
- With no standby attached the tail captures nothing (`_capturing` false) and `Mark` is an increment: R1.3.

## 3. Coalesced wake and the gather window (R2, R3)

`Storage/ChangeSignal.cs` becomes an awaitable, coalescing signal (R5 too):

```csharp
internal sealed class ChangeSignal
{
    private long _changes;
    private TaskCompletionSource _next = New();      // RunContinuationsAsynchronously: a commit never runs a pull

    public long Mark => Interlocked.Read(ref _changes);

    public void Mark()                                // commit thread: no lock, no wait
    {
        Interlocked.Increment(ref _changes);
        Volatile.Read(ref _next).TrySetResult();      // first commit after a pull went waiting wakes it; later ones no-op
    }

    public async ValueTask<bool> WaitForChangeAsync(long mark, TimeSpan timeout, TimeSpan gather, CancellationToken ct)
    {
        var waiter = Volatile.Read(ref _next);
        if (Mark == mark) await waiter.Task.WaitAsync(timeout, ct) (timeout → return false);
        Interlocked.CompareExchange(ref _next, New(), waiter);   // re-arm for the next wait
        if (gather > TimeSpan.Zero) await Task.Delay(gather, ct); // R2.3: let the burst land
        return Mark != mark;
    }
}
```

- **One wake per window, however many commits** (R2.3): after the first `TrySetResult`, later ones are no-ops until
  the pull re-arms.
- **Gather** (R2.1, R2.2): the woken pull waits `GatherWindow` before `TryRead`, then ships every batch since its
  cursor up to `maxBytes`. Commits during the reply are shipped by the next pull, which finds the mark moved and does
  not wait.
- **Catch-up** (R2.4): `HandlePull` only waits when the standby is caught up (its cursor is the latest); a standby
  behind is served at once, page after page, as today.
- **Lag** (R3): idle, a commit reaches the standby in `GatherWindow` + one round trip (~2–3 ms on a LAN). Under load a
  pull is always in flight or about to be, so lag is one reply's worth. R3.1's bound (p99 ≤ 50 ms) is Highway 3.0's
  cadence and is measured, not assumed.
- `ReplicationOptions.GatherWindow` (default 2 ms; `TimeSpan.Zero` = no gather, today's per-commit behaviour, kept for
  tests that want the lowest lag and for comparison).

## 4. The tail's end (R4)

`Replication/ReplicationTail.TryRead` returns false when the cursor's next sequence is not in the tail; `HandlePull` then
reads the WAL (`WalReaderPool`). With concurrent committers the next sequence can be missing only because its commit is
still between RocksDB's write and `Tail.Add` — a microsecond hole — and the WAL read is a disk read beside the commits'
fsyncs.

- **First, measure** (R4.1): a counter in `HandlePull`, `crystal.replication.pull.source` {`tail`, `wal`}, per database.
- **Then, if R4.2's threshold is met:** `TryRead` distinguishes "the cursor is behind the tail's oldest entry" (a real
  catch-up: WAL, as today) from "the next sequence is newer than the tail's newest entry, or a hole at the end"
  (a commit still landing: serve nothing now; the gather window or the next pull picks it up). The standby sees an empty
  page and pulls again — the same as any caught-up pull. No protocol change.

## 5. The non-blocking long poll (R5)

`Storage/ReplicationSource.HandlePull` is called from `ReplicationConnection.DispatchAsync`, which is already async; it
becomes `HandlePullAsync` and awaits `WaitForChangeAsync` (§ 3). No thread is held while a standby waits. The 5 s
long-poll timeout, cancellation on connection close, and the reply shape are unchanged.

## 6. `DeleteHeavy` (R6)

`Storage/ProfileOptions.For(DeleteHeavy)` sets deletion-triggered compaction (`CompactOnDeletions`, as Highway 3.0) and
memtable scan-flush triggers (`MemtableScanFlushTriggers(1000, 16)`, which Highway never had). Measure under R7 with the
scan-flush triggers off; the change, if any, is to those two numbers or to dropping that call, and
`engineering/box-layout.md`'s profile table states what `DeleteHeavy` sets.

## 7. Measurement (R7)

The harness is Highway's (`internal/features/076-highway-on-crystal/crystal/profilerun.ps1` in the Highway repo, with
`ab_summary.py`): the published Highway host as a primary and a standby, Highway's load harness for 70 s, `dotnet-counters`
on the primary (`crystal.commit.duration`, `highway.rpc.latency`) and the harness's handled / expired counts and
end-to-end latencies. Lag (R3.1) is read from the standby's `ReplicationStatus` once a second during the load. Run with
Highway's `Directory.Packages.props` pointing at a local package of this spec's build, two runs each side; record in
`engineering/measurements.md` beside the table in the requirements.

## 8. What this design does not do

- It does not make replication synchronous or change what a commit guarantees.
- It does not change the wire protocol: a pull, its page and `maxBytes` are as they are; only when the primary answers
  changes.
- It does not add a benchmark suite: R7 reuses Highway's harness. New unit tests are limited to the three behaviours
  without one (coalescing, the tail's end, the non-blocking wait).

## 9. Risks

- **Lag under a very bursty load** could rise by up to one gather window per burst. Bounded by R3 and measured.
- **The empty-page answer at the tail's end** (R4.2) could make a standby spin if a hole persists; a hole persists only
  if a commit failed after RocksDB's write, which already fails the database (`Fail`), so the spin cannot outlive it.
- **Timeline resolution** (§ 2): lag-in-time is sampled per pull instead of per commit; status reads report the same
  values within one sample interval.

## Addendum 2026-10-16: what the measurements changed

The sections above stay as written; where they disagree with this addendum, the addendum decides. Numbers are in
`engineering/measurements.md`, section *2026-10-16 · spec 014*.

### A1. The cost is the standby's synced write per page, not the pull

Each page a standby applies is one synced write (`003` R4.2). Shipped one page per commit, a standby on the same disk
doubled the fsyncs under the primary's commits: in-process, a single writer's Strict commit went from 1.04 ms (no
standby) to 1.84 ms. That is why removing either the tail capture or the wake removed the gap: either one stopped the
page-per-commit flow. Highway 3.0's standby also syncs every page; its 50 ms idle poll is what kept its pages few.

### A2. The gather window paces a standby near the head (replaces § 3's gather after a wake)

§ 3 gathered only after a wake from an idle wait. Under load a pull rarely waits (there is always something new), so
that gathered nothing. Instead, **a standby whose previous reply was neither a full page nor read from the WAL is
answered no sooner than `GatherWindow` after it** (`ReplicationSource.PaceAsync`). A standby catching up, or one whose
reply carried any entry but a plain one, is answered at once (R2.4). A pull whose previous reply is older than the window
is not paced, so a commit after an idle spell reaches a waiting pull at once; a commit made while a pull paces waits for
the window to end (R3.2: the window plus one round trip). The pacing wait is cut short by an urgent change (role,
epoch, database set), which `ChangeSignal` counts apart from commits; the pull takes that mark first, before anything
else. The window must be from zero to 1 s (checked when the store opens), well inside a standby's reply timeout.

### A3. The default window is 10 ms, not 2 ms (Q1)

At 2 ms the window is shorter than a standby's own cycle (reply, synced apply, next pull), so it paced nothing: under
Highway's harness the 2 ms build was no faster than rc.1. At 5 ms and wider the commit cost disappears. The wait is a
timer, so on Windows it rounds up to the 15.6 ms timer resolution; on Linux it is 10 ms.

**R3.1 is not met as it is defined.** Under Highway's harness, lag read once a second had a median of 14–15 ms
(R3.1: at most 10 ms) and a p99 of 16 and 46 ms (at most 50 ms: met). Those reads tick on the same 15.6 ms timer as
the window, so they land near its end; sampled at irregular moments in an in-process pair, the median was 9.3 ms and
the p99 21.7 ms. That second measurement is not R3.1's. The maintainer decides whether R3.1's median is restated
(for example, at most one window), or a finer wait is pursued (a high-resolution timer on Windows), or the window is
narrowed (at 5 ms it still gathered in-process, but on Windows it is the same 15.6 ms).

### A4. The timeline stays per commit, without its lock (Q2)

Sampling the timeline at pull time would date a batch by when it was shipped, under-reporting lag. Instead
`CommitTimeline.Committed` takes its lock only when a sample is due (about ten times a second); between samples a
commit raises a lock-free high-water mark, folded into the newest sample when the next sample is taken or the timeline
is read. Separately, the tail now keeps each batch's commit time (null when unknown, so a clock whose timestamps start at 0
is still exact), so a lag in time is exact while the tail holds the first batch a standby lacks (the timeline's 100 ms
samples beyond it).

### A5. Waits use a timer, never the clock

The long poll and the gather window wait on a timer (`Task.WaitAsync(TimeSpan)`), as `Monitor.Wait` did, never on the
store's `TimeProvider`: Crystal reads time only through its clock, which also measures how much of a wait is left
after an early wake.

### A6. R4.2 not built

T1 measured no WAL page under the harness (15 854 pages, all from the tail). The tail can tell a cursor behind it from
a batch still landing (`ReplicationTail.ClassifyMiss`), reported as `crystal.pull.miss`. Had R4.2 been built, note that
a batch whose commit raced the tail's first enable is never added, so "still landing" must be bounded or a standby
could wait on it until the next commit.
