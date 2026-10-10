# 010 — Backup and restore: tasks

> The maintainer approves the spec, answers the open questions and chooses which tasks to start. **Needs `009`
> T1–T3** (identity records and `LoadCatalogue(expected)`) before T5. T1–T4 can start before `009` lands.

- [ ] **T1** Native surface (R8.1; design § 3): the `NativeRocks` wrappers, `BackupEngineHandle`, the read-only open
  and `rocksdb_verify_checksum`, each listed in `BindingSurfaceTests`. Done when a storage-level test backs up a
  `RocksInstance`, lists the backup with its metadata, verifies it, restores it into another folder and reads the same
  keys, and the surface test passes.
- [ ] **T2** The root and the manifest (R2.1–R2.4, R4.1–R4.3; design § 1, § 2): `crystal-backup.json`, `backup.lock`,
  the destination checks, `BackupManifest` with its SHA-256, write-temp-flush-rename, stale `.tmp` cleanup. Done when
  tests show each destination refusal by name, a second lock refused across two `CrystalBackupRoot` instances, and a
  manifest edited by one byte refused.
- [ ] **T3** The public types and exceptions (design § 8). Done when they compile with XML documentation stating that
  the async methods yield and that databases are captured one at a time (R1.4, R8.2).
- [ ] **T4** Backup (R1, R3, R4; design § 4, § 5): `BackupRunner`, `CrystalStore.BackupAsync`, the in-progress flag,
  membership under the gate, opening closed databases one at a time, the slot's `RunningBackup` stopped by drop and
  dispose, cancellation through `BackupStop`, the `backup.copying` hook. Done when R9.1's backup half, R9.2, R9.5, R9.6
  and the concurrent-backup refusal pass.
- [ ] **T5** Restore (R6; design § 7): `RestoreRunner`, `CrystalStore.RestoreAsync`, format checks, name and UID
  checks, allocator advance, restore into `db-{id}`, identity check through `009`, `NewIdentity`, subset and rename,
  the single registry batch, cleanup on failure. Done when R9.1 round-trips and R9.9's in-process cases pass (all or
  nothing with an injected failure, `NewIdentity`, subset, restore into a serving store), and the refusals of R9.8 for
  restore pass.
- [ ] **T6** Verify and purge (R5; design § 6): `CrystalBackupRoot.ListSets`, `DamagedManifests`, quick and deep
  `VerifyAsync`, `Purge`. Done when R9.7 and R9.10 pass.
- [ ] **T7** Observability (R7; design § 9): the 1700s events, the `crystal.backup.*` instruments, `StoreStatus.Backup`.
  Done when tests with a capturing logger and a `MeterListener` see each event and instrument, and
  `engineering/observability.md` lists them.
- [ ] **T8** Process tests (R9.3, R9.4; design § 10): a `backup` command in `NodeHost` and `TestNode`; the backup under
  the crash harness's stream, restored and audited as a whole prefix per database; a kill at `backup.copying` followed
  by verify, restore and a new backup; the root lock held across two processes. Done when they pass on Windows.
- [ ] **T9** Mutation checks (R9.11) of every guard in the real source: destination inside the data directory, root
  lock, in-progress flag, manifest written last, manifest checksum, drop stopping the copy, the omitted
  record, the restore's name and UID checks, the single registry batch, cleanup on failure, purge's order and `keep`
  count, deep verify's checksum call. Each must be caught by a named test, run as `006` and `007` recorded them. Done
  when every mutation is caught and the source is restored byte-identical.
- [ ] **T10** A real run (R9.12): the packed library used from a separate program (a throwaway console project outside the solution, referencing the `.nupkg`) backs up a store
  of several databases, purges, verifies deep and restores into a new store. Done when the run's output is recorded
  here.
- [ ] **T11** Documents (R10): `engineering/box-layout.md` § 2 and § 9, the new `engineering/backup.md`, and
  `product/intro.md`. Done when the documents describe Crystal as built.
- [ ] **T12** Independent review (R9.11) by a second reviewer, its findings fixed or recorded here. Done when the
  review is recorded and the full suite passes with 0 warnings.
