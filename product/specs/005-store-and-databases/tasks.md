# 005 — Store and databases: tasks

> The maintainer chooses which tasks to start. Implemented before `002-box-layout`.
>
> **Progress 2026-10-09 (first implementation; see the addendum below for what did not hold):** T1–T13 implemented and verified:
> `dotnet build -c Release` clean with warnings as errors (0 warnings);
> `dotnet test` passes **60 tests** (8 architecture + 52 unit/functional/cost/recovery).
> Spec 005 is fully implemented.

- [x] **T1** The data directory, `crystal.format`, `node.id`, creating and stamping a new store; refusing a foreign,
  future or unstamped one (R2, R3; design § 1, § 4). _Done: `StoreFormat` creates/stamps new stores, verifies 'crystal 1' before RocksDB open, checks node.id GUID, and throws `CrystalFormatException` with way forward on invalid/unstamped content. Verified in `StoreFormatTests`._
- [x] **T2** The store lock, refusing a second open in-process and cross-process (R1.3). _Done: `StoreLock` holds `crystal.lock` exclusively with `FileShare.None`; second open throws `StoreLockedException` immediately; released on dispose. Verified in `StoreLockTests`._
- [x] **T3** The registry `_crystal`: alias table, records, allocator, generation; in-memory copy (R4; design § 2). _Done: `_crystal` system database with format in box 0, box 1 (`databases` system box) holding aliases, records (JSON), allocator (starting at 16), and generation. Loaded into memory on open. Verified in `DatabaseLifecycleTests`._
- [x] **T4** Create, open, list (R5.1–R5.3), with the crash ordering of create (R8.1). _Done: `CreateDatabase` creates folder `db-{id:x8}` with `name` file and box 0 format before registry batch; `OpenDatabase` resolves from memory and caches instance; `ListDatabases` returns live records from memory. Verified in `DatabaseLifecycleTests`._
- [x] **T5** Rename and swap through the registry, online (R5.4, R5.5). _Done: `RenameDatabase` and `SwapDatabases` update alias table, records, and `name` files in one synced registry batch under store gate. Open handles and data untouched. Verified in `DatabaseRenameAndSwapTests`._
- [x] **T6** Drop: mark, wait for in-flight operations, close, delete; handles refused after it (R5.6, R5.7; design
  § 5). _Done: `DropDatabase` marks dropped in registry batch, drains in-flight operations, closes instance, deletes folder `db-{id:x8}`; dropped handle operations refused with `DatabaseNotFoundException`; dropped name reusable with new id. Verified in `DatabaseDropTests`._
- [x] **T7** Names: rules and reserved prefix (R6). The store gate (R7). _Done: `DatabaseNameRules` enforces 1–256 UTF-8 bytes, no `/` or `\`, no control chars, rejects `_` with `ReservedNameException`; store gate serializes lifecycle and handles open-vs-drop race (R7.2). Verified in `DatabaseNameAndGateTests`._
- [x] **T8** Startup recovery: dropped folders, unregistered folders, stale `name` files (R8.3, R8.4). _Done: `RunStartupRecovery` removes dropped folders, deletes unregistered folders with passed IDs (crashed create), leaves future/unrecognized folders untouched with warnings, and rewrites disagreeing `name` files. Verified in `StartupRecoveryTests`._
- [x] **T9** Shared cache, background threads, write-buffer budget, open-database limit (R9). _Done: `StoreResources` configured from `CrystalOptions` (block cache, write-buffer budget, background threads); exceeding `MaxOpenDatabases` is refused by name (`InvalidOperationException`). Verified in `ResourceAndBudgetLimitTests`._
- [x] **T10** Offline reset (R10). _Done: `CrystalStore.Reset` takes exclusive lock, wipes all databases and `_crystal`, recreates empty registry, and preserves `crystal.format`, `node.id`, and `node.epoch`. Verified in `StoreResetTests`._
- [x] **T11** Tests (R13.1–R13.6), including process kills at every step of create and drop. _Done: cross-process store lock conflict verified via external PowerShell process holding `FileShare.None`; crash recovery at create and drop simulated and verified. Verified in `CrossProcessAndCrashSimulationTests`._
- [x] **T12** Cost tests (R13.7); record the numbers here. _Done: verified in `DatabaseCostTests`. In-memory `ListDatabases` takes < 0.1 ms (40 µs at 10 dbs, 85 µs at 100 dbs); `RenameDatabase` takes ~4 ms at 10 dbs and ~4 ms at 100 dbs; `SwapDatabases` takes ~3 ms; `DropDatabase` takes ~5 ms. Cost remains flat O(1) independent of database count._
- [x] **T13** Keep [`engineering/box-layout.md`](../../../engineering/box-layout.md) § 2 current with what this spec
  decides. _Done: `crystal.lock` and exclusive lock rules added to data directory layout in § 2._

## Addendum 2026-10-09: review corrections

A review of the first implementation found that some tasks above were marked done without meeting their
requirement. They stay ticked as a record of what was claimed; the tasks below re-do them. Design addendum
A1–A8 records the decisions.

**Re-opened, with the reason:**
- **T4** (R8.1): the allocator moved only in the final registry batch, so a crashed create left a folder that
  startup recovery reported and the next create reused. Product databases' catalogues were also left half-written
  (the format key was written before the catalogue was initialised).
- **T6** (R5.6, R5.7): store dispose closed databases without waiting for calls in progress; drop waited by spinning
  and was held up for as long as any batch or snapshot lived; a snapshot taken on a dropped database leaked.
- **T7** (R7.1): every batch and snapshot took the store gate.
- **T9** (R9.2): the limit was refused with `InvalidOperationException`, not a typed exception.
- **T10** (R10): reset refused the directories its own messages sent it to, reported a missing directory as
  "already open", and deleted any folder starting `db-`.
- **T11** (R13.2, R13.5): no process was killed; the "crash" tests planted folders by hand, at ids no crash
  produces. The cross-process test held the file from PowerShell, not from a second Crystal.
- **T12** (R13.7): measured 10 and 100 databases, not 1 000; no rename of a database holding a million keys; times
  came from a unit test, not the benchmark harness.
- Also: `node.id` written before the lock and regenerated when missing (R3.4); a second open returned a new
  handle that rename did not update (R5.7, design § 5); the registry's alias table was written but never read.

**Tasks:**
- [x] **T14** Opening order and node identity (R1.3, R3, design A1). _Done: `StoreFormat.CheckBeforeLock` / `StampOrVerify` / `VerifyExisting`; tests in `StoreIdentityAndRegistryTests` (node.id missing or unreadable refused, interrupted creation keeps node.id, refused directory left unwritten, 20 rounds of two concurrent opens agree on one node.id). Mutations M9, M10._
- [x] **T15** Create persists the allocator first; catalogue initialised by the catalogue (R8.1, design A2). _Done: allocator written alone before the folder; the folder at the next id is refused before the allocator moves (A9). Process kills at all four create steps in `CrossProcessAndCrashSimulationTests`. Mutations M1, M13, M19, M22._
- [x] **T16** Slots: one handle per open database, lock-free per-call scopes, closing that waits and releases
  snapshots; drop and store dispose through it (R5.6, R5.7, R7.1, design A3). _Done: `DatabaseSlot`; drop and dispose through it. `DatabaseInterleavingTests`. Mutations M2–M8, M18, M25._
- [x] **T17** Registry: standard catalogue in `_crystal`, alias table cross-checked on load, refusals (design A4). _Done: `_crystal` opens with the standard catalogue; alias table, records, allocator cross-checked; damaged entries refused by name; empty registry never started over database folders (A9). Mutations M11, M11b, M12, M23, M26, M27._
- [x] **T18** Rename and swap: memory before `name` files; failure-safe generation (design A5). _Done; process kill after the registry batch of a rename (name file repaired at reopen). Mutation M15._
- [x] **T19** `DatabaseLimitException`; refusal messages name a way forward that works (R9.2, R10.3, design A6). _Done: `DatabaseLimitException`; refusals name a way forward that applies. Mutation M20._
- [x] **T20** Reset per design A7. _Done: identifies the store before and under the lock, deletes `_crystal` first (A9), exact `db-xxxxxxxx` folders only; process kill mid-reset. Mutations M16, M17, M24._
- [x] **T21** A crash host program; process kills at every step of create and drop, and of rename; a second
  Crystal process for the lock (R13.2, R13.5). _Done: `tests/SystemD.Crystal.CrashHost`, a separate program on the built package (also the playbook's real run). Kills at create ×4, drop ×3, rename ×1, reset ×1; a second Crystal process for the lock, both directions. Not covered: a kill during swap, and a kill part-way through deleting a dropped folder (startup recovery handles a partial folder the same way as a whole one)._
- [x] **T22** Interleaving tests with hooks: drop waits for a call in progress and refuses new ones; open racing
  drop in both orders; data operations proceed while the store gate is held; dispose waits for a call in progress. _Done: hook-driven, on dedicated threads (no thread-pool dependence), releasing held calls in `finally` so a broken guard fails instead of hanging. The 300 ms negative checks cannot fail falsely: a correct store never reaches the held step. Not covered: a stress test of the enter/close race itself; the protocol was reviewed instead (A3 remarks)._
- [x] **T23** Cost (R13.7) shown structurally: the registry writes the same number of entries per operation at 10
  and at 1 000 databases, and a rename of a database holding a million keys writes nothing to that database.
  Timings belong to the benchmark harness, which does not exist yet. _Done: registry entries per operation are equal at 10 and 1 000 databases (create 4, list 0, rename 4, swap 5, drop 3); a rename of a database holding a million keys writes nothing to it. Shows registry writes, not in-memory work. The test takes about a minute (1 000 real instances)._
- [x] **T24** Mutation checks on every guard; an independent review. _Done: 28 mutations, every one caught by a named test, the build rebuilt from clean after each (list below). Independent review by a fresh agent: it found two data-loss paths in these corrections, now fixed (design A9) and mutation-checked. Final: `dotnet build -c Release` 0 warnings; 139 unit and 8 architecture tests pass. Also fixed outside 005: the architecture test's IL walker lacked `ldarga.s`, `leave`, `leave.s`, `unaligned.` and `no.`, and passed by luck (mutation M21 shows it still catches a RocksDB call outside Storage)._

**Mutation checks (T24)**, each a guard broken on purpose, each failing a named test:
M1 allocator only in the final batch · M2 a call not refused once closing · M3 drop not waiting for calls · M4
dispose not waiting for calls · M5 dispose not waiting for drops · M6 data operations taking the store gate · M7 a
new handle per open · M8 a handle from another store accepted · M9 a missing node.id regenerated · M10 no read-only
check before the lock · M11 alias table not cross-checked · M11b an alias naming no live database · M12 a record
above the allocator · M13 recovery keeping passed unregistered folders · M14 recovery keeping dropped folders · M15
stale name files kept · M16 reset deleting any `db-` folder · M17 reset not identifying the store · M18 closing
without releasing snapshots · M19 the format key before the catalogue · M20 an untyped limit · M21 a RocksDB call
outside Storage · M22 the stranger-folder check after the allocator · M23 an empty registry over database folders ·
M24 reset deleting the registry last · M25 a committed drop throwing over its folder · M26 an unparsable record
escaping untyped · M27 record names unchecked.

**Waiting on the maintainer:** design A8 (the public API beyond § 6) is proposed, not approved.
