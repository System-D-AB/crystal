# SystemD.Crystal

A minimal, embedded storage kernel for .NET on RocksDB.

Crystal gives an application a small set of storage primitives it can trust:

- **Boxes:** named, isolated key spaces, created, listed, locked and dropped atomically.
- **Atomic multi-key batches** across any number of boxes.
- **Snapshots and bounded, ordered scans.**
- **Durability you choose:** buffered, interval-synced, or synced on every commit.
- **Replication, backup and restore.**
- **Format stamps:** a directory a build cannot read is refused by name, with the way forward.

Crystal never parses a value. What the bytes in a box *mean* belongs to the application built on top.

> **Status: pre-release.** This is the project skeleton (`0.1.0-preview.1`). The storage API arrives in
> later releases.

## Platform

- .NET 10.
- RocksDB through its .NET binding, pinned to one version.
- Native AOT and IL trimming are not supported (the RocksDB binding generates code at runtime).

Licensed under the MIT License.
