# Changelog

## Unreleased

No format, catalogue, encoder or protocol change; a few source-breaking API changes from the review before the
freeze, listed below. From 1.0 the public API follows semantic versioning,
recorded in `tests/SystemD.Crystal.Architecture.Tests/PublicApi.txt` and checked on every build.

- **For a product's operators and failover policy** (spec `013`, taken for 1.0 by spec `015`):
  - `CrystalStore.Compact`: offline compaction of every database or the ones named, with each one's size before and
    after; refused while the store is open anywhere.
  - `CrystalReplication.Promote(reason, minimumEpoch)`: promote above a floor in one persisted step.
  - `CrystalReplication.Unfence`: a fenced primary returns to primary at the same epoch; refused if another node has
    been primary since. A new `Unfenced` event and log event 2015.
  - `ReplicationStatus.ConnectedToPrimary` and `ReplicationStatus.LastReplyFromPrimary`: a standby's link to its
    primary.
  - `DatabaseStatus.LatestPosition`: a database's latest committed position, with no standby attached.
  - `BoxInfo.Handle`: every box `BoxCatalog.List()` returns comes with its handle.
- **Breaking, from the review of the public API before it is frozen** (spec `015` R3): each was incompatible to fix
  after 1.0. Upgrading from rc.2:
  - `Box` is a plain `readonly struct`: no public constructor, `with` or `Deconstruct`, and equal when it addresses the
    same box of the same open database (its name is informational). A handle could be altered with `with` and still
    pass the database's ownership check; it cannot now.
  - `CrystalVersion.Value` is a property, not a constant, so an application reports the version it runs.
  - `CrystalReplication` is no longer `IDisposable`: the store owns it.
  - `ReplicationStatus.Role` is a `ReplicationRole`, not a string.
  - `BoxLocks.Lock`, `LockMany` and `LockAll` return `LockLease`, as the async forms and key locks do.
  - `CrystalDatabase.ApproximateBytes` returns `long`, like every other byte count.
  - Crystal's exceptions have internal constructors; an application throws its own.
  - `ReplicationEventKind`, `ReplicationRole` and `ReplicationSlotState` have explicit values (unchanged).
- **Crash recovery proven per durability mode:** 20 hard kills each for Strict, Interval and Buffered, a torn WAL tail,
  and a primary killed mid-load (spec `015`).
- **CI:** Linux and Windows runners pinned; long cost probes moved out of the everyday run; a hang fails within three
  minutes, naming the test.

## 1.0.0-rc.2

The second 1.0 release candidate. No format, catalogue, encoder or protocol change: stores written by 1.0.0-rc.1
open unchanged, and a standby on 1.0.0-rc.1 follows a primary on this build, and the reverse.

- **Replication ships in batches** (spec `014`). A Strict commit on a primary with a standby attached no longer costs
  a page, and a synced write on the standby, per commit: a standby near the head is answered at most once per
  **gather window** (`ReplicationOptions.GatherWindow`, 10 ms; `TimeSpan.Zero` restores per-commit shipping), and a
  standby catching up is answered at once. Under Highway's load harness, Strict commit p50 fell from 3.5 ms to 2.2 ms,
  the same as with no standby attached. The price is lag: a standby near the head now trails by up to about one
  window (on Windows the window rounds up to the 15.6 ms timer; lag sampled once a second had a median of 14–15 ms
  and a p99 of up to 46 ms under that harness).
- **A waiting pull holds no thread:** the primary's long poll and the gather window are asynchronous waits.
- **Less replication work per commit:** the commit timeline takes its lock only when a sample is due (about ten times
  a second), not on every commit. The tail still takes its own short lock to record each batch.
- **Exact lag in time:** `crystal.replication.lag_time` and a slot's `LagTime` come from each batch's commit time
  while the primary's tail holds it, no longer only from 100 ms samples.
- **Observability:** `crystal.replication.pull.source` (pages served from the tail or the WAL) and
  `crystal.replication.ship_delay` (commit to shipping of each page's oldest batch).

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
