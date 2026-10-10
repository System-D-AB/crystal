# 001 — Project skeleton: design

> **Revised (draft) 2026-10-09:** one project and one package (see requirements).

## 1. Layout

```
/
├── SystemD.Crystal.slnx
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── .gitignore
├── README.md
├── LICENSE                         MIT, System D AB
├── AGENTS.md                       the one agent file published (tools' own files are git-ignored)
├── .github/workflows/build.yml
├── product/                        intro, playbook, specs
├── engineering/                    how Crystal works (box-layout.md, …)
├── src/
│   └── SystemD.Crystal/            the library: public API + internal storage (RocksDB)
└── tests/
    ├── SystemD.Crystal.Tests/              including the native smoke test
    └── SystemD.Crystal.Architecture.Tests/
```

## 2. Dependencies and namespaces

```
SystemD.Crystal (one assembly, one package)
   ├──► RocksDB (the .NET binding, pinned)
   └──► Microsoft.Extensions.Logging.Abstractions   (System.Diagnostics.Metrics is part of .NET)

namespaces
   SystemD.Crystal            public API: store, databases, boxes, batches, snapshots (from spec 002)
   SystemD.Crystal.Encoding   public key-encoding helpers
   SystemD.Crystal.Storage    internal only: everything that touches RocksDB
```

- **Why one package.**
  - RocksDB is Crystal's only backend by definition, and no other is planned.
  - Every application that uses Crystal needs storage, so it needs RocksDB.
  - A second package would add a reference to make, a version to align and `InternalsVisibleTo` between assemblies,
    and protect nothing that a test cannot protect inside one assembly.
- **The boundary is a namespace plus a test**, not an assembly: RocksDB is touched only by `internal` types in
  `SystemD.Crystal.Storage`, and nothing public exposes a RocksDB type (§ 4).

## 3. Build settings

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <!-- The RocksDB binding generates code at runtime: no Native AOT, no trimming. -->
    <PublishAot>false</PublishAot>
    <PublishTrimmed>false</PublishTrimmed>
    <RootNamespace>$(MSBuildProjectName)</RootNamespace>
  </PropertyGroup>
</Project>
```

`ImplicitUsings` is off: the kernel's files state what they use.

`Directory.Packages.props` (central package management):

| Package | Version | Used by |
|---|---|---|
| `RocksDB` | `11.8.1.5032` (exact) | `SystemD.Crystal` |
| `Microsoft.Extensions.Logging.Abstractions` | current .NET 10 | `SystemD.Crystal` |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | current | tests |
| — | — | no assertion or mocking library: plain xUnit assertions (see the requirements' decisions) |

## 4. Architecture tests

Reflection over the built assemblies, no extra framework:

| Rule | How it is checked |
|---|---|
| R3.1: no RocksDB type on a public surface | every public type's public and protected members: base types, interfaces, fields, properties, method parameters and returns, generic arguments, recursively; no type from `RocksDbSharp` |
| R3.2: RocksDB only inside `SystemD.Crystal.Storage`, all internal | every type in `SystemD.Crystal.Storage` is not public; IL scan of method bodies and field types outside that namespace finds no `RocksDbSharp` reference |
| R3.3: no product references | referenced assemblies match an allow-list: `System.*`, `Microsoft.Extensions.*.Abstractions`, `RocksDbSharp` |

Each rule is mutation-checked once (R3.4): add a violation, see the test fail, remove it. The playbook's rule on
verification is that a guard nobody has seen fail protects nothing.

## 5. The smoke test

```
open RocksDB in a fresh temporary directory (through an internal SystemD.Crystal.Storage type)
put  key [0x00 0x00 0x00 0x10 0x01] = "x"
get  the key → "x"
close; delete the directory
```

It lives in `SystemD.Crystal.Tests` and reaches the internal storage type through `InternalsVisibleTo` (granted to
the test projects only). The point is the
platform check (R4.2): the native library loads, and a write survives a read on each CI platform.

## 6. Continuous integration

`.github/workflows/build.yml`:
- on push and pull request;
- a matrix of `windows-latest` and `ubuntu-latest`;
- steps: restore, build (Release), test, pack, upload test results and packages as artifacts.

No publish step.

## 7. What this spec does not decide

- **The kernel's API.** `002` defines boxes, batches, snapshots and scans. The skeleton's public surface is empty
  apart from a version constant.
- **Which existing code is brought in.** Crystal's first features are seeded from the ordered-key storage layer that
  StowDB runs today: its order-preserving key encoding, ordered store, striped lock and RocksDB leaf. Its document,
  index and query layers stay in StowDB. `002` decides what is brought in, file by file, against the box layout.
