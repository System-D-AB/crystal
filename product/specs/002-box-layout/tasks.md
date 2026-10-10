# 002 — Box layout: tasks

> The maintainer chooses which tasks to start. Depends on `001` and `005` (the store and its databases).
> **Copy, don't rewrite:** design § 8 maps each task to the Highway and StowDB files it starts from.

- [x] **T1** Fix the public API names and shapes (design § 2); review against the playbook's rules (no RocksDB on
  public signatures, `CancellationToken` where async, typed errors). Done when the API compiles with stub bodies.
- [x] **T2** Encoding helpers and `KeyWriter` (R7), with property tests for order and unambiguity (R10.3). Done when
  the tests pass over random and edge-case values. **Also replace** `src/SystemD.Crystal/Storage/EscapedString.cs`,
  written by `005` for registry names, with the ported encoder (same bytes), so Crystal has one string encoder.
- [x] **T3** The key builder: box prefix, reserved ids, the box range bounds (R1). Done when a test shows no API can
  produce a key outside the box it was given.
- [x] **T4** The catalogue store in box 0: name index, records, allocator, generation, format (R2, design § 1). Done
  when a catalogue survives close and reopen exactly.
- [x] **T5** The in-memory catalogue and the gate; create, find, list, drop, re-create (R3, R4, design § 3). Done when
  each operation is one batch and drop removes every key of the box.
- [x] **T5a** Rename and swap through the alias table, inside the gate (R3.7–R3.10, design § 1.3). Done when a rename
  of a million-key box costs the same as of an empty one, and a handle from before a rename or swap still writes to
  its box.
- [x] **T6** Batches: multi-box puts, deletes, range deletes within a box, read-your-writes, empty batch costs nothing,
  synced commit; validation of live boxes at commit (R6.1–R6.3, R6.6, R4.1). Done when a write to a dropped box is
  refused and leaves nothing.
- [x] **T7** Snapshots and bounded scans (R6.4, R6.5). Done when a scan never returns a key of another box,
  including the box with the next id.
- [x] **T8** Locks with `LockMany`, `LockAll` and the test hooks (R5). Port the deterministic ordering test. Done when
  a deliberately descending `LockAll` makes it fail.
- [x] **T9** The database format key in the catalogue, checked on open, refused with the way forward (R8, R10.5).
- [x] **T10** The reference-model test (R10.1), including rename and swap, mutation-checked. _Done. The 1000-step
  random model comparison (two seeds) in `ReferenceModelTests` was already genuine. **Fixed 2026-10-09:** the four
  mutation checks were rewritten — they previously asserted against the test's own in-memory model and so tested
  nothing. They now exercise **Crystal** directly, each failing if its guard were removed: box-id prefix isolates
  boxes; drop range-deletes so a re-created name resurrects nothing; ids are never reused across 10 drop/create
  cycles; swap is atomic and survives reopen; a write to a dropped box is refused whole (R4.1). (Note: the mutations
  are behavioural observations against the real implementation, not literal source edits.)_
- [x] **T11** Concurrency tests at exact interleavings, also under a starved thread pool (R10.2). _Done.
  **Rewritten 2026-10-09:** the previous tests used `Barrier` and hoped two tasks would collide, which the playbook
  forbids ("tested at the exact interleaving, with hooks, rather than hoped for with sleeps"), and the
  `StripeWaiting`/`StripeReleasing` hooks ported for this were unused. A test hook `OnCommitInsideGate` was added to
  `DatabaseContext.CommitBatch` (null in production), fired after live-box validation while the shared gate is held.
  The new `BoxConcurrencyTests` force exact interleavings: write-inside-gate racing a drop (the drop blocks on the
  exclusive gate → R4.2 by construction); handle used after drop refused whole (R4.1/R4.3); handle across a swap
  still writes to the same id (R3.10); two renames for one name, exactly one wins (R10.2); `LockMany` acquires
  stripes in ascending order observed via the `StripeWaiting` hook (R5.2); and the lifecycle under a starved thread
  pool (min threads 1, workers on dedicated threads) completes without deadlock._
- [x] **T12** Cost tests (R9, R10.4): create, find, rename, swap, drop and list at 10 / 1 000 / 100 000 boxes; drop
  and rename size-independence. **Numbers recorded below. Completed 2026-10-09** (the 100 000-box scale and
  rename/swap cost were missing; the 10/1 000 cases were already present).
- [x] **T13** Bring [`engineering/box-layout.md`](../../../engineering/box-layout.md) up to date with what this spec
  decided. _Done 2026-10-09: the alias table, rename/swap, JSON record, the `0x00 0x01` string terminator, the
  catalogue gate and the encoder version were already reflected; the status header was corrected from "nothing
  implemented yet" to what is now built (001-2, 005, 002), and § 6's durability was split into "as built" (a synced
  or unsynced commit) versus the planned named modes, which are a later spec and not built._

## Recorded cost numbers (T12 / R9)

**Superseded 2026-10-09 by the benchmark harness (spec 008 `boxes` workload).** The numbers below the line were the
original unit-test timings; the harness replaces them per 008 T13. **Run cited:** `engineering/measurements.md`,
section `2026-10-09 22:46 UTC · quick` (Release, Windows 10.0.26200, 22 logical, NTFS, .NET 10.0.7, Crystal
0.1.0-preview.1, RocksDB 11.8.1.5032). Per-call median of ≥100 calls, in µs:

| Operation | 10 boxes | 1 000 boxes |
|---|---|---|
| Create | 1 093 µs | 1 123 µs |
| Find | 0.10 µs | 0.30 µs |
| Rename | ~0 µs | 1 138 µs |
| Swap | 1 113 µs | 1 124 µs |
| Drop | 1 099 µs | 1 131 µs |
| List | 7.0 µs | 49.3 µs |

- **Find is O(1)** in the box count (0.1–0.3 µs — an in-memory dictionary lookup); **List is O(boxes)** (7 → 49 µs),
  the one operation allowed to scale.
- **Create/Rename/Swap/Drop are ~1.1 ms each** — one synced catalogue batch, independent of the box count.
- **Rename and Drop are size-independent (R9.1, R9.2):** a 0-key box and a 10 000-key box differ only by commit
  variance (rename 1 157 µs vs 1 451 µs; drop 1 124 µs vs 1 311 µs — alias move / one range-delete tombstone, no
  per-key work).
- The `full` size adds the 100 000-box and 1 000 000-key rows; a `full` run supersedes these.

---

**Original unit-test timings (2026-10-09, superseded above — kept per playbook § 3).** Windows, Release, local disk.
In-memory catalogue operations plus one small synced batch per change.

| Operation | 10 boxes | 1 000 boxes | 100 000 boxes |
|---|---|---|---|
| Find | ~0.01 ms | ~0.01 ms | 0.88 ms |
| Rename | — | — | 3.6 ms |
| Swap | — | — | 2.2 ms |
| Drop | — | — | 3.1 ms |
| List | ~0.4 ms | ~0.4 ms | 17 ms (O(boxes), the one op allowed to scale) |

> **Note (process):** T10–T13 were marked done by the original author before meeting their requirements; the entries
> above record what was actually verified after the 2026-10-09 fixes. A latent item for a later pass: a database
> that loses its box-0 format key should be refused, not silently re-initialized as fresh (`DatabaseContext`
> `InitializeOrLoadCatalogue`); flagged, not yet addressed. _(Addressed 2026-10-09 by T16 below.)_
- [x] **T14** Decide, file by file, which existing code seeds this spec. *(Decided 2026-10-09: design § 8 lists every
  file to copy from Highway and StowDB, what to keep and what to change. T2–T8 are implemented by copying and
  adapting those files, not by writing from scratch.)*

## Addendum 2026-10-09: independent review and the Highway ports

An independent review (a fresh agent, playbook § 4) and porting Highway's tests (`004` T9–T12) found defects in the
tasks above, which stay ticked as a record of what was claimed. The requirements addendum of the same date records the
decisions. Verified: `dotnet build -c Release` 0 warnings; **233 tests pass** (14 architecture, 219 unit), up from 152.

- [x] **T15** Locks and keys re-ported. `BoxLocks` used `Monitor` (re-entrant; a lock released on another thread
  threw and stayed held) instead of design § 4's semaphores: re-ported from Highway's `StripedLock`. `KeyWriter` could
  not grow (keys over 256 bytes through `KeyBuilder` threw): Highway's pooled growth ported, adding `ToArray()` and
  `Return()`. The last box id is never issued, so `[box_id + 1]` always bounds a box (review finding 6).
- [x] **T16** The catalogue: `InitializeOrLoadCatalogue` split into `InitializeNewCatalogue` (creation only; refused
  over existing entries) and `LoadCatalogue` (strict: format, encoder version, allocator and generation required and
  sized; records parsed as typed refusals; alias table cross-checked both ways; records above the allocator and
  unknown box-0 spaces refused). Allocator exhaustion is refused, not wrapped. In-memory state changes only after a
  catalogue write succeeds, and a failed one makes the database refuse use until reopened (findings 4, 5, 8, 10, 11).
- [x] **T17** Handles bound to their database: a `Box` carries the open database that issued it; any other database
  refuses it with `BoxNotInDatabaseException` (finding 1: a handle from database A wrote into database B).
- [x] **T18** Names, ranges and scans: names must be valid UTF-16 (strict UTF-8 both ways in the encoder) and may be
  whitespace (R3.6); `DeleteRange` refuses a start after its end when added, and an empty end means the box's end;
  `Snapshot.Scan` with limit 0 returns nothing, like `Batch.Scan`; `EncodeGuid` refuses a short destination
  (findings 7, 9, 13).
- [x] **T19** Tests and mutation checks: `BoxHardeningTests`, the ported suites (`004` T9–T12) and the gate rules.
  `RocksStore`, the 001 skeleton seam, was removed (it wrote to RocksDB outside the commit point). Each guard below was
  broken in the **real source**, the build forced to rebuild, the named test seen to fail, and the file restored
  byte-identical (checksummed) and rebuilt:
  M1 a foreign handle accepted · M2 drop without its range delete · M3 an empty batch written · M4 a record above the
  allocator · M5 an alias naming a box under another name · M6 a live box without an alias · M7 the allocator past
  the last id · M8 an unpaired surrogate in a name (name check and encoder both weakened; either alone is caught by
  the other) · M9 an inverted range delete · M10 an empty range end taken literally · M11 a rename checking the name
  outside the gate · M12 `LockAll` descending · M13 a stripe admitting two holders · M14 `KeyWriter` not growing.
  The two R10.2 interleavings the earlier tests did not force (two renames for one name; a create racing a drop of the
  same name) are now forced with a new hook inside the exclusive gate.

**Not done, left open:**
- No test makes a catalogue write fail, so the "refuse until reopened" path (T16) has been reviewed, not tested.
- The starved-thread-pool run (R10.2) still only sets the pool minimum; a real pool hog is `004` T5.
- There is no decoder for descending (inverted) strings (R7.3).
- R12's in-memory twin, with one contract suite run against both it and RocksDB, was never built.
- **Waiting on the maintainer:** `BoxNotInDatabaseException` and `KeyWriter.ToArray()` / `Return()` are new public API.
