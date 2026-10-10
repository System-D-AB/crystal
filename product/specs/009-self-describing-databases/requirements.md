# 009 — Self-describing databases

> **Status:** implemented (2026-10-10), reviewed. **Depends on:** `002-box-layout` (the catalogue in box 0),
> `005-store-and-databases` (the registry in `_crystal`), `003-replication` T1 (database UIDs, implemented).
> **Comes before:** `010-backup-and-restore`, which moves a database between stores and relies on this spec.

## Introduction

A database is one RocksDB folder, `db-{id}`, and the unit of isolation, backup and drop (box-layout § 1). But it does
not yet carry everything that says what it is. Its box 0 already holds its own format and key-encoder version
(box-layout § 4, spaces `0x05` and `0x06`). Its **UID** and **creation time**, though, live only in the store's
registry (`_crystal`, kernel box 1, `DatabaseRecord`). A database folder taken out of its store, as a backup does, is a
database without an identity.

This spec splits what a database **is** from what a store **does with it**:

- **The database describes itself:** its UID, its kind and its creation time join its format and encoder version in
  its own box 0. They travel with every copy of the folder: a backup, a replication snapshot, a restore.
- **`_crystal` holds the store's view of its databases:** which databases this store holds, under which names and ids,
  whether each is replicated, live or dropped, plus the id allocator and the generation. It keeps copies of the UID and
  the creation time so that listing never opens a database. The copies are checked against the database whenever it
  opens.

With this, restoring a database into a store is the same as attaching it: the target store gives it an id and a name,
and the database brings everything else. `010-backup-and-restore` builds on that; it never needs to copy `_crystal`.

## Requirements

### R1 — The identity record

- **R1.1** Every database holds an **identity record** in box 0, space `0x07`: its **UID**, its **kind** (`database`
  for a product's database, `registry` for `_crystal`), and its **creation time**. It is written when the database is
  created, in the same synced batch as the rest of its catalogue, and **never changes afterwards**.
- **R1.2** The identity record is versioned. A record that does not parse, lacks its version, holds a version this
  build does not read, or has a UID that is not a GUID is refused on open with `CrystalFormatException`, naming the
  database and the way forward, never defaulted.
- **R1.3** **The identity record is required.** A catalogue without one is refused as damaged. The catalogue format
  stays 1. No store holds databases yet, so there is nothing to upgrade (maintainer, 2026-10-10). A build from before
  this spec already refuses space `0x07` as one it does not write (`002`).
- **R1.4** **Nothing inside a database records its database id or its name.** The id and the name belong to the store
  that holds it. The same database may have another id and another name in another store.

### R2 — The registry's view

- **R2.1** `_crystal` keeps, per database: the **name** (the alias table and the record), the **id** (and so the
  folder), the **state** (`live` or `dropped`, with the drop time), the **replicated** flag, and **copies of the UID
  and the creation time**. The allocator and the generation are unchanged.
- **R2.2** **The name and the replicated flag stay in the registry only.** A swap exchanges two names atomically in
  one registry batch, which it could not do if names lived in two databases. Whether a database is replicated is this
  store's choice about it, and a standby learns the replicated set from its primary's registry (`003` R1.4).
- **R2.3** **The database's own record is the source.** When a database opens:
  - **its UID and kind are compared with the registry's,** and a mismatch is refused with `CrystalFormatException`
    naming both UIDs. A wrong folder in `db-{id}` (copied by hand, or installed by a faulty bootstrap) is refused
    rather than served under another database's name.
  - **its creation time replaces the registry's copy if the two differ.** Crystal's own paths write the registry's
    copy from the database (create, and a standby's snapshot install, which reads it from the snapshot), so a
    difference means a folder was put in place some other way. The correction is one synced registry write that does
    not move the generation (it is not a set change), logged at `Debug`. **It never fails an open:** with the registry
    failed (`006` R5) it is skipped until the next open, and a write that fails leaves the registry failed and the
    open goes on.
  - **The registry's own refusals** (no identity, damaged, wrong kind) name the registry's way forward: a build that
    reads it, or `CrystalStore.Reset`.
- **R2.4** `ListDatabases` and `DatabaseInfo` keep reading the registry only. Listing never opens a database.
- **R2.5** The registry record's format (`DatabaseRecord` version 2) does not change.

### R3 — The registry database describes itself too

- **R3.1** `_crystal`'s box 0 holds an identity record of kind `registry`, with its own UID. A folder of kind
  `registry` is never accepted as a product's database, and a product's database is never accepted as a registry.
  Either is refused by name on open.

### R4 — No upgrade

- **R4.1** **No existing database is upgraded.** Crystal is unreleased and no store holds databases, so a database
  without an identity record is refused (R1.3), never given one. A developer's local store from before this spec is
  deleted or reset (`CrystalStore.Reset`).

### R5 — Replication

- **R5.1** A standby's database bootstrapped from a primary's checkpoint (`003` R7) carries the primary's identity
  record, so its UID is the primary's, as `003` R1.3 requires. After installing a snapshot, the standby opens the
  database and **R2.3's check applies**: a snapshot whose UID differs from the one the standby asked for is refused,
  and the database is left as it was.
- **R5.2** A database new to a standby arrives whole by snapshot: reconciliation installs the snapshot and registers
  it in one step (`InstallBootstrap`), with the UID and creation time read from the snapshot's identity. There is no
  placeholder database without a catalogue. _(Corrected 2026-10-10 after review: the draft said reconciliation created
  a database with no catalogue before its bootstrap, as `003` design § 2 describes; the code does not.)_

### R6 — Proof

- **R6.1** Unit tests:
  - a new database has an identity record matching its registry record;
  - the identity survives reopen, rename and swap unchanged;
  - `_crystal` has kind `registry`;
  - each refusal of R1.2, R1.3, R2.3 and R3.1 is raised by name: a mismatched UID, a missing record, a damaged
    record, an unknown record version, and a registry folder placed as a database.
- **R6.2** A test **places a database's folder under another id** in another store's registry (written by the test,
  as `010` will do) and shows it opens with its UID and boxes intact (R1.4).
- **R6.3** Mutation checks of every new guard in the real source, as `006` and `007` recorded them.
- **R6.4** An **independent review**, because this changes the catalogue (playbook § 4).

### R7 — Documents

- **R7.1** `engineering/box-layout.md` § 4 gains the `0x07` row and the rule that ids and names belong to the store,
  and § 2 states what `_crystal` holds as R2.1 does. `005`'s design gets a dated addendum pointing here. `003`'s
  design § 2 gets a dated note saying where the UID now lives.

## Out of scope

- Backup, restore and attaching a database to another store (`010`).
- Changing the registry record's format, or the order of create, drop, rename and swap (`005`).
- Moving the replicated flag or the name into the database (R2.2 says why not).

## Open questions

None. The upgrade question was settled by the maintainer on 2026-10-10: there are no databases to upgrade (R4.1).
