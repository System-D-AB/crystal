# 010 — Backup and restore

> **Status:** implemented (2026-10-14; approved and amended 2026-10-13, see the addendum at the end). **Depends on:** `009-self-describing-databases` (a database carries its own
> identity, so it moves between stores without `_crystal`), `005-store-and-databases` (the registry, create's crash
> ordering, the store gate), `001-2-rocksdb-binding` (every native call through `NativeRocks`), `006-durability`,
> `007-observability`. **Uses:** `004-test-suite` (the crash harness and test nodes).

## Introduction

Crystal promises that a database is "the unit of isolation, backup and drop" (box-layout § 1), but has no backup.
This spec delivers one: a **store-wide backup** taken while the store serves, **verified**, **kept to a retention
limit**, and **restored** into a new or a running store.

**What the products taught.** StowDB's backup copied each database with a RocksDB checkpoint into a folder inside its
own data directory. It had:
- no manifest, no checksum and no retention;
- a gate held for the whole copy, which stalled every request;
- a destination the client chose and the server ignored;
- no restore tool: "put the folder back" was the restore, and it was proven once, by hand.

An earlier design that shared one RocksDB instance across databases could take one checkpoint of everything. Each
Crystal database is now its own instance, so that is gone. Highway has no backup at all, only replication snapshots.
Neither product used RocksDB's `BackupEngine`.

**The mechanism.** RocksDB's `BackupEngine`, which the pinned binding exposes as native calls:
- **incremental:** files are shared between backups by name and checksum, so a second backup copies only new files;
- flushes before copying, so every acknowledged commit is in the backup, whatever its durability mode;
- lists, verifies, purges and restores backups;
- can be stopped mid-copy;
- cleans up an interrupted backup the next time it opens, and never touches a finished one.

**The shape.**
- **One `BackupEngine` per database**, one database at a time, under a backup root the caller chooses, outside the
  data directory.
- **A set manifest** written last, which ties one backup of each database into a **backup set**.
- **`_crystal` is not copied.** Each database describes itself (`009`), and the manifest records the names and
  replicated flags the store gave them.
- **A restore attaches databases to a store**, like a create, with new ids from that store. The restored databases
  appear together, in one registry batch, or not at all.

## Requirements

### R1 — Backing up

- **R1.1** `CrystalStore.BackupAsync(destination, options, cancellationToken)` backs up **every live database of the
  store**, replicated or not, into the backup root `destination`, and returns the new set's description.
- **R1.2** **The store keeps serving.** Reads, commits, box changes and database operations continue during a backup.
  The store gate is held only to read the list of databases at the start (R3.1), never across a copy.
- **R1.3** **One database at a time,** each with its own `BackupEngine`. The engine flushes the database's memtables
  first, so every commit acknowledged before that database's backup began is in it, in every durability mode.
- **R1.4** **Each database is captured at its own moment,** not one moment across all databases. A product that needs
  an invariant across data keeps that data in one database. This is the same limit as replication (`003` R1.7), and the
  API documentation states it.
- **R1.5** For each database the set records a **sequence**: every commit at or below it is in the backup. Commits
  above it may be in it too.
- **R1.6** **One backup at a time per store.** A second call while one runs is refused with
  `BackupInProgressException`, naming the running backup's destination.
- **R1.7** **Options:**
  - a label, recorded in the set;
  - a copy rate limit in bytes per second (default none);
  - the number of background copy threads (default 2).

### R2 — The destination

- **R2.1** `destination` is an absolute path. It is created if missing. It must be **empty**, or a **backup root**
  (it holds the root file). Anything else is refused with `BackupDestinationException`, naming the path and the way forward.
- **R2.2** A destination **inside the data directory, or containing it,** is refused. A backup that shares a folder
  with what it protects protects nothing.
- **R2.3** **Any store may back up into any root.** The caller names the folder on every call, and a root is not tied
  to the store that first used it. Each set records the `node.id` of the store it came from. After a failover, the new
  primary backs up into the same root: its databases keep their UIDs, so their backups continue in the same engines,
  and every earlier set stays listed and restorable.
- **R2.4** **A root is used by one operation at a time,** across processes: backup, verify, purge and restore each
  hold an exclusive lock file in the root. A second operation is refused at once with `BackupRootLockedException`,
  never queued.

### R3 — Membership

- **R3.1** **The set's databases are fixed at the start.** Under the store gate, for a moment, the backup reads the
  live databases with their names, UIDs, replicated flags and creation times. A database created after that moment is
  not in this set.
- **R3.2** **A drop during a backup goes ahead and is never delayed by it.**
  - If the backup is copying that database, the drop stops the copy (the engine's stop call), and the backup moves on.
  - The set lists the database under **omitted**, with the reason `dropped`.
  - A rename or swap during a backup changes nothing in the set: it records the names read at R3.1.
- **R3.3** A database that is closed is opened for its backup and closed again afterwards. The backup holds **at most
  one** database open that the product did not open. If `MaxOpenDatabases` leaves no room for it, the backup fails
  with `DatabaseLimitException`, and the sets already written are untouched.
- **R3.4** **Disposing the store stops a running backup.** The backup fails with `ObjectDisposedException`, and no set
  is written.

### R4 — The backup set

- **R4.1** **A set exists only once its manifest exists.** The manifest is written last, to a temporary file that is
  flushed and then renamed. A backup that fails, is cancelled or is killed leaves no set. Its partial engine backups
  are cleaned up by the engine when it next opens.
- **R4.2** **The manifest records:**
  - its own format, the set id, start and completion times, the label, and the source `node.id`;
  - the store format, catalogue format, encoder version, and the Crystal and RocksDB versions;
  - for each database: its name, UID, replicated flag, creation time, engine backup id, sequence (R1.5), size and file
    count;
  - the omitted databases (R3.2);
  - a **SHA-256 of its own content**.

  A manifest whose checksum does not match is never used.
- **R4.3** Each engine backup carries the set id and the database UID in its application metadata, so an engine
  backup can be traced to its set without the manifest.
- **R4.4** **Cancellation** (the token) stops the native copy promptly, then fails the backup with
  `OperationCanceledException`. **Earlier sets are untouched** by a cancelled, failed or killed backup.

### R5 — Listing, verifying, purging

These work on a backup root without a store, through `CrystalBackupRoot.Open(path)`, which holds the root's lock
(R2.4).

- **R5.1** **List:** every complete set with its manifest's facts, oldest first. A manifest that fails its checksum
  is listed as damaged, never as a set.
- **R5.2** **Quick verify:** the manifest's checksum, and each engine backup present with every file at its recorded
  size.
- **R5.3** **Deep verify:**
  1. Each database is restored into a scratch folder: under the root by default, or a folder the caller names.
  2. It is opened read-only.
  3. Every file's checksum is verified by RocksDB.
  4. Its catalogue is loaded, and its identity is checked against the manifest.
  5. The scratch folder is deleted.

  Deep verify is the only check that reads every byte. The documentation says so.
- **R5.4** The result names each database and either `ok` or what was wrong. A verify that finds damage returns it
  rather than throwing. It throws only if it cannot run.
- **R5.5** **Purge** keeps the newest `keepLast` sets (at least 1):
  1. It deletes the other manifests first.
  2. Then, in each engine, it deletes the engine backups older than the oldest one a kept set references.
  3. Then it deletes the folders of engines no kept set references.

  A crash part-way leaves only data no set references, never a set with missing data.

### R6 — Restoring

- **R6.1** `CrystalStore.RestoreAsync(source, setId, options, cancellationToken)` **attaches the set's databases to
  this store**, open or new. To restore into a new directory, the caller opens a new store there and restores into it.
  The restored store has its own `node.id`, never the source's.
- **R6.2** **Before writing anything**, the restore checks:
  - the manifest's checksum, and every format it records: the store format, the catalogue format and the encoder
    version, each one this build reads;
  - each database's quick verify (R5.2).

  A set this build cannot read is refused with `CrystalFormatException`, naming the format and the way forward
  (a build that reads it).
- **R6.3** **Each restored database gets a new id** from this store's allocator, in a `db-{id}` folder like a create.
  It keeps its UID, its creation time and its boxes. **Its name** is the one in the manifest, or the one the caller
  maps it to. **Its replicated flag** is the manifest's, unless the caller overrides it.
- **R6.4** **Refusals** happen before anything is written:
  - a name already live in the store: `DatabaseNameTakenException`;
  - a UID already in the store: `DatabaseUidTakenException`, new. The way forward is to drop the existing one, or to
    restore it as a copy (R6.5).
- **R6.5** **Restoring as a copy:** with `NewIdentity`, each restored database gets a new UID, written into its
  identity record before it is registered. This is the only time an identity record changes (`009` R1.1). It is how a
  product restores an earlier copy beside the live database, checks it, and then swaps the two.
- **R6.6** **All or nothing.**
  1. The restored databases are written into their folders and opened.
  2. Each one's catalogue is loaded, and its identity is checked against the manifest, through `009`'s check.
  3. Only then are all of them registered, in **one synced registry batch** under the store gate, which re-checks the
     names and UIDs.

  Before that batch, no restored database is visible. A failure deletes the folders it made. A crash leaves folders
  whose ids the allocator has passed and the registry does not name, which the next open removes (`005` addendum A2).
- **R6.7** **A subset:** the caller may name which of the set's databases to restore. The default is all of them.
- **R6.8** A restored database is a **new incarnation** for replication: it starts no history it did not have.
  - **On a primary,** a restored replicated database is bootstrapped by its standbys like any new database.
  - **A standby** whose copy has the same UID but a different history detects the divergence through `003` R5.2.
  - **A store following a primary refuses to restore** (when `003` T12 gives stores their roles); the primary owns
    the replicated set.

### R7 — Observability

- **R7.1** **Log events in the 1700s range:**
  - backup started, completed and failed;
  - database omitted;
  - restore started, completed and failed;
  - verify found damage;
  - purge completed.

  Each event names the root and the set. None contains a key or value (`007`).
- **R7.2** **Metrics on the store's meter:**
  - `crystal.backup.duration` (histogram, seconds);
  - `crystal.backup.bytes` and `crystal.backup.files` (counters: what was copied, not what was shared);
  - `crystal.backup.failures` (counter);
  - `crystal.backup.last_success` (gauge: Unix seconds of the last complete set, absent before the first).
- **R7.3** `Status()` reports a running backup: its destination, the databases done and to do, and the bytes copied.

### R8 — Boundaries

- **R8.1** **Every native call goes through `NativeRocks`** and is listed in `BindingSurfaceTests`. No RocksDB type
  appears on a public signature (playbook § 7).
- **R8.2** `BackupAsync`, `RestoreAsync` and `VerifyAsync` run their native work on a thread-pool thread. **They
  yield** the calling thread, and the API documentation says so (playbook § 7).

### R9 — Proof

- **R9.1** **Round trip:** a store of several databases with boxes of both profiles, renamed and swapped before the
  backup, is backed up, restored into a new store, and compared key by key and box by box, names included.
- **R9.2** **Incremental:** a second backup after a small change copies only new files (the bytes counter), and both
  sets restore correctly.
- **R9.3** **Under load:** a backup taken while the crash harness writes its stream restores to a **whole prefix** of
  the stream in each database. Every batch is whole, and the prefix reaches at least the sequence the manifest
  records.
- **R9.4** **Killed mid-backup:** a process killed during a backup leaves every earlier set listed, verifiable and
  restorable. The next backup into the root succeeds.
- **R9.5** **Cancelled:** a cancelled backup throws `OperationCanceledException`, writes no set, and leaves earlier sets
  intact. Its copy stops within a bounded time (the test states it).
- **R9.6** **Drop during backup:** with the copy held at a hook, a drop completes without waiting for it. The set lists
  the database as omitted, and the other databases are in the set.
- **R9.7** **Verify:**
  - a deleted engine file is caught by quick verify;
  - a flipped byte inside a table file is caught by deep verify and not by quick verify;
  - an edited manifest is caught by both.
- **R9.8** **Refusals, each by name:**
  - a destination inside the data directory, or a non-empty foreign one;
  - a locked root;
  - a second concurrent backup;
  - restore of a taken name or UID;
  - an unknown set;
  - a damaged manifest;
  - a foreign format.
- **R9.9** **Restore:**
  - all or nothing: a failure injected after the second of three databases leaves the store as it was;
  - a process killed before the registry batch leaves no visible database, and leaves no folder after the next open;
  - `NewIdentity` restores beside the original;
  - a subset;
  - restore into a running store while it serves other databases.
- **R9.10** **Purge:** `keepLast` sets remain, each restorable. Engines of databases no kept set includes are deleted.
- **R9.11** **Mutation checks** of every guard in the real source, and an **independent review** (playbook § 4:
  backup).
- **R9.12** **A real run:** the packed library used from a separate program backs up and restores a store.

### R10 — Documents

- **R10.1** `engineering/box-layout.md`:
  - § 2 drops `_backups/` from the data directory;
  - § 9 states what a backup is: one engine backup per database plus a set manifest, with no `_crystal`;
  - a restore attaches with new ids.
- **R10.2** A new `engineering/backup.md`: the root's layout, the manifest's fields, the guarantees and limits of
  R1.3–R1.5, and how to back up, verify, purge and restore. `product/intro.md` is updated where it speaks of backup.

## Out of scope

- **Logical export and import** (a product's own format, as StowDB's document export).
- **Point-in-time recovery** from archived WAL; one moment across all databases.
- **Encryption, compression, and remote targets** such as object stores: the product copies or ships the root.
- **Scheduling:** the product decides when to back up.
- **Backing up `_crystal`, `node.id` or `node.epoch`:** a restored store is a new node (R6.1).

## Open questions

1. **The default scratch folder for deep verify** is under the root (R5.3). If the root is on slow or remote storage,
   the caller names a local folder. Is the default right?
2. **A restore into the data directory of a store that is not open** is done by opening it first (R6.1). An offline
   restore tool (a command-line program) is not part of this spec. Is one wanted later?

## Addendum 2026-10-13: after `009`, `003` and `011` were implemented

This spec was drafted before `009` landed and before replication was completed by `011`. `009` was built as this spec
assumed; replication now gives stores roles, histories and per-database records that a restore must respect. The
requirements below are added; where they change a requirement above, they say so. The maintainer approved starting
the work on 2026-10-13; the open questions keep their stated defaults (the scratch folder under the root; no offline
restore tool for now).

- **R11.1 A drop removes the database's replication records.** Dropping a replicated database deletes its follow
  record, incarnation record and resync marker (`replication/follow/{uid}.json`, `replication/incarnation/{uid}.json`,
  `replication/resync/{uid}`), durably. Without this, a restore that brings the UID back would inherit a history it
  does not have, and a promotion would claim it (a gap in `003` that a restore exposes).
- **R11.2 A restore clears the same records for each UID it restores,** before it registers them: a crash before
  R11.1 existed, or a hand-made folder, may have left them. A restored database then starts a fresh history on a
  primary (it has neither an incarnation record nor a follow record), and its standbys bootstrap it like any new
  database. This replaces R6.8's first two points; its third is R11.3.
- **R11.3 A restore of replicated databases is refused unless this node is the primary** and accepts writes: on a
  standby, a fenced node, or a primary handing over (`011` R14), with `NotPrimaryException`, before anything is written,
  and admitted through the same write gate as `CreateDatabase`, so a demotion waits for a restore in progress. A
  database restored as not replicated (`RestoreOptions.Replicated = false`) may be restored on any node. A backup is
  allowed on any node: it only reads.
- **R11.4 A restore is a change of the replicated set:** its registry batch goes through the same path as a create, so
  waiting pulls wake at once (`011` R11.1) and standbys bootstrap the restored databases.
- **R11.5 Crash ordering as `011` R9:** every rename that publishes state (the root file, a manifest, each restored
  folder moved into `db-{id}`) is followed by a sync of its directory, and the allocator is advanced before any folder
  is moved in (as R6.6 already requires).
- **R11.6 `009` names:** `009` built the identity check as `LoadCatalogue(kind, expectedUid)`; R5.3, R6.6 and the
  design use it under that name. A restored folder's identity is checked before it is moved into place, as
  `InstallBootstrap` checks a downloaded snapshot (`009` T3), so a wrong folder never reaches `db-{id}`.
- **R11.7 `NewIdentity` is `009`'s one exception,** recorded in `009` by its own addendum: the identity record of a
  database restored as a copy is rewritten with a new UID before it is registered, and never again.
- **R11.8 Proof:** tests for R11.1 (a drop leaves none of the records), R11.2 (a restored replicated database on a
  primary has a fresh history: no incarnation of its earlier life is claimed), R11.3 (refused on a standby, a fenced
  node and during a handover; a not-replicated restore allowed on a standby), and R11.4 (a standby following the
  primary bootstraps the restored database promptly); each guard with a mutation check.
