# 011 — Replication completion

> **Status:** approved (2026-10-12). **Depends on:** `003-replication` (everything here completes it),
> `009-self-describing-databases` (databases are reconciled by UID), `005-store-and-databases` (the registry, the
> allocator, the store gate), `006-durability` (sync rules), `007-observability` (meter, log events, `Status()`).
> **Uses:** `004-test-suite` (test nodes, the crash harness).

## Introduction

`003` built replication: a primary ships each replicated database's log to standbys over a small RESP2 protocol,
standbys pull and apply it, and the product drives promotion, demotion and fencing. Its tasks are done and its
addenda record two review rounds of fixes (blockers B1–B4, should-fix 1–9, TLS, contiguity, connection limits, and
the smaller items of 2026-10-11).

A third independent review (2026-10-12) of the whole replication surface found that `003` cannot be marked
implemented yet:
- **one blocker:** mutual authentication can be defeated by reflection;
- **eight should-fix defects:** silent divergence after a crash, a bootstrap stalling every database, size caps that
  stop replication for good, two deadlocks, a native use after close, a conflict check that never fires, a
  reconciliation report that under-reports, and crash ordering;
- **minor gaps** against `003`'s own requirements: metrics and log events defined but never emitted, a set change that
  does not wake a waiting pull, and others.

Every finding below was checked against the code (file and line in design.md) before it was written here. This spec
fixes all of them, so that `003` and `011` can be marked implemented together. **It changes no format on disk except
where stated, and no wire command except AUTH (R1), which is a protocol version change.**

Requirement numbers in `003` (R*x.y*) are cited as `003` R*x.y*.

## Requirements

### R1 — Authentication cannot be reflected (blocker)

- **R1.1** The proof a primary sends and the answer a standby sends are **different functions**: each HMAC input
  starts with a direction label (`crystal-auth-client` or `crystal-auth-server`), so neither can stand for the other.
- **R1.2** Each MAC covers **both nonces and both node ids** (the server's nonce, the client's nonce, the client's node
  id, the server's node id), so an answer is bound to one pairing of two named nodes.
- **R1.3** **A node refuses a peer that states its own node id,** on both sides: the listener refuses a `HELLO` naming
  the listener's node id, and the client refuses a `HELLO` reply naming the client's node id. Both refusals happen
  before any epoch, page or snapshot is taken from the peer, and are logged without secret material (`003` R10.9).
- **R1.4** The protocol version (`003` R10.7) is incremented, because R1.1–R1.2 change `AUTH`. A node refuses a peer
  of the old version, as `003` R10.7 already requires.
- **R1.5** A test performs the reflection attack of the review against a real listener and a real follower and shows
  it fails: the attacker is not authenticated, the follower takes nothing, and the epoch does not change.
- **R1.6** `engineering/replication.md` and the `ReplicationOptions` documentation state that over plain TCP an active
  man in the middle can still relay a whole session, and that **TLS (`003` R10.6) is required on any network not
  trusted end to end.**

### R2 — Promotion never claims a history the standby does not hold

- **R2.1** When a standby continues a log, it records the served incarnation and history in `follow/{uid}.json`
  **before** the page's synced write, not after. A crash between the two then leaves a record that describes a prefix
  of what was served, which is still true of what the database holds.
- **R2.2** Installing a snapshot **deletes the database's follow record before** the folder is moved into place, and
  writes the snapshot's record (`003` R7.7) after. A crash in between leaves no record, which `003` R5.5 treats as
  "nothing known": the database is re-bootstrapped, never continued.
- **R2.3** Promotion gives a database that carries a **divergence** resync marker a **fresh history** (no shared
  incarnation), never one continuing the log it followed: its data may hold batches that log does not. A database
  carrying a **gap** marker (its data is a valid prefix, only behind) continues its followed history as today.
- **R2.4** Promotion raises one warning event naming every database promoted with a pending resync marker and its
  reason, so the product can decide whether to serve it (Crystal still promotes; policy is the product's, `003` R9).
- **R2.5** Tests: a crash (process kill) between apply and record, then promotion, then a second standby that holds
  batches the old primary lost: that standby re-bootstraps. And a crash between an install's move and its record:
  promotion does not continue the followed history.

### R3 — A bootstrap never stalls the other databases

- **R3.1** A snapshot download runs **outside the pull loop** (`003` R7.1): while one database downloads, every other
  followed database keeps pulling and applying.
- **R3.2** At most `ReplicationOptions.MaxConcurrentBootstraps` (new, default 2) downloads run at once; others wait
  their turn. The retry backoff and the in-session refetch of `003` remain.
- **R3.3** **Resumable within a session** (`003` R7.1): a dropped connection during a download reconnects and continues
  from the file and offset reached, while the snapshot session is alive; only an expired or refused session restarts
  the download.
- **R3.4** The standby checks the `SNAPSHOT BEGIN` reply's epoch as it checks a page's (`003` R4.3): an older epoch
  than the newest seen is refused, a newer one adopted.
- **R3.5** Install, follow-start and reconcile still happen on the follower's loop, so the set of followed databases
  is only changed by one thread.
- **R3.6** Tests: a large snapshot downloading (slowed by a hook) while another database receives and applies
  commits; a download cut mid-file resumes at its offset (the bytes before it are not fetched again).

### R4 — Size limits never stop replication silently

- **R4.1** **An oversized batch:** a committed batch whose encoded size cannot fit in one reply frame is never sent.
  The primary reports that database's entry as status `oversize`; the standby re-bootstraps that database alone (the
  snapshot carries the batch) and raises a `Gap` event whose detail says "oversize batch". The others keep following.
- **R4.2** **A large PULL:** the request cap after `AUTH` is large enough for a `PULL` naming every database the store
  can open: it is derived from `CrystalOptions.MaxOpenDatabases`, not fixed at 64 KiB.
- **R4.3** A frame refused by a cap on either side is logged once per connection with the cap and the declared size,
  and counted (`crystal.replication.frames_refused`, tagged by side).
- **R4.4** Tests: a batch larger than the reply cap (the cap lowered for the test) leads to a re-bootstrap of that
  database and the others keep applying; a `PULL` for `MaxOpenDatabases` databases with the longest UIDs is accepted.

### R5 — No deadlock in role changes or events

- **R5.1** **Waiting for admitted writes never holds the epoch lock.** Shutting the write gate (demote, fence, a higher
  epoch adopted) marks it shut under the lock and waits for admitted writes after releasing it; the call still returns
  only when those writes have ended (`003` addendum, race windows).
- **R5.2** **Events are raised on Crystal's own event thread,** in the order they occur, never on the thread of a
  follower, a listener connection or a control call. A handler may call any `store.Replication` member, including
  `Status()`, `Promote`, `Demote`, `Fence`, `Follow` and `StopFollowing`, without deadlocking.
- **R5.3** A handler that throws does not stop later events; the exception is logged at Error.
- **R5.4** Disposing the store stops the event thread after delivering the events already queued, bounded by 5 s.
- **R5.5** Tests at the exact interleaving (hooks, not sleeps), also under a starved thread pool (playbook § 4):
  - a re-`Promote` of a primary, an authenticated higher-epoch `AUTH`, and an admitted replicated `CreateDatabase`,
    forced into the three-way order of the review: all three complete;
  - an event handler calling `Status()` and then `Promote` from inside a follower-raised event: both complete.

### R6 — No native use after close on a role change

- **R6.1** Clearing a database's tail and WAL readers (an epoch or role change) **enters the database** like any other
  call, so a concurrent drop or close waits for it, and a database already closing is skipped.
- **R6.2** The reader pool's `Clear` and `Dispose` cannot both close the same reader, nor can `Clear` close a reader
  after `Dispose` has returned.
- **R6.3** Test: a hook pauses `ClearAllHistory` between taking a database and clearing it while the database is
  dropped on another thread; the drop waits, and no native call touches a closed instance.

### R7 — Two primaries at the same epoch are reported

- **R7.1** **The standby detects it:** a follower that has seen primary *P* at epoch *E*, and then completes `AUTH` with a
  primary *Q ≠ P* also stating epoch *E*, raises a `Conflict` event naming both node ids and both endpoints, and takes
  nothing from *Q* (`003` R6.4). The last (node id, epoch) followed is kept in `follow/primary.json`, so a restart does
  not forget it.
- **R7.2** **The primary detects it too:** a client states its real role in `HELLO` (today it always states
  `standby`); the existing check (`ReplicationSource.ObservePeer`) then fires for a peer that is itself a primary.
- **R7.3** `ReplicationEvent` gains `PeerNodeId` and `PeerEndpoint` (nullable), set on `Conflict`, `EpochChanged`,
  `Demoted` from a peer, and the connection log events.
- **R7.4** Tests between two real nodes (not a hand-crafted `HELLO`): two nodes promoted to the same epoch, a standby
  pointed first at one and then at the other, raises `Conflict` and takes nothing from the second.

### R8 — The reconciliation report is complete

- **R8.1** The report (`003` R9.2) covers **every replicated database** of the node, served or not: one no standby has
  pulled reports everything from position 0 that the WAL still holds (and says so when the WAL no longer reaches 0).
- **R8.2** A slot's acknowledged position is the **last** one the standby stated, not the highest ever: a standby that
  re-bootstraps lower lowers its slot, so the report floor and the lag are not inflated.
- **R8.3** **Every demotion writes a report,** including one caused by a higher epoch from a peer; the `Demoted` event
  carries the report's path in its detail, as a requested demotion does.
- **R8.4** Tests for each of R8.1–R8.3.

### R9 — Crash ordering

- **R9.1** **A rename is made durable:** after each rename of `node.epoch`, a follow record, an incarnation record, the
  primary record (R7.1), and each folder move of an install, the parent directory is synced (on Linux and macOS;
  Windows has no directory handle to sync and needs none, and the code says so).
- **R9.2** **An install of a new UID advances the allocator before the folder is moved** into `db-{id}`, as create does
  (`005` design addendum A2). A crash between the two leaves an allocator past an empty id, never a folder at the next
  id that blocks `CreateDatabase`.
- **R9.3** Tests: a kill at each step of an install of a new UID (hook-driven, `004` crash harness) leaves a store that
  opens and creates; the directory sync is called after each rename (counted through an internal hook, since a power
  loss cannot be simulated).

### R10 — Observability as `003` R13 states it

- **R10.1** `pages_applied`, `bytes_applied` and `bootstraps` are counted by the standby; `bytes_shipped` is added and
  counted by the primary (`003` R13.1).
- **R10.2** Log events 2011 (connected) and 2012 (connection lost) are emitted by the follower and the listener, with
  the peer's node id and endpoint, never secret material (`003` R13.3, R10.9).
- **R10.3** A standby adopting a newer epoch (from a `HELLO` reply, a page, or a snapshot) raises `EpochChanged`.
- **R10.4** The standby keeps each entry's `latest` (`003` R3.7) and reports, per followed database, its position and its
  lag in sequences in `Status()` (`003` R13.2) and as metrics (tagged only when `TagMetricsWithDatabase` is on).

### R11 — Smaller defects

- **R11.1** **A set change wakes a waiting pull** (`003` R3.4): creating, dropping, renaming or swapping a replicated
  database signals the source.
- **R11.2** **A gap is reported correctly:** the first available position comes from the WAL's earliest retained
  sequence, and reading for it holds WAL deletion as the reader pool does.
- **R11.3** **Reconcile on every new connection:** the set version last reconciled is forgotten when a connection ends,
  so a primary at the same endpoint with the same registry generation is still reconciled.
- **R11.4** **Rejoin uses its endpoint:** `ScheduleRejoin(endpoint)` follows that endpoint on the next start, as
  `003` R7.8 says, without the product calling `Follow` again.
- **R11.5** **`Follow` validates its endpoint** (host and port) when called, throwing `ArgumentException`, rather than
  looping on a parse error.
- **R11.6** **An idle standby with nothing to follow** asks `DATABASES` at most once per `LongPollMax`, not every 200 ms.
- **R11.7** **The RESP reader** refuses an integer that overflows 64 bits, and a negative length other than -1, as
  protocol errors.
- **R11.8** **TLS:**
  - one deadline covers the TLS handshake and `AUTH` together (`003` R10.5's 10 s), not one 10 s each;
  - with custom trust, the intermediate certificates the peer sent are passed to the chain build;
  - a peer's certificate must allow its use (server authentication for a primary, client authentication for a
    standby) when it states an extended key usage.
- **R11.9** **Connections before `AUTH` cannot crowd out standbys:** at most a quarter of `MaxConnections` (at least 1)
  may be unauthenticated at once; one more is closed at accept.
- **R11.10** **Playbook § 7:** a method that blocks a thread says so in its documentation (`ReplicationConnection`'s
  long-poll wait); the follower's synchronous waits on async calls are removed or documented with why they are safe;
  `ReplicationEpoch.Role`'s documentation matches its lock.
- **R11.11** **`003` R15 headers:** every file copied and adapted from Highway starts with
  `// Ported from Highway: <path>` naming its source path in the Highway repository (the table in `003` R15).

### R12 — Tests that prove what they claim

- **R12.1** `EpochAndRoleTests.AStalePrimary_IsDemotedByAHigherEpoch_OnHello` drives the store's own epoch, not a second
  `ReplicationEpoch` loaded from the same file.
- **R12.2** The hard-kill test of `003` R14.2 commits **multi-batch pages** (several keys per batch, several batches per
  page), and checks after each kill that the standby holds a whole number of pages: never part of a page.
- **R12.3** `ReplicationProtocolTests.APeerThatKeepsSayingHello_IsClosedWhenTheDeadlineFromConnectRunsOut` failed
  once in a full run (closed after 9 999 ms, against an expected 600 ms deadline). **Root-cause it** (playbook § 4) and
  fix the cause, in the code or the test. Record the cause in tasks.md.
- **R12.4** Every guard this spec adds has a mutation check caught by a named test (playbook § 4).

### R13 — Closing `003`

- **R13.1** When every task here is done, an **independent review** of the replication surface (playbook § 4) finds no
  blocker and no should-fix item, or each one it finds is fixed with a test.
- **R13.2** `003` and `011` are marked **implemented** together; `003`'s tasks.md gains an addendum pointing here.
- **R13.3** `engineering/replication.md` describes Crystal as it then is (playbook § 2).

### R14 — Planned handover (added 2026-10-12 by the maintainer)

A product moves the primary role on purpose (its own "goodbye" to the current primary, its own roster and priority, as
Highway's replication does today). Crystal supplies the step it cannot do from outside: stop writes on the primary,
**keep serving** until the chosen standby holds everything, then stand down. Today `Fence` and `Demote` both stop
serving at once, so the last batches committed before the handover reach no standby and appear only in the
reconciliation report.

- **R14.1** `CrystalReplication.StepDownAsync(standbyNodeId, timeout, cancellationToken)` on a primary: local writes
  to replicated databases (and changes to the replicated set) are refused at once, with `NotPrimaryException` naming
  the step-down; writes already admitted end first (as `003`'s race-window fix requires). The node **keeps its role
  and epoch and keeps serving pulls and snapshots** while it waits.
- **R14.2** It completes when the standby `standbyNodeId` has acknowledged, at this node's epoch, the latest position
  of **every** replicated database. Then the node **demotes** (as `Demote` does: role standby, a reconciliation report,
  which is then empty, a `Demoted` event saying "stepped down to {standbyNodeId}"), and returns each database's final
  position. The product then promotes the standby and points this node at it with `Follow`; this node continues
  without a re-bootstrap (the exact-history rule: the new primary's history holds this node's log up to those
  positions).
- **R14.3** If the standby has not caught up within `timeout`, or `cancellationToken` fires, writes are accepted again,
  the node stays the primary, and the call throws (`TimeoutException`, or `OperationCanceledException`), naming the
  databases the standby had not caught up on. No half-done handover is left behind.
- **R14.4** A role change during the wait (a fence, a higher epoch from a peer, `Demote`) ends the wait: the call throws
  `InvalidOperationException` naming the change, and the role the change set stands.
- **R14.5** Refused when the node is not a primary, or when a step-down is already running (`InvalidOperationException`).
- **R14.6** A standby that follows none of a replicated database, or has no slot for it, has not caught up on it.
- **R14.7** Tests: a handover under continuous writes (a writer thread committing until refused) loses nothing: after
  the step-down, the promotion of the standby and the old primary's `Follow`, both nodes hold the same data and the
  report is empty; the old primary continues without a re-bootstrap. A timeout reopens writes. A fence during the wait
  ends it. Mutations: serving stopped during the wait; a database left out of the caught-up check; writes not reopened
  on timeout.

## Out of scope

- Synchronous replication or waiting for standbys before acknowledging (`003` R12.2).
- Channel binding of `AUTH` to TLS (R1.6 documents the limit instead).
- Certificate revocation with custom trust (an internal CA usually publishes no list; `003` R10.6).
- Automatic failover policy: Crystal still only reports, the product decides (`003` R9).
- Any change to the on-disk layout of databases or the registry.

## Open questions

**Decided 2026-10-12 by the maintainer: the recommendation in each case** (fresh history plus the event; ship an
oversized batch by snapshot; the last primary persisted in `follow/primary.json`; events on Crystal's own thread).
The questions as they were asked:

1. **R2.3:** a database with a divergence marker gets a fresh history at promotion. Alternative: refuse the promotion
   while such markers exist. Recommended: fresh history plus the R2.4 event; refusing would block failover on the
   one node that may be the only survivor.
2. **R4.1:** an oversized batch is shipped by snapshot. Alternative: refuse such a commit on a replicated database
   with a typed exception (a new public behaviour). Recommended: ship by snapshot; a 64 MiB batch is already far past
   what a product should commit, and refusing would change `002`'s commit contract.
3. **R7.1:** the last primary followed is persisted in a new file `follow/primary.json`. Alternative: in memory only
   (a restart forgets it). Recommended: persisted, so a conflict across a restart is still seen.
4. **R5.2:** events move to Crystal's own thread, which changes when a handler runs relative to the call that caused
   it (a handler of `Promoted` no longer runs before `Promote` returns). Recommended: accept; document it on `Events`.
