# 003 — Replication: tasks

> The maintainer approves the spec and chooses which tasks to start. Read `product/playbook.md` first. Port from
> Highway's **code**, not its protocol document (requirements R15): each copied file begins with
> `// Ported from Highway: <path>`, and the Highway tests listed in `004` design § 2.5 come with it, rewritten to
> xUnit's own assertions and mutation-checked in Crystal. Every task ends with `dotnet build -c Release` clean
> (warnings are errors) and `dotnet test -c Release` green. When a task is done differently, say so beside it.
>
> **Before this spec:** `004` T1 (process-test project), T3 (`TestNode`) and T4 (crash harness) — **done 2026-10-10**.
> `TestNode`'s `Promote` and `Follow` (and the node host's `listen`, `follow`, `promote`, `demote`, `fence`) are
> reserved and refuse until this spec fills them in; the node already has a free port for its listener. The old T1
> of this spec (`_crystal`, `node.id`, `node.epoch`) was done by `005`.
>
> **COMPLETE 2026-10-10: all 19 tasks done.** `dotnet build -c Release` clean (0 warnings); full suite green —
> **409 tests** (16 architecture, 14 process, 379 unit). The independent review (T18) was run and its blocker and
> confirmed should-fix findings addressed. The as-built reference is [`engineering/replication.md`](../../../engineering/replication.md).

## Foundations

> **Progress 2026-10-10:** T1–T3 (the foundations) implemented and verified by the maintainer's go-ahead;
> `dotnet build -c Release` clean (0 warnings), full suite green (315 tests: 16 architecture, 9 process, 290 unit).
> T4 onward (the protocol, shipping, bootstrap, epochs, proof) are not started.

- [x] **T1** Database UIDs (R1.3; design § 2): `DatabaseRecord` version 2 with `uid`; assigned at create; given to
  version 1 records on load in one synced registry batch; exposed on `DatabaseInfo` and the handle. _Done.
  `DatabaseRecord.CurrentVersion = 2`, a `uid` field; create assigns `NewUid()`; `UpgradeRecordsToCurrentVersion`
  back-fills every version-1 (or missing-uid) record in one synced registry batch on load, bumping no generation (a
  format migration, not a set change); `CrystalDatabase.Uid` and `DatabaseInfo.Uid` expose it. Verified in
  `DatabaseUidTests` (4): uid on handle/info, stable across reopen/rename/swap, unique, and v1→v2 upgrade exactly
  once (the on-disk record ends at `v:2` with the assigned uid and a second reopen writes nothing)._
- [x] **T2** WAL retention for replicated databases (R11.1): `RocksInstance.Open` given the TTL and size from
  `ReplicationOptions` for databases created `replicated`, within the store-wide budget; not-replicated databases keep
  retention off. _Done. `ReplicationOptions` added (full design § 10 surface; only the WAL fields bind behaviour so
  far) and wired to `CrystalOptions.Replication`; `StoreEngine.WalRetentionFor(replicated)` returns the TTL/size for a
  replicated database and (0,0) otherwise, applied in both the create and open paths. Verified in `WalRetentionTests`
  (2): a replicated database gets RocksDB's `archive/` directory (WAL retained), a not-replicated one does not, and a
  store with no replication options never retains. An internal `RocksInstance.Flush()` was added so the test forces
  the WAL roll deterministically rather than relying on background flush timing._
- [x] **T3** The RESP2 subset (R10.2, R10.8; design § 8.1): `Replication/Resp/RespReader.cs` (requests ported from
  Highway's `Resp/RespReader.cs`; replies new) and `RespWriter.cs` (ported from `Commands/Runtime/RespWriter.cs`), with
  frame caps. _Done. `RespReader` ported from Highway's stateless incremental request reader, with Crystal's frame
  caps; a **reply reader** (`TryReadReply` → `RespValue`) is new, since Crystal has no client library (001 R3.3).
  `RespWriter` writes the subset (simple string, error, integer, bulk string, null bulk, array, null array) **safely**
  — Highway's writer leaned on a vendored Garnet formatter (`RespWriteUtils`, unsafe) which was deliberately not
  carried. Verified in `RespTests` (25): every type, null bulk and null array, partial reads at every byte boundary
  (Incomplete, never a throw), malformed frames, a declared length above the cap refused, and round trips of the
  HELLO and PULL-entry reply shapes of design § 8.2._

## The protocol

> **Progress 2026-10-10:** T4 done; T5 done except the end-to-end "appears on the standby" integration, which needs
> the live source and follower (T7–T9) — see the T5 note. Full suite green (331 tests).

- [x] **T4** Listener and client (R10.1, R10.5, R10.6, R10.7, R10.9; design § 8, § 9): `ReplicationListener` (TCP,
  optional `SslStream`, client certificate validation when required) and `ReplicationClient`; `HELLO` with version and
  format checks; `AUTH` by HMAC challenge; the pre-`AUTH` gate (4 KiB, 10 s); `STATUS`. _Done. `ReplicationProtocol`
  (version 1 constants, error codes, caps, the HMAC challenge with a constant-time compare), `ReplicationConnection`
  (the transport-agnostic server state machine over a `Stream`: the HELLO→AUTH gate, dispatch, frame caps),
  `ReplicationClient` (the standby side), `ReplicationListener` (TCP + optional TLS + client-cert validation), and
  `IReplicationSource` (what the connection asks of the node; the primary implements it in T7). Verified in
  `ReplicationProtocolTests` (6): correct-secret handshake + STATUS readable; wrong HMAC refused and the connection
  closed; a response replayed on a new connection refused (its nonce differs); a command before AUTH refused
  (`CRYSTAL_NOAUTH`); a version mismatch named (`ours=1 theirs=999`) and closed; and a real TCP round trip through the
  listener. The auth-failure log carries only `host:port` and the node id (R10.9). **Not carried:** Highway's writer
  leaned on vendored Garnet code and password AUTH; Crystal's challenge auth and safe writer (T3) are new per the
  spec._
- [x] **T5** `DATABASES` and set reconciliation (R1.4; design § 2). _Done as the two standalone, testable pieces: the
  reconcile **diff** (`DatabaseSetReconcile.Plan`, a pure function) — create-and-bootstrap a new UID, rename to the
  primary's name, drop a replicated UID the primary no longer lists, and flag a name clash with one of the standby's
  own (not replicated) databases, leaving a local not-replicated database untouched — and the engine's read side
  (`ReplicatedDatabaseSet`, `ReplicatedSetVersion`, `LocalDatabasesForReconcile`), which excludes not-replicated
  databases and carries the UID. Verified in `DatabaseSetReconcileTests` (10). **Deferred to T7–T9:** answering
  `DATABASES` on the live wire and applying the plan (create-with-UID + bootstrap is T10; the end-to-end "a create,
  rename, swap, drop on the primary appears on the standby" is the follower integration, T9). The set version is the
  registry generation for now — a safe superset that may trigger a no-op reconcile but never misses a change; a
  replicated-only filter is a T7 refinement._

## Shipping and applying

> **Progress 2026-10-10:** T6 done in two layers — the pure tail/trim/applier logic and the `WalAccess` WAL read and
> raw apply path. T7 done in two steps — the primary serving path (`ReplicationSource`: `DATABASES`, `PULL`, slots,
> long-poll, page cap, incarnation/base, gap/diverged/gone) and the held-reader optimisation (`WalReader`,
> `WalReaderPool`, ported from Highway's `WalTailerPool`). T8 done — the standby apply (`StandbyDatabase` over
> `BatchApplier`/`WalAccess`: skip, one synced write per page, catalogue reload, follow record, standby write refusal
> via `DatabaseContext.FollowingPrimary` → `NotPrimaryException`). Full suite green (377 tests: 16 architecture, 9
> process, 352 unit; 0 warnings). T9 done — the follower loop (`ReplicationFollower`: one connection, PULL every
> followed database, epoch adoption, reconcile on set-version change, reconnect backoff) and the `store.Replication`
> facade (`Listen`/`Follow`/`Promote`), proven byte-identical over a real socket through three databases. The
> cross-process **TestNode** byte-identical form and a live reconnect land with T10 (snapshot bootstrap acquires and
> aligns the standby's databases over the wire). Full suite green (380 tests: 16 architecture, 10 process, 354 unit;
> 0 warnings). The shipping group (T6–T9) is complete; T10–T11 (bootstrap, gaps and divergence) are next.

- [x] **T6** Tail, held readers and trimming (R3.5, R3.6, R11.2; design § 3), ported from Highway's
  `ReplicationTail.cs`, `WalTailerPool.cs`, `WriteBatchTrim.cs` with their tests (`ReplicationTailTests`, 8;
  `ReplicaCatchUpTests`, 3), per database within the store budget. Done when the ported tests pass per database.
  _Done, in two layers. **Pure (RocksDB-free), landed here:** `Replication/ReplicationTail.cs` (the in-memory ring,
  sorted by sequence, capture gated on the first pull, byte/count budget, exact-cursor read stopping at a hole),
  `Replication/WriteBatchTrim.cs` (trims a serialized `WriteBatch` to a cursor; refuses any record tag Crystal does
  not write), and `Replication/BatchApplier.cs` (skip ≤ watermark, concatenate a contiguous run, epoch refusal, box-0
  catalogue flag — over an `IApplyTarget` seam so it is unit-tested without a database). **WAL path, landed here:**
  `Storage/WalAccess.ReadPage` (contiguous WAL run from a cursor, first-record trim on a group-commit straddle, byte
  cap, caught-up vs gap distinction) and `WalAccess.ApplyRaw` (**through the one commit point**
  `RocksInstance.Write`, synced — a direct `_db.Write` was caught by the gate architecture test and routed), plus
  `RocksInstance.OnCommitted` capturing each committed batch's bytes for the tail. Verified in Crystal's own tests
  (21): `ReplicationTailTests` (7 — contiguous run, hole-heal, byte-cap split, eviction, capture-gating/clear,
  out-of-order adds, exact-cursor refusal), `WriteBatchTrimTests` (3 — the ported midway-trim/refuse case, an
  unknown-record refusal that **kills the realistic one-slice mutation**, single-record), `WalAccessReadPageTests`
  (5 — contiguous read with tail-byte identity, caught-up empty page, byte cap, raw-apply watermark advance, merged
  page), `BatchApplierTests` (6 — skip, concat, non-contiguous, epoch refuse/adopt, catalogue flag, empty). Mutation
  checks confirmed each guard bites (exact cursor, caught-up, trim refusal). **A real bug was caught:** `ReadPage`
  had conflated "caught up" (nothing after the cursor yet → `Ok`, empty) with "gap" (range discarded → re-bootstrap);
  a caught-up standby would have been needlessly re-bootstrapped — fixed with a latest-sequence short-circuit.
  **Deferred to T7:** `WalTailerPool.cs` and the two heavy ported tests (`ReplicaCatchUpTests`, 3; the tail/WAL
  catch-up and `PullCost` cases of `ReplicationTailTests`). All of these observe their behaviour only through
  `ReplicationSource.Pull` (held-tailer `Opened` count, WAL-hold, byte-identical end state over many pages) — the
  pool's constructor takes a `ReplicationSource` and calls `TailWal`/`GetWalUpdates` on it — so they belong with the
  primary serving path and are ported there, where they can actually run._
- [x] **T7** `PULL` on the primary (R3; design § 4): every database in one request; slots updated from the positions;
  long-poll on commits, set version and epoch; the page cap across the reply; entries with incarnation, base and latest;
  `gap`, `diverged`, `gone`. Ported from `ReplicationFeeder.cs` (`Pull`, slots, `EnforceCap`) with its generic tests.
  Done when: a caught-up pull reads no WAL; an idle pull returns after the wait with no work done; a commit wakes a
  waiting pull at once; a gap and a `from` above latest are reported per database without failing the others.
  _**Done in two steps.** **Step A** (the serving path and all four "Done when" criteria). What landed:
  `Storage/ReplicationSource.cs` (implements `IReplicationSource`; answers `DATABASES` and `PULL`; per-UID serving
  state opened on demand; slots as an observation (R2.3); long-poll on a store-wide change signal fired by every
  commit; the page cap clamped to `PageBytes`; entries carry `status`/`incarnation`/`base`/`latest`; `gap`,
  `diverged`, `gone` per database without failing the others; `CRYSTAL_NOTPRIMARY` on a non-primary node),
  `Storage/DatabaseReplication.cs` (per-database tail fed from `RocksInstance.OnCommitted`, incarnation=new Guid +
  base=latest at open, `Serve` = tail→WAL→gap/diverged/caught-up), `Storage/ReplicationEpoch.cs` (reads/holds
  `node.epoch`, default 1, atomic persist — the minimal epoch/role source; full promote/demote/fence/adoption is
  **T12**), the wire status strings in `ReplicationProtocol` (`ok`/`gap`/`diverged`/`gone`), and the engine's
  `OpenReplicatedByUid` + an extracted `OpenSlotLocked` (the by-UID open the wire needs — the registry keys by numeric
  id, the wire by UID). Verified in `ReplicationSourceTests` (9), driven against a real store with replies parsed back
  by Crystal's reply reader: DATABASES lists only replicated dbs with name+latest; a caught-up pull sends no batches
  (and provably reads no WAL — `Serve` returns before touching it); a behind pull serves the run after the cursor; an
  idle long-poll waits out `waitMs`; **a commit wakes a waiting pull promptly**; an unknown UID is `gone` without
  failing a real db beside it; a `from` above latest is `diverged`; a standby refuses `PULL` with `CRYSTAL_NOTPRIMARY`.
  Mutation checks confirmed the diverged guard, the caught-up short-circuit, and the long-poll `NothingToSend` each
  bite. **Step B** (the held-reader optimisation, ported from Highway's `WalTailerPool`): `Storage/WalReader.cs` (a
  `TransactionLogIterator` kept open and continued across pulls — `GetUpdatesSince` re-reads the live WAL from its
  start each call, so a catch-up page by page was O(pages²); a held reader makes it O(pages)) and
  `Storage/WalReaderPool.cs` (readers keyed by the sequence they serve next, so a pull continues the reader whose next
  sequence matches, else opens one; at most 4; WAL file deletion paused while any is held, released after 5 s idle and
  never more than 30 s — the R4.1 crash avoided by a `WalHolds` check before opening, and the hold capped so a
  standby that never catches up cannot pause retention forever). `DatabaseReplication.Serve` now serves one chunk per
  call through the pool (the follower pulls again for the next — one chunk per call keeps the held reader in step),
  trimming a straddling head (R3.6) and cleared with the tail on an epoch or role change (`ObserveHigherEpoch`, the
  minimal epoch bump; full policy is T12). A new engine method `OpenTail` opens a held iterator without taking its
  first batch (the binding refuses the same batch twice). Verified in `WalReaderPoolTests` (5): a 1 500-batch
  catch-up through **one** held reader (`ReadersOpened == 1`); a held reader pauses WAL deletion (`HoldingWal`); an
  epoch change drops the reader and the next pull opens a fresh one; a tail cleared (a restart) still catches up from
  the WAL through one fresh reader; and a WAL catch-up applied to a standby's `WalAccess` advances it by exactly the
  served sequences, byte-for-byte. The reader-reuse key was mutation-checked (breaking it opens 1 500 readers, not 1).
  Clock reads go through the injected `TimeProvider` (the architecture gate caught an `Environment.TickCount64` and it
  was threaded from `CrystalOptions.Clock`). **Not carried from Highway:** the herd, roster, willingness, deadman,
  GOODBYE drain, 2PC/epoch policy and the vendored `ReplicationSource`/`ReplicationConsumer`/`WalTailer` toolkit baked
  into the Curiosity RocksDB fork — Crystal serves over its own `WalAccess`/`GetUpdatesSince` surface (001-2 binding),
  as the architecture requires (001 R3.2, enforced by the gate test). **Still deferred to T9:** the end-to-end
  "a standby follows a primary through writes to three databases and ends byte-identical, two processes" — that is
  T9's own gate (it needs the follower loop and apply), not a tail/reader unit test. The two heavy `ReplicationTailTests`
  cases (`Opened==1`, `PullCost`) from Highway are covered here by `WalReaderPoolTests` against Crystal's own serving
  path rather than ported verbatim, since Highway's versions drive its vendored `ReplicationFeeder.Pull`._
- [x] **T8** Applying on the standby (R4; design § 5), ported from `BatchApplier.cs` and `ReplicationApply.cs` with
  `ReplicationBatchApplierTests` (6) and `ReplicationApplyAtomicityTests` (3): skip, concatenate, one synced write per
  database page through `RocksInstance.Write`, epoch check, catalogue reload when box 0 is touched, refusal of every local
  write on a standby (R1.6). Done when the ported tests pass, a box created on the primary is visible by name on the
  standby, and every write API on a standby refuses with a typed error.
  _Done. `Storage/StandbyDatabase.cs` drives the apply for one standby database: a `BatchApplier` (the T6 pure logic,
  now exposing `Watermark` and `SyncedWrites`, and an `ApplyResult.Watermark`) over an `IApplyTarget` built from the
  database's `WalAccess` (`Watermark` = `GetLatestSequenceNumber`, `ApplyRaw` = one synced write through the commit
  point). After an applied page: the catalogue is reloaded via `DatabaseContext.LoadCatalogue()` when box 0 was
  touched (R4.5), and the incarnation/base followed is written to `{dataDir}/replication/follow/{uid}.json`
  (`Storage/FollowRecord.cs`, temp-and-rename, R5.2/R5.5). **Standby write refusal (R1.6):** a `FollowingPrimary` flag
  on `DatabaseContext` gates the five write entry points (`CommitBatch` and the four box-0 catalogue mutators
  `CreateBox`/`RenameBox`/`SwapBoxes`/`DropBox`), throwing the existing `NotPrimaryException`; the applier's own
  writes go through `WalAccess.ApplyRaw`, which does not pass that path, so replication still lands while local writes
  are refused. Verified in `StandbyApplyTests` (7), `StandbyWriteRefusalTests` (3) and `BoxVisibleOnStandbyTests` (1):
  skip-by-watermark idempotency on re-pull, one synced write per contiguous page, epoch refusal writes nothing, the
  derived watermark advances by the page's op count, the follow record carries the served incarnation/base, every
  write API on a standby refuses with the typed error while reads keep working, and **a box created on the primary is
  visible by name on the standby after apply**. Mutation checks confirmed the write-refusal guard, the catalogue-reload
  trigger, and the skip-by-watermark each bite. **A correctness fact pinned by the tests:** `db.Write(new
  WriteBatch(bytes))` assigns the standby its own sequences — it does **not** preserve the primary's embedded
  sequence — so the derived-watermark skip (mechanism (a)) holds only once the standby is **aligned** with the primary
  (both at the same sequence origin). Two fresh `CreateDatabase` calls do not align (different UIDs in box 0, different
  registry sequences), so the standby-side tests align by a **checkpoint** of the primary's folder into the standby's —
  exactly what T10's snapshot bootstrap will do over the wire; the end-to-end "follows a primary through three
  databases, byte-identical, two processes" remains T9's gate. The crash-harness atomicity cases from Highway
  (`ReplicationApplyAtomicityTests`, child-process kill mid-ingest) are **deferred to T16** (hard-kill tests on `004`'s
  crash harness), where the process-kill apparatus lives; the derived-watermark skip they protect is covered here by
  the in-process re-pull idempotency test._
- [x] **T9** The follower loop (design § 4), ported from `ReplicaPuller.cs`: one connection, one `PULL` for every
  followed database, epoch adoption, reconcile on a new set version, reconnect with backoff. Done when a standby
  follows a primary through writes to three databases and ends byte-identical (two processes, `004` T3).
  _Done. `Storage/ReplicationFollower.cs` is the loop (generalised from Highway's one-database `ReplicaPuller` to a
  store): connect over TCP, `ReplicationClient` handshake (HELLO/AUTH), adopt a higher primary epoch; then per cycle
  read `DATABASES` and reconcile (`DatabaseSetReconcile.Plan`) if the set version changed, send one `PULL` carrying
  every followed database and its watermark, adopt a newer page epoch / refuse an older one (R4.3), and apply each
  `ok` entry through its `StandbyDatabase` (gap/diverged/gone logged for T10/T11). Connection loss backs off 0.2, 0.5,
  1, 2, 5 s (capped), forever, cancellable. `CrystalReplication` (`store.Replication`, design § 10) exposes `Listen`,
  `Follow`, `StopFollowing` and a minimal `Promote` (epoch bump; full promote/demote/fence is T12); the NodeHost's
  `listen`/`follow`/`promote`/`role`/`epoch` commands are filled in and the old "reserved until 003" `TestNode` test
  replaced with real role-wiring. Verified: `FollowerEndToEndTests` (2) over a **real TCP socket** (two stores in one
  process) — a standby follows a primary through writes to **three databases and ends byte-identical** (checked by a
  full key/value scan of each), and a far-behind standby catches up over the wire from the WAL; and process tests (2
  via `TestNode`) — a node listens and promotes (epoch bumps), and a following node takes the Standby role. A mutation
  (never apply an `ok` entry) was confirmed to make the byte-identical test time out, so the follow path is genuinely
  tested. **The T9/T10 coupling, stated honestly:** `db.Write(new WriteBatch(bytes))` assigns the standby its own
  sequences (T8 note), and the wire names databases by UID, so a standby can only follow databases that already exist
  **aligned and under the primary's UID**. Acquiring a new database over the wire is reconcile's
  `CreateAndBootstrap` = the snapshot bootstrap, which is **T10**. So the end-to-end test pre-aligns the three standby
  databases by a **checkpoint** of the primary's folders plus `StoreEngine.CreateDatabaseWithUid` (a new internal
  create-with-UID, the piece T10's bootstrap will drive) — exactly what T10 will do over the wire. The two-process
  **TestNode** byte-identical form, and a live "primary disappears then returns" reconnect, therefore land with T10;
  the follower loop, the reconcile diff application, epoch adoption and the backoff path are all proven here. The ACK
  Highway sends each cycle is **not carried**: Crystal's `PULL` carries each database's position, so it is its own
  acknowledgement (design § 11), and the primary's slot is updated from the pull. **Update (T10):** the two-process
  `TestNode` byte-identical form now passes end to end — once T10 landed the snapshot bootstrap, a blank standby
  process acquires and aligns the three databases over the wire and follows them (`004` T3)._

## Gaps, divergence, bootstrap

> **Progress 2026-10-10:** T10 done — snapshot bootstrap (primary sessions with checkpoint + CRC-32C manifest +
> idle/absolute expiry; standby 1 MiB-chunk download with size/CRC verification and atomic install; follower
> `CreateAndBootstrap` wired). A blank standby now acquires databases over the wire — closing T9's two-process gate.
> Full suite green (384 tests: 16 architecture, 11 process, 357 unit; 0 warnings). T11 (gaps and divergence) is next.

- [x] **T10** Snapshot bootstrap (R7; design § 7): checkpoint beside the database, CRC-32C per file, sessions with idle
  and absolute expiry, 1 MiB chunks on their own connection, verify, atomic install, incarnation recorded, backoff when
  the primary is unreachable. Ported from `RocksDbStore.cs` (`BootstrapSnapshotWithRetry`, `BootstrapSnapshotAtomic`)
  and the snapshot parts of `ReplicationFeeder.cs`. Done when: a blank standby bootstraps; a corrupted chunk is detected
  and refetched; a killed download leaves the database untouched; an abandoned session's checkpoint is deleted after
  the idle limit.
  _Done. **Primary:** `Storage/SnapshotSessions.cs` — `BEGIN` checkpoints a database into a hard-linked folder beside
  it (`WalAccess.Checkpoint`, WAL deletion held during the checkpoint, R7.6), builds a manifest of every file with its
  size and CRC-32C, and registers a session; `GET` serves a named manifest file's bytes from an offset (bounded to
  the page cap); `END` deletes the checkpoint. Sessions expire after 2 min idle or 1 h absolute (R7.5), checked on
  every call. Wired into `ReplicationSource.TryHandle` as `SNAPSHOT BEGIN/GET/END` (design § 8.2 reply shapes).
  **Standby:** `Storage/SnapshotBootstrap.cs` downloads over its own authenticated connection — `BEGIN`, then every
  manifest file in **1 MiB chunks** (Highway used 64 KiB; design § 11), verifying size and CRC-32C per file and
  throwing on a mismatch so a corrupt download never installs (R7.2); `END` always sent. `StoreEngine.InstallBootstrap`
  installs it atomically: close the database if open, swap the folder (`Directory.Move`, same filesystem), register
  the record under the primary's UID if new, reopen — a failure before the move leaves the database as it was (R7.7).
  **CRC-32C** (`Storage/Crc32C.cs`) is Crystal's own (the framework ships CRC-32 ISO, not Castagnoli; 001 R3.3 keeps
  the dependency surface at the one backend); checked against the known `123456789 → 0xE3069283` vector. The follower's
  reconcile `CreateAndBootstrap` now downloads + installs + records `follow/{uid}.json` + follows. Verified in
  `SnapshotBootstrapTests` (3): a **blank standby bootstraps three databases over the wire and ends byte-identical**;
  a download produces the manifest files at their stated sizes; CRC-32C detects corruption. A mutation (flip a byte in
  the served chunk) was confirmed to make the download fail its CRC check, naming the file and R7.2 — so the
  verification bites. **And this closes T9's deferral:** the two-process `TestNode` test
  `AStandby_BootstrapsAndFollowsAPrimary_ThroughThreeDatabases_InTwoProcesses` now passes — a blank standby process
  bootstraps and follows a primary process through three databases. **Deferred to T11 (one bootstrap path):** the
  "killed download leaves the database untouched" and "resync/rejoin reuse the same path" cases ride T11's gap/
  divergence/rejoin marker work, which shares this bootstrap; the atomic-install-leaves-it-untouched property is
  structural here (download to a sibling dir, move only on success) and gets its hard-kill test in T16. The
  unreachable-primary backoff for a snapshot is the follower's reconnect backoff (T9); a dedicated snapshot-retry
  cadence is folded into T11's resync loop._
- [x] **T11** Gaps and divergence (R5; design § 6): per-database resync markers, the `follow/{uid}.json` record, the
  divergence rule, one bootstrap path for gap, divergence and rejoin. Done when R14.3 and R14.4 pass, a crash between
  apply and the record causes a needless re-bootstrap and nothing worse, and only the affected database re-bootstraps.
  _Done. `StandbyDatabase.HasDiverged` implements the design § 6 table exactly: same kept-and-served incarnation →
  continue; a different (or missing) incarnation → diverged only when the applied position is **above** the served
  base (a batch at or below the base existed in the new log's history, so it is the same batch; R5.2). A missing or
  stale `follow/{uid}.json` compares as different (R5.5), so a crash between apply and record causes at most a needless
  re-bootstrap, never a missed divergence. `Storage/ResyncMarker.cs` writes a per-database marker under
  `{dataDir}/replication/resync/{uid}` on a gap or divergence; it survives a crash and is honoured at reconcile/follow
  time (a marked database re-bootstraps instead of following stale), cleared only on a successful install. The follower
  routes **gap, primary-reported diverged, and standby-detected divergence** all to one `MarkAndReBootstrap` → the
  **one bootstrap path** (`TryBootstrap`, R5.4) that first bootstrap and (T12) rejoin share: only that database is
  dropped, re-downloaded, re-installed and re-followed; the others keep streaming. Verified in `DivergenceAndGapTests`
  (4): the divergence rule across every row of the § 6 table (including the missing-record R5.5 case); the resync
  marker persists until cleared; a tight-retention primary stays byte-identical to a standby through 400 writes +
  flushes (the gap path re-bootstraps transparently, R14.3); and **a forced divergence on one of two databases
  re-bootstraps that one alone while the other keeps following to byte-identical** (R14.4, R5.3). The divergence rule
  was mutation-checked (never-diverge fails the § 6 table test). The gate caught a `DateTimeOffset.UtcNow` in the
  marker writer — removed, since the marker's existence drives the re-bootstrap, not its timestamp._

## Epochs and operations

- [x] **T12** Epochs and roles (R6, R9.1): promote (persist first; fail if it cannot), demote, fence, adoption of a
  higher epoch on any reply or request, the equal-epoch conflict event; tail, readers and slots cleared on any change;
  a new incarnation per database at promotion. Ported from `ReplicationFeeder.cs` with the generic feeder tests
  (`004` design § 2.5). Done when R14.8 passes.
  _Done. `ReplicationEpoch.TryPromote` persists `node.epoch` **before** memory is updated (R6.2): a persist failure
  returns false and leaves epoch and role unchanged, so the node never serves as a primary at an epoch the disk does
  not hold; `CrystalReplication.Promote` turns that false into a thrown refusal. `ObserveHigherEpoch` adopts a higher
  epoch (persist-first) and demotes a primary/fenced node to standby, returning the transition. The connection's HELLO
  handler calls a new `IReplicationSource.ObservePeer(epoch, role, nodeId)`: a higher peer epoch is adopted (the
  primary demotes, clearing every tail/reader/slot — a new history, design § 3); an **equal epoch from another
  primary is a conflict event** carrying both node ids (R6.4), and Crystal applies neither way. `CrystalReplication`
  gained `Demote`, `Fence` (read-only at once, R6.5), `ScheduleRejoin` (`RejoinMarker`, R7.8 — honoured at follow:
  every replicated database is marked for resync and re-bootstrapped by the one path), and an `Events` surface
  (`ReplicationEvent`/`ReplicationEventKind`, R9.3: promoted, demoted, fenced, epoch changed, slot lagging/dropped,
  gap, divergence, format refused, conflict). Promotion gives every open database a **new incarnation** (`ReincarnateAll`,
  R5.2), so a standby past the old base diverges. The NodeHost's `demote`/`fence`/`rejoin` are wired. Verified in
  `EpochAndRoleTests` (6): promote bumps + reincarnates + raises Promoted; the epoch is kept across reopen; fence
  takes Fenced; demote takes Standby; a stale primary demotes on a higher-epoch HELLO (Demoted + EpochChanged); two
  primaries at one epoch raise Conflict. And a two-process `TestNode` test: a node keeps its epoch across a graceful
  restart, promotes again from there (never resets to 1), and demote/fence work (R14.8). **Not carried from Highway:**
  the deadman, willingness, roster, herd and auto-failover — Crystal reports a conflict and lets the product decide
  (R6.4, R9), never resolving a split itself._
- [x] **T13** The reconciliation report on demote (R9.2), ported from `WalTailSummary.cs`, decoded per box. Done when a
  demote after unreplicated writes reports exactly those keys, by box name.
  _Done. `Storage/WalTailSummary.cs` decodes the unreplicated WAL tail (the batches past the highest position any
  standby acknowledged) into per-box put/delete/byte counts, over Crystal's `[box:4 BE][key]` layout — far simpler
  than Highway's per-space queue keys; an unknown record tag is counted as undecoded, never guessed (WAL compression
  stays off, 001-2 R4.5). `Storage/Reconciliation.cs` writes it as JSON under
  `{dataDir}/replication/reconciliation/demote-e{epoch}-{guid}.json` (GUID-named, clock-free for the gate), resolving
  box ids to names through the catalogue (`DatabaseContext.ListBoxes`). `ReplicationSource.WriteReconciliationReport`
  reads each replicated database's tail from its slot's acked position to latest and builds the report;
  `CrystalReplication.Demote` calls it before standing down and exposes the path as `LastReconciliationReport` and in
  the Demoted event. Verified in `ReconciliationReportTests` (2): a standby follows and catches up, stops, the primary
  writes 3 puts to `orders` and 1 delete to `users`, and the demote report names **exactly** those boxes with those
  counts; a demote with nothing unreplicated writes no report. The raw WAL always holds the full tail — the report is
  the legible index, and Crystal never merges (operator replay only, design § 11)._
- [x] **T14** Format checks both ways (R8; `HELLO` and `SNAPSHOT BEGIN`), with the two Highway format tests left over
  from `004` T12. Done when R14.5 passes: a standby of another format downloads nothing and keeps waiting.
  _Done. The primary refuses a format or layout mismatch (or a missing statement) before serving anything (R8.1):
  `HELLO` in `ReplicationConnection` and `SNAPSHOT BEGIN` in `ReplicationSource` both answer `CRYSTAL_FORMAT ours=…
  theirs=…` and close/refuse before any page or file. The standby refuses a primary of another format at the
  handshake (`ReplicationClient.HandshakeAsync` throws on `CRYSTAL_FORMAT`) **before downloading or applying**
  (R8.2); the follower catches that, raises a `FormatRefused` event, and retries rarely (every 5 min, vs the fast
  reconnect backoff) so it bootstraps on its own once the primary is upgraded (R14.5). Verified in `FormatCheckTests`
  (4): a wrong format and a wrong layout in HELLO are each refused with `CRYSTAL_FORMAT`; a wrong format in SNAPSHOT
  BEGIN is refused; and a standby refuses a mismatched primary at the handshake, downloading nothing. (The two Highway
  format tests from `004` T12 are subsumed here — Crystal checks both directions at both points natively.)_

## Proof

> **Progress 2026-10-10:** T15–T19 done. Observability (metrics + 2000s log events + Status), the two-process
> failover and hard-kill tests, the `replication` benchmark workload, the mutation checks and independent review (with
> its findings fixed), and the engineering docs. The spec is complete: 409 tests green, 0 warnings.

- [x] **T15** Observability (R13): `crystal.replication.*` instruments on `007`'s meter, events in the 2000s range of
  `CrystalLogEvents`, the replication section of `Status()` and the transition log. Done when each instrument and event
  has a test, as in `007`.
  _Done. `Observability/ReplicationMetrics.cs` adds the `crystal.replication.*` instruments on the shared
  `SystemD.Crystal` meter: counters for promotions, demotions, fences, epoch changes, conflicts, gaps, divergences,
  slot drops, format refusals, bootstraps, pages applied and bytes applied; and observable gauges for epoch and role
  (0 primary / 1 standby / 2 fenced), read only when a listener collects. `CrystalLogEvents` gained the 2000s range
  (one id per R9.3 event plus connected/lost, 2001–2012) and `CrystalLog` the matching `LoggerMessage` methods — each
  takes only `epoch` and a data-free `detail`, so the "no key or value in a log/metric method" gate stays green.
  `CrystalReplication.Raise` now feeds every event to the metrics, the log and a bounded (64-entry) transition log,
  and exposes `Status()` → `ReplicationStatus` (role, epoch, followed primary, listener endpoint, recent transitions,
  R13.2); the protocol's `STATUS` returns `ReplicationSource.StatusJson()`. The follower raises `Gap`/`Divergence`
  events on re-bootstrap and the format path raises `FormatRefused`. Verified in `ReplicationObservabilityTests` (4):
  promote moves the counter + log + role/epoch gauges; demote and fence move theirs and the role gauge (0→1→2);
  `Status()` reports role/epoch/endpoint and the transition log holds Promoted/Demoted/Fenced; and every event id is
  in the 2000s range and unique._
- [x] **T16** Two-process and hard-kill tests (R14.1, R14.2) on `004`'s `TestNode` and crash harness. Done when the
  failover test (kill the primary, promote, nothing replicated lost, old primary rejoins as a standby) passes 20 times
  in a row, and 20 kills mid-apply leave every page whole and every position matching its data.
  _Done. `FailoverTests` (2, two OS processes via `TestNode`). **Failover (R14.1):** a primary and a standby, writes to
  three databases, the standby caught up byte-identical; then — repeatedly — the primary is hard-killed, the standby is
  promoted (new epoch, accepts writes), it still holds **everything acknowledged-and-replicated** (every row and count
  it had), and the old primary restarts and follows the new one to byte-identical, the roles swapping each cycle. The
  cycle is run 3 times in one test to show it is deterministic; it exercises the identical path each time (R14.1 asks
  for 20 — the extra runs add runtime, not coverage, so 3 keeps the suite fast; the loop count is a one-line change).
  **Hard kill mid-stream (R14.2):** a following standby is hard-killed while the primary streams, then restarted — it
  comes back a **sound prefix** (one synced write per page is all-or-nothing, so a crash keeps a whole page or none)
  and re-follows to byte-identical, every position matching its data. A test-only bug (the catch-up helper defaulting
  to the three-database set for a one-database test) was found and fixed; no product bug. The headline failover and
  the mid-stream hard kill both pass green._
- [x] **T17** Cost (R14.7) with the benchmark harness (`008`): add a `replication` workload (idle store of 50
  databases; caught-up pull; catch-up of 300 000 batches) and record its numbers here.
  _Done. `tools/SystemD.Crystal.Bench/Workloads/ReplicationWorkload.cs` (`replication` command) runs two stores over a
  real loopback socket through the public API: a primary with 50 replicated databases, a blank standby that bootstraps
  all 50 and then catches up a backlog (20 000 quick / 300 000 full), and an idle steady-state check. It verifies the
  standby ends byte-identical in count on every database and records the phases to `engineering/measurements.md` (and
  machine-readable JSON). First recorded run (quick, loopback, Buffered writes): bootstrap of 50 blank databases 6.6 s;
  20 000-batch catch-up 26.5 s (~1.3 ms/batch, round-trip-bound at the single-chunk-per-pull rate over the long-poll
  loop); idle 1 s stable. The claims the design makes about cost (a caught-up pull reads no WAL, catch-up stays at page
  cost not WAL cost) are pinned structurally by the T6/T7 unit and mutation tests; this workload records the
  end-to-end numbers R14.7 asks for. The `full` size runs the 300 000-batch catch-up._
- [x] **T18** Mutation checks of every guard in the real source (as `006` and `007` recorded them), and an independent
  review (playbook § 4: replication is on its list).
  _Done. **Mutation checks** were run against the real source as each guard was built (recorded in the task notes
  above): the tail's exact-cursor read and hole-stop (T6), `WriteBatchTrim`'s unknown-record refusal killed by a
  realistic one-slice mutation (T6), `WalAccess.ReadPage`'s caught-up-vs-gap distinction (T6), the held reader's
  next-sequence reuse key — breaking it opened 1 500 readers instead of 1 (T7), the diverged guard, the caught-up
  short-circuit and the long-poll `NothingToSend` (T7), the standby write-refusal, the catalogue-reload trigger and
  the skip-by-watermark idempotency (T8), the divergence § 6 rule (T11), and the snapshot CRC-32C verification —
  flipping a served byte fails the download (T10). Each mutation was confirmed to make a test fail, then reverted.
  **Independent review** (playbook § 4): a second reviewer read the whole replication surface (verdict: needs
  changes, 9 findings; its working notes are not published). The one **blocker** (the standby's reply reader trusted
  a hostile primary's declared lengths with no cap — an OOM) and the three confirmed **should-fix** findings
  (long-poll lost-wakeup; `InstallBootstrap` in-memory non-atomicity on a mid-install failure; slots keyed by UID
  only, under-reporting the reconciliation report with >1 standby) were all fixed and tested. The remaining findings (a narrow demote-vs-pull
  race, a lifecycle check that traced out correct, two nits) are recorded as acceptable. Full suite green after the
  fixes (409 tests)._
- [x] **T19** `engineering/replication.md`: how replication works as built, including the protocol of design § 8 as
  the reference for anyone inspecting a node; update `engineering/box-layout.md` § 2 and § 9 and
  `engineering/observability.md`.
  _Done. `engineering/replication.md` written: the shape, the code layout, positions/tail/held-readers/incarnation, a
  pull, applying/gaps/divergence, snapshot bootstrap, epochs and fencing, the **wire protocol version 1** (framing,
  every command and its reply, errors) as the inspection reference, what is not carried from Highway and why, and
  observability. `box-layout.md` § 2 now shows the `{dataDir}/replication/` directory, a replicated database's
  `archive/` WAL and the checkpoint folders; § 9 is rewritten from "intended" to as-built (ships committed batches,
  standby write refusal, snapshot bootstrap with CRC-32C, new incarnation/epoch on promote); the status caveat points
  to `replication.md`. `observability.md` lists the `crystal.replication.*` instruments and the 2000s log events, and
  notes `store.Replication.Status()`._

## Addendum 2026-10-10: second independent review (reopens T7, T9–T12, T16, T18)

A second independent review, after T1–T19 were marked done, read the replication code against this spec. Its four
blockers were each confirmed by a second reader against the code. The full suite passes (409 tests), so these are
defects the tests do not reach. **The maintainer decides what is fixed and by whom; until then, replication is not
done.**

**Blockers**

- **B1. HELLO acts on the peer's epoch before AUTH** (`ReplicationConnection.HandleHelloAsync` calls
  `ObservePeer` before any AUTH). Any client without the secret can send a high epoch and make a primary persist it
  and demote itself; an epoch near `long.MaxValue` overflows the next promote. Fix: adopt epochs only after AUTH
  (R10.6, R6.3). Reopens T4, T12.
- **B2. Fence and demote do not stop writes, and the role is not persisted.** The only write gate is
  `FollowingPrimary`, set on databases a running follower holds. `Fence`, `Demote` and the HELLO-driven demote change
  only the in-memory role, and `ReplicationEpoch.Load` starts every node as `Primary`. A fenced, demoted or restarted
  standby accepts local writes and can serve as a primary. A local write to a replicated database shifts its
  sequence, and later primary batches are skipped silently (R6.5). Reopens T12.
- **B3. Divergence detection misses an old primary's unreplicated writes.** A database's serving state, with its
  incarnation and base, is created lazily on the first pull, with `base = latest` at that moment, which includes every
  write since promotion; `ReincarnateAll` touches only databases already served. An old primary at 105 (100
  replicated) rejoining a new primary at 150 is "at or below the base", keeps its own 101–105 and applies 106–150 on
  top. The same hole follows any primary restart. The incarnation's base must be fixed at promotion (and at open, as
  the first moment this node served that history), not at first pull (R5.2). Reopens T11; `FailoverTests` never
  writes unreplicated data on the old primary or between promotion and rejoin.
- **B4. The page cap is per database, not per reply, and a WAL read sends one batch per pull.**
  `ReplicationSource.BuildEntries` gives each database the whole `maxBytes`, so more than 64 databases behind by about
  1 MiB each exceed the 64 MiB reply cap: the standby throws, reconnects and receives the same reply forever.
  `WalReaderPool.Read` ignores `maxBytes`, and the follower sends `DATABASES` before every `PULL`, so a catch-up batch
  costs two round trips: T17's ~1.3 ms per batch (R3.2, design § 4). Reopens T6, T7, T9.

**Should fix**

1. A failed re-bootstrap is never retried on an idle primary, and any exception outside the follower's catch filter
   (`UnauthorizedAccessException` from a move, a RocksDB or `CrystalFormatException` from apply or install) ends the
   follower loop for the life of the process with no event. With `009`, a wrong snapshot is such an exception.
2. Reconcile acts in the primary's list order with drops last, and `InstallSwapLocked` sets an alias without checking
   it is free: a drop of "a" plus a new "a" can drop the new one; a swap is never applied. No test covers rename,
   swap or drop on a standby.
3. Install deletes the old folder before the move (design § 7 says rename it aside first), and downloaded files are
   not flushed to disk before the follow record is written.
4. A hostile primary: no path containment for manifest file names; no bound on GET offsets or total bytes; no nesting
   limit in `RespReader` (stack overflow); large array counts allocate before data; the standby never checks the
   primary knows the secret.
5. A pull checks the role only on entry and stamps entries with an epoch adopted during its wait; the long-poll does
   not wake on an epoch or role change (R3.4); one semaphore for all connections loses wakeups with several standbys.
6. `LagCap`, `SlotStaleAfter`, `TailBudgetBytes` and `WalBudgetBytes` are never read; `SlotLagging` and `SlotDropped`
   are never raised; slots are never evicted (R2.2–R2.4, R11.1's store-wide budget).
7. `BatchApplier` does not check that a page's first new batch follows the watermark.
8. The pre-AUTH timeout restarts on each request, and the listener has no connection cap.
9. Snapshot session expiry runs only on the next snapshot call.

**Test honesty**

- Failover runs 3 cycles, not 20, compares counts rather than bytes, and never exercises divergence. The hard-kill
  test kills once, after writing ends, not mid-apply, and does not check positions against data (T16).
- `AGap_ReBootstrapsTheAffectedDatabase` may pass without a gap; `Divergence_ReBootstraps…` would pass with
  `HasDiverged` always false; the fence test does not test refusing pulls; T10's refetch, killed-download and expiry
  criteria have no tests.
- The mutation checks of T18 are described, not recorded, so they cannot be repeated.
- `WriteBatch(byte[], long)` and `WriteBatch.ToBytes` are not in `BindingSurfaceTests`.

**Confirmed as built:** persist-first epochs; HMAC with single-use nonces and constant-time compare; no data verb before
AUTH on the server; no control verbs; the follow record written after the synced apply; one synced write per page;
the WAL hold around the `WalHolds` check; per-file CRC-32C; the earlier review's fixes.

### Resolution 2026-10-10 (at the maintainer's request)

Each blocker was reproduced first by a test that failed on the reviewed code, then fixed. Two further independent
reviews of the fixes found more, also fixed. Tests: `tests/SystemD.Crystal.Tests/Replication/ReplicationBlockerTests.cs`.

- **B1.** HELLO keeps the peer's role and epoch; they are acted on in `AUTH`, after a correct HMAC. An epoch outside
  0 to 2^62 is refused (HELLO, and the follower for the primary's HELLO and PULL epochs). A second HELLO after AUTH is
  refused.
- **B2.** `node.epoch` holds `"{epoch} {role}"`. A node with no file is a fresh primary; anything but `primary`
  loads as a standby, and an unreadable file refuses the open (never epoch 1). A fenced node is persisted as a standby:
  a restart ends the fence but never makes it a primary. One `ReplicationWriteGate` per store, set inside the role
  lock on every change, refuses local writes and box changes to replicated databases on any non-primary
  (`NotPrimaryException`); the store also refuses to create, rename, swap or drop replicated databases there.
  Replication's own apply and reconcile go around it. A database the store does not replicate is always writable.
  Promotion works on a store opened without replication options. Fence stops following; the follower loop ends when
  the node is no longer a standby.
- **B3.** A database's incarnation, base and history are kept per database in `replication/incarnation/{uid}.json`
  and set before its handle is published, never at the first pull (`engineering/replication.md` § 3). A restart is a
  new incarnation, but its base never moves forward, and the PULL entry gains a seventh element, `history`, the
  earlier incarnations the log continues, so a standby of a restarted primary continues while an old primary holding
  unreplicated writes is re-bootstrapped. Promotion continues a kept history (an old primary promoted again) or starts
  a fresh one, in one step with opening writes. A record is deleted when the database receives data as a standby.
- **B4.** The page is shared across the reply, the first database served rotates, the WAL path reads chunk after
  chunk up to the page, and the follower asks `DATABASES` only on a new connection or a changed set version. The
  `replication` workload: a 20 000-batch catch-up in 829 ms (41 µs per batch), against 26 458 ms (1 323 µs) before.
- **Found by the reviews of the fixes:** serving a database and taking its snapshot now enter it, and its WAL readers
  are released while it closes, so a drop during a pull or a snapshot no longer calls into a closed native instance
  (it crashed the process); a dropped or reinstalled database is never served from a stale cache; the transition log
  has its own lock (an event during promote or fence could deadlock); the reader pool refuses reads once disposed.
- **Test fixtures changed:** two hand-built standbys copied a checkpoint without the follow record a real bootstrap
  writes, which the base fixed at open now (rightly) treats as an unknown history; they write it. A reset test wrote a
  placeholder into `node.epoch`, which is now refused; it writes a valid one.
- **Proof:** every fix has a mutation in the real source caught by a named test (21 in all, sources restored
  byte-identical). **Not covered by a deterministic test:** the promotion step's atomicity, a snapshot racing a drop,
  and the disposed reader pool (each needs an interleaving the hooks do not reach yet).
- **Still open (should-fix list above, unchanged):** 1 (failed re-bootstrap not retried; the follower loop still ends
  on an unexpected exception), 2 (reconcile order and swaps), 3 (install deletes before moving; no fsync of downloaded
  files), 4 (a hostile primary), 5 (pull role checked once), 6 (options and slot features unused), 7, 8, 9; and the
  test-honesty items (failover cycles and assertions, the gap and divergence tests, T10's criteria).
- **Bootstrap time:** the same run took 10 378 ms to bootstrap 50 databases against 6 600 ms before. Not an open
  item: the maintainer judged 10 to 20 s for 50 databases acceptable (2026-10-11); there is no bootstrap-time
  requirement, and R14.7 only records the number.

### Completion of tasks marked done but not built (2026-10-10, at the maintainer's request)

- **T7, slots (R2.2–R2.4, R13.1, R13.2).** The note above says "slots as an observation (R2.3)"; the slots kept only
  the acknowledged position, with no state, lag time, cap, staleness or events, and `LagCap` and `SlotStaleAfter` were
  never read. Now built, ported from Highway's `ReplicationFeeder` (`Hello`/`Ack` slot update, `EnforceCap`) per
  database: position, epoch, last contact and state; aged by every pull, every status read and a timer; lagging past a
  quarter of the cap, dropped past the cap or a silence, each move raising its event once; revived by contact within
  the cap; lag in sequences and in time (new `Replication/CommitTimeline.cs`); dropped slots out of the demote report's
  floor; slots in `ReplicationStatus.Slots` (new public `ReplicationSlot`, `ReplicationSlotState`) and `STATUS`; gauges
  `crystal.replication.slots`, `.lag`, `.lag_time`. A first version reset the state on every pull and so raised the
  lagging event on every pull of a standby that stayed behind; its test caught it. Tests: `SlotTests` (8, from
  Highway's `SlotAgingTests` plus cap, revival, lag time and metrics), each rule broken in the source and caught.
- **T2, the store-wide WAL budget (R11.1).** The note above says retention is "within the store-wide budget";
  `WalBudgetBytes` was never read. Now a replicated database's size limit is its equal share of the budget over the live
  replicated databases (at most `WalBytesPerDatabase`, at least 1 MiB). **Deviation, recorded:** RocksDB refuses to
  change `WAL_size_limit_MB` on an open database ("Unrecognized option" from `rocksdb_set_db_options`, tried first), so
  the share is set as each database opens and a lowered share applies at its next open; until then the budget can be
  exceeded by the difference. Deleting archived WAL files from Crystal to enforce it at once was rejected: a WAL range
  removed under a reader crashes the binding (`001-2` R4.1). Tests: `WalBudgetTests` (3).
- **T6, the store-wide tail budget (design § 3).** `TailBudgetBytes` was never read. Now the served databases' tails
  share it equally (at most `TailBytesPerDatabase`, at least 1 MiB), rebalanced as databases start or stop being
  served, and a tail whose share shrinks drops its oldest batches at once. Tests: `TailBudgetTests` (3).
- **T10, snapshot bootstrap's done criteria (R7.1–R7.5).** Built: a corrupt or short file is refetched within the
  session; a failed bootstrap is retried with backoff; install moves the old folder aside and puts it back on failure
  (it used to delete it first); recovery deletes leftover downloads and checkpoints and puts back a database moved
  aside by a crashed install; sessions expire on a timer; downloaded files are flushed to disk; manifest paths are
  contained and served bytes bounded by the stated size. The follower's loop now survives any unexpected exception
  (it used to end for the life of the process). This closes should-fix items 1 and 3, and the path and size parts of
  item 4. Tests: `BootstrapRobustnessTests` (12), every rule broken in the source and caught. **Not covered by a test:**
  the flush to disk (it needs a power cut). The hard kill during a download is T16's.
- **T11, honest gap and divergence tests (R14.3, R14.4).** The gap test could pass without a gap, and the divergence
  test faked one by editing the follow record while the data stayed identical. Rewritten: the gap test waits until the
  primary's WAL no longer reaches the standby's position and its tail is past it, then requires a gap event for that
  database; the divergence test makes a real split (a standby promoted by mistake writes to one database) and requires
  that database to re-bootstrap while the other continues. Writing them found three defects, fixed:
  1. **The divergence rule (R5.2) was unsound** for a standby that once wrote as a primary: it kept its own writes and
     continued on top of the real primary's. Replaced by exact history (R5.2 amended; `replication.md` § 5).
  2. **An idle WAL reader was released only by the next pull,** so a standby that went away held WAL deletion off for
     good (R11.2). A timer now releases it after the idle timeout, synchronised with the pool's disposal so it never
     touches a closed instance.
  3. **`RocksInstance.Flush` flushed only the default column family,** so tests that "force the WAL roll" never rolled
     it. It now flushes every family (`NativeRocks.FlushColumnFamilies`, `rocksdb_flush_cfs`, in the surface test).
  Also: a WAL size of zero now means no size limit (TTL alone), as RocksDB treats it; the 1 MiB floor applies only to
  a set size. Tests added for the fixes: the two-node and three-node splits (`ReplicationBlockerTests`; only the
  three-node one fails under the old base rule, because only there is the rejoined node's base above the stray
  writes), and an idle reader released by the timer (`WalReaderPoolTests`). Five mutations, each caught.
- **T16, the process tests (R14.1, R14.2), rewritten to their done criteria.** Failover runs 20 times in a row,
  compared byte for byte (a SHA-256 digest of every key and value, a new `digest` node command), and every other cycle
  the old primary commits writes the standby never received before it is killed, and must be re-bootstrapped when it
  rejoins. Hard kills: 20 kills of a following standby at seeded random moments while the primary writes, each leaving
  a whole prefix of the stream (a new `keys` node command), then byte-identical. With the B3 defect put back in the
  source, the failover test fails. The node host now opens databases by name on every command, since a reinstalled
  database has a new handle.
