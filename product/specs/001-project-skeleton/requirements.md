# 001 — Project skeleton

> **Status:** drafted. **Depends on:** nothing.
> **Revised (draft) 2026-10-09:** one library project and one package, `SystemD.Crystal`, instead of a separate RocksDB
> leaf. RocksDB is never planned to be swapped, every user needs it, and the boundary that matters (no RocksDB type on
> the public surface) is enforced inside one assembly by the architecture tests.

## Introduction

The repository structure for SystemD.Crystal: the solution, the projects and their boundaries, the build settings, the
test projects, the architecture tests that keep the boundaries honest, packaging metadata, and continuous
integration. **No storage features.** Boxes, batches, durability, replication and backup come in later specs. This
spec ends with an empty kernel that builds, tests and packs on Windows and Linux, and that proves the RocksDB native
library loads on both.

## Requirements

### R1 — Solution and projects

- **R1.1** One solution, `SystemD.Crystal.slnx`, at the repository root.
- **R1.2** One library project, `src/SystemD.Crystal`.
  - The public API is in `SystemD.Crystal` and its sub-namespaces.
  - Everything that touches RocksDB is in **internal** types under the `SystemD.Crystal.Storage` namespace.
- **R1.3** Two test projects under `tests/`:
  - `SystemD.Crystal.Tests`, including the native smoke test (R4);
  - `SystemD.Crystal.Architecture.Tests`.
- **R1.4** Root namespaces equal project names, under `SystemD.Crystal`.

### R2 — Build settings

- **R2.1** `global.json` pins the .NET 10 SDK.
- **R2.2** `Directory.Build.props`, shared by every project:
  - `net10.0`, C# 14, nullable enabled;
  - warnings as errors;
  - deterministic builds;
  - Native AOT and trimming off (the RocksDB binding generates code at runtime).
- **R2.3** `Directory.Packages.props` manages every package version centrally. RocksDB is pinned to one exact version
  (`RocksDB` 11.8.1.5032, the version the products built on Crystal use).
- **R2.4** `.editorconfig` with the playbook's style rules; `.gitignore` for .NET build output, test results and local
  data directories.

### R3 — Architecture tests

- **R3.1** No public type, and no public or protected member signature, exposes a RocksDB type: no base type,
  interface, field, property, parameter, return type or generic argument.
- **R3.2** RocksDB types are used only by types in the `SystemD.Crystal.Storage` namespace, and every type in that
  namespace is `internal`.
- **R3.3** The assembly references only an allow-list: the .NET libraries, `Microsoft.Extensions.*` abstractions and
  the RocksDB binding. No product.
- **R3.4** Each rule is shown to fail when broken on purpose (a temporary violation), then removed.

### R4 — A smoke test of the native library

- **R4.1** A test in `SystemD.Crystal.Tests` opens a RocksDB instance in a temporary directory through an internal
  storage type, writes one key, reads it back, closes the instance and deletes the directory.
- **R4.2** It passes on Windows x64 and Linux x64 on CI.
- **R4.3** **Supported platforms** are those the pinned RocksDB package ships native libraries for: Windows x64,
  Linux x64 and ARM64 (glibc and musl), macOS x64 and ARM64 (checked 2026-10-09). Windows x64 and Linux x64 are tested
  on CI; the others are supported on the binding's word until a CI leg is added.

### R5 — Packaging

- **R5.1** The library packs as **one** NuGet package, `SystemD.Crystal`, depending on the RocksDB binding: a
  pre-release version (`0.1.0-preview.1`), description, repository URL, license expression, a package README, and
  symbols (`.snupkg`).
- **R5.3** Later capabilities (replication, backup) ship in the same package unless one brings heavy dependencies of
  its own; splitting then is that spec's decision.
- **R5.2** Packing only. **Nothing is published** in this spec.

### R6 — Continuous integration

- **R6.1** A GitHub Actions workflow builds, tests and packs on every push and pull request, on `windows-latest` and
  `ubuntu-latest`.
- **R6.2** Test results are kept as workflow artifacts.

### R7 — Repository documents

- **R7.1** `README.md`: what Crystal is (from `product/intro.md`), its status, how to build and test.
- **R7.2** `LICENSE`: MIT, copyright System D AB.
- **R7.3** `CLAUDE.md`, `AGENTS.md` and `.kiro/steering/playbook.md` point to the playbook (already present).
  _Amended 2026-10-11 (maintainer): only `AGENTS.md`, kept generic, is published; tools' own agent files are kept out
  of the repository (`.gitignore`) and point to the playbook from the contributor's machine (playbook § 8)._

## Out of scope

Boxes and the catalogue, batches, snapshots and scans, durability modes, profiles, replication, backup and restore,
benchmarks. Later specs, roughly in this order:

| Spec | Delivers |
|---|---|
| `001-2-rocksdb-binding` | Crystal's internal RocksDB layer: the pinned binding, native calls behind one wrapper, the binding's traps as tested rules, shared resources, profiles, metrics, offline compaction, the upgrade checklist |
| `002-box-layout` | Boxes and the catalogue; atomic batches; snapshots and bounded scans; key encoding helpers; striped locks |
| `003-replication` | Replication: streams per database, epochs and fencing, snapshot bootstrap, its own protocol and listener |
| `004-test-suite` | The test suite's structure and infrastructure, and the tests inherited from Highway |
| `005-store-and-databases` | The store, its data directory and format stamp, the `_crystal` registry, the database lifecycle (folders by id, names as aliases), reset. Implemented before `002` |
| later | Durability modes and the process-kill test; workload profiles (`General`, `DeleteHeavy`); backup and restore; the benchmark harness. Numbers assigned when written. |

## Decisions

Recorded 2026-10-09. The addendum below records what the implementation chose; this table and the code agree.

| Question | Decision | Basis |
|---|---|---|
| License | **MIT**, copyright System D AB | the same as Highway; implemented (T8, T9) |
| Assertion and mocking libraries | **Plain xUnit assertions; no FluentAssertions, no NSubstitute** | chosen in the implementation (addendum); avoids FluentAssertions 8's commercial license entirely |
| Package prefix | `SystemD.Crystal`; the maintainer reserves the `SystemD` prefix on nuget.org before the first publish | package ids and namespaces are distinct from Linux's `systemd` |
| Platforms | as R4.3 | the native libraries the pinned RocksDB package ships |
| Repository | **`github.com/System-D-AB/crystal`**: replaces the `RepositoryUrl` placeholder; `git init` stays deferred until the maintainer says so | the same organization as Highway |

## Addendum 2026-10-09 — open-question answers and implementation notes

Recorded as the skeleton was built (tasks T2–T9). The original open questions above stay readable; this
addendum says what was decided and what is still open.

- **OQ1 — License: MIT.** The repository `LICENSE` is MIT, and the package carries the `MIT` license
  expression. (T8, T9.)
- **OQ3 — Assertion library: none.** FluentAssertions 8's commercial-use license change is friction for an
  open-source library, so the test projects use plain **xUnit** assertions instead of FluentAssertions or
  NSubstitute. `Directory.Packages.props` therefore pins only `xunit`, `xunit.runner.visualstudio` and
  `Microsoft.NET.Test.Sdk`. If a mocking need appears later, that spec decides it. (T2.)
- **OQ5 — Repository: still open; git not initialized.** The maintainer is removing content before the first
  push, so **T1 was not run** — there is no git repository and no commit yet. The package's
  `RepositoryUrl` is a placeholder (`https://example.invalid/systemd-crystal`) marked with a `TODO(repo)`
  in `src/SystemD.Crystal/SystemD.Crystal.csproj`, to be set to the real URL before the first publish.
- **OQ4 — Platforms:** unchanged. R4.2 answers this by CI result, not assumption; the workflow runs on
  `windows-latest` and `ubuntu-latest`.
- **OQ2 — NuGet prefix reservation:** unchanged; outward-facing and deferred to publish time.

**Implementation notes (what was done differently or worth recording):**

- **SDK pin.** `global.json` pins the .NET 10 SDK to `10.0.100` with `rollForward: latestFeature` (the
  installed SDK on the build machine is `10.0.203`, which satisfies it).
- **RocksDB version.** Pinned exactly to `[11.8.1.5032]` in `Directory.Packages.props` (R2.3), and the pin
  carries through to the package's dependency group.
- **`InternalsVisibleTo`.** Granted to both test projects via `AssemblyAttribute` MSBuild items (the
  `<InternalsVisibleTo>` item form is not recognized by this SDK).
- **Architecture tests (R3).** Implemented by reflection over the built assembly with no extra framework:
  R3.1 walks every public/protected surface for RocksDB types; R3.2 checks every `SystemD.Crystal.Storage`
  type is internal and scans method IL (a proper opcode walk, not a blind byte scan) for RocksDB tokens
  outside that namespace; R3.3 checks referenced assemblies against an allow-list.
- **Mutation checks (R3.4) are permanent, not manual.** Rather than hand-mutate the kernel and revert, the
  architecture test project holds planted violations (a public type exposing `DbOptions`, a non-Storage body
  that calls RocksDB, a public type in the Storage namespace, a product assembly name) and asserts the same
  rule functions catch each one, while the real kernel assembly stays clean. This is why the architecture
  test project references `RocksDB`.
- **Verified:** `dotnet build -c Release` is clean with warnings as errors (0 warnings); `dotnet test`
  passes 9 tests (8 architecture, 1 native smoke) on Windows; `dotnet pack` produces
  `SystemD.Crystal.0.1.0-preview.1.nupkg` and its `.snupkg`, with the README embedded and RocksDB pinned.
  CI (both OS legs) is defined but not yet observed green — there is no repository to run it.
