# SystemD.Crystal

**A minimal, embedded storage kernel for .NET 10, built on RocksDB.**

Crystal gives an application a small set of storage primitives it can trust:

- **Boxes:** named, isolated key spaces that can be created, listed, locked and dropped atomically.
- **Atomic multi-key batches:** any number of puts, deletes and range deletes, across any number of boxes, committed
  as one unit.
- **Snapshots and bounded, ordered scans.**
- **Durability you choose:** buffered, interval-synced, or synced on every commit.
- **Replication:** a primary and standbys over Crystal's own small protocol and port.
- **Backup and restore.**
- **Format stamps:** a data directory written in a format a build cannot read is refused by name, with the way forward.
- **Workload profiles:** tuning for general or delete-heavy key spaces.

Everything else, what the bytes in a box *mean*, belongs to the application built on top.

## What Crystal is not

Crystal deliberately has **no**:
- documents, schemas or secondary indexes;
- queries, query languages or algebra;
- queues or messaging semantics;
- failover decisions;
- consensus, elections or sharding.

To Crystal, a box is a named key range and a value is bytes. It never parses a value.

**Safety belongs to Crystal; policy belongs to the product.** Crystal guarantees that a commit is atomic, that a
demoted primary is fenced, that a gap in the replication log is never applied as if it were contiguous, and that an
unreadable format is refused. When to fail over, which node leads, and what a key means are the product's decisions.

## Built on Crystal

Two products are built on Crystal, each owning its own model:

| Product | What it builds on Crystal |
|---|---|
| **StowDB** | A document database with a JSON-over-HTTP API. Its documents, declared indexes and query semantics (a restricted relational algebra) live in StowDB, stored in Crystal boxes. |
| **Highway** | A durable message broker for distributed applications. Its queues, leases, retries, dead letters and subscriptions live in Highway, stored in Crystal boxes. Its automatic failover follows its own client herd. |

Crystal is shaped by both: a document store's reads and a broker's constant churn are opposite workloads, and a kernel
that serves both stays general.

## Principles

1. **Bytes, not meaning.** If a feature needs to understand a value, it belongs in a product, not in Crystal.
2. **One batch, one truth.** A product's data and its metadata live in boxes of the same database, so one batch
   changes both atomically, and they are replicated and backed up together.
3. **Everything bounded.** Scans have bounds; per-operation cost does not grow with uptime, data size or the number of
   boxes.
4. **Never silent.** An unreadable format, a log gap, a fenced primary: each is refused loudly, by name.
5. **Measured, not assumed.** Performance claims come from a published, self-checking benchmark harness, with the
   durability mode stated beside every number.
6. **Specified before built.** Every change starts as a numbered spec (see [`playbook.md`](playbook.md)).

## Platform

- .NET 10 (C# 14).
- RocksDB through its .NET binding, pinned to one version; a RocksDB upgrade is a Crystal release.
- Native AOT and IL trimming are not supported: the RocksDB binding generates code at runtime.
- Shipped as NuGet packages under the `SystemD.Crystal` prefix, with semantic versioning.

## Status

**Pre-release.** The project is being specified. The physical layout of boxes is described in
[`../engineering/box-layout.md`](../engineering/box-layout.md); the first spec,
[`specs/001-project-skeleton`](specs/001-project-skeleton/), sets up the repository.
