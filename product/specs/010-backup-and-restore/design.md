# 010 — Backup and restore: design

## 1. The backup root

```
{root}/
  crystal-backup.json          the root file: { "format": "crystal-backup 1", "created": "…" }
  backup.lock                  held exclusively by one operation (FileShare.None), like crystal.lock
  engines/{uid}/               one RocksDB BackupEngine per database UID: its own meta/, shared_checksum/, private/
  sets/{setId}.json            one manifest per complete set
  sets/{setId}.json.tmp        a manifest being written (ignored, deleted by the next operation)
  scratch/{guid}/              deep verify's restores (deleted after each database)
```

- **`setId`** is `yyyyMMddTHHmmssfffZ-{8 hex}`: the start time in UTC, then random hex. It sorts by time.
- **Engines are keyed by UID, not id.** An id belongs to a store (`009` R1.4); a UID is the database's. So a
  database's backups stay in one engine across renames, swaps, and a failover to a standby (which has the same UIDs).
- **Two stores may share an engine** (a primary and, after a failover, its former standby), one at a time under the
  root lock. That is safe: with `share_files_with_checksum`, a shared file's name includes its checksum, size and the
  session of the database that wrote it, so different files never share a name. Each engine backup is a complete list
  of its own files. The two stores' files differ on disk, so the first backup after a failover copies most of a
  database again. Later ones are incremental.
- **A new root** writes `crystal-backup.json` (temporary file, flush, rename) before anything else. A directory that
  holds anything else and no root file is refused (R2.1).
- **The lock is taken first,** before reading anything, and released last. A second operation fails at once
  (`BackupRootLockedException`), from this process or another.

## 2. The manifest

```json
{
  "format": "crystal-backup-set 1",
  "set": "20261010T120000000Z-3f9a0c12",
  "label": "nightly",
  "node": "…",
  "started": "2026-10-10T12:00:00.000Z",
  "completed": "2026-10-10T12:03:41.512Z",
  "versions": { "store": "crystal 1", "catalogue": 2, "encoder": 1, "crystal": "0.3.0", "rocksdb": "11.8.1" },
  "databases": [
    { "name": "shop", "uid": "…", "replicated": true, "created": "…",
      "backup": 7, "sequence": 918273, "bytes": 104857600, "files": 41 }
  ],
  "omitted": [ { "name": "tmp", "uid": "…", "reason": "dropped" } ],
  "sha256": "…"
}
```

- **`sha256`** is over the manifest serialised with `sha256` set to the empty string, as UTF-8 bytes, compact (no
  indentation). Readers recompute it the same way. `System.Security.Cryptography.SHA256` is in `System.*`, so the
  architecture allow-list permits it.
- **`backup`** is the engine's backup id. `bytes` and `files` are the engine's info for that backup: its whole size,
  shared files included.
- Types: `Backup/BackupManifest.cs` (internal record plus serialiser, `System.Text.Json`), with the checksum computed
  and checked there.

## 3. Native surface

`Storage/NativeRocks.cs` gains these wrappers, each listed in `BindingSurfaceTests`:

| Wrapper | Native calls |
|---|---|
| `BackupEngineOpen(dir, backgroundOps, rateLimit)` | `rocksdb_backup_engine_options_create`, `_set_share_table_files` (true), `_set_share_files_with_checksum` (true), `_set_sync` (true), `_set_backup_log_files` (false: the flush makes the WAL unnecessary), `_set_max_background_operations`, `_set_backup_rate_limit`, `rocksdb_backup_engine_open_opts`, `_options_destroy` |
| `BackupCreate(engine, db, metadata)` | `rocksdb_backup_engine_create_new_backup_with_metadata` with flush before backup |
| `BackupStop(engine)` | `rocksdb_backup_engine_stop_backup` |
| `BackupList(engine)` | `rocksdb_backup_engine_get_backup_info`, `_info_count`, `_info_backup_id`, `_info_timestamp`, `_info_size`, `_info_number_files`, `_info_app_metadata`, `_info_destroy` |
| `BackupVerify(engine, id)` | `rocksdb_backup_engine_verify_backup` |
| `BackupPurge(engine, keep)` | `rocksdb_backup_engine_purge_old_backups` |
| `BackupRestore(engine, id, dir)` | `rocksdb_restore_options_create`, `_set_keep_log_files` (false), `rocksdb_backup_engine_restore_db_from_backup`, `_options_destroy` |
| `BackupEngineClose(engine)` | `rocksdb_backup_engine_close` |
| `VerifyChecksums(db)` | `rocksdb_verify_checksum` |
| `OpenReadOnly(dir, families)` | `rocksdb_open_for_read_only_column_families`, if `RocksInstance` has no read-only open yet |

- **An internal `Storage/BackupEngineHandle.cs`** owns the native pointer (a `SafeHandle`) and exposes only the
  wrappers' operations. Nothing outside `SystemD.Crystal.Storage` sees a pointer.
- **The binding has no call to delete one backup.** Purge uses `purge_old_backups(keep)`, which keeps the newest
  `keep` backups (§ 6).
- **Verify without checksums.** `rocksdb_backup_engine_verify_backup` in the C API checks only that each file is
  present at its recorded size, so it serves quick verify. Content is checked by deep verify (§ 6).
- **Getting the database pointer.** `RocksInstance` passes it to these wrappers through an internal accessor, as
  `WalAccess` does today.

## 4. A backup

`Backup/BackupRunner.cs`, started by `CrystalStore.BackupAsync`, which runs it with `Task.Run`:

```
1. validate destination (absolute; not inside the data directory, nor containing it)   → BackupDestinationException
2. take the store's backup flag (Interlocked)                                          → BackupInProgressException
3. open the root: take backup.lock, create or check crystal-backup.json,
   delete stale sets/*.tmp
4. under the store gate, for a moment: read the live databases (name, uid, replicated, created)
5. for each database, in id order:
     a. enter it: open its slot if closed (R3.3); if dropped meanwhile → omitted "dropped"
     b. register the running copy on the slot (§ 5), so a drop can stop it
     c. sequence ← the database's latest sequence
     d. engine ← engines/{uid} (opened per database, closed after)
        BackupCreate(engine, db, metadata: {"set": setId, "uid": uid})
     e. unregister; leave the slot; close it if the backup opened it
     f. if the copy was stopped by a drop → omitted "dropped"; if by the token → throw OperationCanceledException
     g. read the new backup's id, size, files from BackupList (the newest whose metadata names this set)
6. write sets/{setId}.json.tmp, flush, rename to sets/{setId}.json                      (the set exists)
7. release backup.lock; clear the flag; metrics and the completed event
```

- **The sequence is read before the copy** (step c). The copy starts with a flush, which includes everything at or
  below it. That is R1.5's lower bound.
- **Cancellation:** a token registration calls `BackupStop` on the current engine. The engine is closed after a
  stop, because a stopped engine refuses further backups, and the next database opens its own anyway.
- **Store dispose** cancels a running backup through an internal token the runner links to the caller's, then waits
  for the runner before closing databases.
- **Failure** at any step after 3 throws, after closing the engine and releasing the lock. No manifest is written.
  Partial engine backups are removed by the engine on its next open.

## 5. A drop during a backup

The drop must not wait behind a copy (R3.2), yet a slot's closing waits for calls in progress (`005` addendum A3).
The copy is such a call, so the drop **stops it**:

- **`DatabaseSlot` gains a field** for the running copy: `BackupEngineHandle? RunningBackup`, set in step 5b and
  cleared in 5e.
- **Drop and store dispose,** after marking the slot closing and before waiting for calls in progress, call
  `BackupStop` on it if set. The copy returns promptly with an error. The runner sees the slot closing and records the
  database as omitted. The drop's wait then ends.
- **The order matters:** the slot is marked closing first, so a copy registered after the mark sees `closing` in
  5a or 5b and never starts.
- A hook point `backup.copying` (the existing internal `CrystalOptions.Hook`) lets R9.6 hold a copy at the exact
  interleaving.

## 6. Verify and purge

`Backup/CrystalBackupRoot.cs` (public):

- **Quick verify:** the manifest checksum, then `BackupVerify(engine, backup)` for each database.
- **Deep verify,** per database:
  1. `BackupRestore` into `scratch/{guid}`;
  2. `OpenReadOnly`;
  3. `VerifyChecksums`;
  4. load the catalogue with the expected identity from the manifest (`009`'s `LoadCatalogue(expected)`, read-only:
     opened read-only, so nothing is written);
  5. close and delete the scratch folder, also on failure.
- **Purge:**
  1. Read every valid manifest; keep the newest `keepLast` by `completed`.
  2. Delete the other manifests.
  3. For each engine folder, let `min` be the smallest backup id a kept set references.
     - **None:** delete the folder.
     - **Otherwise:** count the engine's backups with id ≥ `min` as `k`, then `BackupPurge(engine, k)`.

  Engine backup ids only grow, so this keeps every referenced backup. Backups newer than `min` and in no set (a
  backup killed after its copy, before its manifest) are kept until they age out.

## 7. A restore

`Backup/RestoreRunner.cs`, started by `CrystalStore.RestoreAsync`, which runs it with `Task.Run`:

```
1. open the root (lock); read sets/{setId}.json; check its sha256      → BackupSetNotFoundException / BackupDamagedException
2. check formats (R6.2): store, catalogue, encoder → CrystalFormatException
3. choose databases (subset), map names; quick-verify each
4. under the store gate: names free, UIDs absent (unless NewIdentity)
   → DatabaseNameTakenException / DatabaseUidTakenException
   advance the allocator by n: one synced registry write (005 A2 step 1)
5. for each database, ids allocated in order:
     BackupRestore(engine, backup, db-{id});  write the name file
     open it; LoadCatalogue(expected: manifest uid, kind database)
     if NewIdentity: rewrite the identity with a new UID, same kind and created  (009 R1.1's one exception)
     close it
6. under the store gate: re-check names and UIDs; one synced registry batch:
   aliases, records (v2: name, uid, replicated, live, created), generation; update the in-memory registry
7. release the lock; metrics and the completed event; return the restored databases' infos
```

- **Failure in steps 4–5:** delete the folders this restore made. Their ids stay skipped; ids are never reused.
- **Failure in step 6:** keep the folders, as create does (`005` addendum A9), since a failed synced write may
  still be durable. The next open keeps them if they are registered, and removes them if not.
- **Crash:** the folders are unregistered, and the allocator has passed their ids. The next open removes them
  (`005` addendum A2).
- **Rewriting the identity for `NewIdentity`** is an internal `DatabaseContext.ReplaceIdentityUid(string)`, used only
  here, on a database no registry names yet.

## 8. Public API

```csharp
public sealed partial class CrystalStore
{
    /// Backs up every live database into the backup root. Yields: runs on a thread-pool thread.
    public Task<BackupSetInfo> BackupAsync(string destination, BackupOptions? options = null,
        CancellationToken cancellationToken = default);

    /// Attaches a backup set's databases to this store, all or none. Yields.
    public Task<IReadOnlyList<DatabaseInfo>> RestoreAsync(string source, string setId,
        RestoreOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed class BackupOptions
{
    public string? Label { get; init; }
    public long? RateLimitBytesPerSecond { get; init; }
    public int BackgroundOperations { get; init; } = 2;
}

public sealed class RestoreOptions
{
    public IReadOnlyCollection<string>? Databases { get; init; }          // null: all
    public IReadOnlyDictionary<string, string>? Rename { get; init; }    // set name → name in this store
    public bool NewIdentity { get; init; }
    public bool? Replicated { get; init; }                                 // null: as recorded
}

public sealed class CrystalBackupRoot : IDisposable
{
    public static CrystalBackupRoot Open(string path,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null);
    public string Path { get; }
    public IReadOnlyList<BackupSetInfo> ListSets();
    public IReadOnlyList<string> DamagedManifests();
    public Task<BackupVerifyResult> VerifyAsync(string setId, bool deep, string? scratchDirectory = null,
        CancellationToken cancellationToken = default);
    public BackupPurgeResult Purge(int keepLast);
}

public sealed record BackupSetInfo(string SetId, string? Label, string SourceNodeId, DateTimeOffset Started,
    DateTimeOffset Completed, IReadOnlyList<BackupDatabaseInfo> Databases, IReadOnlyList<OmittedDatabase> Omitted);
public sealed record BackupDatabaseInfo(string Name, string Uid, bool Replicated, DateTimeOffset Created,
    long Sequence, long Bytes, int Files);
public sealed record OmittedDatabase(string Name, string Uid, string Reason);
public sealed record BackupVerifyResult(string SetId, bool Deep, IReadOnlyList<DatabaseVerifyResult> Databases)
{ public bool Ok => Databases.All(d => d.Ok); }
public sealed record DatabaseVerifyResult(string Name, string Uid, bool Ok, string? Problem);
public sealed record BackupPurgeResult(int SetsKept, int SetsDeleted, int EnginesDeleted);
```

**New exceptions,** each with the way forward in its message:
- `BackupDestinationException` (path);
- `BackupRootLockedException` (path);
- `BackupInProgressException` (destination of the running backup);
- `BackupSetNotFoundException` (set id);
- `BackupDamagedException` (path and what failed);
- `DatabaseUidTakenException` (UID, the name it has here).

`StoreStatus` gains `Backup` (null when idle): destination, databases done and total, and bytes copied.

**Placement:** these types live in the `SystemD.Crystal` namespace, and the runners in `SystemD.Crystal.Backup`
(internal). Native work stays in `SystemD.Crystal.Storage`. The architecture tests' rule that no RocksDB type is
public covers it.

## 9. Observability

`CrystalLogEvents`, 1700s:

| Id | Name | Level |
|---|---|---|
| 1701 | `BackupStarted` | Information |
| 1702 | `BackupCompleted` | Information |
| 1703 | `BackupFailed` | Error |
| 1704 | `BackupDatabaseOmitted` | Warning |
| 1711 | `RestoreStarted` | Information |
| 1712 | `RestoreCompleted` | Information |
| 1713 | `RestoreFailed` | Error |
| 1721 | `BackupVerifyFailed` | Error |
| 1731 | `BackupPurged` | Information |

Metrics as in R7.2, on the store's meter; `engineering/observability.md` lists them.

- `crystal.backup.bytes` and `crystal.backup.files` count what the engine copied. That is the difference in the
  engine's shared-file total before and after, not the backup's whole size.
- `CrystalBackupRoot` has no meter. It logs through the logger factory it is given.

## 10. Tests

Tests are in `tests/SystemD.Crystal.Tests/Backup/` (unit, real RocksDB in temporary directories), plus
`tests/SystemD.Crystal.Process.Tests/Backup/` for R9.3 and R9.4:
- **R9.3** uses `004`'s crash harness: it writes its stream while a backup runs.
- **R9.4** runs the backup in a `NodeHost` process, which gains a `backup` command, and kills it at the
  `backup.copying` hook.

Corruption tests flip a byte in the middle of an `.sst` file under `engines/{uid}/shared_checksum/`.

## 11. What this design deliberately does not do

- **No `Checkpoint` for backups.** `Checkpoint` stays replication's snapshot mechanism (`003` R7).
- **No backup of `_crystal`,** `node.id` or `node.epoch` (`009`).
- **No one moment across databases,** and no WAL archiving.
- **No offline restore tool:** open a store, then restore into it (open question 3).
- **No background scheduling,** encryption, compression or remote storage.

## 12. Risks

- **A long first backup of a large database** holds that database's slot entered (not the store gate) for the whole
  copy. Commits continue. Only a drop or a close of that database interacts with it, and both stop the copy (§ 5).
- **Flush before backup** adds a memtable flush per database per backup, which is visible as a write-stall risk on a
  database already near its limits. The rate limit bounds the copy, not the flush.
- **`BackupEngine` across processes:** two engines on one directory corrupt it. The root lock (R2.4) is what
  prevents that, so it is covered by a two-process test (R9.8, locked root).
- **Purge's `keep` count** (§ 6) relies on engine ids growing, which RocksDB guarantees per engine. It keeps every
  backup at or above the oldest referenced one, whichever store wrote it, so a shared engine (R2.3) is purged
  correctly: it may keep an unreferenced backup a little longer, never drop a referenced one.
