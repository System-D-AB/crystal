# Backup and restore

> **Status: implemented** by spec `010-backup-and-restore`. Where this document and the code disagree, one of them has a
> defect, and it is fixed or recorded, never left silent.

A Crystal backup is **one RocksDB `BackupEngine` backup per database, plus a set manifest** that ties them together.
`_crystal`, `node.id` and `node.epoch` are not copied: each database carries its own identity (spec `009`), and the
manifest records the names and replicated flags the store gave them. A restore **attaches** databases to a store, with
new ids from that store, like a create.

## 1. Using it

```csharp
// Back up every live database. Allowed on any node (it only reads); yields the calling thread.
BackupSetInfo set = await store.BackupAsync(@"D:\backups\shop", new BackupOptions { Label = "nightly" });

// List, verify and purge, without a store.
using (var root = CrystalBackupRoot.Open(@"D:\backups\shop"))
{
    IReadOnlyList<BackupSetInfo> sets = root.ListSets();
    BackupVerifyResult quick = await root.VerifyAsync(set.SetId, deep: false);
    BackupVerifyResult deep = await root.VerifyAsync(set.SetId, deep: true);
    root.Purge(keepLast: 7);
}

// Restore into a store (a new one: open it on an empty folder first). All or nothing.
IReadOnlyList<DatabaseInfo> restored = await store.RestoreAsync(@"D:\backups\shop", set.SetId);

// Restore an earlier copy beside the live database, check it, then swap the two.
await store.RestoreAsync(@"D:\backups\shop", set.SetId, new RestoreOptions
{
    Databases = ["orders"],
    Rename = new Dictionary<string, string> { ["orders"] = "orders-restored" },
    NewIdentity = true,
});
```

The product decides when to back up, where the root lives, and how it is shipped elsewhere. Crystal has no schedule,
no encryption, no compression and no remote target.

## 2. The root

```
{root}/
  crystal-backup.json          the root file: { "format": "crystal-backup 1", "created": "…" }
  backup.lock                  held exclusively by one operation at a time, across processes
  engines/{uid}/               one BackupEngine per database UID: meta/, shared_checksum/, private/
  sets/{setId}.json            one manifest per complete set
  scratch/                     deep verify's restores, deleted after each database
```

- **The destination** is an absolute path, empty or already a root, and neither inside the store's data directory nor
  containing it. A non-empty folder without a root file is refused before anything is written into it. The check
  compares full paths as written: a junction, symbolic link, `subst` drive or 8.3 short name that leads into the data
  directory is not detected, so name the root by its real path.
- **One operation at a time:** backup, restore, verify and purge each hold `backup.lock`; a second is refused at once
  with `BackupRootLockedException`, never queued. `CrystalBackupRoot` holds the lock until it is disposed.
- **Engines are keyed by UID,** so a database's backups stay in one engine across renames, swaps and a failover: any
  store may back up into any root, and the new primary after a failover continues in the same engines.
- **Table files are shared** between backups of a database, named by their checksum and size: a second backup copies
  only new files. The first backup after a failover copies most of each database again (the new primary's files differ
  on disk); later ones are incremental.

## 3. The manifest

`sets/{setId}.json`, written last: to a temporary file that is flushed, then renamed, and the directory synced. **A set
exists only once its manifest does.** The set id is the start time in UTC, then 8 random hex digits, so ids sort by
time.

```json
{
  "format": "crystal-backup-set 1",
  "set": "20261013T120000000Z-3f9a0c12",
  "label": "nightly",
  "node": "…",
  "started": "2026-10-13T12:00:00.0000000Z",
  "completed": "2026-10-13T12:03:41.5120000Z",
  "versions": { "store": "crystal 1", "catalogue": 1, "encoder": 1, "crystal": "1.0.0-rc.2", "rocksdb": "11.8.1" },
  "databases": [
    { "name": "shop", "uid": "…", "replicated": true, "created": "…",
      "backup": 7, "sequence": 918273, "bytes": 104857600, "files": 41 }
  ],
  "omitted": [ { "name": "tmp", "uid": "…", "reason": "dropped" } ],
  "sha256": "…"
}
```

- **`sha256`** is over the manifest serialised compactly with `sha256` empty. A manifest that fails it, or names
  another set than its file, is damaged: never used, listed by `DamagedManifests()`, never by `ListSets()`.
- **`backup`** is the engine's backup id; each engine backup also carries the set id and UID in its own metadata.
- **`bytes` and `files`** are the engine's figures for that backup, shared files included.

## 4. What a backup guarantees

- **The store keeps serving.** The store gate is held only to read the list of databases at the start. Commits, reads,
  box changes and database operations continue throughout.
- **One database at a time, each at its own moment.** There is no one moment across databases. A product that needs
  an invariant across data keeps that data in one database (the same limit as replication).
- **Every commit acknowledged before a database's copy began is in it,** whatever its durability mode: the engine
  flushes the memtables first. The manifest's `sequence` for the database is read before the copy: every commit at or
  below it is in the backup; later ones may be too.
- **Membership is fixed at the start.** A database created later is not in the set. A database dropped during the
  backup is not waited for: the drop stops its copy, and the set lists it under `omitted` as `dropped`. A rename or swap
  during the backup changes nothing in the set. On a standby, a snapshot install of a database being copied stops the
  copy too, and the backup copies the installed database instead; only if that happens on three attempts in a row is it
  omitted, as `replaced`. A database replication holds open stays open after its copy.
- **A closed database** is opened for its copy and closed again; the backup holds at most one database open that the
  product did not open.
- **Cancellation** stops the native copy promptly; **closing the store** stops it too (`ObjectDisposedException`). A
  backup that fails, is cancelled or is killed writes no set and leaves earlier sets untouched; its partial engine
  backup is removed by the engine when the root is next used.
- **One backup at a time per store** (`BackupInProgressException`).

## 5. Verify and purge

- **Quick verify:** the manifest's checksum, and each database's engine backup present: every file its meta file lists
  is on disk, and each shared table file is the size its name records. It reads no data. The small private files
  (`CURRENT`, `MANIFEST`, `OPTIONS`) have no recorded size, so quick verify sees only whether they are there.
- **Deep verify:** each database is also restored into a scratch folder (under the root, or one the caller names),
  opened read-only, every table file's block checksums verified by RocksDB, and its catalogue loaded with the identity
  the manifest expects. **Deep verify is the only check that reads every byte.** The restore checks each file against
  the checksum the backup recorded; the block checksums also catch a table file that was already damaged in the
  database when it was backed up. The scratch folder is deleted after each database.
- A verify **returns** the damage it finds, per database; it throws only when it cannot run (an unknown or damaged set).
- **Purge** keeps the newest `keepLast` sets by completion time: it deletes the other manifests first, then in each
  engine the backups older than the oldest one a kept set references, then the engine folders no kept set references.
  A crash part-way leaves only data no set references, never a set with missing data. A backup killed after its copy
  and before its manifest stays in its engine until it ages out. A damaged manifest is never used, so it keeps nothing:
  an engine only a damaged manifest references is deleted with the rest, and the manifest itself is left in place.

## 6. What a restore does

1. Takes the root's lock, reads the manifest and checks its checksum (`BackupSetNotFoundException`,
   `BackupDamagedException`), and checks the store format, catalogue format and encoder version are ones this build
   reads (`CrystalFormatException`).
2. Chooses the databases (all, or `RestoreOptions.Databases`), maps their names (`Rename`) and replicated flags
   (`Replicated`), and quick-verifies each one.
3. Restoring a **replicated** database needs this node to be the primary accepting writes: on a standby, a fenced node
   or a primary handing over it is refused with `NotPrimaryException`, and a demotion waits for a restore in progress. A
   database restored as not replicated may be restored on any node.
4. Under the store gate, checks every name is free (`DatabaseNameTakenException`) and every UID absent
   (`DatabaseUidTakenException`, unless `NewIdentity`), then advances the id allocator past one id per database, in one
   synced write.
5. For each database: restores the engine backup into a staging folder in the data directory, opens it, loads its
   catalogue expecting the manifest's UID, and, for a copy, rewrites its identity with a new UID (the one time an
   identity record changes, spec `009`'s addendum). Only then is it moved into `db-{id}`, the directory synced, and its
   `name` file written.
6. Under the store gate: re-checks names and UIDs, removes any replication records left for the restored UIDs, and
   registers every restored database in **one synced registry batch**, which wakes the standbys' pulls.

**Cancellation and close.** A restore checks its token, and the store's close, between databases; the restore of one
database's files cannot be stopped part-way, so a close waits for it.

**All or nothing.** Until that batch, no restored database is visible. A failure before it deletes the folders the
restore made; their ids stay skipped. A crash before it leaves folders below the allocator that the registry does not
name, and a staging folder (`restore-*`), all of which the next open removes.

**Each restored database keeps** its UID (unless restored as a copy), its creation time and its boxes. It gets a new id,
and the name in the set or the one it is mapped to. A restored replicated database starts a **fresh history**: it has
no incarnation or follow record, so a promotion can never claim an earlier life's log for it, and its standbys
bootstrap it like any new database. A restored store is its own node: restore into a newly opened store, which has its
own `node.id`.

## 7. Observability

Log events 1701–1731, the `crystal.backup.*` instruments and `StoreStatus.Backup` are listed in
[`observability.md`](observability.md).
