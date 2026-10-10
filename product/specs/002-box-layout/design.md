# 002 — Box layout: design

## 1. Physical layout

As [`engineering/box-layout.md`](../../../engineering/box-layout.md), which this spec keeps current. In short:

```
key   = [box_id : u32 BE][key chosen by the product]
box 0 = the catalogue (system box) · ids 1–15 reserved · product boxes from 16
```

### 1.1 The catalogue (box 0)

| Key | Value |
|---|---|
| `[0][0x01][name : escaped UTF-8]` | box id (u32 BE): **the alias table** |
| `[0][0x02][box_id : u32 BE]` | the box record (§ 1.2) |
| `[0][0x03]` | allocator: the next box id (u64 BE) |
| `[0][0x04]` | catalogue generation (u64 BE) |
| `[0][0x05]` | the database's format (u32 BE) |

The rest of box 0's first-byte space (`0x06`–`0xFF`) is reserved for Crystal.

### 1.2 The box record

JSON, versioned:

```json
{ "v": 1, "name": "queue/orders", "profile": "General", "state": "live",
  "created": 638600000000000000, "dropped": null }
```

`state` is `live` or `dropped`. A dropped record is kept, so the id stays burnt and a later write to it is
attributable. `name` is the box's **current** alias, kept here so a box id can be named without searching the alias
table.

### 1.3 Names are aliases

The id is the box: its keys are `[id][…]`, its lock stripe is chosen by the id, its handle is the id, and replication
and backup report it. The name is only an entry in the alias table pointing at the id. Therefore:

| Operation | Alias table | Record | Keys and data |
|---|---|---|---|
| create | add `name → id` | new | none |
| rename `a → b` | remove `a`, add `b → id` | `name = b` | **untouched** |
| swap `a ⇄ b` | `a → id_b`, `b → id_a` | both names updated | **untouched** |
| drop | remove `name` | `state = dropped` | one range delete |

Each row is one batch inside the catalogue gate (§ 3), with the generation incremented.

### 1.4 Databases

The store, its directory, the `_crystal` registry and the database lifecycle (folders named by id, names as aliases,
rename and swap) are `005-store-and-databases`. Everything in this spec happens inside one database.

## 2. Public API (this spec's part)

```csharp
namespace SystemD.Crystal;

// CrystalStore (open, reset, databases): 005-store-and-databases.

public sealed class CrystalDatabase                                // a handle: carries the database id
{
    public uint Id { get; }
    public string Name { get; }                                    // the current alias
    public BoxCatalog Boxes { get; }
    public BoxLocks Locks { get; }
    public Batch NewBatch();
    public Snapshot Snapshot();
}

public sealed class BoxCatalog
{
    public Box Create(string name, BoxProfile profile = BoxProfile.General);
    public Box? Find(string name);
    public Box Rename(Box box, string newName);             // id unchanged; the returned handle carries the new name
    public void Swap(Box first, Box second);                // exchange names atomically
    public IReadOnlyList<BoxInfo> List();
    public void Drop(Box box);
    public long Generation { get; }
}

public readonly record struct Box(uint Id, string Name);           // a handle; validated at commit (R4).
                                                                   // Name is informational: the Id addresses the box,
                                                                   // so a handle stays valid across rename and swap.

public sealed class Batch : IDisposable
{
    public void Put(Box box, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);
    public void Delete(Box box, ReadOnlySpan<byte> key);
    public void DeleteRange(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to);
    public byte[]? Get(Box box, ReadOnlySpan<byte> key);                              // reads its own writes
    public IEnumerable<KeyValue> Scan(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, int limit);
    public void Commit();                                // the store's default durability; empty → nothing
    public void Commit(DurabilityMode durability);       // spec 006 (was Commit(bool sync = true))
}

public sealed class Snapshot : IDisposable
{
    public byte[]? Get(Box box, ReadOnlySpan<byte> key);
    public IEnumerable<KeyValue> Scan(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, int limit);
}

public sealed class BoxLocks
{
    public IDisposable Lock(Box box);
    public IDisposable LockMany(IEnumerable<Box> boxes);    // ascending stripes
    public IDisposable LockAll();                           // every stripe, ascending
}
```

Keys and ranges are always **within** the box; the prefix is added inside. Names are final in task T1.

## 3. How R4 holds: no write in a dropped box

Highway learned this one the hard way: a box id resolved before a lock, the box dropped and re-created in between,
and a write enqueued into the deleted range. The commit reported success and the message was gone. Crystal makes it
impossible by construction:

1. **The catalogue gate.** Each database has one reader/writer gate.
   - **Every commit** takes it **shared**.
   - **Create and drop** take it **exclusive**: each builds and commits its catalogue batch while holding it.
2. **Rename and swap** also take the gate exclusively. A commit never sees a half-renamed catalogue, and a reader
   resolving names inside the gate sees all of a swap or none of it.
3. **Validation inside the gate.** Before a commit hands its batch to RocksDB, still holding the gate, it checks that
   every box it touches is `live` in the in-memory catalogue. If any is not, the commit is refused whole (R4.1).
4. **So a drop and a commit are ordered** (R4.2):
   - either the commit is inside the gate first, and the drop's later range delete removes its keys;
   - or the drop is first, and the commit's validation sees `dropped` and refuses.
5. **Handles are ids, not names** (R4.3, R3.10). A `Box` handle from before a drop carries the old id, which is `dropped`
   forever (ids are never reused), so it is refused, never redirected to the new box of the same name.

**Cost:** an uncontended shared gate plus one dictionary lookup per touched box per commit. A drop or create briefly
pauses commits in that database. Catalogue changes are rare next to writes.

**The in-memory catalogue is exact.** Within a process, only Crystal changes the catalogue, and only inside the
exclusive gate, so there is no stale cache to invalidate. That is a class of problem Highway had to fix: its cache was
filled by reads outside any lock. On a replication standby, catalogue changes arrive in replicated batches, and the
standby reloads its catalogue when it applies them (spec `003-replication`).

## 4. Locks (R5)

A fixed number of stripes (256), non-re-entrant semaphores, chosen by hashing the box id:
- `LockMany` takes the distinct stripes of its boxes in ascending order;
- `LockAll` takes every stripe in ascending order.

Same order, so no deadlock. The design and its tests (including the deterministic ordering test) are carried over
from Highway's lock, where they were mutation-checked.

Locks protect a **product's** read-decide-write sequence, such as Highway's claim (read the head, decide, write).
They are not needed for R4: the gate handles that.

## 5. Encoding (R7)

Carried over from the order-preserving encoders both products already use:
- **strings:** `0x00` → `0x00 0xFF`, terminator `0x00 0x01` (StowDB's, the single source);
- **unsigned integers:** fixed-width big-endian;
- **signed integers:** sign bit flipped;
- **GUIDs:** big-endian 16 bytes;
- **descending order:** the complement.

A `KeyWriter` builds composite keys without intermediate allocations.

## 6. Format (R8)

Every database's catalogue holds its format at `[0][0x05]`, checked when the database opens. A mismatch throws
`CrystalFormatException` with the database, the found and the supported format, and the way forward. The store's
`crystal.format` stamp, `node.id` and reset are `005-store-and-databases`.

## 7. Mapping from Highway's box storage

| Highway's box storage | Crystal | Stays in Highway |
|---|---|---|
| `[box:4][space:1][rest]` keys | `[box:4][key]`; Highway's space byte becomes the first byte of its key | the spaces and their meaning |
| system box 0: name index, meta, allocator, catalogue version, format | box 0: alias table, record, allocator, generation, format | the box *kind* in the name index (Highway encodes it in its names) |
| — (no rename) | rename and atomic swap of aliases (R3.7–R3.10), from the collection rename that StowDB offers | — |
| system box family keys (registry, membership) | — | Highway's own boxes |
| expiry index for reply slots and idempotency markers | — | Highway's own box |
| ids never reused; drop = one range delete + state `retired` | the same (R2.2, R3.4) | — |
| catalogue cache with a generation, re-check of the live id under the lock | an exact in-memory catalogue behind a gate (§ 3) | — |
| per-box lock, `LockMany`, `LockAll`, test hooks | the same (R5) | — |
| bounded scans per space, head counters | scans bounded to a box (R6.5) | head counters (product data) |
| time floors (process-local, lowered on insert, raised after a committed scan) | — (a scan takes a lower bound) | the floors |
| counters in the same batch | — (a product writes its counters in its batch) | the counters |
| format guard + `--wipe-data` keeping the epoch | the database format key (R8); the store stamp and `Reset` keeping the node identity are `005-store-and-databases` | the command-line flag |
| reference-model test with a clock that steps back | reference-model test for boxes (R10.1) | the queue-model test |

## 8. Seed code: copy and adapt, do not rewrite

Most of this spec already exists, proven, in the two products built on Crystal. **Implement by copying these files
and adapting them**, not by writing from scratch.

Both products are MIT-licensed by the same owner as Crystal. Every copied file starts with a comment naming its
origin, for example `// Ported from Highway: src/Highway.Server/Storage/StripedLock.cs`, so the two can be compared
later. A copied file's tests come with it (`004-test-suite` design § 2).

**Highway** (`github.com/System-D-AB/highway`, `src/Highway.Server/Storage/`):

| Source | Lines | Becomes | Keep | Change |
|---|---|---|---|---|
| `StripedLock.cs` | 151 | `Boxes/BoxLocks` (§ 4) | stripes, ascending `LockMany`, `LockAll`, the release-through-owner leases, the `StripeWaiting` / `StripeReleasing` hooks | keyed by box id instead of a name string; public API per § 2 |
| `Layout/KeyWriter.cs`, `Layout/KeyEncoding.cs` | 201 | **not copied** | — | an older, smaller copy of StowDB's encoder with a different string terminator; encoding comes from StowDB only (requirements R7.1, R12). Its tests are ported. |
| `Boxes/BoxKeys.cs` | 174 | the box prefix and box-0 key builders (§ 1) | `[box:4]` prefix, `Box`, range bounds, system-box key builders, `BoxOf` | **drop** the space byte and every queue space (ready, in-flight, lease, …): those are Highway's. Box 0's spaces per § 1.1 |
| `Boxes/Catalog.cs` | 116 | `Boxes/BoxCatalog` (§ 2, § 3) | allocate, find, list, retire as one range delete, ids never reused, the catalogue version | **add** rename and swap (§ 1.3), the catalogue gate and an exact in-memory catalogue (§ 3); **drop** the box *kind*; JSON record per § 1.2 |
| `Rocks/RocksDbBatch.cs` | 165 | `Batch` (R6) | the read-your-writes overlay, empty-batch skip, the single synced `Write` | box-relative keys; validation of live boxes at commit (R4) |
| `Rocks/RocksDbSnapshot.cs` | 27 | `Snapshot` (R6.4) | as is | box-relative, bounded scans |
| `InMemoryStore.cs`, `InMemoryBatch.cs`, `InMemorySnapshot.cs` | 479 | an internal in-memory twin for tests | the twin, and the discipline of running **one contract suite against both** the twin and RocksDB | only the ordered-key, batch and snapshot surface; **drop** the legacy key-value, list and set families |
| `Boxes/BoxRuntime.cs` | 88 | — | **nothing** | its id cache is replaced by the exact catalogue behind the gate (§ 3); its time floors are Highway's |

**StowDB** (`src/Crystal/Crystal.Storage/Encoding/` in its repository):

| Source | Becomes | Keep | Change |
|---|---|---|---|
| `OrderPreservingEncoder.cs` | `Encoding/` (every primitive type of R7.1: the single source) | doubles with `-0.0` normalised to `0.0` and NaN handled; decimals; descending as the byte complement | **drop** the Absent, Null and Empty markers, `FieldSpec` and `SlotValue`: those are StowDB's document semantics |
| `EncoderContracts.cs` | `Encoding/EncoderVersion` | an encoder version that turns drift into a refused open instead of silent corruption | tied to Crystal's format (R8) |
| `ScanRange.cs` | the scan range type | the range shape | box-relative |

**Tests come with the code:** `KeyEncodingTests`, `StripedLockAllTests`, the catalogue and key tests of
`BoxLayoutTests`, and the batch contract of `StoreContractTests` (`004-test-suite` design § 2.1–2.3).

## 9. Risks

| Risk | Mitigation |
|---|---|
| The gate becomes a write bottleneck | Shared mode for commits; measure it in the cost tests (R10.4). If it shows up, move to per-box validation with box locks. |
| JSON records cost on open with very many boxes | The catalogue loads once per open; measure it at 100 000 boxes (R9.1) |
| A product holds a `Box` across a drop and is surprised by refusals | That is the point (R4.3); the error names the box and its state |
