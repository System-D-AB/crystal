# 003 — Replication

> **Status:** implemented (2026-10-13, completed by `011-replication-completion`). **Depends on:** `001-2-rocksdb-binding`, `005-store-and-databases`, `002-box-layout`,
> `006-durability`, `007-observability`, and `004-test-suite` T1, T3, T4 (two-process test nodes and the crash harness).
> **Revised (draft) 2026-10-10**, after a reading of Highway's replication code and the maintainer's decisions:
> the wire protocol is **a subset of RESP2** on Crystal's own reader and writer (R10); **one `PULL` serves every
> database** and is the acknowledgement (R3); databases are identified on the wire by a **UID** and the database set is
> **reconciled**, not log-replicated (R1); **divergence** is detected, closing `006`'s open question 3 (R5); the protocol
> carries **no control verbs** and authenticates by **HMAC challenge** (R10); replicated databases **retain their WAL**
> (R11). The previous text is in this file's history; what changed and why is listed under *Revision 2026-10-10*.

## Introduction

A Crystal store can have **one primary and one or more standbys**. A standby applies the primary's committed batches
in commit order, byte for byte, and serves no writes. A blank standby bootstraps from a snapshot of the primary and
then follows its log.

**Crystal owns the mechanism.** Crystal provides and carries out:
- log shipping and snapshot bootstrap;
- positions and lag;
- gap and divergence detection, and re-bootstrap;
- epochs and fencing;
- format checks in both directions;
- its own small wire protocol on its own port.

**The product owns the policy.** Crystal never decides:
- which node is primary, or when to promote;
- how clients find the primary;
- whether to fail over automatically, or only by an operator.

It offers operations (promote, demote, fence, schedule a rejoin) **as in-process calls**, and reports what it sees
(positions, lag, gaps, divergence, conflicts). Whatever a product decides, Crystal's fencing keeps a wrong decision from
corrupting data: at worst a product promotes the wrong node, an availability problem, not a correctness one.

**Origin.** The mechanism is taken from the replication of Highway, a durable message broker, where it runs in
production pairs and has been rehearsed with real binaries across upgrades and hard kills: its RESP2 framing, its
in-memory tail and held WAL readers, its group-commit trimming, its one synced write per page, its epochs and its
two-way format checks. A reading of its code (2026-10-10) also found what not to copy, each corrected below: no
long-poll (three round trips and a 50 ms sleep per idle cycle), a dropped slot revived by the next `HELLO`, snapshot
sessions that never expire, snapshot files with no checksum and their sizes ignored, checkpoints in the system temp
folder, any authenticated client able to promote or fence, and client certificates accepted unchecked. What made it
Highway's (the client herd, peer checks, priorities, roster, the broker's commands) stays in Highway.

**What is new for Crystal.** Highway replicated one RocksDB instance. A Crystal store holds many databases, each with
its own write-ahead log (WAL). This spec defines replication for the store as a whole: **one node epoch, one stream
per database, one connection per standby.**

## Requirements

### R1 — Roles, the unit of replication, and the database set

- **R1.1** A **store** is the unit of replication and failover. A node's store is a primary, a standby, or fenced, as
  a whole.
- **R1.2** **One stream per database.** Each replicated database has its own log position, its own standby
  positions, its own gap and divergence detection and its own bootstrap.
- **R1.3** **Every database has a UID**, a random 128-bit identifier assigned when it is created and kept in its
  registry record on every node. The wire names databases by UID, never by the numeric id: a standby allocates its own
  ids, and keeps databases of its own (R1.5), so the same number can name different databases on two nodes. A UID
  survives rename and promotion.
- **R1.4** **The database set is reconciled, not log-replicated.** The primary states the replicated set (UID, name,
  latest position) and a **set version** that changes with every create, rename, swap or drop of a replicated
  database. A standby that sees a new set version reads the set and makes its own registry match: it creates and
  bootstraps new databases, drops removed ones, and applies renames. The registry database `_crystal` is never shipped
  as a log (it holds each node's own databases and allocator).
- **R1.5** A database can be created **not replicated** (for data a product rebuilds at start). It exists only on the
  node that created it, and is never listed, pulled or snapshotted.
- **R1.6** **A standby never writes locally into a replicated database.** Every write API and every catalogue change
  refuses on a standby with a typed error. Applied batches are the only writes; the standby's sequence numbers stay
  aligned with the primary's only because of this.
- **R1.7** **No atomicity across databases.** A standby, or a newly promoted primary, may hold database A at one point
  in time and database B at another. A product that needs an invariant across data keeps that data in one database.

### R2 — Positions and slots

- **R2.1** A database's position is its log sequence number. **On a standby, the position is derived from the data
  itself** (the database's latest sequence), never stored beside it, so no crash can leave a position that disagrees
  with the data.
- **R2.2** The primary keeps a **slot** per standby and database: the standby's acknowledged position, its epoch, its
  last contact, and a state (`Active`, `Lagging`, `Dropped`). Slots live in memory; they are rebuilt from the next
  pulls after a restart and cleared on any role or epoch change.
- **R2.3** A slot is `Lagging` past a quarter of its lag cap, and `Dropped` past the cap or after a stale period with
  no contact; a drop raises an event. **A slot is an observation, never a hold:** the primary keeps no log for a
  standby (R11.2), so a dropped standby that comes back resumes if the log still reaches its position, or meets a gap
  and re-bootstraps (R5). A standby that went silent never keeps showing as `Active`.
- **R2.4** Lag is reported in sequences **and in time** (age of the standby's last acknowledged batch), because
  sequences are not comparable across databases.

### R3 — Shipping the log

- **R3.1** A standby **pulls**: it owns its cursors and asks for pages from its positions. **One `PULL` names every
  replicated database with the standby's position in it, and those positions are its acknowledgement**: the primary
  updates the slots from them. There is no separate acknowledgement command.
- **R3.2** A page holds whole committed batches, each with its sequence, up to the requested size across the whole
  reply. A batch larger than the size is still sent alone.
- **R3.3** **A caught-up pull costs nothing.** A database at the latest position contributes nothing to the reply and
  reads no WAL.
- **R3.4** **A standby idles when there is nothing to apply.** A `PULL` in which every database is caught up waits on
  the primary, up to the requested time, until any of them commits, the set version changes, or the epoch changes. An
  idle store with 50 replicated databases costs one request per wait interval in total.
- **R3.5** **Pull cost stays flat with uptime and log size.** Recent batches are served from an in-memory tail of
  committed batches, captured after each commit. Older ranges come from WAL readers held between pulls. (Reading the
  WAL from the start of its file on every pull made a caught-up pull grow from about 1.5 ms to 54 ms as the log grew.)
- **R3.6** A standby positioned inside a group-commit record (several committers' batches written as one) receives
  that record trimmed to start right after its position. A record that cannot be trimmed is treated as a gap (R5).
- **R3.7** Each database's part of a reply carries the primary's latest position in it, so the standby knows its lag
  without asking.

### R4 — Applying on the standby

- **R4.1** Batches at or below the standby's position are skipped, so a re-sent page applies nothing twice.
- **R4.2** A database's page is applied as **one synced write**: a crash keeps the whole page or none of it.
- **R4.3** A reply stamped with an epoch older than the newest the standby has seen is refused whole (R6).
- **R4.4** Batches are applied byte-identical: the standby's data is the primary's data.
- **R4.5** **The standby's catalogue follows its data.** When an applied page touches box 0 (the catalogue), the
  database's in-memory catalogue is reloaded from disk before the next read, so a box created, renamed or dropped on
  the primary appears on the standby with its data.

### R5 — Gaps, divergence and re-bootstrap

- **R5.1** **A gap is never applied as if contiguous.** The primary reports a gap for a database whose retained log no
  longer reaches the standby's position: the first available batch starts after it, a record cannot be trimmed, or
  nothing can be served while the primary is ahead (a WAL that restarted after a crash no longer shows older batches).
- **R5.2** **Divergence is never applied either.** A standby can hold batches its primary does not have: written by a
  primary that then lost them (a power loss under `Buffered` or `Interval`, `006` R1.1), or by an old primary when the
  standby now follows a newly promoted node that applied fewer. Sequence numbers alone cannot show it, because the
  primary may later write different batches at the same numbers. So each database's log on a primary has an
  **incarnation** (a random identifier, new each time the database opens on a primary and at every promotion) and a
  **base** (its latest position at that moment). A standby keeps, per database, the incarnation it last applied from,
  in a node-local file. **When the incarnation it is served differs from the one it kept, and its position is above
  the base, the database has diverged.** (Batches at or below the base existed in the new log's history, so they are
  the same batches.)
- **R5.2 amended (2026-10-11, exact history).** The rule above is not sound: a standby can hold batches at or below
  the base that the new log never had, when it once wrote as a primary itself (promoted by mistake, then stepped down
  and pointed at the real primary after that one was promoted past it). A test showed it keeping its own writes and
  continuing on top of them. The rule is now: each log names the incarnations it shares and how far (its history); a
  standby continues only where the log it holds and the served log name a common incarnation within both ends at its
  position, and re-bootstraps otherwise; a node taking a primary role records the incarnation its data came from in
  its history and then holds its own. `engineering/replication.md` § 5 states it in full.
- **R5.3** A gap or a divergence marks **that database** for re-bootstrap; the others keep following. The reply
  reports it per database, never failing the pull for the rest.
- **R5.4** Re-bootstrap, first bootstrap and rejoin are one code path.
- **R5.5** A crash between applying a page and recording its incarnation can only cause a needless re-bootstrap,
  never a missed divergence: the incarnation is recorded after the page is applied, and a missing or stale record
  compares as different.

### R6 — Epochs and fencing

- **R6.1** **One epoch per node** (R1.1), in the node-local file `node.epoch` (built by `005`), outside every database,
  written atomically (temp and rename). Kept out of the databases on purpose: a replicated write must never overwrite a
  standby's own epoch.
- **R6.2** Promotion increments the epoch and persists it before the node serves as primary; a failure to persist
  fails the promotion.
- **R6.3** Every reply carries the sender's epoch, and every request the requester's. A node that sees a higher epoch
  adopts it: a standby follows the newer primary, and a primary or fenced node demotes itself. (Without a persisted
  epoch, a restarted node came back at epoch 1 and fencing failed.)
- **R6.4** **Two primaries at the same epoch** are detected and reported as a conflict event, with both node ids and
  endpoints. Which one yields is the product's policy (R9); Crystal never applies pages from either into the other.
- **R6.5** **Fence** makes a node read-only at once: it serves no writes and no pages until it is promoted, demoted or
  restarted.

### R7 — Snapshot bootstrap

- **R7.1** A database on a standby bootstraps from a checkpoint of the primary's database, downloaded in chunks
  **resumable by file and offset within a session**, on its own connection, so a bootstrap never stalls the stream of
  the other databases.
- **R7.2** **Every file's size and checksum (CRC-32C) are stated by the primary and verified by the standby** before
  install. A mismatch discards the download and starts again.
- **R7.3** **Atomic install:** the download goes into a temporary sibling directory and moves into place only when
  complete and verified. A failure leaves the database untouched. Node-local files are never overwritten.
- **R7.4** **An unreachable primary is waited for,** with backoff and cancellation, not treated as a fatal error at
  start.
- **R7.5** Snapshot sessions **expire** after a period without requests (default 2 minutes) and at an absolute limit
  (default 1 hour), releasing the checkpoint they pin.
- **R7.6** The checkpoint is created **on the database's own filesystem**, beside it, so it uses hard links instead of a
  copy (Highway created it in the system temp folder).
- **R7.7** The snapshot states the incarnation and base of the log it was taken from, and the standby records them
  with the installed database, so the following pulls continue that log without a false divergence.
- **R7.8** **Rejoin:** an operation records "on next start, wipe this node's replicated databases and bootstrap from
  this primary". The record survives a failed download. Whether and when to schedule it is the product's decision.

### R8 — Formats in both directions

- **R8.1** A standby states its storage format and column-family layout in `HELLO` and in each `SNAPSHOT BEGIN`. A
  primary refuses a mismatch, or a missing statement, before sending anything.
- **R8.2** The primary's replies state its format. A standby refuses a primary whose format differs, or that states
  none, **before downloading or applying anything**, and keeps waiting, retrying rarely, so it bootstraps on its own
  once the primary is upgraded.

### R9 — Operations, not decisions

- **R9.1** Crystal offers, **as in-process calls on the store** (never on the wire, R10.4):
  - `Promote(reason)`;
  - `Demote()`;
  - `Fence(reason)`;
  - `Follow(primary)` and `StopFollowing()`;
  - `ScheduleRejoin(primary)`;
  - `Status()`, with a replication section (R13.2).

  It decides none of them.
- **R9.2** **Demote** writes a **reconciliation report**: the batches this node committed beyond the highest position
  any standby acknowledged, decoded to boxes (box name, keys written and deleted), so an operator or a product can see
  what a failover may have lost.
- **R9.3** **Events:** promoted, demoted, fenced, epoch changed, slot lagging, slot dropped, gap, divergence, format
  refused, and conflict (R6.4). A product subscribes and drives its policy from them.

### R10 — The wire protocol and its listener

- **R10.1** Crystal owns a small, versioned replication protocol on its own port. A product configures the endpoint,
  the secret and TLS; it never implements the protocol.
- **R10.2** **Framing is a subset of RESP2:** requests are arrays of bulk strings; replies use simple strings, errors,
  integers, bulk strings and arrays, with RESP2's null bulk and null array. Nothing else (no RESP3, no inline
  commands, no pipelining beyond one request at a time per connection). Crystal implements it itself (no client
  library: `001` R3.3 allows no such dependency), porting Highway's server-side reader and writer.
- **R10.3** **Commands:** `HELLO`, `AUTH`, `DATABASES`, `PULL`, `SNAPSHOT BEGIN | GET | END`, `STATUS` (design § 8).
  Nothing else is accepted; an unknown command is an error and the connection stays open.
- **R10.4** **No control verbs on the wire.** Promote, demote, fence, follow and rejoin are in-process calls (R9.1). A
  node's role can never be changed through its port.
- **R10.5** **Authentication by challenge.** `HELLO` returns a fresh random nonce; `AUTH` answers with
  HMAC-SHA256 of the nonce and the node id, keyed by the store's shared secret, compared in constant time. The secret
  never crosses the wire. Before `AUTH` succeeds only `HELLO` and `AUTH` are accepted, frames are capped at 4 KiB, and
  an idle connection is closed after 10 s.
- **R10.6** **TLS is optional**, configured by certificate on the listener. When a client certificate is required, it
  is validated against the configured trust (Highway accepted any).
  _Amended 2026-10-11 (maintainer approved the shared secret plus the TLS option as the design): as first built, only
  the listener spoke TLS and the standby never did, so turning TLS on stopped replication, and no trust could be
  configured. Now a node's certificate is used both ways (it serves TLS and follows over TLS, presenting it as its
  client certificate); `TrustedCertificates` names the roots peers must chain to (the operating system's trust
  without it); the standby checks the primary's certificate name against the host it connects to. Crystal does no
  user authentication or authorization; replication's secret and TLS only prove the peer is a node of the cluster._
- **R10.7** The protocol version is an integer stated in `HELLO`. A node refuses a peer whose version it does not
  speak, naming both versions. Version 1 is this spec.
- **R10.8** Every frame has a size cap (after `AUTH`, the page size plus a margin); a declared length above it closes
  the connection. A malformed frame closes the connection.
- **R10.9** Logs show `host:port` and node ids only, never the secret, a nonce's response, or data.

### R11 — Retention

- **R11.1** **A replicated database retains its WAL** by age and total size: 24 h and 1 GiB per database by default,
  within a store-wide budget (16 GiB). A database that is not replicated keeps RocksDB's default (no retention), as
  today.
- **R11.2** **No standby can pin the log forever.** Holding WAL files from deletion is allowed only in bounded windows
  (snapshot capture; held readers, with an idle timeout and an absolute limit). A standby that falls past retention
  meets a gap (R5) and re-bootstraps.
- **R11.3** WAL compression stays off, so the log can be decoded for the tail, trimming and reconciliation
  (`001-2` R4.5).

### R12 — Writes are acknowledged by the primary

- **R12.1** Replication is **asynchronous**: a commit returns when the primary has it, with the durability chosen for
  it (`006`), not when a standby has it. The data at risk on a failover is the lag (R2.4), reported as a time.
- **R12.2** Waiting for standbys before acknowledging is **out of scope**, recorded as a possible later option.
- **R12.3** Batches are shipped as soon as they are committed, whatever their durability. A batch the primary later
  loses to a power failure is caught by divergence detection (R5.2) and never left on a standby.

### R13 — Observability

- **R13.1** Metrics on `007`'s meter, `crystal.replication.*`: role and epoch; slots and their states; lag in
  sequences and time, per database (tagged only when `TagMetricsWithDatabase` is on); bytes shipped and applied; pages
  applied; bootstraps; gaps; divergences; promotions, demotions, fences, conflicts.
- **R13.2** `CrystalStore.Status()` gains a replication section (role, epoch, followed primary, per-database
  position and lag, slots), and the protocol's `STATUS` returns the same as JSON.
- **R13.3** Log events in `CrystalLogEvents`' 2000s range (`007` R3.1), one per event of R9.3 plus connection
  established and lost; and a bounded log of recent transitions, readable through `Status()`.

### R14 — Verification

- **R14.1** **Two-process tests** (`004` T3):
  - a primary and a standby, writes to several databases, the standby caught up byte-identical;
  - kill the primary, promote the standby, check nothing acknowledged-and-replicated is lost;
  - restart the old primary as a standby of the new one.
- **R14.2** **Hard kills** of the standby mid-apply: the page is all or nothing; the position matches the data.
- **R14.3** **Gaps:** retention shortened under a stopped standby → a gap → re-bootstrap of that database only.
- **R14.4** **Divergence:** a standby that applied batches its primary then lost (simulated by restoring the primary
  to an earlier copy of the database) re-bootstraps that database; a standby behind the new primary does not.
- **R14.5** **Formats:** both directions, at `HELLO` and at snapshot, with a standby that downloads nothing and keeps
  waiting.
- **R14.6** **Protocol:** the RESP reader and writer against malformed frames, oversized lengths and partial reads;
  authentication (a wrong HMAC, a replayed response, a command before `AUTH`); a version mismatch; inspection with a
  plain RESP client (`STATUS` answers).
- **R14.7** **Cost:** an idle store with 50 replicated databases costs one `PULL` per wait interval and stays flat over
  hours; a caught-up pull reads no WAL; catch-up of 300 000 batches stays at page cost. Numbers from the benchmark
  harness (`008`), recorded here.
- **R14.8** **Epochs:** a restarted node keeps its epoch; a stale primary is fenced by a higher epoch; an equal-epoch
  conflict is reported, not resolved.
- **R14.9** The tests carried over from Highway's replication suite (`004` design § 2.5), adapted to the store's
  multi-database shape, each mutation-checked in Crystal.

### R15 — Reuse: copy and adapt

Implement by **copying and adapting** the sources below (playbook, *Reuse before writing*). Every copied file starts
with `// Ported from Highway: <path>`, and the tests that prove it come with it. StowDB has no replication.

| Source (Highway repository) | Lines | Take | Change |
|---|---|---|---|
| `src/Highway.Server/Resp/RespReader.cs` | 284 | parsing a request (array of bulk strings), incremental over a buffer, its protocol errors | Crystal's frame caps (R10.5, R10.8); a **reply reader** is new (Highway's replicas used a client library) |
| `src/Highway.Server/Commands/Runtime/RespWriter.cs` | 379 | writing simple strings, errors, integers, bulk strings, arrays, null array | only the subset (R10.2); Crystal's reply shapes (design § 8) |
| `src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs` | 1 123 | slots, lag cap, stale rule, drop events, pulls, snapshot sessions, epoch handling, promote, demote, fence, status, the reconciliation report | per database and UID; long-poll; slot never revived into a hold; session expiry; **drop** willingness, priority, roster, deadman, herd, `NOTPRIMARY`, `GOODBYE` |
| `src/Highway.Server/Storage/Rocks/ReplicaPuller.cs` | 391 | the pull loop, gap → resync marker, format refusals both ways, bootstrap retry with backoff | one `PULL` for all databases; divergence; set reconciliation; no client library |
| `src/Highway.Server/Storage/Rocks/ReplicationTail.cs`, `WalTailerPool.cs`, `WriteBatchTrim.cs` | 164 + 230 + 115 | the in-memory tail, held WAL tailers, group-commit trimming | per database, within the store budget |
| `src/Highway.Server/Storage/Rocks/BatchApplier.cs`, `ReplicationApply.cs` | 151 + 46 | skip, concatenate, one synced write per page, epoch check, derived watermark | per database; catalogue reload (R4.5); incarnation record (R5.2) |
| `src/Highway.Server/Storage/Rocks/WalTailSummary.cs` | 198 | decoding the WAL for reports | decodes per box |
| `src/Highway.Server/Storage/Rocks/RocksDbStore.cs` (`BootstrapSnapshotWithRetry`, `BootstrapSnapshotAtomic`, `WipeForResync`, `WipeForRejoin`) | — | atomic bootstrap, markers, backoff | per database; checksums; checkpoint beside the database |
| `src/Highway.Server/HighwayReplicationOptions.cs` | 119 | option names and defaults | per the decisions below |
| `src/Highway.Server/Observability/HighwayMetrics.cs` (replication instruments) | — | promotions, demotions, fences, role, epoch, slots, lag, RPO | `crystal.replication.*` on `007`'s meter |
| **not copied:** `HigherPeerWatch.cs`, `RosterRecord.cs`, `Willingness.cs`, the `CLIENT`/`PEER` forms of `HELLO`, `JOIN`, `GOODBYE`, `WITNESS`, the deadman fence, the topology channel | — | — | failover policy; stays Highway's |

Highway's own protocol document is not a source of truth: it describes a WAL hold at the slowest standby's position
that the code does not have. Port from the code.

## Out of scope

**Policy** (the products'): choosing a primary; automatic failover; client routing and redirection; client herds; peer
priority and willingness checks; membership rosters; graceful hand-off semantics that depend on the product's in-flight
work.

**Also not in this spec:** synchronous replication (R12.2); consensus, election, sharding; replicating a not-replicated
database (R1.5); backup and restore (its own spec, sharing the checkpoint code); compression of pages; more than one
request in flight per connection.

## Decisions

| Question | Decision | Basis |
|---|---|---|
| Framing | **A subset of RESP2**, Crystal's own reader and writer (maintainer, 2026-10-10) | Highway runs it in production; binary-safe; a node can be inspected with any RESP client; no dependency |
| Acknowledgement | **The positions in `PULL`** | one round trip per cycle; Highway needed three |
| Database identity on the wire | **UID**, set reconciled by `DATABASES` | numeric ids repeat across nodes; replicating `_crystal` would ship each node's own databases |
| Control | **In-process only** | nothing on the network can change a node's role |
| Authentication | **HMAC-SHA256 challenge**, one shared secret per store, optional TLS | the secret never crosses the wire, even without TLS |
| Divergence | **Incarnation and base** per database log | catches a standby ahead of its primary, which sequences alone cannot |
| In-memory tail budget | **16 MiB per replicated database** within a **store-wide 256 MiB**; equal shares, at least 1 MiB, when more databases than fit | 16 MiB took Highway's caught-up pull under 1 ms flat |
| WAL retention | **24 h and 1 GiB per replicated database**, within a **store-wide 16 GiB** | Highway's production values per instance |
| Long-poll and page size | **5 s** maximum wait, **1 MiB** pages (the whole reply) | Highway's page size; the wait keeps idle cost near zero without stalling shutdown |
| Snapshot sessions | **2 min** idle, **1 h** absolute; 1 MiB chunks | Highway's never expired; its 64 KiB chunks made large databases slow to fetch |
| Consistent points across databases | **Ruled out** (R1.7) | each database is its own instance |

## Revision 2026-10-10

What changed from the first draft, and why:
- **R1.3, R1.4 (new):** UIDs and set reconciliation replace "`_crystal` replicated first", which would have shipped
  each node's own databases and allocator.
- **R3.1, R3.4:** one `PULL` for every database, which is also the acknowledgement; `ACK` is gone.
- **R5.2, R5.5, R7.7, R12.3, R14.4 (new):** divergence detection, answering `006` open question 3.
- **R2.3:** a slot is an observation, never a hold (Highway's protocol document claimed a hold its code lacks).
- **R4.5 (new):** a standby reloads its catalogue when a page touches box 0.
- **R7.2, R7.5, R7.6:** checksums, session expiry and the checkpoint's location, each a gap found in Highway's code.
- **R9.1, R10.4:** control verbs leave the wire.
- **R10.2, R10.5, R10.6, R10.8:** the RESP2 subset, challenge authentication, validated client certificates, frame caps.
- **R11.1:** replicated databases retain their WAL; today every database opens with retention off.
- **R13:** built on `007`'s meter, log events and `Status()`.
- **Depends on** gained `006`, `007` and `004` T1, T3, T4; the old task T1 (`_crystal`, `node.id`, `node.epoch`) was
  done by `005`.
