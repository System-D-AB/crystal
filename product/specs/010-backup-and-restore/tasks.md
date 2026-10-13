# 010 — Backup and restore: tasks

> Approved 2026-10-13 with the addendum at the end of requirements.md (R11, after `009`, `003` and `011`). `009` is
> implemented; its identity check is `LoadCatalogue(kind, expectedUid)`. The maintainer chose Claude to execute every
> task; nothing is committed until the maintainer approves.

- [x] **T0** A drop removes the database's replication records (R11.1): follow record, incarnation record and resync
  marker, durably. Done when a test drops a replicated database that had all three and finds none, with a mutation
  check.
  _Done 2026-10-13: `StoreEngine.ForgetReplicationRecords` (each record deleted with `DurableFile.Delete`, which syncs
  the directory), called by the drop after the slot closes and by startup recovery for every dropped record whose UID
  is not live again (a crash between the drop's registry batch and the removal). `DropForgetsReplicationTests` (3);
  mutations X1–X3 caught. The crash test throws at `drop.closed`: thrown at `drop.drained`, the in-process "crash"
  left the instance open and its RocksDB lock held._

- [x] **T1** Native surface (R8.1; design § 3): the `NativeRocks` wrappers, `BackupEngineHandle`, the read-only open
  and `rocksdb_verify_checksum`, each listed in `BindingSurfaceTests`. Done when a storage-level test backs up a
  `RocksInstance`, lists the backup with its metadata, verifies it, restores it into another folder and reads the same
  keys, and the surface test passes.
  _Done 2026-10-13: 30 native entry points and `RocksDb.OpenReadOnly` in `BindingSurfaceTests`; `BackupEngineHandle`
  is a `SafeHandle`, so a stop from another thread never reaches a closed engine. `BackupEngineTests` (2): a backup of
  buffered, never-synced commits is listed with its metadata, verified, restored, opened read-only, checksum-verified
  and equal family by family; a deleted table file fails verify. The binding's `Env` has no `Dispose`; the handle
  frees it through `IDisposable` when it is one._
- [x] **T2** The root and the manifest (R2.1–R2.4, R4.1–R4.3; design § 1, § 2): `crystal-backup.json`, `backup.lock`,
  the destination checks, `BackupManifest` with its SHA-256, write-temp-flush-rename, stale `.tmp` cleanup. Done when
  tests show each destination refusal by name, a second lock refused across two `CrystalBackupRoot` instances, and a
  manifest edited by one byte refused.
  _Done 2026-10-13: `Backup/BackupRoot.cs`, `Backup/BackupManifest.cs`; `BackupRootTests` (11). Decisions recorded:
  times are written at full precision (`…ss.fffffffZ`, not the design's milliseconds), so a restored database keeps
  its creation time exactly (R6.3); a manifest whose `set` does not match its file name is damaged too (a copy under
  another set's name); a folder holding only `backup.lock` and the root file's temporary (a backup killed while
  creating the root) counts as empty; a root of another format is refused with `CrystalFormatException`; the RocksDB
  version recorded is a constant beside the binding (`NativeRocks.RocksDbVersion`)._
- [x] **T3** The public types and exceptions (design § 8). Done when they compile with XML documentation stating that
  the async methods yield and that databases are captured one at a time (R1.4, R8.2).
  _Done 2026-10-13: `BackupTypes.cs` (options, set and database infos, verify and purge results), `CrystalBackupRoot`,
  six exceptions in `Exceptions.cs`, `StoreStatus.Backup` and `BackupStatus`; the API documents the yield, the
  per-database moment, the node rules of R11.3 and every exception._
- [x] **T4** Backup (R1, R3, R4; design § 4, § 5): `BackupRunner`, `CrystalStore.BackupAsync`, the in-progress flag,
  membership under the gate, opening closed databases one at a time, the slot's `RunningBackup` stopped by drop and
  dispose, cancellation through `BackupStop`, the `backup.copying` hook. Done when R9.1's backup half, R9.2, R9.5, R9.6
  and the concurrent-backup refusal pass.
  _Done 2026-10-13: `Backup/BackupRunner.cs`, `CrystalStore.BackupAsync`; `DatabaseSlot` gains the running copy
  (`BeginBackup` registers it with a full fence against `BeginClose`, so either the closer stops the copy or the copy
  sees the closing and never starts), `HandedOut` (set under the store gate by create and open, so a database the
  product took during its backup is not closed under it), `StopRunningBackup` (called by drop and by store close
  after marking the slot closing). Hooks `backup.entered`, `backup.copying`, `backup.manifest.written`. Store close
  cancels a running backup or restore and waits for it before closing anything (an operations count taken under a
  lock that close takes after marking the store disposed). `BackupTests` (12), `BackupInterleavingTests` (two of
  four). Timing proofs use a 1 MB/s rate limit on ~20 MB: a cancel, a drop and a close each end the copy in under 5
  s (each took well under 1 s on Windows)._
- [x] **T5** Restore (R6; design § 7): `RestoreRunner`, `CrystalStore.RestoreAsync`, format checks, name and UID
  checks, allocator advance, restore into `db-{id}`, identity check through `009`, `NewIdentity`, subset and rename,
  the single registry batch, cleanup on failure. Done when R9.1 round-trips and R9.9's in-process cases pass (all or
  nothing with an injected failure, `NewIdentity`, subset, restore into a serving store), and the refusals of R9.8 for
  restore pass. Also (addendum): the replication records of each restored UID cleared (R11.2); replicated restores
  admitted through the write gate and refused on a non-primary (R11.3); registration through the same generation path
  as a create (R11.4); directory syncs after each move (R11.5); each folder's identity checked before it is moved in
  (R11.6); `NewIdentity` through `ReplaceIdentityUid` (R11.7); and R11.8's tests.
  _Done 2026-10-13: `Backup/RestoreRunner.cs`, `CrystalStore.RestoreAsync`, engine steps `AllocateForRestore`,
  `CheckRestoredFolder`, `MoveRestoredIntoPlace`, `RegisterRestored`; `DatabaseContext.ReplaceIdentityUid`. A design
  change, recorded: each database is restored into a staging folder `restore-{guid}` in the data directory, opened and
  its identity checked there (and replaced for a copy), and only then renamed into `db-{id}` (R11.6: a wrong folder
  never reaches `db-{id}`); startup recovery deletes a staging folder left by a crash. The creation time registered is
  the database's own, from its identity record. `RestoreOptions.Rename` must name databases the restore includes.
  `RestoreTests` (13), `RestoreReplicationTests` (1: a standby in a 10 s long poll bootstraps the restored database in
  under 8 s), `BackupInterleavingTests` (two of four: a name taken mid-restore is refused at registration, an engine
  folder holding another database is refused before anything moves in). The runner records each `db-{id}` before its
  move, found by the injected failure at `restore.moved`: recorded after, a failure inside the move left the folder._
- [x] **T6** Verify and purge (R5; design § 6): `CrystalBackupRoot.ListSets`, `DamagedManifests`, quick and deep
  `VerifyAsync`, `Purge`. Done when R9.7 and R9.10 pass.
  _Done 2026-10-13: `CrystalBackupRoot.cs`; deep verify in `Storage/BackupVerification.cs` (it touches RocksDB types,
  so it lives in Storage), with small resources of its own and the default clock (nothing it does records a time).
  `VerifyAndPurgeTests` (6). Damaged manifests are never used, so purge keeps nothing for them and leaves them in
  place for the operator._
- [x] **T7** Observability (R7; design § 9): the 1700s events, the `crystal.backup.*` instruments, `StoreStatus.Backup`.
  Done when tests with a capturing logger and a `MeterListener` see each event and instrument, and
  `engineering/observability.md` lists them.
  _Done 2026-10-13: events 1701–1704, 1711–1713, 1721, 1731; `crystal.backup.duration`, `.bytes`, `.files`,
  `.failures`, `.last_success` (bytes and files: the growth of the engine folder over each database's copy, so shared
  files are not counted; `last_success` is this store's since it opened); `StoreStatus.Backup`.
  `BackupObservabilityTests` (4); `engineering/observability.md` lists them._
- [x] **T8** Process tests (R9.3, R9.4; design § 10): a `backup` command in `NodeHost` and `TestNode`; the backup under
  the crash harness's stream, restored and audited as a whole prefix per database; a kill at `backup.copying` followed
  by verify, restore and a new backup; the root lock held across two processes. Done when they pass on Windows.
  _Done 2026-10-13 (Windows): the crash harness gains `--backup`/`--backup-after`; `BackupProcessTests` (3): under the
  stream, in Strict and Buffered, the restored store audits as a whole prefix holding every batch acknowledged before
  the backup began (the relation to the manifest's sequence is proved in-process by
  `BackupTests.EveryCommitAcknowledgedBeforeTheCopy…`: the process tests have no internals); a `NodeHost` `backup`
  command and `TestNode.Backup`, its set verified deep and restored. The kills use the crash host (`backup`,
  `restore`, `holdroot`, and a `+run` step that reports a step and carries on, so `CrashHost.RunAndKillAfter` kills
  1.5 s into the native copy): `BackupKillTests` (4)._
- [x] **T9** Mutation checks (R9.11) of every guard in the real source: destination inside the data directory, root
  lock, in-progress flag, manifest written last, manifest checksum, drop stopping the copy, the omitted
  record, the restore's name and UID checks, the single registry batch, cleanup on failure, purge's order and `keep`
  count, deep verify's checksum call. Each must be caught by a named test, run as `006` and `007` recorded them. Done
  when every mutation is caught and the source is restored byte-identical.
  _Done 2026-10-14: 35 mutations, 34 caught by their named tests (V2 below is the one left), the sources restored
  byte-identical after each run.
  Destination (B1–B3), root lock (B4), in-progress flag (B5), manifest temporary file (B6) and checksum (B7), drop and
  close stopping the copy (B8, B9), the omitted record (B10), a copy registered after a drop began (B11), cancellation
  thrown (B12), closing what the backup opened (B13) but not what the product took (B14); the restore's name and UID
  checks (R1, R2) and their re-check at registration (R3), cleanup on failure (R4, R5), allocator first (R6), staging
  recovery (R7), identity checked before the move (R8), `NewIdentity` (R9), quick verify before writing (R10), formats
  (R11), replication records cleared (R12), primary only (R13); deep verify's checksums (V1), quick verify's size and
  presence checks (V3, V4); purge's keep count, kept sets and engine deletion (P1–P3); the drop's record removal (D1)._
  _Found by the mutation checks and fixed:_
  - _Quick verify missed a truncated table file. The engine's verify compares sizes with those it read from disk when
    it opened, so a file truncated before the open passed (R5.2 says "every file at its recorded size"). Quick verify
    now also reads the backup's meta file: each listed file must be present, and each shared table file the size its
    name records (`…_{size}.sst`). The private files (`CURRENT`, `MANIFEST`, `OPTIONS`) have no size recorded, so only
    their presence is checked; deep verify reads their bytes (`VerifyAndPurgeTests.ATruncatedTableFile_…`, 2 cases)._
  - _The flipped-byte test could not tell deep verify's checksum pass from the restore's own file checksum, which
    catches a byte flipped in the backup first. The case only the checksum pass catches is a table file damaged in the
    database before it was backed up: the backup records a checksum of the damaged bytes. Tested now
    (`ATableFileDamagedBeforeItWasBackedUp_…`)._
  - _B11 and B13 first written as `if (false)` did not compile (unreachable code); rewritten as conditions that never
    hold._
  _Left, recorded: removing the engine's own `Verify` call (V2) is caught by nothing, since a file missing at the open
  drops the backup from the engine's list and the file check finds the rest first; it stays as the engine's own check._
- [x] **T10** A real run (R9.12): the packed library used from a separate program (a throwaway console project outside the solution, referencing the `.nupkg`) backs up a store
  of several databases, purges, verifies deep and restores into a new store. Done when the run's output is recorded
  here.
  _Done 2026-10-14 (Windows): the library packed as `0.1.0-realrun010` (a throwaway version, so no cached package is
  used), referenced from a console project outside the repository. Three databases (two replicated, one renamed, boxes
  of both profiles), three backups, a purge to two sets, deep verify of each, a restore of the newest into a new
  store, every row compared:_
  ```
  backup …-66c0983c 'round 0': 3 databases, 169278 bytes, omitted 0
  backup …-c0b0d3b6 'round 1': 3 databases, 170716 bytes, omitted 0
  backup …-3978cad5 'round 2': 3 databases, 172154 bytes, omitted 0
  sets before purge: 3
  purge: kept 2, deleted 1, engines deleted 0
  deep verify …-c0b0d3b6: ok
  deep verify …-3978cad5: ok
  restored 3 databases into a new store: audit, orders, users
  rows compared: 12003, identical: True
  ```
- [x] **T11** Documents (R10): `engineering/box-layout.md` § 2 and § 9, the new `engineering/backup.md`, and
  `product/intro.md`. Done when the documents describe Crystal as built.
  _Done 2026-10-13: box-layout § 2 drops `_backups/` and lists `restore-{guid}/` (the code no longer treats `_backups`
  as a known entry); § 9 states what a backup and a restore are; `engineering/backup.md` is new; `product/intro.md`
  says what backup and restore give._
- [x] **T12** Independent review (R9.11) by a second reviewer, its findings fixed or recorded here. Done when the
  review is recorded and the full suite passes with 0 warnings.
  _Review 2026-10-14 (a fresh agent, read-only): one blocker, four should-fix, seven minor. Fixed:_
  - _B1 (blocker): a backup closed a database it had opened even when a standby's follower had taken the same slot
    meanwhile; the follower writes into the slot without entering it, so it would have written into a disposed
    instance. A slot `OpenReplicatedByUid` returns now counts as handed out (`BackupReviewTests.ASlotReplicationTakes…`,
    mutation RB1 caught)._
  - _S1: a standby's snapshot install of a database being copied waited, under the store gate, for the whole copy,
    then failed the backup as if the store had closed. The install now stops the copy; the backup tries the database
    again (up to three times, then omits it as `replaced`), and only a store close throws (`ASnapshotInstallDuringACopy…`:
    the install returns in under 3 s and the set holds the installed data; RS1a, RS1b caught)._
  - _S2: the backup closed a database it opened after releasing the store gate, so a concurrent open could start a
    second instance on the folder while the first still held its lock. It now closes under the gate, as an install
    does. No test: the window needs an open to land between two statements._
  - _S3: a drop removed the replication records after releasing the gate, so it could delete those of a database a
    restore had just brought back under the same UID. It now removes them under the gate, and only if no live database
    has the UID (`ADropThatEndsAfterARestore…`, through the `drop.closed` hook; RS3 caught)._
  - _S4: manifest UIDs name folders and were not validated, so a resealed manifest could point outside the root. Every
    UID must be a canonical GUID, or the manifest is damaged (`AManifestUidThatIsAPath…`; RS4 caught)._
  - _M3: a set id in another case found the manifest on Windows and was refused as damaged; sets are now found by exact
    name (`ASetIdInAnotherCase…`; RM3 caught). M5: the engine is opened before the database is entered, so a drop never
    waits for an engine's open. M6: test values are random per key (incompressible), so the rate-limited copies take
    their stated time and the under-5-s bounds prove the copy was stopped. M7: the store's close token is disposed._
  - _Documented rather than changed: M1 (the destination check compares paths as written; links, `subst` and short
    names are not resolved), M2 (an engine only a damaged manifest references is purged), M4 (a restore cannot be
    stopped part-way through one database). Left: R9.3's "prefix reaches the manifest's sequence" is proved in-process
    only, as T8 records._
