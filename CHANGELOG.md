# Changelog

## 1.0.0-rc.1

The first 1.0 release candidate. Every addition is to the API; nothing was removed or renamed. No format,
catalogue, encoder or protocol change: stores written by 1.0.0-preview.1 open unchanged. Tested on Windows and Linux.

- **Backward scans** on snapshots (`ScanDirection.Backward`).
- **Range size estimates:** `CrystalDatabase.ApproximateBytes`, one range or several in one native call.
- **Key locks** (`CrystalDatabase.KeyLocks`, `CrystalOptions.KeyLockStripes`), sync and async, and **async box locks**
  (`BoxLocks.LockAsync`, `LockManyAsync`); both return a `LockLease`.
- **Key ranges:** `KeyRange`, `KeyBound`, and the `KeyRanges` builders, with overloads of scans and range deletes.
- **Batch accounting:** `Batch.Count`, `Batch.Bytes`.
- **`KeyReader`**, the reading half of `KeyWriter`.
- **A key size limit** (`CrystalOptions.MaxKeyBytes`, `KeyTooLargeException`), off by default.
- **Observability:** the `crystal.scan.open` gauge, a `crystal.lock.scope` tag on lock waits, and more storage status
  (files per level, active memtable bytes, delayed write rate).
- **Fixed: a replaced database's handle no longer says the store closed.** When a standby re-creates a database from
  its primary's snapshot, the old `CrystalDatabase` handle (and its boxes) now throw `DatabaseNotFoundException`
  saying to open the database again by name; `ObjectDisposedException` means only that the store itself was closed.

## 1.0.0-preview.1

The first 1.0 preview: the complete storage kernel. The public API may still change before 1.0 as the first products
adopt it. Tested on Windows; the first Linux run of the suite is pending.

- **Stores and databases** (specs `005`, `009`): one data directory, any number of databases, each its own RocksDB
  instance and WAL; create, open, rename, swap and drop online; a crash-safe registry; databases that carry their own
  identity.
- **Boxes and records** (`002`): named, ordered key spaces with a catalogue; atomic batches across boxes that read their
  own writes; snapshots; bounded, ordered scans; box locks; a delete-heavy profile.
- **Durability** (`006`): strict, interval and buffered modes, per store and per commit; failed databases refuse writes.
- **Observability** (`007`): stable log events, metrics, and a status snapshot.
- **Replication** (`003`, `011`): primary/standby WAL shipping over Crystal's own authenticated protocol, TLS optional;
  snapshot bootstrap; epochs, promotion, demotion and fencing; divergence detection and re-bootstrap; planned handover.
- **Backup and restore** (`010`): incremental online backups per database into a backup root, set manifests with
  SHA-256, quick and deep verify, retention, all-or-nothing restore, restore as a copy.
- **Format stamps:** a directory or database in a format this build cannot read is refused by name.

## 0.1.0-preview.1

The project skeleton (spec `001`): an empty kernel that builds, tests and packs, and loads RocksDB. Never published.
