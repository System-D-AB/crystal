# Replication

> **Status: as built (spec `003`).** This document describes Crystal's replication as it is implemented today. Where
> this document and the code disagree, one of them has a defect, and it is fixed or recorded, never left silent. The
> requirements and the design in [`../product/specs/003-replication/`](../product/specs/003-replication/) are the
> authority for *why*; this is *what and where*, plus the wire protocol as the reference for anyone inspecting a node.

## 1. Shape

Crystal replicates a **store** of many databases from a primary to one or more standbys, over **one TCP connection per
standby**, on Crystal's own port. The node's **role** (primary, standby, fenced) and **epoch** are node-local — one
`node.epoch` file, never replicated — so one node is a primary or a standby as a whole; *which* databases ride the
connection is per database (`DatabaseRecord.Replicated`). A node offers `Promote`, `Demote`, `Fence`, `Follow`,
`StopFollowing`, `ScheduleRejoin`, `Status` and `Events` as **in-process calls** on `store.Replication`; no verb
changes a node's role over the wire. Crystal reports (positions, lag, gaps, divergence, conflicts) and the product
decides failover; Crystal's fencing keeps a wrong decision an availability problem, never a correctness one.

```
 primary node                                           standby node
 ┌───────────────────────────────┐                      ┌───────────────────────────────┐
 │ store (epoch E, Primary)      │   one TCP connection │ store (epoch E, Standby)      │
 │  db A ─ commit ─► tail(A)     │◄── PULL A@n B@m … ───│  db A (applied, read-only)    │
 │  db B ─ commit ─► tail(B)     │─── pages, epoch ────►│  db B                         │
 │  local db (not replicated)    │                      │  local db (its own)           │
 │ ReplicationSource :port       │◄── SNAPSHOT (own conn)│ ReplicationFollower           │
 └───────────────────────────────┘                      └───────────────────────────────┘
   ▲ Promote · Demote · Fence · Follow · ScheduleRejoin · Status · Events (in-process) ▲
```

Node-local files, never replicated, under `{dataDir}`: `node.id`, `node.epoch`, and under `{dataDir}/replication/`:
`follow/{uid}.json` (the incarnation and base a standby last applied from), `resync/{uid}` (a database to re-bootstrap),
`rejoin` (a node to rejoin on next follow), `reconciliation/*.json` (demote reports).

## 2. Code layout

RocksDB stays inside `SystemD.Crystal.Storage` (`001` R3.2, enforced by the gate tests). The pure, RocksDB-free pieces
live in `SystemD.Crystal.Replication`; the public control is `SystemD.Crystal.CrystalReplication`.

```
src/SystemD.Crystal/
  CrystalReplication.cs            store.Replication: Listen, Follow, Promote, Demote, Fence, ScheduleRejoin, Status, Events
  ReplicationEvent.cs              the events a product subscribes to (R9.3)
  Replication/
    ReplicationProtocol.cs         version 1 constants: commands, error codes, roles, caps, the HMAC challenge
    Resp/RespReader.cs, RespWriter.cs   the RESP2 subset, both sides (reply reader is Crystal's own)
    ReplicationListener.cs         TCP accept, optional TLS, client-cert validation
    ReplicationConnection.cs       the server state machine over a Stream: HELLO→AUTH gate, dispatch, frame caps
    ReplicationClient.cs           the standby side of one connection: handshake, request/reply
    IReplicationSource.cs          what the connection asks of the node (the primary implements it)
    ReplicationTail.cs             the in-memory ring of recent commits (pure)
    WriteBatchTrim.cs              trims a group-commit record to a cursor (pure)
    BatchApplier.cs                skip / concatenate / epoch-check / catalogue-flag over IApplyTarget (pure)
    DatabaseSetReconcile.cs        the reconcile diff (pure)
  Storage/
    ReplicationEpoch.cs            node.epoch and role, atomic persist, higher-epoch adoption
    ReplicationSource.cs           primary: DATABASES, PULL, SNAPSHOT, slots, long-poll, conflict, reconciliation report
    DatabaseReplication.cs         per-database serving state: tail, incarnation/base, serve(tail→reader→WAL→gap)
    WalReader.cs, WalReaderPool.cs held WAL readers across pulls (the catch-up optimisation)
    WalAccess.cs                   the WAL read/apply/checkpoint surface over RocksDB
    StandbyDatabase.cs             standby: apply a page, catalogue reload, follow record, divergence, read-only
    ReplicationFollower.cs         the standby loop: connect, reconcile, PULL all, apply, re-bootstrap, backoff
    SnapshotSessions.cs            primary: checkpoint + CRC-32C manifest + session expiry
    SnapshotBootstrap.cs           standby: download + verify (1 MiB chunks, size + CRC-32C)
    Crc32C.cs                      CRC-32C (Castagnoli), Crystal's own
    WalTailSummary.cs, Reconciliation.cs   the demote report, decoded per box
    FollowRecord.cs, ResyncMarker.cs, RejoinMarker.cs   the node-local markers
    ReplicationOptions.cs (as CrystalReplication's options)
  Observability/ReplicationMetrics.cs   crystal.replication.* instruments
```

## 3. Positions, the tail, held readers, incarnation

- **Position:** a database's RocksDB latest sequence. On a standby it is read from the database itself, derived — there
  is no separate watermark write, so a crash cannot leave the position ahead of the data (`BatchApplier`).
- **Tail** (primary, per replicated database): a ring of committed batches captured right after each commit, holding no
  lock across the write. It starts on the first pull, is cleared on any epoch or role change, and is budgeted: the
  served databases' tails share `TailBudgetBytes` (256 MiB) equally, each at most `TailBytesPerDatabase` (16 MiB) and
  at least 1 MiB, rebalanced whenever a database starts or stops being served. A tail whose share shrinks drops its
  oldest batches at once; they are still served from the WAL.
- **WAL retention** (replicated databases only): `WalTtl` (24 h), and a size limit that is the database's equal share
  of `WalBudgetBytes` (16 GiB) over the live replicated databases, at most `WalBytesPerDatabase` (1 GiB) and at least
  1 MiB. RocksDB cannot change this limit on an open database (it refuses the option at runtime), so a share is set as
  the database opens: one lowered by a later create applies when each database next opens, and until then the budget
  can be exceeded by the difference.
- **Slots** (primary, memory, per standby and database): the position the standby last reported, the epoch, its last
  contact and a state, updated by every `PULL` and cleared on any epoch or role change. Every pull, every status read
  and a timer (a quarter of `SlotStaleAfter`, 1 to 15 s) age them, as Highway's `EnforceCap` does: silent past
  `SlotStaleAfter` (60 s; zero turns it off) or more than `LagCap` sequences behind (100 000; zero turns it off) is
  **dropped**, more than a quarter of the cap is **lagging**, otherwise **active**. Only a move into lagging or dropped
  raises its event, so a standby that stays behind raises it once. A dropped slot is an observation, never a hold: it
  holds no log, and a standby in contact again within the cap is active again (or meets a gap). Dropped slots do not
  count in the demote report's floor. **Lag** is reported in sequences and in time: the age of the oldest batch the
  standby lacks, from commit times each served database samples every 100 ms (2 048 samples; a lower bound beyond
  them), zero when caught up.
- **Held WAL readers** (`WalReaderPool`): a `TransactionLogIterator` kept open across pulls, keyed by the sequence it
  serves next, so a standby catching up page by page does not re-read the live WAL from its start each time
  (O(pages), not O(pages²)). At most 4 per database; while any is held WAL file deletion is paused, released after 5 s
  idle and never more than 30 s. A reader is opened only after `WalAccess.WalHolds` confirms the WAL still reaches the
  sequence — the binding crashes on a gone range (`001-2` R4.1).
- **Incarnation, base and history** (per replicated database on a primary, node-local in
  `replication/incarnation/{uid}.json`, and on the database's slot). Set before the database's handle is published,
  so before anything can be written through it, and never at the first pull (a base taken then would include writes
  made since, and an old primary holding writes no standby received would pass as caught up):
  - **a record is deleted when the database receives data as a standby** (before the first page with batches is
    applied, and before a snapshot is installed): from then on its log is what it received, and none of this node's
    own earlier history may be continued;
  - **at the first open as a primary**, with no record: a fresh history, a new incarnation, `base` = latest;
  - **at a reopen (restart)**, with a record: a new incarnation, since a primary that lost unsynced batches to a power
    loss must not pass as the same log; `base` = the lower of the kept base and the latest recovered, so it never moves
    forward; and the **history** lists the earlier incarnations this log continues, newest first, each with the last
    sequence the log still shares with it (at most the latest recovered), up to 16;
  - **at promotion**, every open database the same way: continued if it still has a record (an old primary promoted
    again without having received anything keeps its base below its own unreplicated writes), fresh at the latest
    otherwise. The new histories are set and the epoch and role taken (which opens writes) in one step under the
    store gate, so no database opens in between without a history.

  A store opened without replication options continues a record that exists (its databases take writes), but starts
  none. A standby's databases and databases the store does not replicate keep an in-memory history nothing serves. A
  standby keeps the incarnation it last applied from in `follow/{uid}.json`, updated on every accepted page, and
  compares it to detect divergence. Serving a database, and taking its snapshot, enter it like any call, so a drop or
  close waits for them; its WAL readers are released while it closes, before its instance goes.

## 4. A pull

One `PULL` carries every followed database and its position; it is also the acknowledgement (there is no separate ACK).
`maxBytes` is the page for the **whole reply**: each database is served from what the ones before it left, a database
is skipped once the page is spent (its entry carries no batches and it is served on the next pull), a single batch
larger than what is left is still sent alone, and the first database served rotates from pull to pull. For each
database the primary serves from the tail if it holds the cursor, else from a held WAL reader, chunk after chunk until
the page is full, else reports a gap; a group-commit record straddling the cursor is trimmed to start just after it, or reported as a gap if it cannot
be trimmed. Each entry carries the database's incarnation, base and latest even when it has no batches, so the standby
always knows its lag and can check divergence. A caught-up pull long-polls up to `waitMs` on a commit, set-version or
epoch change, so an idle standby does not spin and a commit wakes a waiting pull at once.

The standby loop (`ReplicationFollower`): connect and handshake; read `DATABASES` and reconcile the set on a new
connection and whenever a `PULL` reply states a set version other than the one last reconciled (never before every
pull, so a catch-up costs one round trip per page); `PULL` every followed database; adopt a newer reply epoch (refuse an older one); for each entry check
divergence, apply its batches, or act on gap/diverged/gone; pull again. On connection loss, reconnect with backoff
0.2, 0.5, 1, 2, 5 s (capped), forever, cancellable.

**Reconcile** applies the primary's set **by UID, never by name**, in an order where no step meets a name another
still holds: first it drops the replicated databases the primary no longer lists; then it moves every database to be
renamed to a temporary name (`~reconcile-{uid}`, valid so that a crash between the steps leaves a store that opens) and
on to the primary's name, which handles swaps and chains of renames; then it bootstraps the new ones. An install never
takes a name another database holds. The set version counts as handled only when every step took effect, so a step
that failed, or a name clash with one of the standby's own databases, is tried again on the next pull (a clash is
logged once).

A waiting pull wakes on any commit, a change of the set, the epoch or the role, and **every** waiting pull wakes, not
one of several. It checks the role and epoch again before it answers: one that stood down or moved to another epoch
while it waited answers `CRYSTAL_NOTPRIMARY`, so no reply is stamped with a history the node no longer serves.

## 5. Applying, gaps, divergence

- **Apply** (`BatchApplier` over `StandbyDatabase`): skip batches at or below the position; concatenate a contiguous
  run into **one synced write per page** through the one commit point (`RocksInstance.Write`), so a crash keeps the
  whole page or none; refuse a page below the seen epoch, writing nothing. If a page wrote box 0 the catalogue is
  reloaded (`DatabaseContext.LoadCatalogue`). After the write, `follow/{uid}.json` is recorded if it changed.
- **A standby refuses every local write** while it follows a database (`DatabaseContext.FollowingPrimary` →
  `NotPrimaryException`); the applier's own writes go through `WalAccess.ApplyRaw`, which does not pass that path.
- **Gap:** the retained WAL no longer reaches the standby's position → that database re-bootstraps; the others keep
  following.
- **Divergence** (design § 6, as amended 2026-10-11: **exact history**). What a standby holds is named by its follow
  record: the incarnation it last applied from, and that log's history (each earlier incarnation with how far it is
  shared). What is served is the served incarnation and its history. The standby continues only if the two name a
  common incarnation, within both ends, at its position; otherwise that database re-bootstraps. The served base is no
  longer a reason to continue: a standby that once wrote as a primary holds its own batches below it.
  - When a node takes a primary role for a database (at promotion, or opening as a primary without a history of its
    own), its new history names the incarnation its data came from and that incarnation's history, each shared up to
    its latest; and its follow record then names its own incarnation, so if it ever follows another log it is
    recognised only where that log continues its own.
  - So a standby of a primary that restarted, or of the node that was promoted from following the same log, continues;
    a node that was promoted by mistake and wrote, then follows the real primary, re-bootstraps the databases it wrote
    to and continues the others.
  - A missing or unreadable follow record knows nothing shared: only an empty database continues, so a crash between
    apply and record costs at most a needless re-bootstrap.
- **One bootstrap path** (R5.4): first bootstrap, gap, divergence and rejoin all run the same snapshot download +
  atomic install.

## 6. Snapshot bootstrap

On its own connection: `SNAPSHOT BEGIN <uid>` checkpoints the database beside it (hard links; WAL deletion held during
the checkpoint), builds a manifest of each file with its size and **CRC-32C**, and opens a session that expires after
2 min idle or 1 h absolute, checked on every call and every 15 s, so an abandoned session's checkpoint is released
without another call.

The standby downloads each file in **1 MiB chunks** into a sibling `bootstrap-*` folder:
- every file stays inside that folder (a manifest naming an absolute path, a drive or a path climbing out with `..` is
  refused), and never grows past its stated size or takes a chunk at another offset than asked;
- a file whose size or CRC-32C does not match is **fetched again within the session**, up to 3 times;
- each file is flushed to disk before the install.

Then it checks the snapshot is the database asked for (spec 009), and **installs** it: close the database, move its
old folder aside (`db-*.replaced-*`), move the snapshot in, and only then delete what was moved aside; if the snapshot
cannot be moved in, the old folder goes back. It registers the record under the primary's UID, reopens, and records
`follow/{uid}.json` from the snapshot's incarnation and base. **A bootstrap that fails is retried** on the follower's
own schedule (1, 2, 5, 10, 30 s), not only when the primary's set changes. At store open, recovery deletes leftover
downloads and checkpoints, and puts back a database an install moved aside but never replaced. A format mismatch is
refused before a byte is downloaded; an unreachable primary is retried with backoff. **The follower never stops on its
own:** anything unexpected while following is logged, and it reconnects with backoff; only `StopFollowing`, promotion
or fencing ends it.

## 7. Epochs and fencing

The epoch and the role are node-local, in `node.epoch` (`"{epoch} {role}"`, role `primary` or `standby`), written
atomically. A node with no file starts as a primary at epoch 1. **Only a primary accepts local writes to replicated
databases:** a standby, demoted or fenced node refuses them with `NotPrimaryException` (a database the store does not
replicate is always writable). The role survives a restart: a demoted node, or a standby, comes back a standby; a
fenced node comes back a standby too, since a restart ends the fence but never makes a node that stood down a primary
again. **Promotion** stops following, gives every open database a new incarnation at what the node received, then
increments the epoch and persists it with the role **before** the node accepts writes or serves (a persist failure
fails the promotion), and clears the tails, readers and slots. A node adopts a **higher epoch** stated by an
authenticated peer only, after `AUTH` (a primary or fenced node demotes to standby); an epoch above 2^62 is refused.
**Two primaries at the same epoch** are reported as a conflict event with both node ids, and Crystal applies neither
into the other. **Fence** makes a node read-only at once. **Demote** writes a reconciliation report of what it
committed beyond any standby's acknowledgement, decoded per box.

## 8. The wire protocol, version 1 (the inspection reference)

### 8.1 Framing: a subset of RESP2

| Type | Bytes | Used for |
|---|---|---|
| simple string | `+OK\r\n` | `OK` |
| error | `-CRYSTAL_<CODE> <detail>\r\n` | every refusal |
| integer | `:<n>\r\n` (signed 64-bit) | epochs, positions, sizes, versions |
| bulk string | `$<len>\r\n<bytes>\r\n`, null `$-1\r\n` | node ids, UIDs (36-char text), names, batch bytes, file data, JSON |
| array | `*<n>\r\n…`, null `*-1\r\n` | requests, replies, lists |

Requests are an array of bulk strings, the command first; numbers in requests are decimal text. One request at a time
per connection; a reply always precedes the next request. **Caps:** before `AUTH` a frame may not exceed 4 KiB and the
connection closes after 10 s without `AUTH`; after `AUTH` a request may not exceed 64 KiB. A declared length above a
cap, or any malformed frame, closes the connection without an answer.

### 8.2 Commands

| Command | Request | Reply |
|---|---|---|
| `HELLO` | `HELLO <protocolVersion> <nodeId> <role> <epoch> <format> <layout>` | `[protocolVersion, nodeId, role, epoch, format, nonce]` |
| `AUTH` | `AUTH <hmacHex> <ownNonceHex>` | bulk string: the server's proof, `hex(HMAC-SHA256(secret, ownNonce ‖ serverNodeId))` |
| `DATABASES` | `DATABASES` | `[setVersion, [[uid, name, latest]…]]` |
| `PULL` | `PULL <maxBytes> <waitMs> (<uid> <from>)…` | `[epoch, setVersion, [entry…]]` |
| `SNAPSHOT BEGIN` | `SNAPSHOT BEGIN <uid> <format> <layout>` | `[epoch, uid, session, incarnation, base, checkpointSeq, [[file, size, crc32c]…], format]` |
| `SNAPSHOT GET` | `SNAPSHOT GET <session> <file> <offset> <maxBytes>` | `[file, offset, data, nextOffset \| null]` |
| `SNAPSHOT END` | `SNAPSHOT END <session>` | `+OK` |
| `STATUS` | `STATUS` | bulk string: JSON of the node's replication status |

- `role` is `primary`, `standby` or `fenced`; `format` is `crystal 1`; `layout` is `default,general,delete_heavy`.
- `nonce` is 32 random bytes, hex; `hmacHex = hex(HMAC-SHA256(secret, nonce ‖ requesterNodeId))`. The secret never
  crosses the wire; a nonce is valid for one `AUTH` on its connection only.
- **Authentication goes both ways.** `AUTH` carries the requester's own 32-byte nonce, and the server answers with its
  proof over it. A standby refuses a primary whose proof is missing or wrong before it takes a page, an epoch or a
  snapshot from it: anyone who can answer at the endpoint otherwise could feed it data.
- **A reply is read defensively.** Arrays may nest at most 16 deep, a declared array count must fit the frame cap at
  three bytes an element, and no array is sized from its declared count alone.
- A **PULL entry** is `[uid, status, incarnation, base, latest, batchesOrGap, history]`, where `status` is `ok`,
  `gap`, `diverged` or `gone`; for `ok`, `batchesOrGap` is `[[seq, bytes]…]` (possibly empty); for `gap`, it is the
  first available sequence as an integer; `history` is `[[incarnation, end]…]`, the earlier incarnations the log
  continues (possibly empty). An entry of another length is a protocol error.

### 8.3 Errors

`CRYSTAL_VERSION`, `CRYSTAL_FORMAT`, `CRYSTAL_AUTH`, `CRYSTAL_NOAUTH`, `CRYSTAL_NOTPRIMARY`, `CRYSTAL_EPOCH`,
`CRYSTAL_SESSION`, `CRYSTAL_ARG`, `CRYSTAL_INTERNAL`. A detail never contains data or a secret.

## 9. What Crystal does not carry from Highway

The herd, roster, willingness, deadman, GOODBYE drain and auto-failover are **product policy**, not Crystal's: Crystal
reports a conflict and lets the product resolve it. The RocksDB fork's vendored replication toolkit
(`ReplicationSource`/`ReplicationConsumer`/`WalTailer`) and its read-only/secondary instances are **not used** — see
[`rocksdb-binding.md`](rocksdb-binding.md) § 6 for why (the architecture gate forbids the toolkit types on Crystal's
surface, and Crystal's store-level multi-database protocol is richer than the toolkit's one-database model). Crystal
builds on the same `GetUpdatesSince` primitive the toolkit wraps, through `WalAccess`.

## 10. Observability

Metrics on `007`'s meter under `crystal.replication.*`: counters for promotions, demotions, fences, epoch changes,
conflicts, gaps, divergences, slot drops, format refusals, bootstraps, pages and bytes applied; observable gauges for
epoch and role, slots by state (`crystal.slot.state`), and the worst lag of the slots not dropped in sequences
(`crystal.replication.lag`) and seconds (`crystal.replication.lag_time`), per database when `TagMetricsWithDatabase`
is on. Log events in `CrystalLogEvents`' 2000s range, one per event of R9.3 plus connected/lost.
`store.Replication.Status()` reports role, epoch, the followed primary, the listener endpoint, a bounded log of recent
transitions and, on a primary, every slot with its lag and state; the protocol's `STATUS` returns the same as JSON.
