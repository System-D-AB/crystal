# 002 — Box layout

> **Status:** implemented (2026-10-09). **Depends on:** `001-project-skeleton`, `001-2-rocksdb-binding`, `005-store-and-databases` (a box lives in a database,
> a database in a store). All tasks T1–T14 implemented and verified; the verification tasks (T10 mutation checks,
> T11 interleavings, T12 cost) were hardened on 2026-10-09 — see `tasks.md`.
> **Revised (draft) 2026-10-09:** boxes can be **renamed**, and two boxes can **swap** names atomically. A name is an
> alias for a box id; the id is the box's identity (R3.7–R3.10).
> **Revised (draft) 2026-10-09:** the store and databases (directory, registry, database lifecycle, store format,
> reset) moved to their own spec, `005-store-and-databases`. This spec is what lives inside one database.
> **Authority document:** [`engineering/box-layout.md`](../../../engineering/box-layout.md). This spec implements it,
> and brings it up to date where implementation decides a detail.

## Introduction

Crystal's foundation is the **box**: a named, isolated key range inside a database, created and dropped atomically,
with a catalogue that lists everything without scanning data. This spec delivers:
- the box key layout;
- the catalogue;
- the box lifecycle;
- the order-preserving key encoding;
- per-box locking;
- the minimal batch, snapshot and scan surface needed to use boxes;
- the storage-format stamp.

**Origin.** The design is taken from the box storage of Highway, a durable message broker that moved every queue
into its own box. Its lessons are carried over:
- every per-message operation became O(1) or O(log n);
- a queue became one contiguous range, dropped by one range delete;
- names, counts and lists stopped needing scans of the data.

The lessons learned the hard way afterwards are carried too:
- **a box id resolved before a lock may be stale by the time the lock is held;**
- **a write must never land in the range of a dropped box;**
- **an id cache must never serve an id after its box is gone.**

**What stays in the products.** What the keys inside a box mean. Highway's spaces (ready, in-flight, lease, delayed,
dead, counters, schedules), its time floors and its expiry index; StowDB's documents and index entries. Crystal stores
bytes under a box and never interprets them.

## Requirements

### R1 — Keys

- **R1.1** Every key of a box is `[box_id : 4 bytes, big-endian][key]`. A whole box is one contiguous range, from
  `[box_id]` up to (not including) `[box_id + 1]`.
- **R1.2** A product passes `(box, key)`; **Crystal prepends the prefix.** No public API accepts a raw, prefixed key,
  so no write through one box can address another box's keys.
- **R1.3** Box id 0 is the catalogue (the system box). Ids 1–15 are reserved for Crystal. Product boxes start at 16.
- **R1.4** Keys compare as unsigned bytes. An empty key (the box's first possible key) is valid.

### R2 — The catalogue

- **R2.1** The catalogue lives in box 0 of each database:
  - a **name index**, name → box id;
  - a **record** per box id: name, profile, state, created time, dropped time, and the record's version;
  - the **id allocator**;
  - the **catalogue generation**;
  - the database's **format**.
- **R2.2** **Ids are never reused.** The allocator only moves forward, from 16 up to the end of the 4-byte id space.
  Running out is a typed error, but it is unreachable in practice (over four billion creations).
- **R2.3** Each change to the catalogue writes only its own keys. **No change rewrites or copies the whole
  catalogue.** Its cost does not grow with the number of boxes.
- **R2.4** The catalogue is held in memory once the database is open. Lookups and listing do not read the store and
  never scan data.
- **R2.5** The catalogue generation is a durable counter incremented by every catalogue change. A caller can detect
  "something changed" without comparing lists.

### R3 — Box lifecycle

- **R3.1** **Create** allocates an id and writes its name entry and record, in one batch. Creating a name that exists
  is refused by name.
- **R3.2** **Find** returns the id of a live box by name, or nothing.
- **R3.3** **List** returns every live box (name, id, profile, created time) from the in-memory catalogue.
- **R3.4** **Drop** removes the name entry, marks the record dropped, and range-deletes the box's keys, in **one
  batch**: after the commit the box is gone whole; before it, untouched. Drop's cost does not depend on how much the
  box holds.
- **R3.5** **A dropped name can be created again.** It gets a new id; nothing of the old box is visible through it.
- **R3.6** Box names: 1–1 024 UTF-8 bytes; no control characters. Any other character is allowed. A product
  namespaces its names (for example `queue/orders`, `orders@billing`) as it likes.
- **R3.7** **A name is an alias.** The name index maps a name to a box id; **the id is the box's identity**. Keys,
  handles, locks and replication positions all use the id, never the name.
- **R3.8** **Rename** moves a box's alias to a new name, in **one batch**: the old name entry removed, the new one
  written, the record's name updated, the generation incremented. The id, the keys and the data do not move: a
  rename's cost does not depend on the box's size. Renaming to a name that is taken is refused by name.
- **R3.9** **Swap** exchanges the names of two live boxes atomically, in one batch. A reader resolving either name
  sees both boxes before the swap or both after, never one of each. (The use: build new data in a fresh box, swap it
  in, drop the old box.)
- **R3.10** **Handles survive a rename and a swap.** A handle carries the id, so it keeps addressing the same box under
  its new name. Only a drop invalidates a handle (R4.3). A product that resolves by name after a rename or swap gets
  the box the name now points at.

### R4 — A write never lands in a dropped box

- **R4.1** A batch that writes to a box dropped before it commits is **refused whole** with a typed error naming the
  box. Nothing of it is written.
- **R4.2** A drop and a commit touching the same box are ordered: either the commit lands first and the drop then
  removes its keys, or the drop lands first and the commit is refused. **There is no interleaving in which a commit's
  keys survive in a dropped box's range.**
- **R4.3** A box handle obtained before a drop and used after it is refused the same way, never silently redirected to
  a new box of the same name.

### R5 — Locks

- **R5.1** A lock per box (striped). The lock is for the product's read-decide-write sequences; Crystal's own
  catalogue changes do not need it to be held.
- **R5.2** Several boxes can be locked together. Stripes are always taken in ascending order, so two multi-box lockers
  cannot deadlock, and two boxes sharing a stripe do not deadlock one locker.
- **R5.3** Every stripe can be taken at once (ascending), the fallback for a plan that keeps moving under a narrower
  lock set.
- **R5.4** Locks are not re-entrant, and say so.
- **R5.5** Test hooks (internal, null in production) fire when a thread is about to wait for a stripe and when a
  stripe is released, so concurrency tests act at an exact interleaving.

### R6 — Batches, snapshots and scans (the surface boxes need)

- **R6.1** A batch holds puts, deletes and range deletes (within one box) for any number of boxes of one database,
  and commits atomically.
- **R6.2** A batch reads its own writes: a read through the batch sees its pending changes over the committed state.
- **R6.3** Committing an empty batch writes nothing and syncs nothing.
- **R6.4** A snapshot gives a consistent view of one database. Point reads and scans go through a snapshot or a batch.
- **R6.5** **Every scan is bounded inside one box**, by a lower and an upper bound, and stops at its limit. No scan
  can cross into another box.
- **R6.6** Durability for this spec: a commit may ask to be synced. The full durability modes come in a later spec.

### R7 — Key encoding helpers

- **R7.1** Order-preserving encoders, public, in `SystemD.Crystal.Encoding`, **from one source, StowDB's encoder**
  (R12):
  - booleans: one byte;
  - unsigned integers: 4 or 8 bytes, big-endian;
  - signed 32- and 64-bit integers: big-endian with the sign bit flipped;
  - doubles: IEEE bits ordered (negatives inverted), `-0.0` normalised to `0.0`, NaN handled;
  - decimals: sign byte, then the magnitude, ordered;
  - timestamps: UTC ticks as signed 64-bit;
  - strings: UTF-8 with `0x00` escaped as `0x00 0xFF`, terminated by `0x00 0x01`;
  - GUIDs: 16 bytes in big-endian text order;
  - descending order: the byte complement of the ascending encoding.
- **R7.2** **Byte order equals value order** for every encoder, and composite keys are unambiguous: `("ab", "c")` and
  `("a", "bc")` never encode to the same bytes.
- **R7.3** Decoders for each encoder.
- **R7.4** **An encoder version,** part of Crystal's format: a change to any encoding's bytes changes it, and data
  written with another version is refused on open, never misread.
- **R7.5** **Ordinal and collation-free, by design.** Strings order by UTF-8 bytes, which is Unicode code-point
  order: stable across .NET, ICU and operating-system versions. Text in any language is stored and compared exactly;
  its *order* is code-point order, not any language's alphabetical order (for example `ä` < `å` < `ö`, and `Z` < `a`).
  A collation would change between ICU versions and silently reorder keys, so Crystal never applies one. A product
  that needs linguistic order stores its own versioned sort key.
- **R7.6** **Normalisation is the product's.** `ä` written as one code point and as `a` plus a combining mark are
  different bytes, so different keys. A product that keys on user text normalises it (to NFC) at its own boundary.
- **R7.7** Strings are never case-folded, cut or otherwise changed by Crystal. Folding and length cuts are a product's
  semantics.

### R8 — Format (of a database)

- **R8.1** Each database carries its format in its catalogue (`[0][0x05]`), checked when the database is opened,
  before anything in it is served. A mismatch is refused by name with the way forward, never opened as if empty.
- **R8.2** The store's format stamp, the node identity and reset are `005-store-and-databases` (R3, R10).

### R9 — Cost

- **R9.1** Create, find, rename, swap, drop and list cost the same with 10 boxes as with 100 000 (O(1) per change,
  O(boxes) for list, no data read).
- **R9.2** Drop costs the same for an empty box as for a box with a million keys.
- **R9.3** A scan of one box never reads another box's keys, whatever their number or churn.

### R10 — Verification

- **R10.1** **A reference-model test.** Random sequences of create, find, rename, swap, drop, re-create, put,
  delete, range delete, get and scan, against a plain in-memory model of boxes, compared after every step. Thousands of steps, a fixed and
  a random seed, mutation-checked (a broken prefix, a missing range delete, a reused id each make it fail).
- **R10.2** **Concurrency at exact interleavings** (R5.5 hooks):
  - a write racing a drop of its box (R4.2);
  - a create racing a drop of the same name;
  - a find before a drop and a write after it (R4.3);
  - a handle taken before a rename or a swap, used after it, still writes to the same box (R3.10);
  - two renames racing for the same new name: exactly one wins;
  - the same tests under a starved thread pool.
- **R10.3** **Encoding property tests:** order preservation and unambiguity (R7.2) over random values, including empty
  strings, embedded `0x00`, extremes and non-ASCII.
- **R10.4** **Cost tests** for R9, with numbers recorded in the spec.
- **R10.5** **Format tests:** a database with a foreign or future format key is refused with the message. (Store
  stamps, reset and database lifecycle tests are `005-store-and-databases` R13.)

### R12 — Reuse: copy and adapt

Implement by **copying and adapting** the sources below, not by writing anew (playbook, *Reuse before
writing*). Every copied file starts with a comment naming its origin (`// Ported from Highway: <path>` or
`// Ported from StowDB: <path>`), and the tests that prove it come with it (`004-test-suite` design § 2). Crystal's own
RocksDB layer, built by `001-2-rocksdb-binding`, is used as is and never re-wrapped.

| Source | Path | Take | Change |
|---|---|---|---|
| Crystal (`001-2`) | `src/SystemD.Crystal/Storage/RocksInstance.cs`, `BoundedIterator.cs`, `ReadOptionsPool.cs` | `Get`, synced writes, snapshots, bounded and counted iteration | used as is |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/StripedLock.cs` | stripes, ascending `LockMany`, `LockAll`, leases released through their owner, the `StripeWaiting` / `StripeReleasing` hooks | keyed by box id |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/Boxes/BoxKeys.cs` | the `[box:4]` prefix, range bounds, box-0 key builders, `BoxOf` | **drop** the space byte and every queue space (they are Highway's) |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/Boxes/Catalog.cs` | allocate, find, list, retire as one range delete, ids never reused, the catalogue version | **add** rename, swap, the catalogue gate and an exact in-memory catalogue; **drop** the box kind; JSON records |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/Rocks/RocksDbBatch.cs`, `RocksDbSnapshot.cs` | the read-your-writes overlay, empty-batch skip, one synced write; snapshots | box-relative keys; live-box validation at commit |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/InMemoryStore.cs`, `InMemoryBatch.cs`, `InMemorySnapshot.cs` | the in-memory twin, and **one contract suite run against both** the twin and RocksDB | only the ordered-key, batch and snapshot surface |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/Layout/KeyEncoding.cs`, `KeyWriter.cs` | **nothing:** an older, smaller copy of StowDB's encoder with a different string terminator | its tests (`KeyEncodingTests`) are ported against Crystal's encoder |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/Boxes/BoxRuntime.cs` | **nothing:** its id cache is replaced by the gate; its time floors are Highway's | — |
| StowDB (the StowDB repository) | `src/Crystal/Crystal.Storage/Encoding/OrderPreservingEncoder.cs` | every primitive encoder and decoder of R7.1, descending order | **drop** the Absent, Null and Empty markers, `FieldSpec`, `SlotValue` and case folding (StowDB's document semantics) |
| StowDB (the StowDB repository) | `src/Crystal/Crystal.Storage/Encoding/KeyWriter.cs`, `EncoderContracts.cs`, `ScanRange.cs` | the key writer and reader; the encoder version (R7.4); the scan range type | tied to Crystal's format; box-relative ranges |

## Out of scope

- **Durability modes**, beyond a synced commit (a later spec).
- **Workload profiles and their column families** (a later spec). This spec records each box's profile in its record
  and stores every box in one family, `General`.
- **Replication** (`003-replication`), including how a standby learns catalogue changes, **and backup** (a later spec).
- **Everything inside a box:** spaces, counters, floors, expiry indexes, documents and index entries are the
  products'.

## Decisions

| Question | Decision | Basis |
|---|---|---|
| Box record encoding | **JSON** with a version field `v` | read once when a database opens; readable in a dump; easy to evolve |
| Box name length | **1–1 024 UTF-8 bytes** (R3.6) | Highway names group queues `{channel}@{group}` from two identifiers of up to 256 bytes each |
| Batches across databases | **Refused**, with a typed error naming both databases | each database is its own RocksDB instance and cannot commit atomically with another |

## Addendum 2026-10-09 — independent review (amends R1.2, R2.2, R3.6, R5.4, R6.1; corrects "Out of scope")

An independent review of the implementation (playbook § 4), together with porting Highway's tests (`004` T9–T12),
found defects that the earlier verification did not. The requirements above stand; this records what they were found
to need, and what the tasks T15–T19 do about it.

- **R1.2 — a handle addresses only its own database.** Box ids repeat across databases (each allocates from 16), so a
  handle that carried only the id let a batch on database B write into box 16 of B with a handle from box 16 of A.
  A handle now also carries the identity of the open database that issued it; every other database refuses it with
  `BoxNotInDatabaseException`, as it refuses a handle built by hand or kept from before the store was reopened. This
  is the Decision below ("batches across databases: refused") made true.
- **R2.2 — the allocator ends, and is never trusted blindly.** The last 4-byte id is never issued, so every box has the
  exclusive upper bound `[box_id + 1]`; a create past it is refused. On open, a missing or damaged allocator, or a
  record the allocator has not passed, is refused rather than defaulted, because either would issue an id again.
- **R2.4 / R8.1 — the catalogue is loaded strictly, and initialised only at creation.** One method used to load a
  catalogue, or write a fresh one if the format key was missing (resetting the allocator over existing data). It is
  now two: initialise (a new database only, refused if box 0 holds anything) and load (every key checked, the alias
  table cross-checked against the records, unknown catalogue spaces refused). A failed catalogue write leaves the
  database refusing use until it is opened again.
- **R3.6 — names are valid Unicode; whitespace is allowed.** A name with an unpaired surrogate is refused (two such
  names shared one alias key); the string encoder and decoder are strict UTF-8 in both directions. A name of only
  whitespace, which R3.6 allows, is no longer refused.
- **R5.4 — locks are semaphores.** The port used `Monitor`, which is re-entrant and throws when a lock is released on
  another thread (after an `await`), leaving the stripe held for ever. Re-ported from Highway's `StripedLock`, as
  design § 4 says.
- **R6.1 — range deletes.** A start after the end is refused when added (it used to fail the whole commit with a
  RocksDB exception); an empty end means the end of the box, as in a scan.
- **R7 — the key writer grows.** `KeyWriter` could not grow, so any key over 256 bytes built through `KeyBuilder`
  threw. Highway's growth to a pooled array is ported; `KeyWriter` gains `ToArray()` and `Return()`.
- **"Out of scope" corrected:** the code already routes `DeleteHeavy` boxes to the `delete_heavy` family
  (`001-2` built the families and profile options), so "every box in one family, `General`" no longer describes it.
  The profiles' *tuning* and its proof remain a later spec.
