# 009 — Self-describing databases: tasks

> The maintainer approves the spec, chooses which tasks to start. Independent of `003`'s
> remaining tasks; `003` T10 (snapshot bootstrap) opens installed snapshots through T3's path once both exist.

- [x] **T1** `DatabaseIdentity` and `DatabaseKind` (`Storage/DatabaseIdentity.cs`), `BoxKeys.IdentityKey`, and unit
  tests of serialising and refusing a damaged or unknown-version record (R1.1, R1.2; design § 1). Done when the tests
  pass. _Done 2026-10-10. Deserialising throws `FormatException` for damage and `NotSupportedException` for an unknown
  version; the catalogue maps both to `CrystalFormatException`._
- [x] **T2** The catalogue (R1.1–R1.3, R2.3, R3.1, R4.1; design § 2): `InitializeNewCatalogue(identity)`,
  `LoadCatalogue(expected)` returning the identity, every refusal of design § 5. Done when the tests of R6.1 for the
  catalogue pass, including a catalogue written by the test without an identity, refused by name. _Done.
  `LoadCatalogue(DatabaseKind expectedKind, string? expectedUid)` rather than one `expected` record: a null UID (the
  registry only) checks the kind alone, as design § 2 describes._
- [x] **T3** The store (R2.1–R2.4, R3.1, R5.1; design § 3): `_crystal` created with kind `registry`;
  create builds the identity from the record; opening a database passes the expected identity and corrects the
  registry's `created` without moving the generation; `StandbyDatabase` passes its expected identity. Done when the
  existing suite passes unchanged and R6.1's store tests pass (identity equals record; survives reopen, rename, swap;
  a mismatched folder is refused by name; a registry folder placed as a database is refused). _Done, with one addition
  for R5.1: `InstallBootstrap` opens the downloaded snapshot on its own and checks its identity before closing or
  moving anything (`VerifySnapshotIdentity`), so a snapshot of another database leaves the database as it was (test
  `InstallBootstrap_ASnapshotOfAnotherDatabase_IsRefused_AndTheDatabaseIsLeftAsItWas`). The registry's `created`
  correction logs event 1310 at `Debug`. Two `003` test fixtures (`StandbyApplyTests`, `BoxVisibleOnStandbyTests`)
  copied a primary's folder under a standby record with a different UID, which R2.3 now refuses; they create the
  standby database with the primary's UID, as reconciliation does. No replication code changed._
- [x] **T4** The move test (R1.4, R6.2): a database created in store A is closed, its folder copied into store B
  under a different id, and B's registry given a record for it (alias, record with the database's UID and created,
  allocator past the id, generation), written by the test through internal API. B opens it under its new id and name
  with every box and key intact. Done when the test passes. _Done:
  `ADatabase_PlacedUnderAnotherIdInAnotherStore_OpensWithItsUidAndData`._
- [x] **T5** Mutation checks of every new guard (UID compare, kind compare, missing identity, damaged identity, the
  `created` correction not moving the generation), each caught by a named test,
  run as `006` and `007` recorded them (R6.3). Done when every mutation is caught and the source is restored
  byte-identical. _Done 2026-10-10, eight mutations, each caught; sources restored byte-identical (MD5) and rebuilt:_
  - _M1 UID not compared: caught by `Open_AnotherDatabasesFolder_IsRefused_NamingBothUids`._
  - _M2 kind not compared: caught by `Open_ARegistryWhereADatabaseBelongs_IsRefused`._
  - _M3 missing identity defaulted: caught by `Open_MissingIdentity_IsRefused`._
  - _M4 damaged identity defaulted: caught by `Open_DamagedIdentity_IsRefused`._
  - _M5 the `created` correction moves the generation: caught by `Open_CreationTimeDiffering_…`._
  - _M6 the `created` correction skipped: caught by `Open_CreationTimeDiffering_…`._
  - _M7 the snapshot not checked before install: caught by `InstallBootstrap_ASnapshotOfAnotherDatabase_…`._
  - _M8 identity not written at create: caught by `Identity_SurvivesReopenRenameAndSwap_Unchanged`._
- [x] **T6** Documents (R7.1; design § 6): `engineering/box-layout.md` § 2 and § 4; `005` design addendum A10; `003`
  design § 2 note. Done when the documents describe Crystal as built. _Done, plus event 1310 in
  `engineering/observability.md`._
- [x] **T7** Independent review (R6.4) by a second reviewer, its findings fixed or recorded here. Done when the review
  is recorded and the full suite passes with 0 warnings. _Done 2026-10-10. Findings and what was done:_
  - _The follower loop ends on a `CrystalFormatException` from a wrong snapshot, so R5.1 holds in the engine but stops
    replication on that standby. **Not fixed here:** it is `003`'s defect (any exception outside the follower's catch
    filter ends the loop), recorded in `003` tasks' addendum of the same date, should-fix 1._
  - _The `created` correction failed an open when the registry had failed: fixed. It is skipped with the registry
    failed, and a failed write leaves the open going (R2.3; test
    `Open_CreationTimeDiffering_WithTheRegistryFailed_StillOpens_AndCorrectsNothing`; mutation M9)._
  - _The registry's identity refusals named a database's way forward: fixed, they name `CrystalStore.Reset` (test
    `Open_ARegistryWithoutAnIdentity_IsRefused_NamingReset`; mutation M10)._
  - _R5.2 said reconcile creates a database without a catalogue; the code installs it whole by snapshot. R5.2 corrected
    by note; `CreateDatabaseWithUid`'s comment corrected; the install now registers the snapshot's own creation time
    (test `InstallBootstrap_ANewDatabase_IsRegisteredWithTheSnapshotsUidAndCreationTime`)._
  - _A UID not in canonical lowercase form is now damage, not "another database" (test
    `Identity_UidNotInCanonicalForm_IsDamage`; mutation M11)._
  - _Accepted: `VerifySnapshotIdentity` runs outside the store gate (the store stops the follower before disposing);
    opening the snapshot recovers its WAL and leaves RocksDB's own files, without changing its data. Not tested: the
    standby's catalogue reload refusing another UID; it runs the same `CheckIdentity` as every other open._
  - _After the fixes: 0 warnings; 432 tests pass (402 unit, 16 architecture, 14 process); M1–M11 all caught._
