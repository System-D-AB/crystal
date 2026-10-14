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

> **Status: `1.0.0-preview.1`, a prerelease.** The storage kernel is complete; the public API may still change before
> 1.0, as the first products adopt it. Tested on Windows; the first Linux run of the suite is pending. Changes per
> version: [`CHANGELOG.md`](CHANGELOG.md).

## Install

```sh
dotnet add package SystemD.Crystal --version 1.0.0-preview.1
```

Requires .NET 10. The package brings RocksDB's native libraries with it (the `RocksDB` package, pinned to one exact
version).

## Using it

```csharp
using System.Text;
using SystemD.Crystal;

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

More of the API:

```csharp
// Durability per store, or per commit: Strict (synced), Interval (synced every few ms), Buffered.
using var fast = CrystalStore.Open("/var/lib/cache", new CrystalOptions { Durability = DurabilityMode.Buffered });

// Box locks for read-modify-write: a striped lock per box, never held by Crystal across your code.
using (shop.Locks.Lock(orders))
using (var batch = shop.NewBatch())
{
    var current = batch.Get(orders, "counter"u8);  // a batch reads its own pending writes
    batch.Put(orders, "counter"u8, Increment(current)); // Increment: your own encoding of the counter
    batch.Commit();
}

// Boxes for delete-heavy key ranges (queues, moving indexes) keep scans fast as records come and go.
var queue = shop.Boxes.Create("jobs", BoxProfile.DeleteHeavy);

// Databases and boxes are renamed, swapped and dropped atomically; open handles are never redirected.
store.RenameDatabase(shop, "shop-v1");
shop.Boxes.Drop(queue);

// Health: per database boxes, failures, last WAL sync and RocksDB's view, plus a running backup.
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
