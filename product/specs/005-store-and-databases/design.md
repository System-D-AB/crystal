# 005 — Store and databases: design

## 1. The directory

```
{dataDir}/
  crystal.format          "crystal 1"
  crystal.lock            held exclusively while the store is open (FileShare.None)
  node.id                 a GUID, created once
  node.epoch              replication's (003-replication)
  _crystal/               the registry: a Crystal database with one box, "databases"
  db-00000010/            "shop"
    name
  db-00000011/            "billing"
    name
  _backups/               a later spec
```

## 2. The registry

`_crystal` is an ordinary Crystal database (same layout as any, `002-box-layout`). It holds one box, `databases`,
whose keys mirror a database's box catalogue:

| Key (within the box) | Value |
|---|---|
| `[0x01][name : escaped UTF-8]` | database id (u32 BE): **the alias table** |
| `[0x02][id : u32 BE]` | the record (JSON): `{ "v": 1, "name": "shop", "replicated": true, "state": "live", "created": …, "dropped": null }` |
| `[0x03]` | allocator: the next database id (u64 BE), starting at 16 |
| `[0x04]` | registry generation (u64 BE) |

Every registry change is one synced batch, under the store gate.

## 3. Operations

| Operation | Folder | Registry batch | In memory |
|---|---|---|---|
| create | `db-{id}` created and opened, `name` written (**before** the batch) | alias + record (`live`) + allocator + generation | added |
| rename `a → b` | `name` rewritten (after the batch) | alias moved, record name, generation | updated |
| swap `a ⇄ b` | both `name` files rewritten (after the batch) | aliases exchanged, both records, generation | updated |
| drop | closed, then deleted (**after** the batch) | alias removed, record `dropped`, generation | removed |

**Crash ordering:**
- **create:** a crash between the folder and the batch leaves an unregistered folder (removed or reported at startup).
- **drop:** a crash between the batch and the folder deletion leaves a dropped record with a folder (deleted at
  startup).
- **rename and swap:** a crash before the `name` file is rewritten leaves a stale `name` file, rewritten at startup
  from the registry.

## 4. Opening a store

```
1. directory missing or empty      → create it, write crystal.format and node.id      (R1.4, R3.3)
2. read crystal.format             → refuse a foreign or future format, or content without a stamp  (R3.2)
3. take crystal.lock               → refuse if held                                    (R1.3)
4. open _crystal; load the registry into memory                                       (R4.4)
5. startup recovery                → dropped folders deleted; unregistered reported or removed;
                                     stale name files rewritten                        (R8.3, R8.4)
6. ready                           → databases open on demand
```

The registry is opened before anything else. Its own format key is checked like any database's (`002-box-layout`).

## 5. Handles and the store gate

```csharp
public sealed class CrystalDatabase        // a handle
{
    public uint Id { get; }
    public string Name { get; }            // current alias; updated by rename and swap
    // boxes, batches, snapshots: 002-box-layout
}
```

- **Every operation through a handle first checks its id is `live`** in the in-memory registry (one dictionary
  lookup), and refuses by name if it is not.
- **Drop** marks the id `dropped` inside the store gate, then waits for operations already past their check
  (a counter per database), then closes the instance. New operations are refused from the moment of the mark.

## 6. API

```csharp
namespace SystemD.Crystal;

public sealed class CrystalStore : IDisposable
{
    public static CrystalStore Open(string dataDirectory, CrystalOptions options);
    public static void Reset(string dataDirectory);                       // offline; keeps format, node.id, node.epoch

    public CrystalDatabase CreateDatabase(string name, bool replicated = true);
    public CrystalDatabase OpenDatabase(string name);
    public IReadOnlyList<DatabaseInfo> ListDatabases();
    public void RenameDatabase(CrystalDatabase database, string newName);
    public void SwapDatabases(CrystalDatabase first, CrystalDatabase second);
    public void DropDatabase(CrystalDatabase database);

    public long RegistryGeneration { get; }
}

public sealed record DatabaseInfo(uint Id, string Name, bool Replicated, DateTimeOffset Created);

public sealed class CrystalOptions
{
    public long BlockCacheBytes { get; init; } = 256L << 20;
    public long WriteBufferBudgetBytes { get; init; } = 512L << 20;    // across all databases
    public int MaxOpenDatabases { get; init; } = 256;
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
```

**Typed errors:**
- `CrystalFormatException`: the format, what was found, the way forward;
- `StoreLockedException`;
- `DatabaseNotFoundException` (unknown, or dropped);
- `DatabaseNameTakenException`;
- `ReservedNameException`;
- `NotPrimaryException`, on a standby (R11.3).

## 7. Shared resources

- **One `Cache` and one `Env`** (background threads) per store, given to every database's options.
- **Write buffers come from one `WriteBufferManager`** sized by `WriteBufferBudgetBytes`, so memory does not grow with
  the number of databases.
- **`MaxOpenDatabases`** caps open instances (file handles, memory). Databases are not closed when idle (R9.3).

## 8. Reset

1. Take `crystal.lock`, refusing if it is held.
2. Delete every `db-*` folder and `_crystal`.
3. Recreate an empty `_crystal`.
4. Keep `crystal.format`, `node.id` and `node.epoch`.
5. Release the lock.

## 9. On a standby

The registry arrives through replication. Applying a registry batch updates the in-memory registry: a new database is
created locally and bootstraps by snapshot; a rename or swap changes only aliases and `name` files; a drop closes and
deletes the folder. Local create, rename, swap and drop are refused with `NotPrimaryException`. A not-replicated
database exists only in its own node's registry, so a standby's registry is the primary's plus its own local
databases. **How the two are kept apart** in one registry (for example a separate local section) is a detail for
`003-replication` to settle.

## Addendum 2026-10-09: corrections after review

A review of the first implementation found defects, some of them in this design's ordering. The sections above stay
as written; where they disagree with this addendum, the addendum decides.

### A1. Opening order and the node identity (replaces § 4 steps 1–3)

```
1. read-only check of crystal.format, writing nothing   → refuse unstamped content, a foreign or a future stamp (R3.2)
2. create the directory if missing; take crystal.lock   → refuse if held (R1.3)
3. under the lock: stamp a new store, or verify the stamp and node.id
```

The first open stamped the store and wrote `node.id` **before** taking the lock, so two processes opening one new
directory could each write a different `node.id`, and a stamped store whose `node.id` was missing was silently given
a new one. Now:

- Stamping writes `node.id`, then `crystal.format`, each to a temporary file that is flushed and renamed.
  `crystal.format` is the commit point.
- A directory holding only `crystal.lock`, `node.id` and stamping temporaries is an **interrupted creation**: it is
  stamped again, keeping its `node.id`.
- A stamped store whose `node.id` is missing or not a GUID is **refused** (R3.4). Crystal never regenerates it,
  because replication knows the node by it.

### A2. Create persists the allocator first (replaces § 3's create row)

```
1. advance the allocator past the new id: one synced registry write      (crash: an id is skipped, nothing else)
2. create db-{id} and its name file; open it; write its catalogue         (crash: a folder the allocator has passed)
3. alias + record + generation: one synced registry batch                 (the database exists)
```

In the first order the allocator moved only in step 3's batch, so a crash after the folder left a folder at an id
the allocator had **not** passed. Startup recovery reported it and left it, and the next create reused the id and
opened the stale folder. Advancing the allocator is not a visible registry change and does not move the generation.

### A3. Handles, operations and closing (replaces § 5's last two bullets)

- **One handle per open database.** `OpenDatabase` returns the handle already open, so rename and swap update the
  name every holder sees.
- Each open database has a **slot**: its instance, its catalogue, an in-flight counter and a closing state. Every
  call through a handle, batch or snapshot enters the slot **without a lock** (an interlocked counter and a closing
  flag), and is refused once the slot is closing. **No data operation takes the store gate** (R7.1).
- A scope lasts **one call**, not the life of a batch or snapshot; a scan's scope lasts until its enumeration ends.
  A batch or snapshot held across a drop is refused on its next call and does not hold the drop up.
- **Closing** (drop or store dispose): mark closing, wait for calls in progress, release native snapshots still
  held, close the catalogue and the instance. Store dispose closes every open database this way, and waits for any
  drop in progress before releasing the shared resources and the lock.

### A4. The registry inside `_crystal` (clarifies § 2)

`_crystal` opens with the standard box-0 catalogue of `002-box-layout`. The registry lives in **kernel box 1**
(ids 1–15 are reserved for the kernel), with § 2's keys; kernel boxes are fixed by id and are not entries of the
product catalogue. Loading reads both the alias table and the records and **refuses** the store when they disagree
or a record's id is not below the allocator. A store with database folders and no registry is refused, never given
an empty one.

### A5. Name files

After a rename or swap, the `name` files are written after the registry batch **and** the in-memory registry are
updated. A failure to write one is logged and repaired at the next open (R8.4); it never leaves memory and disk
disagreeing. `name` files are UTF-8 without a byte-order mark.

### A6. Errors

- `DatabaseLimitException` (new): `MaxOpenDatabases` reached (R9.2, R9.3), naming the limit and the ways forward.
- A refusal names only a way forward that works (R10.3):
  - unstamped content: not a Crystal directory; use an empty one;
  - a foreign or future stamp: a build that reads it;
  - a registry or database whose own format is wrong: `CrystalStore.Reset`, where the data is not needed.

### A7. Reset (replaces § 8 steps 1–2)

Reset takes the lock first, then requires a directory stamped `crystal 1` with a valid `node.id`, and refuses any
other directory: it never empties a directory it cannot identify as a Crystal store. It deletes `_crystal` and the
folders named exactly `db-{8 lowercase hex digits}`, and leaves anything else.

### A8. API beyond § 6 (proposed; for the maintainer's approval)

The first implementation added these without recording them: `CrystalStore.DataDirectory`, `CrystalStore.NodeId`,
`CrystalOptions.BackgroundThreads`, `CrystalOptions.LoggerFactory`, and an `options` parameter on `Reset`. This
addendum adds `DatabaseLimitException`. Tests reach named points in create, drop, rename, swap and dispose through
an **internal** hook on `CrystalOptions`; it is not public API.

### A9. After the independent review (2026-10-09)

A second reviewer found two ways the corrections above could still lose data, and smaller gaps:

- **A refused create must not move the allocator.** A folder already at the next id is refused *before* the allocator
  advances. Otherwise the next open takes the stranger folder for an interrupted create and deletes it.
- **An empty registry is never started over database folders**, whether `_crystal` is missing or present and empty.
- **Damaged registry entries are refused by name** with the way forward: a record that does not parse, lacks its
  version, or names a database against the name rules; an alias that does not decode or has trailing bytes.
- **Reset deletes `_crystal` first**, so a crash part-way leaves a store that `Open` refuses with Reset as the way
  forward, never a registry naming databases whose folders are gone.
- **A committed drop never reports failure** over a folder that will not delete yet; the next open deletes it.
- **Create keeps the folder** if its registry write fails, since a failed synced write may still be durable; the next
  open keeps it (registered) or removes it (not registered).
- **Store dispose closes every instance it can and always releases the lock**, then reports what failed.
- **Accepted, documented:** a scan enumeration that is never disposed holds its database open, so a drop or store
  dispose waits for it (logging every 10 s); calling either from inside a `foreach` over a scan of the same
  database waits forever. This is A3's rule, stated on the public `Scan` methods.

### A10. Databases describe themselves (2026-10-10, spec `009`)

A database's UID, kind and creation time now live in its own box 0 (space `0x07`), written with its catalogue and
never changed. The registry keeps what is the store's: names, ids, replicated, live or dropped, the allocator and the
generation, plus copies of the UID and the creation time so that listing never opens a database. Opening a database
checks its identity against the registry and refuses another database's folder; the registry's creation time follows
the database's. `_crystal` holds an identity of kind `registry`. See `009-self-describing-databases`.
