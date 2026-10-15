# SystemD.Crystal

**An embeddable storage engine for .NET 10, built on a write-ahead log.**

Crystal runs inside your process and stores your data in **databases**, **boxes** and **records**:

- A **database** is a unit of isolation, replication, backup and drop. Each one has its own write-ahead log (WAL) and
  files, so a busy database never slows down a quiet one.
- A **box** is a named, ordered key space inside a database: a table, a queue or an index, whatever your product needs.
  Boxes are created, renamed, locked and dropped atomically.
- A **record** is a key and a value, both bytes, inside a box. Crystal never parses a value; what the bytes mean
  belongs to your application.

Every write is an atomic **batch** that can span any number of boxes. It is appended to the database's WAL before it
is acknowledged, at the durability you choose. Reads come from consistent **snapshots** and bounded, ordered **scans**.
The same WAL feeds **replication** to standby nodes and **incremental backups**. Underneath, Crystal uses RocksDB's
log-structured storage, behind an API that exposes none of it.

Crystal is a kernel, not a database server. It has no query language, documents, schemas or secondary indexes, and no
failover policy: the product built on top decides those. See [`product/intro.md`](product/intro.md) for what Crystal is
and is not, and [`engineering/box-layout.md`](engineering/box-layout.md) for how the bytes are laid out.

> **Status: `1.0.0-rc.2`, a release candidate for 1.0.** The storage kernel is complete. Tested on Windows and Linux. Changes per version: [`CHANGELOG.md`](CHANGELOG.md).

## Install

```sh
dotnet add package SystemD.Crystal --version 1.0.0-rc.2
```

Requires .NET 10. The package brings RocksDB's native libraries with it (the `RocksDB` package, pinned to one exact
version).

## Using it

```csharp
using System.Text;
using SystemD.Crystal;
using SystemD.Crystal.Encoding;

// A store is one data directory holding any number of databases. One open store per directory.
using var store = CrystalStore.Open("/var/lib/myapp");

// Databases are created once and opened by name afterwards.
var shop = store.CreateDatabase("shop");          // or: store.OpenDatabase("shop")
var orders = shop.Boxes.Create("orders");
var byCustomer = shop.Boxes.Create("orders-by-customer");

// A batch is atomic across boxes: the order and its index entry are committed together or not at all.
using (var batch = shop.NewBatch())
{
    batch.Put(orders, "order-1001"u8, Encoding.UTF8.GetBytes("""{"customer":"c-7","total":42}"""));
    batch.Put(byCustomer, "c-7/order-1001"u8, []);
    batch.Commit();                                // Strict by default: synced to the WAL before it returns
}

// Reads come from a consistent snapshot.
using (var snapshot = shop.Snapshot())
{
    byte[]? order = snapshot.Get(orders, "order-1001"u8);

    // An ordered scan over a key range: [from, to), at most 100 records. An empty bound is open.
    foreach (var record in snapshot.Scan(byCustomer, "c-7/"u8, "c-70"u8, limit: 100))
    {
        Console.WriteLine(Encoding.UTF8.GetString(record.Key));
    }
}
```

### Durability

```csharp
// Per store, or per commit: Strict (synced, the default), Interval (synced every few milliseconds), Buffered.
using var cache = CrystalStore.Open("/var/lib/cache", new CrystalOptions { Durability = DurabilityMode.Buffered });

using (var batch = shop.NewBatch())
{
    batch.Put(orders, "draft-7"u8, "{}"u8);
    batch.Commit(DurabilityMode.Buffered);         // this commit only
}
```

### Keys and ranges

Keys are bytes. Crystal's order-preserving encoders build composite keys whose byte order is the order you mean, and
read them back; descending fields are the byte complement of ascending ones.

```csharp
// order/{customer}/{created, newest first}
var key = new KeyWriter(stackalloc byte[128]);
key.WriteString("c-7");
var mark = key.BytesWritten;
key.WriteTimestamp(DateTimeOffset.UtcNow);
key.InvertLast(key.BytesWritten - mark);         // descending
byte[] bytes = key.ToArray();

var reader = new KeyReader(bytes);
string customer = reader.ReadString();
DateTimeOffset created = reader.ReadTimestamp(descending: true);

// Ranges without hand-made successor keys: half-open [From, To), an empty bound is the box's start or end.
KeyRange everyOrderOfC7 = KeyRanges.Prefix(Encoding.UTF8.GetBytes("c-7"));
KeyRange exactlyOne     = KeyRanges.Point("order-1001"u8);
KeyRange between        = KeyRanges.Between(KeyBound.Inclusive("a"u8.ToArray()), KeyBound.Exclusive("m"u8.ToArray()));
```

### Scans

```csharp
using (var snapshot = shop.Snapshot())
{
    // Forward: ascending, at most 100 records.
    foreach (var record in snapshot.Scan(byCustomer, KeyRanges.Prefix("c-7/"u8), limit: 100)) { }

    // Backward: the same range in descending order, newest first. A key equal to the range's end is never returned.
    var latest = snapshot.Scan(orders, KeyRange.All, limit: 10, ScanDirection.Backward).ToList();
}
```

Scans are always bounded to one box and a limit, and pinned to their snapshot. Dispose an enumeration you stop early
(a `foreach` does it for you).

### Batches

```csharp
using (var batch = shop.NewBatch())
{
    var current = batch.Get(orders, "counter"u8);                // reads its own pending writes
    batch.Put(orders, "counter"u8, Increment(current));          // Increment: your own encoding of the counter
    batch.DeleteRange(orders, KeyRanges.Prefix("draft-"u8));     // range deletes stay inside the box
    foreach (var pending in batch.Scan(orders, KeyRange.All, limit: 10)) { } // scans see pending writes too

    // What the batch holds, to bound a bulk load by bytes rather than by count.
    if (batch.Bytes > 8 << 20) { /* commit and start the next page */ }
    Console.WriteLine($"{batch.Count} operations, {batch.Bytes} bytes");
    batch.Commit();
}

// Optional guard against oversized keys: a longer key is refused when it is staged (KeyTooLargeException).
var guarded = new CrystalOptions { MaxKeyBytes = 1024 };
```

### Locks

Crystal never holds a lock across your code; locks are for your read-decide-write sequences. Both kinds are striped,
taken in a fixed order (so two multi-lock callers cannot deadlock), not re-entrant, and released by disposing what they
return, on any thread.

```csharp
// Box locks: one box, several, or every box.
using (shop.Locks.Lock(orders)) { /* ... */ }
await using (await shop.Locks.LockManyAsync([orders, byCustomer], cancellationToken)) { /* ... */ }

// Key locks: one record and the records that depend on it, such as a document and its unique index entries.
await using (await shop.KeyLocks.LockManyAsync(
    [new LockKey(orders, "order-1001"u8.ToArray()), new LockKey(byCustomer, "c-7/order-1001"u8.ToArray())],
    cancellationToken))
{
    // read, decide, write
}
```

The async forms wait without blocking a thread and honour cancellation. Box locks and key locks are independent: a box
lock does not exclude key locks in that box. `CrystalOptions.KeyLockStripes` sets the key-lock table's size
(default 256).

### Size estimates

```csharp
// RocksDB's estimate of the bytes a range occupies, memtables included. It never scans; its cost does not grow with
// the range. One range, or several in one call.
ulong whole = shop.ApproximateBytes(orders, [], []);
ulong[] parts = shop.ApproximateBytes(orders, [KeyRanges.Prefix("2026-"u8), KeyRanges.Prefix("2025-"u8)]);
```

An estimate, not a count: approximate, consistent with no snapshot, and 0 does not mean empty. Turning bytes into a
number of records is yours, since you know your records' sizes.

### Boxes and databases

```csharp
// Delete-heavy key ranges (queues, moving indexes) keep scans fast as records come and go.
var queue = shop.Boxes.Create("jobs", BoxProfile.DeleteHeavy);

// Databases and boxes are renamed, swapped and dropped atomically; open handles are never redirected.
store.RenameDatabase(shop, "shop-v1");
shop.Boxes.Drop(queue);
```

### Status

```csharp
// One consistent snapshot for a health endpoint: per open database its boxes, failure, last WAL sync and RocksDB's
// view (keys, live data, pending compaction, SST files per level, memtable bytes, write stalls), plus a running backup.
StoreStatus status = store.Status();
```

`CrystalOptions` also takes a logger factory and a meter factory: every event has a stable id and every instrument a
stable name ([`engineering/observability.md`](engineering/observability.md)).

## Replication

A **primary** ships each replicated database's committed batches, in WAL order, to its **standbys**. A standby pulls
over Crystal's own protocol and port (mutually authenticated with a shared secret, TLS optional), applies the batches
in order, and refuses local writes to the databases it follows. Every database created with `replicated: true` (the
default) is replicated; a new one reaches a standby as a snapshot first, then as a stream of batches.

```csharp
var options = new CrystalOptions
{
    Replication = new ReplicationOptions
    {
        Secret = Environment.GetEnvironmentVariable("CRYSTAL_SECRET")!,
        ListenEndpoint = "0.0.0.0:7400",
    },
};

// Node A: the primary. It listens; its databases replicate to whoever follows it.
using var primary = CrystalStore.Open("/data/node-a", options);
primary.Replication.Listen();
var orders = primary.CreateDatabase("orders");                  // replicated by default
var local = primary.CreateDatabase("scratch", replicated: false); // this node only

// Node B: a standby. It follows the primary and receives every replicated database.
using var standby = CrystalStore.Open("/data/node-b", options);
standby.Replication.Listen();                                     // so it can serve once promoted
standby.Replication.Follow("node-a:7400");

// Reads work on the standby; writes to followed databases throw NotPrimaryException.
foreach (var followed in standby.Replication.Status().Followed)
{
    Console.WriteLine($"{followed.Name}: {followed.Lag} behind");
}
```

**Changing the primary.** A planned handover stops writes on the primary, waits until the chosen standby has every
batch, and then makes it the primary:

```csharp
await primary.Replication.StepDownAsync(standbyNodeId: standby.NodeId, timeout: TimeSpan.FromSeconds(30));
// The standby is now primary, at a new epoch; node A no longer accepts writes.
```

After a failure, your product decides who leads. It promotes a standby, fences the old primary if it comes back, and
lets it rejoin as a standby:

```csharp
standby.Replication.StopFollowing();
long epoch = standby.Replication.Promote("node-a unreachable");  // a higher epoch: the old primary is outranked
```

**Shipping in batches.** Each page a standby applies costs it a synced write, so a primary does not send one page per
commit: a standby near the head is answered at most once per **gather window**, and the commits of the window arrive
together. A standby catching up is answered at once. The window trades a little lag for commit latency; the default
suits most workloads:

```csharp
Replication = new ReplicationOptions
{
    Secret = Environment.GetEnvironmentVariable("CRYSTAL_SECRET")!,
    GatherWindow = TimeSpan.FromMilliseconds(10), // the default; TimeSpan.Zero ships every commit at once (0 to 1 s)
},
```

The wait is a timer, so it lasts at least the operating system's timer resolution (about 15.6 ms on Windows). Under
Highway's load harness it brought a Strict commit with a standby attached from 3.5 ms to 2.2 ms, the same as with no
standby, for a standby lag of about one window ([`engineering/measurements.md`](engineering/measurements.md)).

Epochs fence a stale primary. A standby that applied past the point where the new primary's history diverges is
detected and re-bootstrapped, never left holding writes the primary never had. Crystal guarantees these mechanics;
failure detection and elections are deliberately left to the product. Details: [`engineering/replication.md`](engineering/replication.md).

## Backup and restore

A backup copies every live database while the store keeps serving, one database at a time. Each backup is
incremental: table files already in the backup root are not copied again. A set exists only once its manifest is
written, last, with a SHA-256 of its content.

```csharp
// Back up into a root outside the data directory. Allowed on a primary or a standby; it only reads.
BackupSetInfo set = await store.BackupAsync("/backups/myapp", new BackupOptions { Label = "nightly" });

// List, verify and keep the newest sets, without a store.
using (var root = CrystalBackupRoot.Open("/backups/myapp"))
{
    foreach (var s in root.ListSets())
    {
        Console.WriteLine($"{s.SetId} {s.Label}: {s.Databases.Count} databases");
    }

    BackupVerifyResult quick = await root.VerifyAsync(set.SetId, deep: false); // files present, sizes right
    BackupVerifyResult deep = await root.VerifyAsync(set.SetId, deep: true);   // every byte, every checksum
    root.Purge(keepLast: 7);
}

// Restore into a new store: all of the set's databases or none, each with its records, boxes and identity.
using var restored = CrystalStore.Open("/var/lib/myapp-restored");
await restored.RestoreAsync("/backups/myapp", set.SetId);

// Or restore one database as a copy beside the live one, check it, then swap the two.
await store.RestoreAsync("/backups/myapp", set.SetId, new RestoreOptions
{
    Databases = ["shop"],
    Rename = new Dictionary<string, string> { ["shop"] = "shop-restored" },
    NewIdentity = true,
});
store.SwapDatabases(store.OpenDatabase("shop"), store.OpenDatabase("shop-restored"));
```

Each database is captured at its own moment, with every commit acknowledged before its copy began. A product that
needs an invariant across data keeps that data in one database. Cancelling a backup, closing the store, or dropping a
database stops its copy promptly and leaves earlier sets untouched. Details:
[`engineering/backup.md`](engineering/backup.md).

## Build and test

Prerequisites: the .NET 10 SDK (pinned in [`global.json`](global.json)).

```sh
dotnet restore SystemD.Crystal.slnx
dotnet build   SystemD.Crystal.slnx -c Release
dotnet test    SystemD.Crystal.slnx -c Release
```

To produce the NuGet package (symbols included):

```sh
dotnet pack src/SystemD.Crystal/SystemD.Crystal.csproj -c Release -o ./artifacts
```

## Repository layout

```
src/SystemD.Crystal/                        the library: public API, internal RocksDB storage, replication, backup
tests/SystemD.Crystal.Tests/                unit and in-process tests, with real RocksDB in temporary folders
tests/SystemD.Crystal.Process.Tests/        multi-process tests: replication pairs, failover, kills, backups under load
tests/SystemD.Crystal.Architecture.Tests/   the boundary tests (no RocksDB on the public surface, no direct clock reads)
tools/                                      the crash harness and the benchmark harness
product/                                    intro, the playbook, and the numbered specs
engineering/                                how Crystal works: layout, replication, backup, observability
```

## How work is done

Every change starts as a numbered spec (requirements, design, tasks) under
[`product/specs/`](product/specs/). The [`playbook`](product/playbook.md) is binding for every
contributor, human or agent.

## Platform

- .NET 10 (C# 14).
- RocksDB through its .NET binding, pinned to one exact version; a RocksDB upgrade is a Crystal release.
- Native AOT and IL trimming are not supported: the RocksDB binding generates code at runtime.

## License

[MIT](LICENSE).
