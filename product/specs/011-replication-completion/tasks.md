# 011 — Replication completion: tasks

> Approved 2026-10-12; the open questions are decided (requirements.md). The maintainer chose Claude to execute every
> task, without a branch, and nothing is committed until the maintainer approves.
> Tasks are ordered by risk: T1–T6 fix correctness and safety and come first; T7–T12 close gaps against `003`.
>
> **For every task:**
> - Read the code at the lines design.md names before changing it; numbers are from commit `8b04637`.
> - Write the tests first where they can be, and see them fail on the unchanged code when they show a defect.
> - **Mutation checks** (playbook § 4): for each guard the task adds, break it in the real source, rebuild
>   (`dotnet build -c Release --no-incremental`), run the named test, see it fail, restore the file
>   **byte-identical** (compare MD5 before and after), and rebuild. Record each mutation and the test that caught it
>   beside the task. A mutation that no test catches means a missing test, not a finished task.
> - The whole suite passes with **0 warnings** before a task is marked done:
>   `dotnet build -c Release` then `dotnet test -c Release`. A test that fails only sometimes is a finding: root-cause
>   it (R12.3), never re-run it until it passes.
> - Update `engineering/replication.md` in the same task where the behaviour it describes changes.
> - **Never commit.** The maintainer reviews and approves every commit (playbook § 5).
> - Mark a task `- [x]` with a short italic note of what was done, and say so beside it where it was done
>   differently from what is written here (playbook § 1).

## Correctness and safety

- [x] **T1** AUTH cannot be reflected (R1.1–R1.6; design § 1). `ClientAnswer`/`ServerProof` with direction labels over
  both nonces and both ids; self-id refusal on the listener and the client; protocol version incremented; the four
  tests of design § 1; the TLS warning in `engineering/replication.md` § 8 and on `ReplicationOptions.Secret`. Done
  when the tests pass and mutations A1–A3 are each caught by the test design § 1 names.
  _Done 2026-10-12. Version 2; node ids limited to 1–256 UTF-8 bytes on both sides (they are length-prefixed in the
  MAC). Done differently from design § 1 in one respect: the design said either defence alone stops the review's
  attack. Testing showed only the **self-id refusal** does: with both self-id checks removed, the reflected answer and
  proof are computed over identical inputs and match, so the labels do not stop it (it is then a plain relay, which
  only TLS stops, R1.6). The labels remain as defence in depth: a proof can never be replayed as an answer. Tests:
  `AuthReflectionTests.TheReflectionAttack_Fails_AndTheNodeTakesNothing` (a real store, a relay in the test), and in
  `ReplicationProtocolTests` `AHelloStatingTheListenersOwnId_IsRefused`,
  `AHelloReplyStatingTheClientsOwnId_IsRefused_BeforeAuth`, `TheClientAnswerAndTheServerProof_DifferForTheSameInputs`,
  `AnAnswer_IsBoundToBothNoncesBothIdsAndTheSecret`, `AVersion1Peer_IsRefused`. Seven mutations, each caught, sources
  restored byte-identical: A1a listener self-id check off (its own test); A1b client self-id check off (its own
  test); A1c both off (the reflection test); A2 one label for both directions; A3 client nonce left out; A4 ids with no
  length prefix (`AnAnswer_…`; a first form that wrote a zero length was not caught, because the zero bytes still
  separate the ids, so the mutation was strengthened); A5 version left at 1._

- [x] **T2** No deadlock in role changes (R5.1, R5.5 first case; design § 5). Split `ReplicationWriteGate.Set` from the
  wait; `ReplicationEpoch.AfterRoleChanged` outside the lock; every path that shuts the gate drains before returning.
  Record the grep of `lock (_gate)` in `CrystalReplication.cs` showing no admitted write takes it. Done when the
  three-way test completes under a normal and a starved thread pool, the existing
  `B2_AFence_WaitsForAWriteAlreadyAdmitted_AndRefusesTheNext` still passes, and a mutation that waits inside the
  lock again is caught by the three-way test.
  _Done 2026-10-12. `ReplicationWriteGate.Set` no longer waits; `WaitForAdmittedWrites` is called from the new
  `ReplicationEpoch.AfterRoleChanged`, invoked after the lock in `SetRole`, `TryPromote` and `ObserveHigherEpoch`
  (in a `finally`, since the role changed in memory even when the disk write throws). The grep: `CrystalReplication.cs`
  has ten `lock (_gate)`; no write path (`DatabaseContext`, `CrystalStore`'s database changes, `StoreEngine`,
  `DatabaseReplication`, `ReplicationSource`) calls the facade, so draining while a demotion or fence holds that lock is
  safe. Two hooks added: `create.admitted` (`CrystalStore.CreateDatabase`) and `promote.incarnations`
  (`StoreEngine.BeginNewIncarnations`). Test `DeadlockTests.APromotion_AHigherEpoch_AndAnAdmittedCreate_AllComplete`
  (normal and starved pool); it disposes the store only when the three complete, so a real deadlock fails the test
  without hanging the run. Found while writing it: every facade member (`ListenerEndpoint`, `Status()`, …) blocks for
  the whole of a `Promote`, which holds the facade lock throughout; that is T3's concern (R5.2). Mutations: B1 drain
  back inside the epoch lock, caught by the three-way test; B2 no drain at all, caught by
  `B2_AFence_WaitsForAWriteAlreadyAdmitted_AndRefusesTheNext`._

- [x] **T3** Events on Crystal's own thread (R5.2–R5.4, R5.5 second case; design § 5). `ReplicationEventPump`; handler
  exceptions logged; disposal drains for up to 5 s; `Events` documented. Fix existing tests that assert an event right
  after a call, by waiting for it with a timeout. Done when the handler-calls-`Promote` test passes, a handler that
  throws does not stop the next event (test), and a mutation that delivers synchronously again is caught.
  _Done 2026-10-12. `ReplicationEventPump` (root namespace, internal): a dedicated background thread over a
  `BlockingCollection`, not a `Channel` read by a pool task, so delivery does not depend on the thread pool (the
  starved-pool runs pass). `Raise` keeps the metric, the log and the transition record synchronous and posts the
  delivery. Handler exceptions are logged as event 2014 (`CrystalLogEvents.ReplicationEventHandlerFailed`, public,
  listed for T15). Disposal completes the queue and joins for at most 5 s, never from the pump's own thread (a handler
  disposing the store). Eight existing assertions in five test classes read an event right after the call: each now
  waits for delivery through `EventDelivery.Drain` (`CrystalReplication.DrainEventsForTests`, internal), never a sleep;
  one in `EpochAndRoleTests` reads a `ReplicationSource`'s own callback, which stays synchronous, and was left. Tests
  in `DeadlockTests`: `AHandlerOfAFollowerEvent_CanCallStatusAndPromote` (a fake primary refusing the format makes the
  follower raise `FormatRefused`; normal and starved pool) and `AHandlerThatThrows_DoesNotStopTheNextEvent`. Mutations,
  each caught: E1 delivery on the raising thread again; E2 a throwing handler ends the pump. Not covered by a test: the
  5 s bound of disposal. The facade's members still wait for a running `Promote`/`Demote` (found in T2): no deadlock,
  but `Status()` is not instant during one; recorded in `engineering/replication.md`._
 

- [x] **T4** The follow record before the write (R2.1, R2.2, R2.5; design § 2). `BatchApplier` split into plan and
  commit; record before `ApplyRaw`; record deleted before an install's move. Hooks `standby.recorded` and
  `install.moved`; the two kill tests in `SystemD.Crystal.Process.Tests`. Done when the kill tests pass, every `003`
  divergence/gap test passes unchanged, and mutations (record after the write again; no delete before the install) are
  each caught.
  _Done 2026-10-12, differently in two respects. (1) Not a split of `BatchApplier` into plan and commit: `ApplyPage`
  takes an `accepted` callback, run once every check has passed and the page is assembled, before the write (also for a
  page with nothing new); `StandbyDatabase` writes the record there. (2) Not process-kill tests: each test stops the
  work at the exact step by throwing from a hook, which leaves the disk as a kill there would, and checks the
  consequence directly rather than through a third node: `FollowRecordOrderTests.ACrashRightAfterAPagesWrite_…` (a page
  of a new incarnation Y continuing X to p0, a crash right after its write, then a promotion: the new incarnation
  record claims X no further than p0) and `AnInstall_RemovesTheOldFollowRecord_BeforeMovingTheSnapshotIn` (a crash right
  after the move leaves no old record). The delete before an install is `FollowRecord.DeleteDurably` (throws on
  failure, syncs the directory) and lives in `StoreEngine.InstallSwapLocked`, so every caller gets it; the existing
  `FollowRecord.Delete` stays best effort, and its comment ("never trusted blindly") is not true of promotion, which is
  why the install no longer relies on it. Hooks `standby.recorded`, `standby.written` (`StandbyDatabase`, through the
  engine's hook) and `install.moved`. Every `003` divergence/gap test passes unchanged. Mutations, each caught: F1 the
  record after the write again; F2 no removal before the install._

- [x] **T5** Promotion with pending resync markers (R2.3, R2.4; design § 2). Fresh history for a divergence marker;
  `PromotedWithPendingResync` event. Done when a test promotes a node holding one database with a divergence marker
  and one with a gap marker, and a standby of the diverged one re-bootstraps while the standby of the gapped one
  continues; mutation (divergence marker ignored) caught.
  _Done 2026-10-12. `ResyncMarker.ReadReason` and `IsGap` (an unreadable marker reads as no gap: the worse case). The
  rule lives in `StoreEngine.NewHistory`, so it holds both at promotion and when such a database opens on a primary
  later. `BeginNewIncarnations` returns the marked databases; `Promote` raises `PromotedWithPendingResync` after
  `Promoted`, logged as event 2013 (`CrystalLogEvents.ReplicationPromotedWithPendingResync`, a public addition, listed
  for T15). Tests check the consequence on the new incarnation records rather than through standbys (the divergence
  rule itself is unit-tested in `DivergenceAndGapTests`): `PromotionWithResyncTests.APromotion_…` (divergence → no X;
  gap and unmarked → X continued; one event naming both marked) and `ADatabaseMarkedForADivergence_OpenedAfter…`.
  Mutations, each caught: P1 divergence marker ignored; P2 every marker taken as a divergence; P3 no event._

- [x] **T6** No use after close (R6.1–R6.3; design § 6). Enter the database in `ClearAllHistory`; `_expiryGate` in
  `WalReaderPool.Clear`. Done when the hook-driven test (clear paused, drop on another thread) passes and both
  mutations (no enter; no gate) are caught: by the test, or, if a use after close does not crash reliably, by an
  assertion in the test that the drop waited for the clear (record which).
  _Done 2026-10-12. `ReplicationSource.ClearAllHistory` enters each database (skipping one already closing);
  `WalReaderPool.Clear` runs under `_expiryGate` and returns once disposed (`Dispose` and `Clear` share `CloseIdle`).
  Hooks: `clear.entered` (engine hook) and `WalReaderPool.OnClearTaken` (through
  `DatabaseReplication.OnReadersClearTaken`). The two guards overlap (either alone stops the review's race), so each
  has its own test in `UseAfterCloseTests`, asserting that the close waited (a use after close does not crash
  reliably): `ADrop_WaitsForAnEpochChangeClearingTheDatabase` (U1, no enter) and
  `ClosingTheReaderPool_WaitsForAClearClosingItsReaders` (U2, clear outside the gate), each caught. Three test-design
  corrections on the way, recorded because they would have made a mutation look caught or hang the run: (1) a failing
  test must not dispose the store, since disposal waits on the paused thread (the first U1 run hung; the harness was
  stopped and the mutated source restored by hand, then checked); (2) a drop is not a precise signal for U2, because a
  reader still open keeps the WAL file in use and stalls the drop's folder delete anyway, so the test checks the pool's
  release (`DatabaseReplication.Detach`) instead; (3) the pool's disposal passes the same hook point, so the hook pauses
  only its first caller._

- [x] **T7** Crash ordering (R9.1–R9.3; design § 9). `DurableFile.SyncDirectory` and its calls; allocator before the
  move in a new-UID install; recovery of an empty folder below the allocator. Done when the kill test at each install
  step leaves a store that opens and creates, the sync count test passes, and mutations (allocator after the move;
  one sync removed) are each caught.
  _Done 2026-10-12. `Storage/DurableFile.cs`: `SyncDirectory` (libc `open`/`fsync`/`close` through `LibraryImport` on
  Linux and macOS, a no-op on Windows; the architecture tests have no rule on native imports, so none was changed) and
  `Replace` (move, then sync). Synced: `node.epoch`, follow records, incarnation records, each folder move of an
  install and of recovery, and also `StoreFormat`'s stamp and node id (the same defect, outside `003` but one line).
  Recovery already removed an unregistered folder below the allocator, so moving the allocator write before the move
  sufficed. Hooks `install.allocated` and `install.moved`; the crash host gained `install {dir} {name} {uid} {snapshot}
  {step}`. Tests in `InstallCrashTests`: the kill at `install.allocated`, `install.aside`, `install.moved` and a full
  run (the store opens, every `db-*` folder is a live database's, a create works), and the sync test. The snapshot
  must sit outside the target's data directory: recovery removes `bootstrap-*` leftovers there at open. Mutations, each
  caught: C1 allocator not advanced before the move (the kill test, at `install.moved`); C2 no sync after the move into
  place; C3 no sync in `DurableFile.Replace` (both by the sync test). Not covered: a real power loss, and the libc path
  itself on Linux (the suite runs on Windows here, where the sync is a no-op; the event fires on every platform)._

## Gaps against `003`

- [x] **T8** Bootstraps off the loop, resumable, epoch-checked (R3.1–R3.6; design § 3). Start/finish split,
  `MaxConcurrentBootstraps`, resume by file and offset, BEGIN epoch check, disposal of running downloads. Done when the
  two R3.6 tests pass, the whole `BootstrapRobustnessTests` and `ReconcileTests` pass, and mutations (download on the
  loop again; resume restarts from offset 0; BEGIN epoch unchecked) are each caught.
  _Done 2026-10-12. `ReplicationFollower.TryBootstrap` starts a download task (bounded by a semaphore of
  `MaxConcurrentBootstraps`, new option); `FinishBootstraps` (on the loop, each iteration) installs, records and
  follows, or schedules the retry; the loop's idle wait also wakes when a download ends. `SnapshotBootstrap.DownloadAsync`
  gained an overload over a connect function: a `ReplicationConnectionLostException` (new: the client's end of stream),
  `IOException` or `ObjectDisposedException` reconnects and continues from a per-file `FileProgress` (offset, bytes,
  CRC state, done); a session error still fails the download, which the follower retries from BEGIN. Disposal waits
  for cancelled downloads and removes their folders. Hooks `bootstrap.chunk` and `bootstrap.failed`. Two defects of my
  own found by the resume test: `Crc32C` is a struct, so the CRC kept in a property was a copy each time (a resumed
  file failed its check and was refetched whole); and a cut right after a file's last chunk fetched that chunk again.
  Done differently: the BEGIN epoch check is tested on its decision (`SnapshotBootstrap.JudgeEpoch`), not end to end,
  because a stale primary cannot be reached that way: the standby's HELLO states its higher epoch and the primary
  stands down before serving anything. Tests in `BootstrapRobustnessTests`: `ABootstrapInProgress_DoesNotStallTheOther…`
  (a download held by the hook while another database applies a new key), `ADownloadCutMidFile_ResumesAtItsOffset_…`
  (a 4 MB table file in 1 MiB chunks; one session; no (file, offset) served twice), `ASnapshotsEpoch_IsJudgedAsAPagesIs`
  (five cases). Mutations, each caught: G1 download on the loop; G2 resume from offset 0; G3 an older epoch accepted;
  G4 the CRC state copied again. Not covered: that the follower applies the verdict (it calls `JudgeEpoch` in
  `CheckSnapshotEpoch`), and the adoption of a newer epoch from BEGIN._

- [x] **T9** Size limits (R4.1–R4.4; design § 4). `oversize` status and re-bootstrap; injectable reply cap;
  `RequestCap` from `MaxOpenDatabases`; `frames_refused`. Done when the two R4.4 tests pass and mutations (oversize
  batch shipped; fixed 64 KiB request cap) are each caught.
  _Done 2026-10-12. The rule: a batch over `MaxShippableBatch` (half the 64 MiB reply cap) is reported `oversize`
  (new entry status), and the page is clamped to `MaxPageBytes` (a quarter), so a reply always fits. The limit is an
  internal setter on `ReplicationSource` for tests (not the client's reply cap, which stays a constant). The standby
  answers `oversize` with `MarkAndReBootstrap(uid, "gap (oversize batch)")`, a `Gap` event. The request cap is
  `ReplicationProtocol.RequestCapFor(MaxOpenDatabases)`, passed listener → connection. `frames_refused` is counted by the
  connection (a request over its cap, after AUTH) and by the client (a reply over its cap), and the connection logs
  it; the follower now receives the facade's `ReplicationMetrics` (also for T12). Tests: `SizeLimitTests` (an oversize
  batch re-bootstraps its database alone, the other applies; a PULL of 1 000 databases, about 75 KB, is answered).
  Mutations, each caught: H1 an oversize batch shipped; H2 the request cap fixed at 64 KiB. Not covered by a test: the
  `frames_refused` counter (T12 reads counters)._

- [x] **T10** Conflicts (R7.1–R7.4; design § 7). `PrimaryRecord` in `follow/primary.json`; real role in `HELLO`;
  `PeerNodeId`/`PeerEndpoint` on events. Rewrite `EpochAndRoleTests.TwoPrimariesAtTheSameEpoch_RaiseAConflictEvent`
  to use real nodes (R7.4). Done when the test passes, a restart between the two follows still raises the conflict,
  and mutations (no record compare; role always `standby`) are each caught.
  _Done 2026-10-12, differently in three respects. (1) The record is `replication/primary.json`, not
  `follow/primary.json`, so nothing that lists the per-database follow records meets it. (2) Only a peer that states
  role `primary` is compared: every node of a healthy cluster shares the epoch, so comparing any peer would report a
  conflict for a standby pointed at another standby (found before testing; a test now pins it). (3)
  `EpochAndRoleTests.TwoPrimariesAtTheSameEpoch_RaiseAConflictEvent` is kept, as the test of the primary-side check
  (which needs a peer stating `primary`, and the follower now states its real role, `_epoch.RoleName`, which is always
  `standby` while it follows); the real-node test is new: `ConflictTests.AStandby_PointedAtASecondPrimary…` (follow A,
  restart, follow B at the same epoch: one `Conflict` with B as `PeerNodeId` and both ids in the detail; nothing of B
  taken, nothing of A reconciled away) and `…AnotherStandbyAtTheSameEpoch_IsNoConflict` (it bounds a negative with a
  one-second wait). `ReplicationEvent.PeerNodeId`/`PeerEndpoint` (public, listed for T15) are set on conflicts and on
  the epoch change and demotion a peer causes; `IReplicationSource.ObservePeer` takes the peer's endpoint. Mutations,
  each caught: I1 no comparison; I2 the primary followed never recorded; I3 a standby taken for a primary._

- [x] **T11** The reconciliation report (R8.1–R8.4; design § 8). Done when the three tests pass and mutations (served
  databases only; `Math.Max` again; no report on a peer demotion) are each caught.
  _Done 2026-10-12. `WriteReconciliationReport` iterates `_engine.ReplicatedDatabaseSet()`, entering each database;
  the slot keeps the last acknowledged position; `ObservePeer` writes the report on a demotion (after the epoch lock
  and the drain) and hands its path to the facade (`OnReport`). Two more defects found: `Demote` wrote no report when
  the replication source had never been created (a primary that never listened), so it now creates it; and the
  existing `Demote_WithNothingUnreplicated_WritesNoReport` encoded the defect (it expected no report from a store no
  standby had pulled from, whose box creation nobody acknowledged). It is rewritten to mean what its name says (a
  standby holds every write; no report), with a comment saying why. Tests in `ReconciliationReportTests`:
  `ADatabaseNoStandbyPulled_IsInTheReport_FromPositionZero`, `ASlot_HoldsThePositionItsStandbyLastStated_NotTheHighest`,
  `ADemotionByAHigherEpochFromAPeer_WritesTheReportToo`. Mutations, each caught: J1 served databases only; J2 the
  highest position kept; J3 no report on a peer's demotion._

- [x] **T12** Observability (R10.1–R10.4; design § 10). Done when a test reads each counter (pages, bytes applied and
  shipped, bootstraps) after a replication run through `MeterListener`, a test captures log events 2011 and 2012
  through a test logger, a test sees `EpochChanged` on a standby that adopts a newer epoch, and `Status().Followed`
  reports a standby's position and lag.
  _Done 2026-10-12. `PageApplied` (bytes of the batches written, skipped ones excluded) and `Bootstrapped` are called
  by the follower; `bytes_shipped` is new, counted by the source per reply (`OnShipped`). Log events 2011/2012 are
  emitted by the follower around its pump (`ConnectAndPumpAsync` now delegates to `PumpAsync` inside a `try/finally`)
  and by the connection after AUTH and when it ends. The follower adopts epochs through `AdoptEpoch`, which raises
  `EpochChanged`. `PullEntry` keeps the entry's `latest`; the follower publishes positions after each reply (an
  immutable list, read lock-free), and the facade names them: `ReplicationStatus.Followed` (an init property, so the
  record's constructor is unchanged) of `FollowedDatabase(Uid, Name, Position, PrimaryLatest, Lag)`, and the gauge
  `crystal.replication.standby_lag` (worst, or per database when tagged). One test,
  `ReplicationObservabilityTests.AFollowingStandby_AndItsPrimary_…`, covers all four requirements between two stores
  with separate captures (the capture's `Collect` clears recorded counters, so counters are asserted first). Mutations,
  each caught: K1 pages not counted; K2 bytes shipped not counted; K3 no connected log; K4 no `EpochChanged` on
  adoption; K5 positions never published. Not covered by a test: the listener side's lost event is asserted, its
  connected event too, but not the bytes-applied value's exactness._

- [x] **T13** Smaller defects (R11.1–R11.11; design § 11). One test per item where it can be tested (R11.10 and R11.11
  are documentation and headers). Done when each test passes and each behavioural item has a mutation caught (R11.1,
  R11.2, R11.3, R11.4, R11.5, R11.6, R11.7, R11.8 (each of the three), R11.9).
  _Done 2026-10-13. R11.1: every registry generation change goes through `StoreEngine.PublishGeneration`, which raises
  `RegistryChanged`; the source signals its waiting pulls. R11.2: `WalAccess.EarliestSequence` (file deletion held);
  the old probe read a page from 0, which a purged WAL always calls a gap, so it reported latest+1 whatever the WAL
  held. R11.3: `_lastSetVersion` reset per connection. R11.4: `CrystalStore.Open` calls `ResumeScheduledRejoin` once the
  store is whole (the facade is built inside the store's constructor, too early to start a follower). R11.5:
  `ParseEndpoint` (ports 1–65535) runs in `Follow` and `ScheduleRejoin`. R11.6, done differently from design § 11 (a
  `LongPollMax` sleep): with nothing to follow, the follower sends a PULL naming no database, which long-polls on the
  primary and, with R11.1, wakes on a set change; `DATABASES` is asked only when the reply states a new set version.
  R11.7: checked accumulation over an unsigned magnitude (a first form refused `long.MinValue`, found by an existing
  `RespTests` case), and a length below -1 refused. R11.8: one `CancellationTokenSource` from accept covers the TLS
  handshake and AUTH (`ReplicationConnection` takes the token); `IsTrusted` adds the presented chain's certificates to
  `ExtraStore` and requires server or client authentication through `ApplicationPolicy`. R11.9: an unauthenticated count
  in the listener, at most a quarter of `MaxConnections` (at least one); `ConnectionLimitTests`' existing test now
  authenticates its connections, since a second unauthenticated one under a cap of 2 is refused by design. R11.10:
  `ReplicationConnection.ServeAsync` and `CrystalReplication` document where they block a thread; the role getter's
  doc was corrected in T2. R11.11: `// Ported from Highway:` headers on `ReplicationSource`, `DatabaseReplication`,
  `SnapshotSessions` (from `ReplicationFeeder.cs`), `ReplicationFollower` and `SnapshotBootstrap` (from
  `ReplicaPuller.cs`). Tests: `SmallerDefectTests` (R11.1, R11.3–R11.7), `DivergenceAndGapTests.AGap_ReportsTheFirst…`
  (R11.2; one assertion removed as racy by design: with a one-second TTL the archived WAL is purged twice a second),
  `TlsTests` (three for R11.8), `ConnectionLimitTests.UnauthenticatedConnections_…` (R11.9). Eleven mutations, M1–M11,
  each caught. Later (2026-10-13), `ALeafIssuedByAnIntermediate_…` failed once in a full run and was not reproduced in
  eleven runs after; the cause inferred is the platform's chain engine remembering the intermediate from the test's own
  earlier chain build, so the check without the presented chain could pass for the wrong reason. The negative check now
  runs first, before the intermediate has been seen; M9 is still caught._

- [x] **T14** Tests that prove what they claim (R12.1–R12.3; design § 12). _R12.3 root-caused 2026-10-12 (found while
  running T1's suite, where it failed again): `ReplicationConnection.ServeAsync` returns at the deadline without closing
  its stream; the listener closes the socket after it returns, but the test's in-memory stream had no such step, so a
  `HELLO` sent just as the deadline fired waited for a reply and an end of stream that never came, until the test's
  10 s guard. A tight repro hung 240 of 300 runs without the close and 0 of 300 with it. A defect of the test, not of
  Crystal: the test now closes the server's stream when serving ends, as the listener does; 20 of 20 runs passed. The
  starved-thread-pool runs remain to do with the rest of T14._
  _Completed 2026-10-13. R12.1: `AStalePrimary_IsDemotedByAHigherEpoch_OnHello` and
  `TwoPrimariesAtTheSameEpoch_RaiseAConflictEvent` go through the store's own listener with a real client and assert the
  store's role, epoch and events (the `LiveEpoch` helper, which loaded a second `ReplicationEpoch` from the file, is
  gone); mutation N1 (an adopted higher epoch does not demote) is caught by the first. R12.2: the node host gained
  `putbatch` and the hard-kill test commits five keys a batch; after each of the 20 kills the standby holds a whole
  number of batches, in order. Recorded honestly: a page torn between its batches is not visible from outside (a
  prefix of batches is still a prefix of the stream), so the test detects a torn batch, which is the only partial
  write an observer can see; no mutation of the apply path tears a batch (RocksDB writes one atomically), so no
  mutation was run for it. R12.3: the deadline test passed 20 of 20 runs with `DOTNET_ThreadPool_ForceMaxWorkerThreads=2`
  (that the runtime honoured the setting was not checked from inside the test)._ Done when the stale-primary test drives the
  store's own epoch, the hard-kill test checks multi-batch pages, and the deadline test's root cause is found, fixed,
  and written here; the deadline test passes 20 runs in a row under a starved thread pool.

- [x] **T19** Planned handover (R14.1–R14.7; design § 13a). The step-down flag in the write gate; `StepDownAsync`;
  the handover test under continuous writes with two nodes, the timeout and fence tests; the three mutations of R14.7.
  `engineering/replication.md` § 7 describes the handover. Done when the tests pass and each mutation is caught.
  _Done 2026-10-13. `ReplicationWriteGate` gained the step-down flag (`BeginStepDown`/`EndStepDown`, refusing as "a
  primary handing over to a standby") and `RefusedByRole`, which database open now uses to decide whether to begin a
  history, so a database opened during a handover still gets one (with `RefusedAs` it would not, and would keep none if
  the handover timed out). The refusal messages were reworded around the reason ("this node is a standby / fenced / a
  primary handing over…"). `CrystalReplication.StepDownAsync` and the record `SteppedDownDatabase` (public, listed for
  T15). A per-database hook `standby.applying:{uid}` in `StandbyDatabase`. Tests in `HandoverTests`: a handover under
  continuous writes to two databases (every acknowledged write on the new primary, the old primary rejoins with no gap
  or divergence, an empty report), a database held behind makes the step-down time out (the follower applies all
  databases on one loop, so the other database is made current and acknowledged before the hold, or the hold would
  stop its acknowledgement too and mask the mutation, which it first did), a timeout reopens writes, a fence ends it,
  and the refusals of R14.5. Mutations, each caught: Q1 serving stops at the start; Q2 only the first database
  checked; Q3 writes not reopened. The mutation harness now retries a write the editor's language server briefly
  holds (it failed twice mid-run; the sources were checked intact both times)._

## Closing

- [x] **T15** Public API approval (design § 13). List every public addition and behaviour change with its
  documentation for the maintainer; nothing is released before approval. Done when the maintainer approves.
  _Listed 2026-10-13, awaiting the maintainer's approval. Additions: `CrystalReplication.StepDownAsync(string
  standbyNodeId, TimeSpan timeout, CancellationToken)` returning `IReadOnlyList<SteppedDownDatabase>`; record
  `SteppedDownDatabase(Uid, Name, Position)`; `ReplicationStatus.Followed` (an init property) of record
  `FollowedDatabase(Uid, Name, Position, PrimaryLatest, Lag)`; `ReplicationEvent.PeerNodeId` and `.PeerEndpoint`;
  `ReplicationEventKind.PromotedWithPendingResync`; `ReplicationOptions.MaxConcurrentBootstraps` (default 2);
  `CrystalLogEvents.ReplicationPromotedWithPendingResync` (2013) and `ReplicationEventHandlerFailed` (2014); metrics
  `crystal.replication.bytes_shipped`, `standby_lag`, `frames_refused`. Behaviour changes: events are delivered on
  Crystal's own thread, after the call that raised them may have returned; `Promote` throws `IOException` when its
  epoch cannot be written; `Follow` and `ScheduleRejoin` throw `ArgumentException` for an endpoint that is not
  host:port; a scheduled rejoin follows its primary when the store next opens; the wire protocol is version 2 (a
  version-1 peer is refused, so every node of a cluster upgrades together); `NotPrimaryException` messages are
  reworded; at most a quarter of `MaxConnections` may be unauthenticated._ _Closed 2026-10-13: the maintainer
  ruled that before v1 the public API may change freely, without an approval step._

- [x] **T16** A real run (playbook § 4): two separate processes (`tests/SystemD.Crystal.NodeHost`) over TLS with mutual
  certificates: create databases, write, follow, kill the primary, promote the standby, rejoin the old primary,
  confirm the data with the node host's `digest` command. The node host has no TLS options today: add `--cert <pfx>`
  and `--trust <cer>` (and `--require-client-cert`) mapping to `ReplicationOptions`. Generate the certificates at run
  time (as `TlsTests` does); never commit a certificate or key (playbook § 6). Record the commands and results here.
  _Done 2026-10-13, as a process test so it runs with the suite: `RealRunTests.TwoProcessesOverMutualTls_…`. The node
  host gained `--cert <pfx>`, `--trust <cer>`, `--require-client-cert` and a `stepdown` command; `TestNode.Start` takes
  a `NodeTls`. The certificates (a CA and one per node, loopback names, server and client authentication) are made in a
  temporary folder for the run and deleted with it. The run, two node processes over mutual TLS: A creates `db` and
  commits 50 batches of 4 keys; B listens and follows A until their digests match; A steps down to B (1 database
  handed over, A is then a standby); B is promoted and A follows it; B commits 50 batches, A catches up; B is
  hard-killed; A stops following, is promoted and commits 50 batches; B restarts, schedules a rejoin of A, stops, and
  starts again, following A by itself; B catches up. Result: the digests are equal and A holds 600 keys (150 batches
  of 4); the run takes about 5 s. One correction while writing it: the node host reports the role as `Standby`, not
  `standby`._

- [x] **T17** Independent review (R13.1). A fresh reviewer (another agent, read-only) reviews the replication surface
  as the 2026-10-12 review did. Each finding is fixed with a test or recorded as decided by the maintainer. Done when
  no blocker or should-fix item remains open.
  _First round 2026-10-13 (a fresh agent, read-only): no blocker; six should-fix, ten minor. Fixed, each with a test
  and a mutation (V1–V6 caught):_
  - _S1: a demotion a peer caused wrote its report after clearing the slots, so the floor was always 0; the report is
    now written first (`ReconciliationReportTests.ADemotionByAPeer_ReportsFromWhatTheStandbyAcknowledged…`, V1)._
  - _S2: a slot could stand for what the standby no longer held. A standby's slots for databases its PULL no longer
    names are removed, a diverged standby's slot is set to 0, and a step-down needs the exact position
    (`HandoverTests.AStandbyThatStoppedNamingADatabase…`, V2; `ReconciliationReportTests.AStandbyToldItDiverged…`, V3).
    The exact-position check is defence in depth behind the 0 and has no mutation of its own._
  - _S3: a download finishing after the primary dropped or renamed its database was installed anyway; it is now checked
    against the primary's set as last parsed (discarded, or installed under the current name), and retries likewise
    (`BootstrapRobustnessTests.ADownloadOfADatabaseThePrimaryDrops…`, V4; hook `bootstrap.discarded`)._
  - _S4: the standby waited forever for a primary that vanished without closing the connection; every reply now has a
    deadline (`ReplicationOptions.RequestTimeout`, internal, 30 s; a PULL gets `LongPollMax` more), after which the
    connection counts as lost (`StandbyLivenessTests`, a proxy that falls silent, V5)._
  - _S5: the promotion warning omitted marked databases that were not open; closed live replicated databases are now
    included (`PromotionWithResyncTests.APromotion_NamesMarkedDatabasesThatAreNotOpen_Too`, V6)._
  - _S6: `RealRunTests` took two clock readings for certificate bounds and once made a leaf outlive its CA; one reading,
    and the leaf ends a day before its issuer._
  - _Minor, fixed: M1 resync markers written durably; M2 the starved pool made real (the old helper's `SetMaxThreads`
    below the processor count was refused unnoticed; now the minimum is 1 and all but two threads are held), B1 and E1
    re-run and caught under it; M5 headers on `ReplicationOptions`, `ReplicationMetrics`, `ResyncMarker`,
    `RejoinMarker`; M6 the event thread starts with the first event and a failed open disposes the facade; M7 a
    database the report cannot open is listed as unreadable instead of failing the demotion; M8 an incomplete
    reconcile is retried at most once per long poll; M9 the set-change test waits on a `pull.waiting` hook, not a
    sleep._
  - _Minor, for the maintainer: M3 (R2.5 tested by throwing from hooks, not by killing the process, with no third
    node: recorded in T4, needs explicit acceptance); M4 (the primary-side conflict check cannot fire through Crystal's
    own follower, which always states `standby` while it follows: R7.2 is met formally only); M10 (the libc directory
    sync has never run: run the suite once on Linux before release)._
  _Second round 2026-10-13 (a fresh agent, read-only, on the fixes): no blocker; two should-fix, both in the S4 fix,
  fixed with a test and a mutation each (W1, W2 caught):_
  - _N1: `SNAPSHOT BEGIN` fell under the 30 s reply deadline, but the primary reads the whole checkpoint for the CRCs
    before answering, minutes for a large database: such a bootstrap would never finish. BEGIN now has its own wait
    (`ReplicationProtocol.SnapshotBeginWait`, 30 minutes) (`BootstrapRobustnessTests.ASnapshotWhoseBeginIsSlow_…`, a
    `snapshot.manifest` hook holds the checksum pass 2.5 s against a 1 s deadline)._
  - _N2: over TLS, the connect and the handshake had no deadline; `ReplicationTls.ConnectAsync` takes the request
    timeout (`StandbyLivenessTests.AListenerThatAcceptsButNeverAnswersTls_…`)._
  - _Minor, fixed: M7 widened (a report that throws for any reason no longer stops the demotion's clearing and events);
    M8 refined (only the set version that failed waits; a new one is reconciled at once); the `UpdateSlot` comment no
    longer says the position only advances; `engineering/replication.md` says a re-bootstrapping standby's slot is
    gone from `Status().Slots`._
  - _Minor, left: the resync directory's first creation is not synced in its parent (only the marker's rename is);
    abandoned snapshot connections stay open until the download ends; M1, M6 and M7 have no tests of their own._
  - _Found by the full suite after these fixes and root-caused: `ReplicationObservabilityTests.AFollowingStandby_…`
    failed once (no event delivered). The standby's epoch is visible a moment before `EpochChanged` is posted, so the
    test's drain could run before anything was queued; it now waits for the event itself. The other drains were
    checked: each follows a call that posts its event before returning, or waits for the event._
  _Done: no blocker or should-fix item is open._

- [x] **T18** Close `003` (R13.2, R13.3). Mark `003` and `011` implemented with the date; add an addendum to `003`'s
  tasks.md pointing to `011`; `engineering/replication.md` describes Crystal as it is.
  _Done 2026-10-13: `003` and `011` marked implemented; `003`'s tasks.md has its addendum pointing here (2026-10-12);
  `engineering/replication.md` describes Crystal as it is. Open for the maintainer, recorded rather than blocking: R2.5
  is tested by throwing from hooks at the exact step, not by killing the process with a third node (playbook § 4 asks
  for kills); R7.2 is met formally only (Crystal's follower always states `standby`, so the primary-side conflict
  check cannot fire through it; the standby-side check is the working one); the libc directory sync has never run
  (run the suite once on Linux before a release)._
