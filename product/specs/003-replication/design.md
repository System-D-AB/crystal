# 003 — Replication: design

> **Revised (draft) 2026-10-10** with the requirements. § 8 is the wire protocol, version 1.

## 1. Shape

```
 primary node                                             standby node
 ┌───────────────────────────────┐                        ┌───────────────────────────────┐
 │ store (epoch E, role Primary) │   one TCP connection   │ store (epoch E, role Standby) │
 │  db A ── commit ─► tail(A)    │◄══ PULL A@n B@m … ════│  db A (applied, read-only)    │
 │  db B ── commit ─► tail(B)    │═══ pages, epoch ══════►│  db B                         │
 │  local db (not replicated)    │                        │  local db (its own)           │
 │ ReplicationSource             │◄── SNAPSHOT (own conn) │ ReplicationFollower           │
 │ ReplicationListener :port     │                        │ ReplicationClient             │
 └───────────────────────────────┘                        └───────────────────────────────┘
   ▲ Promote · Demote · Fence · Follow · ScheduleRejoin · Status · Events (in-process) ▲
   └───────────────────────── the product's failover policy ──────────────────────────┘
```

**Node-local files** live outside every database and are never replicated: `node.id`, `node.epoch` (both from `005`),
and under `{dataDir}/replication/`: `follow/{uid}.json` (the incarnation and base a standby last applied from, R5.2),
`resync/{uid}` markers, the `rejoin` marker, and `reconciliation/{utc}.json` reports.

## 2. Identity and the database set (R1.3, R1.4)

- **The registry record gains `uid`** (`005`'s `DatabaseRecord`, version 2): a GUID assigned at create. A version 1
  record is given a UID when its registry is next loaded, written in the same batch as the load's first write (or
  immediately, in one synced batch, if the store is opened as a primary or standby).
- **Set version:** the primary's registry generation (`005` R4.2), filtered: it changes on any create, rename, swap or
  drop of a **replicated** database. The primary keeps it in memory as `ReplicatedSetVersion`.
- **Reconcile** (standby, on a new set version): read `DATABASES`; for each UID listed and absent locally, create the
  database with that UID (registry batch, `replicated = true`, no catalogue initialisation) and bootstrap it; for each
  UID present locally, replicated, and not listed, drop it; for each listed name differing from the local name, rename.
  A name clash with one of the standby's own (not replicated) databases is refused as an event, and that database is
  not followed until the clash is resolved by the product.
- **Note 2026-10-10 (spec `009`):** the UID's source is now the database's own identity record (box 0, space
  `0x07`); the registry record keeps a copy, which `DATABASES` reads without opening databases. A snapshot is
  checked before install: one whose identity is not the UID asked for is refused, and the database is left as it was.
  The install registers a new database with the UID and creation time read from the snapshot. As built, reconcile
  creates no database without a catalogue: a database new to the standby arrives whole through the install.

## 3. Positions, slots, the tail, held readers

- **Position:** the database's RocksDB latest sequence. On a standby, read from the database itself (R2.1).
- **Slots** (primary, memory): `(standbyNodeId, uid) → (acked, epoch, lastContact, state)`, updated by every `PULL`.
  `Dropped` is an observation (R2.3): the next pull recomputes the state; no log is held for it.
- **Tail** (primary, per replicated database): a ring of committed batches with their sequences, captured right after
  each commit, holding no lock across the write. It starts capturing on the first pull of that database, and is cleared
  on role or epoch change. Budget: 16 MiB per database within 256 MiB store-wide (equal shares, at least 1 MiB).
- **Held WAL readers:** at most 4 per database, keyed by the next sequence each serves. A reader opens only after
  `WalAccess` confirms the WAL still holds that sequence (the binding crashes on a range that is gone, `001-2` R4.1).
  While any reader is held, WAL file deletion is held off; the hold ends after 5 s idle and never lasts more than 30 s.
- **Incarnation and base** (primary, per replicated database, memory): set when the database opens on a primary and at
  promotion: `incarnation = new Guid`, `base = latest sequence`. Not persisted: a restart is a new incarnation.

## 4. A pull (R3)

```
PULL <maxBytes> <waitMs> <uid₁> <from₁> … <uidₙ> <fromₙ>

for each (uid, from):
    update slot(standby, uid) with acked = from
    db = replicated database uid      — none: entry "gone"
    if from == latest(db):            nothing for this db
    elif tail(db) holds from+1…:      serve from the tail
    elif a held reader is at from+1:  serve from it
    elif WAL holds from+1:            open a reader, serve, keep it
    else:                             entry "gap <firstAvailable>"
    a group-commit record straddling from → trimmed to start at from+1; untrimmable → "gap"
    stop adding batches when the reply reaches maxBytes (each db's first batch always fits)
if nothing to send and every db caught up:
    wait up to waitMs for: a commit in any listed db, a set-version change, an epoch or role change
reply [epoch, setVersion, entries]
```

Each entry carries the database's `incarnation`, `base` and `latest`, even when it has no batches, so a standby always
knows its lag and can check divergence.

**The standby loop** (one connection, `ReplicationFollower`):
1. `PULL` with every followed database and its position, `waitMs = 5000`, `maxBytes = 1 MiB`.
2. If the reply's epoch is newer, adopt it (R6.3); older, refuse the reply (R4.3).
3. If `setVersion` changed, reconcile (§ 2) before the next pull.
4. For each entry: check divergence (§ 6); apply its batches (§ 5); act on `gap`, `diverged` or `gone`.
5. Pull again. No sleep: an empty reply already waited on the primary.

On connection loss: reconnect with backoff 0.2, 0.5, 1, 2, 5 s (capped), forever, cancellable.

## 5. Applying (R4)

- **Skip** batches at or below the database's current sequence.
- **Concatenate** the remaining contiguous batches into one write batch (first sequence in its header, counts summed)
  and write it **synced, once per database page**. RocksDB's `IngestBatch` does not sync and ignores its sequence, so
  Crystal writes the bytes itself (`001-2` R4.3), through `RocksInstance.Write` (the one commit point, `004` T10).
- **Catalogue:** if any record of the page is in box 0, mark the database's catalogue stale; it is reloaded
  (`DatabaseContext.LoadCatalogue`) before the next read through the database (R4.5).
- **Record the incarnation:** after the write, write `follow/{uid}.json` (`{incarnation, base}`) if it changed, temp and
  rename (R5.5).

## 6. Divergence (R5.2)

On every entry, the standby compares the served `incarnation` with the one in `follow/{uid}.json`:

| Kept | Served | Position vs served base | Result |
|---|---|---|---|
| same | same | any | continue |
| none or different | any | `position ≤ base` | continue, and record the new incarnation |
| none or different | any | `position > base` | **diverged**: mark for re-bootstrap |

The primary can also refuse early: a `from` above its latest is diverged by definition (the standby has batches the
primary never had).

## 7. Snapshot bootstrap (R7)

On its own connection (`HELLO`, `AUTH`, then):

```
SNAPSHOT BEGIN <uid> <format> <layout>
  → [epoch, uid, session, incarnation, base, checkpointSeq, [[file, size, crc32c]…], format]  | -CRYSTAL_FORMAT …
SNAPSHOT GET <session> <file> <offset> <maxBytes>
  → [file, offset, data, nextOffset | null]
SNAPSHOT END <session>
  → +OK
```

- **Primary:** hold WAL deletion; create a RocksDB checkpoint in `{dataDir}/db-{id}.checkpoint-{session}` (same
  filesystem: hard links, R7.6); release the hold; compute each file's CRC-32C once; register the session. Sessions
  expire after 2 min idle or 1 h (R7.5), deleting the checkpoint.
- **Standby:** download each file into `{dataDir}/db-{id}.bootstrap-{guid}/`, 1 MiB chunks, verify size and CRC-32C
  (R7.2), then: close the database if open, rename its folder aside, rename the bootstrap folder into place, delete the
  old folder, reopen, record `follow/{uid}.json` from the snapshot's incarnation and base (R7.7). Any failure before
  the rename leaves the database as it was.
- **Unreachable primary:** backoff 1, 2, 5, 10, 30 s (capped), cancellable. **Format refusal:** retried every 5 minutes,
  logged once at error level, then at warning level.
- **Gap, divergence, rejoin:** the same path; rejoin applies it to every replicated database.

## 8. Wire protocol, version 1 (R10)

### 8.1 Framing: a subset of RESP2

| Type | Bytes | Used for |
|---|---|---|
| simple string | `+OK\r\n` | `OK` |
| error | `-CRYSTAL_<CODE> <detail>\r\n` | every refusal |
| integer | `:<n>\r\n` (signed 64-bit) | epochs, positions, sizes, versions |
| bulk string | `$<len>\r\n<bytes>\r\n`, null `$-1\r\n` | node ids, UIDs (36-char text), names, batch bytes, file data, JSON |
| array | `*<n>\r\n…`, null `*-1\r\n` | requests, replies, lists |

Requests are always an array of bulk strings, the command first; numbers in requests are decimal text. One request at
a time per connection; a reply always comes before the next request is read.

**Caps:** before `AUTH`, a frame may not exceed 4 KiB and the connection closes after 10 s without completing `AUTH`.
After `AUTH`, a request may not exceed 64 KiB; a reply's bulk strings are bounded by the page or chunk size asked for.
A declared length above a cap, or any malformed frame, closes the connection (Highway answered `-ERR protocol error`
and closed; Crystal closes without answering, as the peer cannot be trusted to read it).

### 8.2 Commands

| Command | Request | Reply |
|---|---|---|
| `HELLO` | `HELLO <protocolVersion> <nodeId> <role> <epoch> <format> <layout>` | `[protocolVersion, nodeId, role, epoch, format, nonce]` |
| `AUTH` | `AUTH <hmacHex>` | `+OK` |
| `DATABASES` | `DATABASES` | `[setVersion, [[uid, name, latest]…]]` |
| `PULL` | `PULL <maxBytes> <waitMs> (<uid> <from>)…` | `[epoch, setVersion, [entry…]]` |
| `SNAPSHOT` | as § 7 | as § 7 |
| `STATUS` | `STATUS` | bulk string: JSON of `Status()` (R13.2) |

- `role` is `primary`, `standby` or `fenced`; `format` is the store format (`crystal 1`); `layout` the column-family
  list (`default,general,delete_heavy`).
- `nonce` is 32 random bytes, hex. `hmacHex = hex(HMAC-SHA256(secret, nonce ‖ requesterNodeId))`. A nonce is valid for
  one `AUTH` on its connection only.
- A **PULL entry** is `[uid, status, incarnation, base, latest, batches]`, where `status` is `ok`, `gap`, `diverged` or
  `gone`; `batches` is `[[seq, bytes]…]` (possibly empty) for `ok`, and for `gap` the first available sequence is in
  place of `batches` as an integer. A database caught up with nothing new is still listed, with an empty `batches`.
  _(Amended 2026-10-10, tasks addendum B3: a seventh element, `history`, `[[incarnation, end]…]`, lists the earlier
  incarnations the log continues across a primary's restarts; see `engineering/replication.md` § 3 and § 8.)_
- Every reply to `PULL` and `SNAPSHOT BEGIN` starts with the replier's epoch; `HELLO` carries both nodes' epochs.

### 8.3 Errors

| Error | When |
|---|---|
| `-CRYSTAL_VERSION ours=<a> theirs=<b>` | `HELLO` with a protocol version this node does not speak |
| `-CRYSTAL_FORMAT ours=<f> theirs=<g>` | format or layout differs or is missing (`HELLO`, `SNAPSHOT BEGIN`) |
| `-CRYSTAL_AUTH` | wrong HMAC; then the connection closes |
| `-CRYSTAL_NOAUTH` | any command but `HELLO`/`AUTH` before `AUTH` |
| `-CRYSTAL_NOTPRIMARY <epoch> <role>` | `PULL`, `DATABASES`, `SNAPSHOT` on a node that is not primary (a fenced node serves nothing) |
| `-CRYSTAL_EPOCH ours=<e> theirs=<f>` | a request from a primary at the same epoch (conflict, R6.4), or with a stale epoch |
| `-CRYSTAL_SESSION <session>` | an unknown or expired snapshot session |
| `-CRYSTAL_ARG <detail>` | a malformed argument, unknown UID in `SNAPSHOT BEGIN`, unknown command |
| `-CRYSTAL_INTERNAL <detail>` | anything else; the detail never contains data or secrets |

### 8.4 A session

```
standby → HELLO 1 <B> standby 7 "crystal 1" default,general,delete_heavy
primary ← *6 :1 $<A> $primary :7 $"crystal 1" $<nonce>
standby → AUTH <hmac>
primary ← +OK
standby → DATABASES
primary ← *2 :42 *2 (*3 $<uidA> $shop :1200) (*3 $<uidB> $users :88)
standby → PULL 1048576 5000 <uidA> 1190 <uidB> 88
primary ← *3 :7 :42 *2 (*6 $<uidA> $ok $<inc> :0 :1200 *10 …) (*6 $<uidB> $ok $<inc> :0 :88 *0)
standby → PULL 1048576 5000 <uidA> 1200 <uidB> 88        (waits up to 5 s on the primary)
```

## 9. Code layout

```
src/SystemD.Crystal/Replication/
  Resp/RespReader.cs           requests (ported from Highway) and replies (new); frame caps
  Resp/RespWriter.cs           the subset (ported from Highway)
  ReplicationListener.cs       TCP accept loop, optional SslStream, one task per connection, HELLO/AUTH gate
  ReplicationSource.cs         primary: slots, PULL (long-poll), DATABASES, snapshot sessions, epochs (from ReplicationFeeder)
  ReplicationTail.cs, WalTailerPool.cs, WriteBatchTrim.cs   (ported)
  ReplicationClient.cs         standby: connection, HELLO/AUTH, request/reply
  ReplicationFollower.cs       standby: the pull loop, reconcile, divergence, apply (from ReplicaPuller, BatchApplier)
  Bootstrap.cs                 snapshot download, verify, atomic install (from RocksDbStore's bootstrap)
  Reconciliation.cs            demote report (from WalTailSummary)
  ReplicationOptions.cs        options and defaults (from HighwayReplicationOptions)
```

RocksDB stays inside `SystemD.Crystal.Storage` (`001` R3.2): replication types call `RocksInstance`, `WalAccess` and
`NativeRocks` through internal methods; anything touching RocksDB types lives in `Storage/`.

## 10. Public API

```csharp
public sealed class ReplicationOptions
{
    public string? ListenEndpoint { get; init; }          // e.g. "0.0.0.0:7700"; a standby listens too, to be promotable
    public required string Secret { get; init; }          // never logged
    public X509Certificate2? Certificate { get; init; }   // TLS on the listener
    public bool RequireClientCertificate { get; init; }
    public long LagCap { get; init; } = 100_000;          // sequences, per slot
    public TimeSpan SlotStaleAfter { get; init; } = TimeSpan.FromSeconds(60);
    public long TailBytesPerDatabase { get; init; } = 16L << 20;
    public long TailBudgetBytes { get; init; } = 256L << 20;
    public TimeSpan WalTtl { get; init; } = TimeSpan.FromHours(24);
    public long WalBytesPerDatabase { get; init; } = 1L << 30;
    public long WalBudgetBytes { get; init; } = 16L << 30;
    public TimeSpan LongPollMax { get; init; } = TimeSpan.FromSeconds(5);
    public int PageBytes { get; init; } = 1 << 20;
}

// CrystalOptions.Replication = new ReplicationOptions { ... }
public sealed class CrystalReplication            // store.Replication
{
    public ReplicationRole Role { get; }          // Primary, Standby, Fenced
    public long Epoch { get; }
    public void Follow(string primaryEndpoint);   // become a standby of it
    public void StopFollowing();
    public long Promote(string reason);           // returns the new epoch
    public void Demote();
    public void Fence(string reason);
    public void ScheduleRejoin(string primaryEndpoint);
    public event EventHandler<ReplicationEvent>? Events;
}
```

Blocking: `Promote`, `Demote`, `Fence` complete synchronously (memory and one synced file write). `Follow` starts the
follower and returns; bootstrap progress is reported through events and `Status()`.

## 11. Where it comes from, and what is left behind

| Highway | Crystal |
|---|---|
| RESP2 on the broker's port, client library on replicas | a RESP2 subset on Crystal's own port, its own reader and writer both sides |
| `HELLO` + `PULL` + `ACK` and a 50 ms sleep per idle cycle | one long-poll `PULL` for every database, which is also the acknowledgement |
| one RocksDB instance, one stream | a store: one stream per database, one connection per standby, UIDs, set reconciliation |
| replica watermark derived from the data | the same (R2.1) |
| slots, lag cap, stale rule, drop events; a dropped slot revived by `HELLO` | the same, per database; a slot is an observation, documented as such |
| in-memory tail, held WAL tailers, group-commit trim | the same, per database, within a store budget |
| one synced write per page; epoch-checked pages | the same, plus catalogue reload and divergence detection |
| snapshot: 64 KiB chunks, no checksums, sizes ignored, sessions never expire, checkpoint in system temp | 1 MiB chunks, CRC-32C verified, sessions expire, checkpoint beside the database |
| gap marker → wipe the whole store | per database: only that database re-bootstraps |
| epoch file, adoption of higher epochs, fencing | the same, node-wide (`node.epoch`) |
| equal-epoch tie broken by roster priority | reported as a conflict; the tie-break is policy |
| `PROMOTE`, `FENCE` on the wire for any authenticated client | in-process only |
| password `AUTH`; any client certificate accepted | HMAC challenge; client certificates validated when required |
| no protocol version | version in `HELLO`, refused by name |
| herd, roster, willingness, deadman, `NOTPRIMARY` redirects, `GOODBYE` | **not in Crystal**: product policy |

## 12. Risks

| Risk | Mitigation |
|---|---|
| Many databases multiply readers, tails and connections | one connection per standby; budgets store-wide; one long-poll for all; the 50-database cost test (R14.7) |
| A large catch-up reply starves the others | the page cap is per reply; each database's first batch always fits; the loop returns quickly |
| Cross-database points differ after a failover | stated limit (R1.7) |
| Porting subtle code wrong (trim, apply, tail) | port the tests with it; hard-kill and two-process tests are gates; mutation checks |
| Divergence check wrong | its own test (R14.4) and mutation checks; a needless re-bootstrap is the safe failure (R5.5) |
| A product's wrong policy decision | fencing and epochs make it an availability issue, never a corruption |
