# SystemD.Crystal

**An embeddable storage engine for .NET 10, built on a write-ahead log.**

Crystal runs inside your process and stores data in **databases**, **boxes** and **records**:

- a **database** is the unit of isolation, replication, backup and drop, with its own write-ahead log;
- a **box** is a named, ordered key space inside a database, created, renamed, locked and dropped atomically;
- a **record** is a key and a value, both bytes. Crystal never parses a value.

```csharp
using var store = CrystalStore.Open("/var/lib/myapp");
var shop = store.CreateDatabase("shop");
var orders = shop.Boxes.Create("orders");

using (var batch = shop.NewBatch())          // atomic across boxes
{
    batch.Put(orders, "order-1001"u8, "{...}"u8);
    batch.Commit();                          // synced to the WAL before it returns
}

using var snapshot = shop.Snapshot();
foreach (var record in snapshot.Scan(orders, "order-"u8, "order."u8, limit: 100))
{
    // record.Key, record.Value
}
```

Also in the box:

- **Durability you choose:** strict (synced per commit), interval-synced or buffered.
- **Replication:** a primary ships each database's WAL to standbys over Crystal's own authenticated protocol (TLS
  optional); promotion, fencing by epoch, planned handover. Failover policy stays with your product.
- **Backup and restore:** incremental online backups into a root you name, quick and deep verify, retention, and
  all-or-nothing restore into a new or running store.
- **Observability:** stable log event ids and `System.Diagnostics.Metrics` instruments; a status snapshot.
- **Format stamps:** a directory this build cannot read is refused by name, with the way forward.

> **Status: 1.0.0-rc.2,** a release candidate for 1.0. Tested on Windows and Linux.

## Platform

- .NET 10.
- RocksDB through its .NET binding, pinned to one exact version.
- Native AOT and IL trimming are not supported (the RocksDB binding generates code at runtime).

Documentation, specs and source: https://github.com/System-D-AB/crystal. Licensed under the MIT License.
