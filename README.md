# SystemD.Crystal

**A minimal, embedded storage kernel for .NET 10, built on RocksDB.**

Crystal gives an application a small set of storage primitives it can trust: isolated **boxes**, atomic
multi-key **batches**, **snapshots** and bounded ordered **scans**, a **durability** mode you choose,
**replication**, **backup and restore**, and **format stamps** that refuse an unreadable directory by name.
Crystal never parses a value — what the bytes in a box *mean* belongs to the application built on top.

See [`product/intro.md`](product/intro.md) for what Crystal is and is not, and
[`engineering/box-layout.md`](engineering/box-layout.md) for the physical layout.

> **Status: pre-release.** The project is being specified. This repository currently holds the project
> skeleton (spec [`001-project-skeleton`](product/specs/001-project-skeleton/)): an empty kernel that
> builds, tests and packs on Windows and Linux, and proves the RocksDB native library loads on both.
> Boxes, batches, durability, replication and backup arrive in later specs.

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
src/SystemD.Crystal/                 the library: public API + internal RocksDB storage
tests/SystemD.Crystal.Tests/         the native smoke test and (later) feature tests
tests/SystemD.Crystal.Architecture.Tests/   the boundary tests (no RocksDB on the public surface)
product/                             intro, the playbook, and the numbered specs
engineering/                         how Crystal works (box-layout.md, …)
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
