# 005 — Store and databases

> **Status:** implemented (2026-10-09). **Depends on:** `001-project-skeleton`, `001-2-rocksdb-binding`. **Implemented before** `002-box-layout`: a box lives in
> a database, and a database lives in a store.
> **Authority document:** [`engineering/box-layout.md`](../../../engineering/box-layout.md) § 2 (the data directory).

## Introduction

A **store** is what an application opens: one data directory, holding any number of **databases**. Each database is
its own RocksDB instance in its own folder, with its own write-ahead log: the unit of isolation, backup and drop.

This spec defines:
- the store's lifecycle;
- the data directory;
- the store's format stamp and the node identity;
- the **registry**, the store's system database `_crystal`;
- the full **database lifecycle**: create, open, list, rename, swap, drop;
- crash safety at every step;
- the resources databases share;
- offline reset.

**Databases follow the same model as boxes, one level up.** A database's folder is named by its **id** and never
moves. Its **name is an alias** in the registry, and can be renamed or swapped while the database is open and in use.
Ids are never reused, so a handle to a dropped database is refused, never redirected to a new database of the same
name.

## Requirements

### R1 — The store

- **R1.1** `CrystalStore.Open(dataDirectory, options)` opens a store: it checks the format stamp (R3), takes the store
  lock (R1.3), opens the registry (R4), runs startup recovery (R8), and opens no other database until asked.
- **R1.2** **One open store per directory per machine.** Disposing the store closes every open database and releases
  the lock.
- **R1.3** **The store lock:** a lock file held exclusively for the store's lifetime. A second open, from this process
  or another, is refused at once by name, never left waiting and never opened twice.
- **R1.4** A store whose directory does not exist, or is empty, is created and stamped (R3.3). A directory with other
  content and no stamp is refused (R3.2).

### R2 — The data directory

- **R2.1** The layout:

  ```
  {dataDir}/
    crystal.format     the store's format stamp
    crystal.lock       the store lock
    node.id            the node's identity
    node.epoch         the node's replication epoch (spec 003-replication)
    _crystal/          the registry: a Crystal database
    db-{id}/           one folder per database, named by its id
      name             the database's current name, for people reading the directory
    _backups/          checkpoints (a later spec)
  ```

- **R2.2** Crystal owns everything in the directory. A product keeps its own files elsewhere.

### R3 — Format stamp and node identity

- **R3.1** `crystal.format` holds the store's format as `crystal <n>`. It is checked **before any RocksDB instance is
  opened**.
- **R3.2** A store whose stamp names a format this build cannot read, or a directory with content but no stamp, is
  **refused by name**: what was found, what this build reads, and the way forward. It is never opened as if empty.
- **R3.3** A new store is stamped as it is created, so the next build can check it.
- **R3.4** `node.id` is created once, with the store. It identifies the node to replication and survives a reset
  (R10).

### R4 — The registry

- **R4.1** The registry is the store's system database, `_crystal`, opened with the store.
- **R4.2** It holds:
  - the **database alias table**, name → database id;
  - a **record** per id: name, replicated or not, state (`live` or `dropped`), created and dropped times, record
    version;
  - the **database id allocator**;
  - the **registry generation**, incremented by every change.
- **R4.3** **Database ids are never reused.** The allocator only moves forward. Products' databases start at 16.
- **R4.4** The registry is held in memory once the store is open. Open and list never read the store.
- **R4.5** **The registry decides which databases exist.** A folder that is not registered is never served.

### R5 — The database lifecycle

- **R5.1** **Create** (`name`, replicated or not):
  1. allocates an id;
  2. creates and opens the folder `db-{id}`, writing its `name` file;
  3. commits the alias and the record in one registry batch.

  A name that exists is refused by name.
- **R5.2** **Open** resolves the name through the registry and opens the database, or returns the instance already
  open. Opening a dropped or unknown name is refused by name.
- **R5.3** **List** returns every live database (name, id, replicated, created) from the in-memory registry.
- **R5.4** **Rename** changes only the registry: the alias moves, the record's name and the `name` file are updated.
  The folder, the data and every open handle are untouched. It works while the database is open and being written.
  A taken name is refused.
- **R5.5** **Swap** exchanges the names of two live databases in one registry batch. A reader resolving either name
  sees both before the swap or both after.
- **R5.6** **Drop**:
  1. commits the record as `dropped` and removes the alias in one registry batch;
  2. waits for the database's in-flight operations to finish;
  3. closes it;
  4. deletes its folder.
- **R5.7** **Handles carry the id.** A database handle survives rename and swap. After a drop, every operation through
  it is refused by name.
- **R5.8** **A dropped name can be created again**: a new id, a new folder, nothing of the old database visible.

### R6 — Names

- **R6.1** 1–256 UTF-8 bytes; no control characters, no `/` or `\`. Names are compared ordinally (case-sensitive).
- **R6.2** Names beginning with `_` are reserved for Crystal. Creating or renaming to one is refused.

### R7 — Serialisation

- **R7.1** Create, rename, swap and drop are serialised per store by a **store gate**, the same pattern as the
  catalogue gate of `002-box-layout`. Operations inside databases do not take it.
- **R7.2** An open that races a drop of the same database either opens before the drop (and is then refused like any
  handle) or is refused.

### R8 — Crash safety and startup recovery

- **R8.1** **Create** makes the folder before the registry batch: a crash leaves an unregistered folder, never a
  registered database without a folder.
- **R8.2** **Drop** commits the registry batch before deleting the folder: a crash leaves a dropped record whose folder
  still exists.
- **R8.3** **Startup recovery,** on every open:
  - deletes the folder of every dropped record;
  - removes every unregistered folder named `db-{id}` with an id the allocator has already passed (left by a crash
    during create, R8.1);
  - reports anything else it does not recognise, and never touches it.
- **R8.4** A `name` file that disagrees with the registry is rewritten from the registry. The registry decides.

### R9 — Shared resources

- **R9.1** All databases of a store share one block cache and one background thread pool. Each database has its own
  WAL and write buffers.
- **R9.2** Budgets are store options: block cache size, total write-buffer memory across databases, and a limit on
  open databases. Exceeding the limit is refused by name, never degraded silently.
- **R9.3** A database stays open until it is dropped or the store closes. Opening more than `MaxOpenDatabases` is
  refused by name.

### R10 — Reset

- **R10.1** `CrystalStore.Reset(dataDirectory)` empties a store **offline**: it deletes every database folder and the
  registry's content, and keeps `crystal.format`, `node.id` and `node.epoch`.
- **R10.2** Reset is refused while the store is open anywhere (the store lock, R1.3).
- **R10.3** Format refusals (R3.2) name reset as one of the ways forward where it applies.

### R11 — Replication interplay

- **R11.1** The registry is replicated first (spec `003-replication`). A standby learns the database set, and every
  create, rename, swap and drop, from it.
- **R11.2** A standby's folders are named by id too, so a rename or swap reaches a standby as an ordinary registry
  change, with no folder moves.
- **R11.3** On a standby, create, rename, swap and drop are refused: the primary decides the database set.
- **R11.4** A database created **not replicated** is local to its node: it is in that node's registry only, and is
  not created on standbys.

### R12 — No migration of other layouts

- **R12.1** Crystal opens only stores it laid out. A product moving onto Crystal adopts this layout; there is no
  importer for folders named any other way.

### R13 — Verification

- **R13.1** **Every operation:** create, open, list, rename, swap and drop, including rename and swap while both
  databases are open and being written.
- **R13.2** **Crash at every step** of create and drop (a process kill), then reopen: no unregistered folder served,
  no registered database without a folder, no half-dropped database.
- **R13.3** **Startup recovery:** a dropped record's folder is deleted; a clearly-Crystal unregistered folder is
  removed; an unknown folder is reported and left alone.
- **R13.4** **Handles:** survive rename and swap; are refused after a drop; a re-created name is a different database.
- **R13.5** **The store lock:** a second open is refused, from the same and from another process; reset is refused
  while open.
- **R13.6** **Format:** a fresh store is stamped; a foreign or future stamp is refused with the way forward; content
  without a stamp is refused.
- **R13.7** **Cost:** create, rename, swap, drop and list cost the same with 10 databases as with 1 000; rename of a
  database holding a million keys costs the same as of an empty one.

### R14 — Reuse: copy and adapt

Implement by **copying and adapting** the sources below, not by writing anew (playbook, *Reuse before
writing*). Every copied file starts with a comment naming its origin (`// Ported from Highway: <path>` or
`// Ported from StowDB: <path>`), and the tests that prove it come with it (`004-test-suite` design § 2). Crystal's own
RocksDB layer, built by `001-2-rocksdb-binding`, is used as is and never re-wrapped.

| Source | Path | Take | Change |
|---|---|---|---|
| Crystal (`001-2`) | `src/SystemD.Crystal/Storage/StoreResources.cs`, `RocksInstance.cs`, `OfflineCompaction.cs` | shared cache, environment, write-buffer manager; opening a database with its families; offline compaction | used as is |
| StowDB (the StowDB repository) | `src/Stow.Storage.Crystal/CrystalEngine.cs` | the multi-database engine: one RocksDB instance per folder, the registry box, create and drop (drop unregisters first), unregistered folders reported and never served, opening the system database first, per-database checkpoints | **folders named by id** with names as aliases; add rename and swap; registry in `_crystal` with an id allocator; **drop** users, grants and everything else StowDB-specific |
| StowDB (the StowDB repository) | `src/Stow.Storage.Crystal/InternalBoxes.cs` | the pattern of system boxes inside the system database | one box, `databases`, with the alias table and records (design § 2) |
| StowDB (the StowDB repository) | `src/Stow.Abstractions/Engine/DatabaseNameRules.cs` | name validation and reserved names | Crystal's rules (R6): `_` reserved, 1–256 bytes, no separators |
| StowDB (the StowDB repository) | `src/Crystal/Crystal.Storage.RocksDb/RocksSharedResources.cs` | the origin of shared resources | already superseded by Crystal's `StoreResources` (`001-2`); nothing more to copy |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/HighwayServerBuilder.cs` (the `highway.format` stamp) | stamping a fresh directory; refusing an older, foreign or unusable directory **before** RocksDB opens, with a message naming the ways out | `crystal.format`; Crystal's wording and typed exception |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/Storage/Rocks/RocksDbStore.cs` (`NodeLocalFiles`, `IsNodeLocalFile`, `AssertColumnFamilyOrder`, `StorageLayoutMessage`) | node-local files kept by a wipe; the family-order check; the refusal message's shape | `node.id` and `node.epoch`; family order from `001-2` |
| Highway (`github.com/System-D-AB/highway`) | `src/Highway.Server/HighwayStorageMaintenance.cs` (`WipeData`, `Compact`) | offline wipe that keeps the node's identity and refuses while the store is held | becomes `CrystalStore.Reset` (R10), over every database folder and `_crystal` |

## Out of scope

- What is inside a database (boxes, batches, the catalogue): `002-box-layout`.
- Replicating the registry and databases: `003-replication`.
- Backup and restore: a later spec.
- Batches across databases: refused (each database is its own RocksDB instance).

## Decisions

| Question | Decision | Basis |
|---|---|---|
| Unregistered folders at startup | **Removed** when named `db-{id}` with an id the allocator has passed (a crash between folder and registry); **anything else reported and never touched** | the only way Crystal leaves an unregistered folder is R8.1's crash ordering |
| Closing idle databases | **No.** A database stays open until it is dropped or the store closes; `MaxOpenDatabases` bounds them, and opening beyond it is refused by name | opening a RocksDB instance is expensive; transparent reopen adds latency spikes and lifetime bugs |
| Defaults | `MaxOpenDatabases` **256**, `WriteBufferBudgetBytes` **512 MiB**, `BlockCacheBytes` **256 MiB** | the block cache default StowDB already runs; the write-buffer budget two default write buffers per open database would otherwise exceed |
