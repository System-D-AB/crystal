# 011 — Replication completion: design

Line numbers are those of commit `8b04637` (2026-10-11), where every finding was checked. Read the code before
changing it: numbers move.

**Before starting, read:** `product/playbook.md` (binding), `engineering/replication.md` (how replication works now),
`product/specs/003-replication/requirements.md` and the addenda at the end of its `tasks.md` (every earlier fix and
why). The source is in `src/SystemD.Crystal/` (paths below are relative to it); the tests are in
`tests/SystemD.Crystal.Tests/Replication/` and `tests/SystemD.Crystal.Process.Tests/`.

## 1. AUTH (R1)

**Today.** `Replication/ReplicationProtocol.cs:87-100` `Response(secret, nonce, requesterNodeId)` is
HMAC-SHA256(secret, nonce ‖ nodeId). `Proof(...)` at `:109-110` **is** `Response(...)`. The client
(`Replication/ReplicationClient.cs:75-99`) answers `Response(secret, serverNonce, ownId)` and expects
`Proof(secret, ownNonce, PrimaryNodeId)`, taking `PrimaryNodeId` from the `HELLO` reply unchecked. The server
(`Replication/ReplicationConnection.cs:190-247`) checks `Response(secret, _nonce, _peerNodeId)` and answers
`Proof(secret, peerNonce, _source.NodeId)`. It accepts any `HELLO` node id (`:149`, `:221`).

**The attack.** A listening standby S and an attacker M in front of S's primary endpoint. M opens S's listener with
`HELLO nodeId=S`, gets nonce Ns, and replies to S's follower with `nodeId=S, nonce=Ns`. S's follower answers
`HMAC(Ns‖S)` with its nonce Nc; M relays it to S's listener, which accepts it and returns `HMAC(Nc‖S)`, the proof S's
follower expects. `ReplicationSource.cs:99` skips the conflict check because the peer id equals S's own.

**Change.**

```
client answer  = HMAC(secret, "crystal-auth-client" ‖ 0x00 ‖ serverNonce ‖ clientNonce ‖ len‖clientId ‖ len‖serverId)
server proof   = HMAC(secret, "crystal-auth-server" ‖ 0x00 ‖ serverNonce ‖ clientNonce ‖ len‖clientId ‖ len‖serverId)
```

- `len‖id` is a 2-byte big-endian length then the UTF-8 bytes, so ids cannot run into each other.
- Replace `Response` and `Proof` with `ClientAnswer(secret, serverNonce, clientNonce, clientId, serverId)` and
  `ServerProof(...)` over the same inputs. Both sides now know both nonces at `AUTH` time: the client sends its
  nonce in `AUTH` already (`AUTH <answer> <clientNonceHex>`), so the wire shape of `AUTH` does not change, only what
  the MACs cover.
- **Self-id refusal:** `ReplicationConnection.HandleHelloAsync` refuses (`-CRYSTAL_AUTH peer states this node's own
  id`) and closes when the `HELLO` node id equals `_source.NodeId`. `ReplicationClient.HandshakeAsync` throws
  `ReplicationProtocolException` when the reply's node id equals `_nodeId`, before sending `AUTH`.
- `ReplicationProtocol.Version` goes from its current value to the next integer.
- Keep the `!= _nodeId` condition at `ReplicationSource.cs:99`: with self ids refused it no longer matters, but it is
  harmless. Say so in a comment.

**Tests (R1.5)** in a new `AuthReflectionTests`. _Corrected 2026-10-12 (T1): only the self-id refusal stops the
review's attack; the labels do not (with both self-id checks off, the reflected MACs are computed over identical
inputs and match). The two self-id checks overlap (either alone stops it), so each has its own test:_
- `TheReflectionAttack_Fails`: a real store S listening and following an endpoint served by a test relay M (a
  `TcpListener` in the test performing the steps above with raw RESP). Assert S's follower takes nothing, S's epoch is
  unchanged, and M is never authenticated on S's listener.
- `APeerStatingThisNodesId_IsRefused_OnBothSides`: asserts the refusal itself (the error text naming the own id) on
  the listener and on the client. Catches mutation A1 (self-id refusal removed).
- `TheClientAnswerAndTheServerProof_DifferForTheSameInputs`: unit test of `ClientAnswer` vs `ServerProof`. Catches A2
  (labels removed).
- `AnAnswer_IsBoundToBothNoncesAndBothIds`: changing any one of the four inputs changes the MAC. Catches A3 (a nonce
  or an id left out).

## 2. The follow record before the write (R2)

**Today.** `Storage/StandbyDatabase.cs` `Apply`: `_applier.ApplyPage` (synced write), then the follow record is
written if it changed. `Storage/ReplicationFollower.cs:373-377` `TryBootstrap`: `InstallBootstrap` then
`FollowRecord.Write`. `Storage/StoreEngine.cs:1783-1792` `NewHistory` names the followed incarnation as shared up to
`latest`, trusting the record.

**Change.**
- `StandbyDatabase.Apply`: write the record (if it changed) **after** the epoch check and contiguity check pass and
  **before** `ApplyRaw`. To keep this simple, split `BatchApplier.ApplyPage` into a `Plan` (checks; returns what it
  would write, or the refusal) and a `Commit` (the write). `Apply` = plan → record → commit. Update the cached
  record (`KeptFollowRecord`) the same way.
- Why it is safe: `HasDiverged` has just shown the database's position lies inside a common incarnation of the
  served log, so everything it holds is part of the served log; the record (served incarnation, served history) is
  true of it before and after the write.
- `TryBootstrap`: `FollowRecord.Delete(uid)` before `InstallBootstrap`; the write after stays.
- **Divergence markers at promotion (R2.3):** `Storage/ResyncMarker.cs` has `Write`, `Exists` and `Clear` but no
  read; the file holds `reason={reason}
`. Add `ResyncMarker.ReadReason(dataDirectory, uid)` (null when absent or
  unreadable). Reasons written today: `gap …` and `diverged …` from `ReplicationFollower.MarkAndReBootstrap`, and
  `rejoin` from a pending rejoin. `StoreEngine.BeginNewIncarnations` (`:1721`) reads it: a reason starting with
  `gap` continues the followed history; **any other reason, or an unreadable marker,** is treated as divergence. For a non-gap marker use `LogIncarnation.Fresh(latest)`
  instead of `NewHistory`. Collect (name, reason) of every database with any marker and return it; `Promote` raises
  one `Warning` event (`ReplicationEventKind.PromotedWithPendingResync`, new) after `Promoted`.

**Tests (R2.5).** Process tests with `TestNode` (`tests/SystemD.Crystal.Process.Tests/Support`): a hook
`standby.recorded` between record and write, the node killed there; and `install.moved` between the move and the
record. Then promote, and attach a third node holding batches the old primary lost (as `DivergenceAndGapTests`
builds one) and assert it re-bootstraps.

## 3. Bootstraps off the loop (R3)

**Today.** `ReplicationFollower.TryBootstrap` (`:366-395`) runs `DownloadSnapshot` (`:418-426`), which blocks on
async calls with `.GetAwaiter().GetResult()`. It is called from reconcile (`:326`, `:341`), `RetryDueBootstraps`
(`:413`) and `MarkAndReBootstrap` (`:554`), all on the loop.

**Change.**
- Split `TryBootstrap` into **start** and **finish**. *Start* (on the loop) records the database as downloading
  (`_downloading[uid] = Task`), and launches `DownloadAsync` as a task, bounded by a `SemaphoreSlim`
  (`MaxConcurrentBootstraps`). *Finish* (on the loop, at the top of each iteration) takes every completed download:
  install, write the record, follow; or schedule the retry on failure. Only the loop touches `_followed`,
  `_pendingBootstraps` and the registry (R3.5).
- A database downloading is neither pulled nor bootstrapped again.
- The loop's long-poll must not delay finishing a download by more than `LongPollMax`; that is acceptable (one wait
  interval). When downloads are running and nothing is followed, the loop waits on the first download's completion
  or the R11.6 interval, whichever is first.
- **Resume (R3.3):** `Storage/SnapshotBootstrap.DownloadAsync` keeps, per file, the offset written and the CRC state;
  on an `IOException` or a closed connection it reconnects (a new `ReplicationTls.ConnectAsync` and handshake) and
  continues `SNAPSHOT GET` from that file and offset, while the session is alive. A `-CRYSTAL_SESSION` error (`ReplicationProtocol.ErrSession`: the session expired or is unknown) restarts from
  `BEGIN`. At most 3 reconnects per download.
- **BEGIN epoch (R3.4):** `SnapshotBegin.Epoch` (`SnapshotBootstrap.cs:21`) checked against the epoch: lower →
  refuse (throw, retried later), higher → `BumpTo` and an `EpochChanged` event (R10.3).
- Disposal: the follower's `DisposeAsync` cancels and awaits running downloads, then deletes their bootstrap folders.

**Tests (R3.6).** `BootstrapRobustnessTests`: a hook `bootstrap.chunk` that blocks the first database's download;
meanwhile commits on a second followed database are applied (assert by reading the standby). A hook that throws on
the n-th chunk once; assert by a counter in `FakeSource` or by a hook on the primary that the bytes before the cut
were not served twice.

## 4. Size limits (R4)

**Today.** `ReplicationProtocol.RequestCap = 64 KiB` (`:57`), `ReplyFrameCap = 64 MiB` (`:64`). A single batch is
always shipped whole (`003` R3.2), so one larger than the reply cap is refused by the standby's reader every time
(`ReplicationClient.cs:156-160`). With `MaxOpenDatabases` (default 256) a PULL fits 64 KiB; above roughly 850 it
does not.

**Change.**
- Primary (`Storage/DatabaseReplication.cs`, the serve path): before adding a batch to a page, if
  `batch.Length + PerBatchOverhead > ReplyFrameCap - PerReplyOverhead`, stop and return the new `ServeResult.Oversize`
  for that database. The reply entry's status is `oversize` (new status string in `ReplicationProtocol`, documented
  in `engineering/replication.md` § 8).
- Standby: `oversize` → `MarkAndReBootstrap(uid, "gap (oversize batch)")`, so it is a `Gap` event with that detail.
- Make the reply cap injectable for tests (an internal setter or an options-internal value), not a constant only.
- `RequestCap` after AUTH becomes `4 KiB + 128 × MaxOpenDatabases` (a UID is 36 bytes, a position at most 20, RESP
  overhead about 20, so 128 is generous); the connection receives it from the source.
- `frames_refused` counter in `Observability/ReplicationMetrics.cs`; the connection and the client increment it with
  tag `side=primary|standby`.

## 5. Deadlocks (R5)

**Today.**
- `Storage/ReplicationWriteGate.Set` spins until admitted writes end. It is called from `RoleChanged`, which
  `ReplicationEpoch` invokes inside `lock (_gate)` (`SetRole` `:142-146`, `TryPromote`, `ObserveHigherEpoch`).
- `CrystalReplication.Raise` (`:300-314`) calls `Events?.Invoke` synchronously; the follower raises through it on its
  own thread (`OnEvent = Raise`, `:295`). `Promote`, `Fence`, `Follow`, `StopFollowing` hold the facade's
  `lock (_gate)` while awaiting the follower's disposal; `Status()` takes the same lock (`:351`).

**Change.**
- `ReplicationWriteGate`: split `Set` into `Set(role)` (no wait) and `WaitForAdmittedWrites()`. `RoleChanged` calls
  only `Set`. `ReplicationEpoch` gains a callback `AfterRoleChanged` invoked **after** releasing its lock, which waits.
  Every public path that shuts the gate (Demote, Fence, the adopted-higher-epoch path in
  `ReplicationSource.ObservePeer` and in the follower's `BumpTo` path) therefore returns only after the drain, but
  holds no epoch lock while draining. The facade's `_gate` may still be held while draining: no admitted write takes
  it (check this with a grep for every `lock (_gate)` in `CrystalReplication.cs` and record the result in tasks.md).
- Events: a `ReplicationEventPump` (new, `Storage/` or the root namespace, internal): an unbounded `Channel<ReplicationEvent>`
  read by one long-running task that invokes `Events` in order. `Raise` keeps its metric, log and transition record
  synchronously and enqueues the delivery. Exceptions from a handler are caught and logged (event id in the 2000s
  range, new). `CrystalReplication.Dispose` completes the channel and waits up to 5 s.
- Document on `CrystalReplication.Events`: handlers run on Crystal's event thread, in order, after the call that
  caused them may already have returned; they may call any member.

**Tests (R5.5).** New `DeadlockTests`, each with a 10 s timeout that fails the test (never hangs the run):
the three-way cycle with hooks (a hook `promote.before-epoch` inside `BeginNewIncarnations`, a hook in the
higher-epoch path after the gate shuts, a paused `CreateDatabase` admitted before); a handler calling `Status()` and
`Promote` on a `Gap` event raised by the follower. Run both also with the thread pool starved
(`ThreadPool.SetMaxThreads` low in a dedicated collection, as the process tests do).

## 6. Use after close (R6)

**Today.** `ReplicationSource.ClearAllHistory` (`:107-118`) calls `serving.ClearTail()` without entering the
database; `DatabaseReplication.ClearTail` (`:96-100`) clears the tail and `_readers.Clear()`; `WalReaderPool.Clear`
(`:267-285`) takes the readers out under `_lock` and closes them outside it, while `Dispose` (`:287-304`) runs under
`_expiryGate`.

**Change.** In `ClearAllHistory`, `using var scope = serving.Enter(); if (scope is null) continue;` around
`ClearTail` (as `WriteReconciliationReport` does at `:133`). In `WalReaderPool.Clear`, take `_expiryGate` around the
whole method and return at once if disposed. A hook `clear.entered` lets the test pause there.

## 7. Conflicts (R7)

**Today.** The follower always sends role `standby` (`ReplicationFollower.cs:176`, `:424`); the primary's check
(`ReplicationSource.cs:96-103`) needs role `primary`. `ReplicationEvent` (`ReplicationEvent.cs:48-63`) has `Kind`,
`Epoch`, `Detail`, `Uid`.

**Change.**
- `ReplicationEvent` gains `public string? PeerNodeId { get; init; }` and `public string? PeerEndpoint { get; init; }`
  (public API change: list it for approval).
- Follower: after `AUTH`, compare `(client.PrimaryNodeId, client.PrimaryEpoch)` with `follow/primary.json`
  (`PrimaryRecord`, new, temp-write-sync-rename like `FollowRecord`). Same epoch, different node id → raise
  `Conflict` with both ids and endpoints (the remembered endpoint is stored in the record), close the connection,
  and retry on the normal backoff. Otherwise write the record when it changed (after R1's checks, before any pull).
- The follower states its real role in `HELLO` (`_epoch.RoleName`).
- The listener passes the remote endpoint into `ObservePeer` for the event.

**Test (R7.4).** Two `CrystalStore`s promoted to the same epoch (promote one twice and the other twice, from epoch 1),
a third following the first, then `Follow` the second: `Conflict` raised with both node ids; nothing applied from the
second (its database keys absent on the third).

## 8. The reconciliation report (R8)

**Today.** `ReplicationSource.WriteReconciliationReport` (`:126-...`) iterates `_byUid` (databases being served);
`Slot` update uses `Math.Max(existing.Acked, acked)` (`:715`); `ObservePeer`'s demotion (`:79-90`) writes no report.

**Change.** Iterate `_engine.ReplicatedDatabaseSet()` (every replicated database), opening each by UID as the report
already does; position 0 when no slot exists, reporting "the WAL no longer reaches position 0" when `ReadPage`
returns `Gap`. `Acked = acked`. In `ObservePeer`, when the transition is `Demoted`, write the report (outside the
epoch lock, after the drain of R5.1) and put its path in the `Demoted` event's detail and in
`LastReconciliationReport` (move that state into the source, or pass a callback to the facade).

## 9. Crash ordering (R9)

**Today.** No directory sync anywhere (`grep -rn "fsync\|FlushDirectory" src` is empty). `StoreEngine`
`InstallSwapLocked` moves the folder (`:741-747`) before advancing the allocator (`:765-773`), unlike create
(`:828-840`).

**Change.**
- `Storage/DurableFile.cs` (new, internal): `SyncDirectory(path)`. On Linux and macOS, .NET cannot open a directory
  as a file handle, so it calls libc `open(path, O_RDONLY)`, `fsync(fd)` and `close(fd)` through `[LibraryImport]`
  (no package); a failed `fsync` throws `IOException`. On Windows it does nothing, with a comment: a directory cannot
  be flushed through .NET, and NTFS journals its metadata. Check first that the architecture tests
  (`tests/SystemD.Crystal.Architecture.Tests`) allow a libc import in `Storage`; if they forbid it, extend their
  rule to allow exactly these three functions and record that in tasks.md. Call it after every rename listed in R9.1,
  and after each `Directory.Move` of install and recovery. Count the calls through an internal hook for the test.
- Install of a new UID: move the allocator write (`:765-773`) before `Directory.Move(bootstrapDirectory, dbPath)`.
  Check that startup recovery then treats an empty `db-{id}` below the allocator with no record as an interrupted
  install and removes it (it does for create; extend if not).

## 10. Observability (R10)

- `ReplicationMetrics.PageApplied(bytes)` and `Bootstrapped()` exist (`Observability/ReplicationMetrics.cs:140-147`)
  and are never called: call them from `StandbyDatabase.Apply` (applied > 0) and after a successful install. Add
  `BytesShipped(n)` called by the primary when a reply is written.
- `CrystalLog.ReplicationConnected` / `ReplicationDisconnected` (`Observability/CrystalLog.cs:167-173`) exist and are
  never called: call them from the follower after `AUTH` / when a connection ends, and from the listener after `AUTH` /
  when a served connection ends.
- The follower's `BumpTo` calls (`ReplicationFollower.cs:184-187`, `:223-226`) raise `EpochChanged` when the epoch
  moved.
- `PullEntry` (parsed in `ReplicationFollower.ParseEntries`, `:640`) carries the primary's latest, which is unused: keep the last value per followed database; `ReplicationStatus` gains
  `IReadOnlyList<FollowedDatabase> Followed` (`Uid`, `Name`, `Position`, `PrimaryLatest`, `Lag`) (public API: list
  for approval); a gauge `crystal.replication.standby_lag`.

## 11. Smaller defects (R11)

| Item | Where | Change |
|---|---|---|
| R11.1 | `ReplicationSource.cs:835-838` `Signal()` only from commits/role | `StoreEngine` raises a set-changed callback after create/drop/rename/swap of a replicated database and after install; the source calls `_change.Signal()` |
| R11.2 | `DatabaseReplication.FirstAvailable` (`:190-202`), `WalAccess.ReadPage` (`:180-200`) | read the earliest retained sequence with `GetUpdatesSince(0)`'s first batch **without** the `seq > want` gap rule (a probe mode of `ReadPage`), under the pool's deletion hold |
| R11.3 | `ReplicationFollower._lastSetVersion` (`:60`, `:261`) | reset to -1 at the start of `ConnectAndPumpAsync` |
| R11.4 | `CrystalReplication.ScheduleRejoin` (`:279`), `Storage/RejoinMarker.cs` (`Write(dataDirectory, primaryEndpoint)`, `Read` returns it) | the marker already stores the endpoint, and nothing uses it: at open, a store with a rejoin marker and replication options starts following `RejoinMarker.Read(...)` |
| R11.5 | `ReplicationFollower.ParseEndpoint` (`:689`) | call it in `CrystalReplication.Follow` before starting; throw `ArgumentException` with the expected form |
| R11.6 | `ReplicationFollower.cs:200-206` (200 ms delay) | wait `LongPollMax` when nothing is followed and no download runs |
| R11.7 | `Replication/Resp/RespReader.cs` `TryParseInt64`, `TryReadBulkString` (`:357`) | checked arithmetic; a length < -1 is a protocol error |
| R11.8 | `ReplicationListener` TLS handshake, `ReplicationTls.IsTrusted` (`:80-103`) | one `CancellationTokenSource` from accept passed to both handshake and connection (the connection's `preAuthTimeout` becomes the remaining time); `chain.ChainPolicy.ExtraStore.AddRange(chain certificates from the callback)`; EKU check when the extension is present |
| R11.9 | `ReplicationListener` `_open` | a second counter of unauthenticated connections; the connection reports when it authenticates |
| R11.10 | `ReplicationConnection.ServeAsync`, `ReplicationFollower`, `ReplicationEpoch.Role` (`:119-123`) | XML docs |
| R11.11 | `Storage/ReplicationSource.cs`, `Storage/ReplicationFollower.cs`, `Storage/SnapshotBootstrap.cs`, and any other file `003` R15's table names | first line `// Ported from Highway: <path>` with the table's path |

## 12. Tests (R12)

- R12.1: `EpochAndRoleTests.cs:118-150` builds a `ReplicationSource` over `LiveEpoch(store)` (`:182-186`), which
  loads a **second** `ReplicationEpoch` from the file. Give the test the store's own epoch (an internal accessor on
  `CrystalReplication`, e.g. `internal ReplicationEpoch EpochForTests`), or drive it through the store's listener with
  a real client, and assert `store.Replication.Role` and `.Epoch` changed.
- R12.2: find the test that claims `003` R14.2 (`tests/SystemD.Crystal.Process.Tests/Nodes/FailoverTests.cs`) and
  make the primary commit batches of several keys with a page size that holds several batches; the check after a kill
  is that for every batch, either all its keys are present or none, and the present batches are a prefix.
- R12.3: the test lowers the deadline to 600 ms and sends `HELLO` every 200 ms. It closed after 9 999 ms once, under
  the full run: likely the deadline's `CancellationTokenSource` is linked per read and the read that observes it
  ran late, or the second `HELLO` path does not observe `_preAuthDeadline`. Reproduce under a starved thread pool
  first; do not raise timeouts as the fix.

## 13a. Planned handover (R14)

- **The gate.** `ReplicationWriteGate` gains a step-down flag beside the role: `BeginStepDown()` sets it (refusing as
  `stepping down`), `EndStepDown()` clears it. `RefusedAs` reports the role's refusal, else the step-down's. The role
  is untouched, so `ReplicationSource` keeps serving (it serves while the role is primary).
- **The wait.** `StepDownAsync` (not under the facade's lock for the wait itself, so `Status()` and events stay
  responsive): check primary and no step-down running under the lock; `BeginStepDown`; `WaitForAdmittedWrites` (off
  the caller's thread); read each replicated database's latest (fixed now: nothing local can write); then poll the
  slots (`ReplicationSource.Slots()`, keyed by standby node id and UID) until the standby's acknowledged position equals
  each latest at the current epoch, on `_engine.Clock` (`Task.Delay(…, clock, ct)`, 20 ms; no direct clock read, the
  architecture gate). A standby acknowledges by its next `PULL`, which it sends at once after applying a page.
- **Completion.** Under the facade's lock: if the role or epoch changed since the start, throw (R14.4); otherwise
  demote exactly as `Demote` does (`SetRole(Standby)`, report, `ClearAllHistory`, `Demoted` event), then
  `EndStepDown` (the role now refuses writes on its own).
- **Failure.** `EndStepDown` in a `finally` on every path that does not demote; the role is still primary, so writes
  resume (R14.3).
- **API.** `public Task<IReadOnlyList<SteppedDownDatabase>> StepDownAsync(string standbyNodeId, TimeSpan timeout,
  CancellationToken cancellationToken = default)`; `public sealed record SteppedDownDatabase(string Uid, string Name,
  long Position)`. Yields a thread (it waits on the clock); documented so.

## 13. Public API changes (to list for approval)

- `ReplicationOptions.MaxConcurrentBootstraps` (int, default 2). Added in T8.
- `ReplicationEventKind.PromotedWithPendingResync`.
- `ReplicationEvent.PeerNodeId`, `ReplicationEvent.PeerEndpoint`.
- `ReplicationStatus.Followed` (an init property) and the record `FollowedDatabase`; metrics `bytes_shipped`,
  `standby_lag`, `frames_refused`.
- `CrystalReplication.StepDownAsync` and the record `SteppedDownDatabase` (R14).
- `CrystalLogEvents.ReplicationPromotedWithPendingResync` (event 2013) and `ReplicationEventHandlerFailed` (2014).
- Behaviour: events delivered on Crystal's event thread (R5.2); `Follow` throws `ArgumentException` for a bad
  endpoint (R11.5); protocol version incremented (R1.4).

## 14. What this design deliberately does not do

- No channel binding: TLS is the answer to an active attacker (R1.6).
- No new on-disk format for databases; the new files are `follow/primary.json` and the endpoint in the rejoin marker.
- No change to how divergence is decided (the exact-history rule of `003`'s addendum stands); R2 only makes its
  input true after a crash.
- No limit on commit size (open question 2).

## 15. Risks

- **R2's reordering** touches the apply path, the most sensitive code in replication. The `003` divergence and gap
  tests must all still pass; add the R2.5 kill tests before changing the order.
- **R3** moves work across threads: keep every change to `_followed` on the loop (R3.5), or the follower gains races
  it never had.
- **R5's event thread** changes timing that existing tests may rely on (asserting an event right after a call). Fix
  such tests by waiting for the event with a timeout, not by sleeping.
