# Box layout

> **Status: implemented, in part.** This document defines Crystal's physical layout and tracks what is built. The
> store and databases (§ 2) are implemented by spec `005-store-and-databases`; the RocksDB layer under it by
> `001-2-rocksdb-binding`; the box layout, catalogue, encoding, locks, batches and scans (§ 3–§ 7) by `002-box-layout`.
> Durability modes and workload profiles beyond the defaults (§ 6, § 8) are later specs. Replication (§ 9) is built by
> spec `003`; see [`replication.md`](replication.md). Backup beyond the checkpoint primitive is a later spec.
> Where this document and the code disagree, one of them has a defect, and it is fixed or recorded, never left silent.

## 1. Terms

| Term | Meaning |
|---|---|
| **Store** | What an application opens: one data directory, holding any number of databases. One per process per directory. |
| **Database** | One RocksDB instance in its own folder, with its own write-ahead log (WAL) and files. The unit of isolation, backup and drop. |
| **Box** | A named key range inside a database, identified by a **box id**. The unit a product creates, locks and drops. |
| **Key** | Bytes chosen by the product, **within** its box. Crystal adds the box prefix. |
| **Value** | Bytes. Crystal never parses a value. |

## 2. The data directory

```
{dataDir}/
  crystal.format            the store's format stamp (a single integer and a name), written once, checked on open
  crystal.lock              the store lock, held exclusively for the store's lifetime (FileShare.None)
  node.id                   the node's identity (a GUID), created with the store, never regenerated, kept by a
                            reset (used by replication)
  node.epoch                the node's replication epoch and role ("{epoch} {role}"): node-local, never replicated,
                            written atomically
  _crystal/                 the system database, laid out like any database (§ 3), with the registry in kernel
                            box 1: the store's view of its databases: the alias table (name → id), a record per
                            database (its name, replicated or not, live or dropped, and copies of its UID and
                            creation time, which the database itself holds, § 4), the id allocator and the
                            registry generation. A standby learns the replicated set by reconciliation (spec 003)
  db-{id}/                  one folder per database, named by its id (8 lowercase hex digits): never renamed or moved
    name                    the database's current name, for people reading the directory; the registry decides
    archive/                a replicated database's retained WAL (spec 003 R11.1); absent when retention is off
  replication/              node-local replication state, never replicated (spec 003, design § 1)
    follow/{uid}.json       the incarnation and base a standby last applied from, per database
    incarnation/{uid}.json  a primary's log history per replicated database: incarnation, base, and the earlier
                            incarnations it continues across restarts (cleared at promotion)
    resync/{uid}            a database marked for re-bootstrap (a gap or divergence)
    rejoin                  a node to rejoin a named primary on its next follow
    reconciliation/*.json   demote reports: what this node committed beyond any standby's acknowledgement, by box
  db-{id}.checkpoint-{s}/   a primary's live snapshot checkpoint for a standby's bootstrap (hard links; deleted at END)
  _backups/{utc}/db-{id}    checkpoints with their manifests (which record the id and the name at the time)
```

- **The format stamp is checked before anything is written.** Opening reads the stamp first and refuses, writing
  nothing, a directory with content and no stamp or a stamp this build cannot read, by name and with the way
  forward. Only then is the lock taken, and a new store stamped under it: `node.id`, then `crystal.format`, which is
  the commit point. A stamped store whose `node.id` is missing is refused, never given a new identity.
- **The store lock is held exclusively.** Only one open store instance per directory per machine; a second open,
  from this process or another, is refused at once by name.
- **The registry decides which databases exist.** A folder that is not registered is not served. At start, a dropped
  database's folder is deleted; an unregistered `db-{id}` folder whose id the allocator has passed (an interrupted
  create, which advances the allocator before making the folder) is removed; anything else is reported and left.
  A registry whose alias table and records disagree, or a store with database folders and no registry, is refused.
- **A database's name is an alias, like a box's.** Rename and swap change only the registry (and the `name` file);
  the folder, the data and open handles are untouched, and a rename reaches a standby as an ordinary registry change.
  Database ids, like box ids, are never reused, so a handle from before a drop is refused, never redirected.
- All databases of a store share one block cache and one background thread pool; each has its own WAL and write
  buffers.

## 3. Inside a database

### 3.1 Column families

| Family | Holds | Profile |
|---|---|---|
| `default` | the catalogue (box 0) | General |
| `general` | the keys of every box created with the `General` profile | General |
| `delete_heavy` | the keys of every box created with the `DeleteHeavy` profile | DeleteHeavy |

A box's profile chooses its column family. Compaction settings are a property of a column family, not of a key range,
so a profile *is* a family. The order of families is fixed and checked on open.

### 3.2 Keys

```
[box_id : 4 bytes, big-endian][key : bytes chosen by the product]
```

- **Every key of a box begins with its box id**, so a whole box is one contiguous key range: `[box_id]` up to
  `[box_id + 1]`.
- **A product never writes the prefix.** It passes `(box, key)`; Crystal prepends the id. A write through one box
  cannot address another box's keys.
- **Box id 0 is the catalogue.** Ids 1–15 are reserved for the kernel. Products' boxes start at 16.
- Keys sort by unsigned bytes. The encoding helpers (§ 5) make composite keys sort the way a product expects.

### 3.3 Values

Bytes, as the product wrote them. Crystal adds no header to a product's value.

## 4. The catalogue (box 0)

| Key | Value |
|---|---|
| `[0][0x01][name : escaped UTF-8]` | the box id (4 bytes): the **alias table**. A name is an alias; the id is the box |
| `[0][0x02][box_id : 4]` | the box's record, versioned: name, profile, state (`live` or `dropped`), created and dropped times |
| `[0][0x03]` | the id allocator: the next box id (8 bytes) |
| `[0][0x04]` | the catalogue generation (8 bytes), incremented by every catalogue change |
| `[0][0x05]` | the database's format (4 bytes), checked on open like the store's stamp |
| `[0][0x06]` | the key-encoder version (4 bytes): data written by another encoder version is refused, never misread |
| `[0][0x07]` | the database's **identity** (JSON): its UID, its kind (`database`, or `registry` for `_crystal`) and its creation time; written with the catalogue, never changed (spec 009) |

Spaces `0x08`–`0xFF` of box 0 are reserved for Crystal. A catalogue holding a key in a space this build does not write
is refused on open (a newer build, or damage).

**Rules:**

1. **Ids are never reused.** The allocator only moves forward. With a 4-byte id there is no practical lifetime limit
   on box creations. The last id, `FF FF FF FF`, is never issued, so every box has the exclusive upper bound
   `[box_id + 1]`; a create past `FF FF FF FE` is refused, never wrapped.
2. **Create** allocates an id, writes the name entry and the record, and increments the generation, in one batch.
3. **Drop** removes the name entry, marks the record dropped, range-deletes `[box_id]` to `[box_id + 1]` in the box's
   family, and increments the generation, **in one batch**: a dropped box disappears whole or not at all.
4. **A dropped name can be created again.** It gets a new id; the old id's keys are already gone.
5. **Rename and swap move aliases, not data.** Rename removes the old name entry, adds the new one and updates the
   record, in one batch; swap exchanges two boxes' names in one batch. The box's id, keys and data never move, and a
   handle (which carries the id) stays valid.
6. **Catalogue changes cost O(1).** A change writes only its own keys; nothing rewrites or copies the whole catalogue.
7. **The catalogue is held in memory, exactly.** Within a process only Crystal changes it, and only under the
   database's catalogue gate (rule 8), so the in-memory copy is never stale. It is **initialised** only when its
   database is created, and **loaded** strictly when it is opened: a missing or unreadable format, encoder version,
   identity, allocator or generation, an unreadable record, an alias table that disagrees with the records, or a record the
   allocator has not passed is refused by name, never defaulted or rebuilt. A catalogue write that fails leaves the
   database refusing further use until it is opened again, because the disk may or may not hold the change.
8. **A write never lands in a dropped box.** Every commit holds the database's catalogue gate in shared mode and
   checks that each box it touches is `live`; create and drop hold it exclusively. A drop and a commit are therefore
   ordered: the commit first, and the drop's range delete removes its keys; or the drop first, and the commit is
   refused whole. A box handle carries the box's id, and ids are never reused, so a handle from before a drop is
   refused, never redirected to a new box of the same name.
9. **A handle belongs to the open database that issued it.** Box ids repeat across databases (each allocates from
   16), so a handle also carries the identity of the open database that issued it, and every other database refuses
   it, as it refuses a handle built by hand or kept from before the store was reopened.
10. **Names are valid Unicode.** A name with an unpaired surrogate is refused: UTF-8 cannot hold it, and two
    different names would otherwise share one alias key.
11. **A database describes itself.** Its format, encoder version and identity are inside it; its id and name belong
    to the store that holds it, and nothing inside it records them, so a database moves between stores whole. Opening
    a database checks that its identity is the one the registry expects (UID and kind), and refuses another
    database's folder by name. The registry's copy of the creation time follows the database's (spec 009).

## 5. Encoding helpers

Products build composite keys with Crystal's order-preserving encoders, so that byte order equals the order the
product means:

| Type | Encoding |
|---|---|
| booleans | one byte |
| unsigned integers | big-endian, fixed width |
| signed integers (32, 64 bit) | big-endian with the sign bit flipped |
| doubles | IEEE bits ordered, `-0.0` normalised to `0.0`, NaN handled |
| decimals | a sign byte, then the ordered magnitude |
| timestamps | UTC ticks as a signed 64-bit integer |
| strings | UTF-8, `0x00` escaped as `0x00 0xFF`, terminated by `0x00 0x01`, so a shorter string sorts before its extensions |
| GUIDs | 16 bytes in their big-endian text order |
| descending order | the byte complement of the ascending encoding |

**Ordinal, never collated.** Strings order by UTF-8 bytes, which is Unicode code-point order, stable across .NET, ICU
and operating-system versions. Any language's text is stored and compared exactly, but ordered by code point, not
alphabetically (`ä` < `å` < `ö`, `Z` < `a`). Crystal never folds case, cuts or normalises a string; a product that keys
on user text normalises it (NFC) itself, and a product that needs linguistic order stores its own versioned sort key.
An **encoder version**, part of the format, refuses data written with different encodings.

The helpers are conveniences. A product may encode its keys any way it likes; Crystal only compares bytes.

## 6. Writes

- **A batch** holds puts, deletes and range deletes on any keys of any boxes of one database, and commits atomically
  as one RocksDB write batch.
- **A batch reads its own writes** before it commits: a read through the batch sees the batch's pending changes over
  the committed state.
- **An empty batch writes nothing and syncs nothing.**
- **A range delete stays inside its box.** Its start may not sort after its end, which is refused when the delete is
  added; an empty end means the end of the box, as it does for a scan.
- **Every write reaches RocksDB through one commit point** (`RocksInstance.Write`), checked on the built assembly.
- **Durability per commit (as built, `006`).** Every commit is acknowledged under one of three modes; the store's
  default is `Strict` (`CrystalOptions.Durability`), and a commit may name another (`Commit(DurabilityMode)`):

  | Mode | A commit returns when | Survives a process crash | Survives power loss or an OS crash |
  |---|---|---|---|
  | `Strict` | its batch is in the WAL and the WAL is synced | yes | yes |
  | `Interval` | its batch is in the WAL; the store syncs every `SyncInterval` (50 ms) | yes | all but the last interval and the time to sync the store's interval databases |
  | `Buffered` | its batch is in the WAL; nothing syncs it but a later strict commit, `SyncAll` or closing | yes | no promise |

  The catalogue and the registry are always `Strict`. Concurrent strict commits share one WAL sync (RocksDB's group
  commit; no Crystal lock is held across the write). One timer per store syncs, each interval, the databases with an
  `Interval` commit not yet synced; closing a database syncs what is left. **A failed write or sync fails the
  database:** it refuses every write until the store is opened again, reads go on, and the failure is logged at
  `Critical`; a sync is never retried. A failed registry write likewise refuses later database changes, and the store's
  `Dispose` reports every failure. A commit whose sync failed was not acknowledged but may replay on reopen. *Not yet
  proven by a process kill:* the kill tests are deferred (`006` T7).
- **Locks are per box,** in a fixed number of stripes (256). Several boxes are locked in ascending stripe order, so
  two multi-box writers cannot deadlock. A writer whose plan keeps changing under its locks may take every stripe
  (`LockAll`). The locks are **not re-entrant**: a thread that locks a box it holds waits like any other. A lock is
  released through the object it returns, on whichever thread disposes it, so it may be held across an `await`.
  Internal `StripeWaiting` / `StripeReleasing` hooks let concurrency tests act at an exact interleaving.

## 7. Reads

- **A snapshot** gives a consistent view of one database: every read through it sees the same committed state.
- **Scans are bounded** both ways. Every iterator carries a lower and an upper bound inside one box, and stops at its
  limit.

## 8. Profiles

| Profile | For | Settings |
|---|---|---|
| `General` | mixed reads and writes | RocksDB defaults |
| `DeleteHeavy` | key ranges with constant insert and delete (queues, moving indexes) | compaction triggered by deletions, and a memtable flush when scans step over too many deleted entries, so the head of a range does not fill with tombstones and scan cost stays flat |

A product may also keep **floors**, lower bounds for scans over time-ordered keys, because it knows its keys. Crystal's
scans accept a lower bound.

## 9. What replication and backup see

- **Replication ships committed batches** of a database by its WAL sequence, whatever boxes they touched (as built,
  spec `003`). A product's data and its metadata, written in one batch, arrive at a standby together; a page that
  touches box 0 makes the standby reload its catalogue before the next read. The full design, the wire protocol and
  the node-local files are in [`replication.md`](replication.md).
- **A standby refuses local writes** while it follows a database (`NotPrimaryException`); the replicated apply writes
  through the WAL directly, not the handle's commit path, so following and refusing coexist.
- **A snapshot bootstrap** is a RocksDB checkpoint of a database (hard-linked beside it) plus a manifest of each file
  with its size and CRC-32C; the standby downloads it in 1 MiB chunks, verifies, and installs it atomically under the
  primary's UID. A restored or promoted database starts a new replication incarnation (and promotion a new epoch), so
  a standby that applied past the old base re-bootstraps rather than diverge silently.
