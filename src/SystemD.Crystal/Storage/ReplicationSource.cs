// Spec 003 T7 / R2, R3; design § 3, § 4 — the primary side of replication: it answers DATABASES and PULL, keeps a
// slot per (standby, database), serves a pull from each database's tail or WAL within a page cap, long-polls a
// caught-up pull on commits and set-version changes, and reports gap / diverged / gone per database without failing
// the others. It implements IReplicationSource, which the connection calls. RocksDB-touching (it reaches the engine
// and each database's WalAccess), so it lives in Storage; the pure pieces (tail, trim, applier) are in Replication.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The primary's replication source (spec 003 design § 4). One per store, shared across every standby connection. It
/// opens each replicated database by UID on demand, keeps its serving state (<see cref="DatabaseReplication"/>), and
/// answers pulls from memory when it can. Slots are an observation (R2.3): every pull recomputes them, none holds a
/// log. The source is thread-safe — many standby connections pull at once — guarded by a lock over its per-UID map
/// and a concurrent slot map.
/// </summary>
internal sealed class ReplicationSource : IReplicationSource, IDisposable
{
    private readonly CrystalStore _store;
    private readonly StoreEngine _engine;
    private readonly string _nodeId;
    private readonly ReplicationEpoch _epoch;
    private readonly ReplicationOptions _options;

    private readonly Lock _dbGate = new();
    private readonly Dictionary<string, DatabaseReplication> _byUid = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly SnapshotSessions _snapshots;

    // A single store-wide change signal: a commit in any database, or a set-version change, releases a waiting pull.
    private readonly ChangeSignal _change = new();
    private volatile bool _disposed;

    public ReplicationSource(CrystalStore store, StoreEngine engine, string nodeId, ReplicationEpoch epoch, ReplicationOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshots = new SnapshotSessions(store.DataDirectory, engine.Clock);

        // R2.3: a standby that goes silent is dropped after SlotStaleAfter even if nobody pulls or reads the status, so its
        // drop event is raised on time. Pulls and status reads age the slots too.
        var period = options.SlotStaleAfter > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Clamp(options.SlotStaleAfter.Ticks / 4, TimeSpan.TicksPerSecond, 15 * TimeSpan.TicksPerSecond))
            : TimeSpan.FromSeconds(5);
        _slotTimer = engine.Clock.CreateTimer(_ => AgeSlotsQuietly(), null, period, period);
    }

    private readonly ITimer _slotTimer;
    private readonly Lock _slotGate = new();

    public string NodeId => _nodeId;

    public string Role => _epoch.RoleName;

    public long Epoch => _epoch.Epoch;

    public string Secret => _options.Secret;

    /// <summary>Raised for each replication event (R9.3). Set by the facade; null drops events.</summary>
    internal Action<ReplicationEvent>? OnEvent { get; set; }

    public void ObservePeer(long peerEpoch, string peerRole, string peerNodeId)
    {
        if (peerEpoch > _epoch.Epoch)
        {
            // A higher epoch: adopt it (R6.3). A primary here demotes to standby; its tails/readers/slots are cleared.
            var transition = _epoch.ObserveHigherEpoch(peerEpoch);
            if (transition != EpochTransition.None)
            {
                ClearAllHistory();
                OnEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.EpochChanged, _epoch.Epoch,
                    $"adopted epoch {peerEpoch} from node {peerNodeId}"));
                if (transition == EpochTransition.Demoted)
                {
                    OnEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.Demoted, _epoch.Epoch,
                        $"a higher epoch {peerEpoch} from node {peerNodeId} superseded this primary"));
                }
            }

            return;
        }

        // Equal epoch from another primary is a conflict (R6.4): report it with both node ids; never apply either way.
        if (peerEpoch == _epoch.Epoch
            && _epoch.Role == ReplicationRole.Primary
            && string.Equals(peerRole, ReplicationProtocol.RolePrimary, StringComparison.Ordinal)
            && !string.Equals(peerNodeId, _nodeId, StringComparison.Ordinal))
        {
            OnEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.Conflict, _epoch.Epoch,
                $"two primaries at epoch {_epoch.Epoch}: this node {_nodeId} and peer {peerNodeId}"));
        }
    }

    /// <summary>Clears every database's tail and held readers, and the slots (a new history on an epoch/role change, design § 3).</summary>
    internal void ClearAllHistory()
    {
        lock (_dbGate)
        {
            foreach (var serving in _byUid.Values)
            {
                serving.ClearTail();
            }
        }

        _slots.Clear();
    }

    /// <summary>
    /// Writes the demote reconciliation report (spec 003 R9.2): for each replicated database, the batches this node
    /// committed beyond the highest position any standby acknowledged, decoded per box, as JSON under
    /// <c>{dataDir}/replication/reconciliation/</c>. Returns the report path, or null when nothing was unreplicated.
    /// The raw WAL always holds the full tail; this is the legible index of it.
    /// </summary>
    internal string? WriteReconciliationReport()
    {
        var databases = new List<ReconciliationDatabase>();
        lock (_dbGate)
        {
            foreach (var serving in _byUid.Values)
            {
                using var scope = serving.Enter();
                if (scope is null)
                {
                    continue; // dropped or closing: nothing of it is served any more
                }

                var latest = serving.Latest;
                var acked = MinAckedForUid(serving.Uid);
                if (latest <= acked)
                {
                    continue; // every local write was acknowledged by every standby of this database
                }

                // Read the unreplicated tail from the acked position, decode per box.
                var slotHandle = _engine.OpenReplicatedByUid(_store, serving.Uid);
                if (slotHandle is null)
                {
                    continue;
                }

                var batches = new List<(ulong Seq, byte[] Data)>();
                var wal = new WalAccess(slotHandle.Instance);
                var status = wal.ReadPage((ulong)acked, int.MaxValue, batches);
                var (boxes, undecoded) = WalTailSummary.Describe(batches, id => NameOfBox(slotHandle, id));
                databases.Add(new ReconciliationDatabase(serving.Uid, acked, latest, status == WalPageStatus.Gap, undecoded, boxes));
            }
        }

        if (databases.Count == 0)
        {
            return null;
        }

        return Reconciliation.Write(_store.DataDirectory, _epoch.Epoch, databases);
    }

    /// <summary>The lowest acked position any standby of <paramref name="uid"/> reported (R9.2): the furthest-behind
    /// standby bounds what a failover could lose. 0 if no standby has a slot for it.</summary>
    private long MinAckedForUid(string uid)
    {
        long? min = null;
        foreach (var slot in _slots.Values)
        {
            // A dropped slot is a standby that must re-bootstrap anyway (R2.3); it does not hold the report's floor down.
            if (slot.State != SlotState.Dropped && string.Equals(slot.Uid, uid, StringComparison.Ordinal))
            {
                min = min is null ? slot.Acked : Math.Min(min.Value, slot.Acked);
            }
        }

        return min ?? 0;
    }

    private static string? NameOfBox(DatabaseSlot slot, uint boxId)
    {
        foreach (var info in slot.Context.ListBoxes())
        {
            if (info.Id == boxId)
            {
                return info.Name;
            }
        }

        return null;
    }

    public string StatusJson()
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("nodeId", _nodeId);
            writer.WriteString("role", _epoch.RoleName);
            writer.WriteNumber("epoch", _epoch.Epoch);
            writer.WriteNumber("setVersion", _engine.ReplicatedSetVersion);
            writer.WriteStartArray("slots");
            foreach (var slot in Slots())
            {
                writer.WriteStartObject();
                writer.WriteString("standby", slot.Standby);
                writer.WriteString("uid", slot.DatabaseUid);
                writer.WriteNumber("acked", slot.Acknowledged);
                writer.WriteString("state", slot.State.ToString().ToLowerInvariant());
                writer.WriteNumber("lagSequences", slot.LagSequences);
                writer.WriteNumber("lagMs", (long)slot.LagTime.TotalMilliseconds);
                writer.WriteString("lastContact", slot.LastContact.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Text.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Adopts <paramref name="observedEpoch"/> if it is higher (R6.3), and on any change clears every database's tail
    /// and held readers — a new epoch is a new history, nothing captured before it may be served (design § 3). The
    /// full promote/demote/fence/adoption policy is T12; this is the durable epoch bump and the history reset the tail
    /// and reader pool depend on. Returns the epoch in force after the call.
    /// </summary>
    public long ObserveHigherEpoch(long observedEpoch)
    {
        var before = _epoch.Epoch;
        var after = _epoch.BumpTo(observedEpoch);
        if (after != before)
        {
            lock (_dbGate)
            {
                foreach (var serving in _byUid.Values)
                {
                    serving.ClearTail();
                }
            }
        }

        return after;
    }

    public bool TryHandle(string command, IReadOnlyList<byte[]> args, string peerNodeId, RespWriter reply)
    {
        switch (command)
        {
            case ReplicationProtocol.Databases:
                HandleDatabases(reply);
                return true;
            case ReplicationProtocol.Pull:
                HandlePull(args, peerNodeId, reply);
                return true;
            case ReplicationProtocol.Snapshot:
                HandleSnapshot(args, reply);
                return true;
            default:
                return false; // unknown command → the connection answers CRYSTAL_ARG
        }
    }

    // ---------------------------------------------------------------- DATABASES

    /// <summary>How many <c>DATABASES</c> requests this source answered (tests: a catch-up must not ask before every pull).</summary>
    internal long DatabasesServed => Interlocked.Read(ref _databasesServed);

    private long _databasesServed;

    private void HandleDatabases(RespWriter reply)
    {
        Interlocked.Increment(ref _databasesServed);
        if (!IsPrimary(reply))
        {
            return;
        }

        // [setVersion, [[uid, name, latest]…]] (design § 8.2).
        var set = _engine.ReplicatedDatabaseSet();
        reply.ArrayHeader(2)
            .Integer(_engine.ReplicatedSetVersion)
            .ArrayHeader(set.Count);
        foreach (var db in set)
        {
            // ReplicatedDatabaseSet reports latest=0 for a database not currently open; open it so the standby sees a
            // real position (so a first pull does not look caught up at 0 when the primary has data).
            var latest = db.Latest;
            if (latest == 0)
            {
                var serving = Serving(db.Uid);
                using var scope = serving?.Enter();
                if (scope is not null)
                {
                    latest = serving!.Latest;
                }
            }

            reply.ArrayHeader(3)
                .BulkString(db.Uid)
                .BulkString(db.Name)
                .Integer(latest);
        }
    }

    // --------------------------------------------------------------------- PULL

    private void HandlePull(IReadOnlyList<byte[]> args, string peerNodeId, RespWriter reply)
    {
        if (!IsPrimary(reply))
        {
            return;
        }

        // PULL <maxBytes> <waitMs> (<uid> <from>)…
        if (args.Count < 3 || (args.Count - 3) % 2 != 0)
        {
            reply.Error($"{ReplicationProtocol.ErrArg} PULL takes maxBytes, waitMs and (uid from) pairs");
            return;
        }

        if (!TryParseLong(args[1], out var maxBytesRaw) || !TryParseLong(args[2], out var waitMsRaw))
        {
            reply.Error($"{ReplicationProtocol.ErrArg} PULL maxBytes and waitMs must be integers");
            return;
        }

        var maxBytes = (int)Math.Clamp(maxBytesRaw, 1, _options.PageBytes);
        var waitMs = (int)Math.Clamp(waitMsRaw, 0, (long)_options.LongPollMax.TotalMilliseconds);

        var requests = new List<(string Uid, long From)>();
        for (var i = 3; i < args.Count; i += 2)
        {
            var uid = Text.UTF8.GetString(args[i]);
            if (!TryParseLong(args[i + 1], out var from) || from < 0)
            {
                reply.Error($"{ReplicationProtocol.ErrArg} PULL from must be a non-negative integer");
                return;
            }

            requests.Add((uid, from));
            UpdateSlot(peerNodeId, uid, from);
        }

        AgeSlots(); // R2.3: every contact re-evaluates every slot

        // The mark is taken BEFORE building entries, so a change that lands after the build still wakes the wait below.
        // Build the entries once; if every database is caught up and nothing is to be sent, wait for a commit, a
        // set-version change, or an epoch or role change, up to waitMs, then build once more.
        var epoch = _epoch.Epoch;
        var mark = _change.Mark;
        var entries = BuildEntries(requests, maxBytes);
        if (waitMs > 0 && NothingToSend(entries) && _change.WaitForChange(mark, waitMs))
        {
            entries = BuildEntries(requests, maxBytes);
        }

        // The role and epoch are checked again before answering (003 should-fix 5): a node that stood down, or moved to
        // another epoch, while this pull waited answers as not primary rather than stamping entries with a history it
        // no longer serves. The standby reconnects and learns the new state.
        if (!IsPrimary(reply))
        {
            return;
        }

        if (_epoch.Epoch != epoch)
        {
            reply.Error($"{ReplicationProtocol.ErrNotPrimary} {_epoch.Epoch} {_epoch.RoleName}");
            return;
        }

        // [epoch, setVersion, [entry…]] (design § 8.2).
        reply.ArrayHeader(3)
            .Integer(epoch)
            .Integer(_engine.ReplicatedSetVersion)
            .ArrayHeader(entries.Count);
        foreach (var entry in entries)
        {
            WriteEntry(reply, entry);
        }
    }

    // ----------------------------------------------------------------- SNAPSHOT

    private void HandleSnapshot(IReadOnlyList<byte[]> args, RespWriter reply)
    {
        if (!IsPrimary(reply))
        {
            return;
        }

        // SNAPSHOT <BEGIN|GET|END> …
        if (args.Count < 2)
        {
            reply.Error($"{ReplicationProtocol.ErrArg} SNAPSHOT takes a subcommand");
            return;
        }

        var sub = Text.ASCII.GetString(args[1]).ToUpperInvariant();
        switch (sub)
        {
            case "BEGIN":
                HandleSnapshotBegin(args, reply);
                break;
            case "GET":
                HandleSnapshotGet(args, reply);
                break;
            case "END":
                HandleSnapshotEnd(args, reply);
                break;
            default:
                reply.Error($"{ReplicationProtocol.ErrArg} unknown SNAPSHOT subcommand '{sub}'");
                break;
        }
    }

    private void HandleSnapshotBegin(IReadOnlyList<byte[]> args, RespWriter reply)
    {
        // SNAPSHOT BEGIN <uid> <format> <layout>
        if (args.Count != 5)
        {
            reply.Error($"{ReplicationProtocol.ErrArg} SNAPSHOT BEGIN takes uid, format and layout");
            return;
        }

        var uid = Text.UTF8.GetString(args[2]);
        var format = Text.UTF8.GetString(args[3]);
        var layout = Text.UTF8.GetString(args[4]);
        if (format != ReplicationProtocol.Format || layout != ReplicationProtocol.Layout)
        {
            reply.Error($"{ReplicationProtocol.ErrFormat} ours={ReplicationProtocol.Format} theirs={format}");
            return;
        }

        // Inside the database for the whole checkpoint, so a drop, reinstall or close waits for it (tasks addendum,
        // review of the fixes); the incarnation and base are read once, from the same slot that is checkpointed.
        var serving = Serving(uid);
        using var scope = serving?.Enter();
        if (scope is null)
        {
            reply.Error($"{ReplicationProtocol.ErrArg} no replicated database with uid '{uid}'");
            return;
        }

        var history = serving!.Current;
        var session = _snapshots.Begin(serving.Slot, uid, history.Id, history.Base);

        // [epoch, uid, session, incarnation, base, checkpointSeq, [[file, size, crc32c]…], format]
        reply.ArrayHeader(8)
            .Integer(_epoch.Epoch)
            .BulkString(uid)
            .BulkString(session.Id)
            .BulkString(session.Incarnation.ToString("D"))
            .Integer(session.Base)
            .Integer(session.CheckpointSeq)
            .ArrayHeader(session.Manifest.Count);
        foreach (var file in session.Manifest)
        {
            reply.ArrayHeader(3)
                .BulkString(file.RelativePath)
                .Integer(file.Size)
                .Integer(file.Crc32C);
        }

        reply.BulkString(ReplicationProtocol.Format);
    }

    private void HandleSnapshotGet(IReadOnlyList<byte[]> args, RespWriter reply)
    {
        // SNAPSHOT GET <session> <file> <offset> <maxBytes>
        if (args.Count != 6)
        {
            reply.Error($"{ReplicationProtocol.ErrArg} SNAPSHOT GET takes session, file, offset and maxBytes");
            return;
        }

        var session = Text.UTF8.GetString(args[2]);
        var file = Text.UTF8.GetString(args[3]);
        if (!TryParseLong(args[4], out var offset) || !TryParseLong(args[5], out var maxBytes))
        {
            reply.Error($"{ReplicationProtocol.ErrArg} SNAPSHOT GET offset and maxBytes must be integers");
            return;
        }

        var chunk = _snapshots.Read(session, file, offset, (int)Math.Clamp(maxBytes, 1, _options.PageBytes));
        if (chunk is null)
        {
            reply.Error($"{ReplicationProtocol.ErrSession} {session}");
            return;
        }

        // [file, offset, data, nextOffset | null]
        reply.ArrayHeader(4)
            .BulkString(file)
            .Integer(offset)
            .BulkString(chunk.Value.Data);
        if (chunk.Value.NextOffset is { } next)
        {
            reply.Integer(next);
        }
        else
        {
            reply.NullBulkString();
        }
    }

    private void HandleSnapshotEnd(IReadOnlyList<byte[]> args, RespWriter reply)
    {
        // SNAPSHOT END <session>
        if (args.Count != 3)
        {
            reply.Error($"{ReplicationProtocol.ErrArg} SNAPSHOT END takes a session");
            return;
        }

        _snapshots.End(Text.UTF8.GetString(args[2]));
        reply.SimpleString("OK");
    }

    private int _nextFirst;

    /// <summary>
    /// One entry per requested database (design § 4). <paramref name="maxBytes"/> is the page for the <b>whole reply</b>
    /// (R3.2, 003 addendum B4): each database is served from what the ones before it left, and a database is skipped
    /// once the page is spent (its entry carries no batches; it is served on the next pull). A reply is bounded by
    /// <paramref name="maxBytes"/> plus one batch: a batch is never split, so the last one may cross the limit. The
    /// first database served rotates from pull to pull, so one database far behind cannot starve the others.
    /// </summary>
    private List<Entry> BuildEntries(List<(string Uid, long From)> requests, int maxBytes)
    {
        var entries = new Entry[requests.Count];
        var remaining = (long)maxBytes;
        var first = requests.Count == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _nextFirst) % (uint)requests.Count);
        for (var n = 0; n < requests.Count; n++)
        {
            var i = (first + n) % requests.Count;
            var (uid, from) = requests[i];
            var serving = Serving(uid);
            if (serving is null)
            {
                // No replicated database with that UID (dropped on the primary): gone (design § 4).
                entries[i] = Entry.Gone(uid);
                continue;
            }

            // Inside the database, so a drop or close waits for this step rather than closing the instance under it.
            using var scope = serving.Enter();
            if (scope is null)
            {
                entries[i] = Entry.Gone(uid); // dropped or closing right now; the next pull resolves it again
                continue;
            }

            var batches = new List<(ulong Seq, byte[] Data)>();
            var result = remaining > 0
                ? serving.Serve(from, (int)remaining, batches, out _)
                : serving.Position(from);
            foreach (var (_, data) in batches)
            {
                remaining -= data.Length;
            }

            entries[i] = result switch
            {
                ServeResult.Diverged => Entry.Diverged(uid, serving),
                ServeResult.Gap => Entry.GapAt(uid, serving, serving.FirstAvailable()),
                _ => Entry.Ok(uid, serving, batches),
            };
        }

        return [.. entries];
    }

    private static bool NothingToSend(List<Entry> entries)
    {
        // A caught-up pull: every entry is ok with no batches. A gap, diverged, gone, or any batch means answer now.
        foreach (var e in entries)
        {
            if (e.Status != ReplicationProtocol.EntryOk || e.Batches.Count > 0)
            {
                return false;
            }
        }

        return true;
    }

    private void WriteEntry(RespWriter reply, Entry entry)
    {
        // A PULL entry: [uid, status, incarnation, base, latest, batches, history] (design § 8.2). For gap, the first
        // available sequence stands in place of batches as an integer; for ok it is [[seq, bytes]…]. History lists the
        // earlier incarnations this log continues, [[incarnation, end]…] (003 addendum B3).
        reply.ArrayHeader(7)
            .BulkString(entry.Uid)
            .BulkString(entry.Status)
            .BulkString(entry.Incarnation)
            .Integer(entry.Base)
            .Integer(entry.Latest);

        if (entry.Status == ReplicationProtocol.EntryGap)
        {
            reply.Integer(entry.GapFirstAvailable);
        }
        else
        {
            reply.ArrayHeader(entry.Batches.Count);
            foreach (var (seq, data) in entry.Batches)
            {
                reply.ArrayHeader(2).Integer((long)seq).BulkString(data);
            }
        }

        var history = entry.History ?? [];
        reply.ArrayHeader(history.Count);
        foreach (var earlier in history)
        {
            reply.ArrayHeader(2).BulkString(earlier.Id.ToString("D")).Integer(earlier.End);
        }
    }

    // ----------------------------------------------------------------- plumbing

    /// <summary>Test-only: the serving state for a UID (opens the database), to assert held-reader behaviour (T7 Step B).</summary>
    internal DatabaseReplication? ServingForTest(string uid) => Serving(uid);

    /// <summary>The serving state for a replicated UID, opening the database if needed; null when there is none (gone).</summary>
    private DatabaseReplication? Serving(string uid)
    {
        lock (_dbGate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_byUid.TryGetValue(uid, out var existing))
            {
                if (existing.IsCurrent)
                {
                    return existing;
                }

                // The database was dropped or reopened since (003 addendum review): never serve a closed instance. Its
                // readers were released when the slot closed.
                existing.Detach();
                _byUid.Remove(uid);
                RebalanceTails();
            }

            var slot = _engine.OpenReplicatedByUid(_store, uid);
            if (slot is null)
            {
                return null;
            }

            var serving = new DatabaseReplication(uid, slot, _options.TailBytesPerDatabase, _engine.Clock, Signal);
            _byUid[uid] = serving;
            RebalanceTails();
            return serving;
        }
    }

    /// <summary>
    /// Gives every served database's tail an equal share of the store-wide tail budget (design § 3): at most
    /// <c>TailBytesPerDatabase</c> each, at least 1 MiB, recomputed whenever a database starts or stops being served.
    /// A share that shrinks drops the oldest batches of that tail at once; they are still served from the WAL.
    /// Called under <c>_dbGate</c>.
    /// </summary>
    private void RebalanceTails()
    {
        const long MiB = 1024 * 1024;
        var served = Math.Max(1, _byUid.Count);
        var share = Math.Max(MiB, Math.Min(_options.TailBytesPerDatabase, _options.TailBudgetBytes / served));
        foreach (var serving in _byUid.Values)
        {
            serving.Tail.SetMaxBytes(share);
        }
    }

    /// <summary>The snapshot sessions (tests: corrupt a chunk, watch expiry).</summary>
    internal SnapshotSessions SnapshotsForTest => _snapshots;

    /// <summary>The byte limit of a served database's tail (tests of the tail budget).</summary>
    internal long TailLimitForTest(string uid)
    {
        lock (_dbGate)
        {
            return _byUid.TryGetValue(uid, out var serving) ? serving.Tail.MaxBytes : 0;
        }
    }

    private void UpdateSlot(string standbyNodeId, string uid, long acked)
    {
        // The slot is keyed by (standby node id, uid), so two standbys at different positions do not share one slot
        // (R2.2). The standby node id is the authenticated one from HELLO. The acked value only ever advances; a stale
        // re-pull does not move it back. The state is left to AgeSlots, which every pull runs next: a contact within the
        // cap makes even a dropped slot active again (R2.3: an observation, not a hold), and a standby that stays behind
        // does not raise its event again on every pull.
        var key = standbyNodeId + "\u0000" + uid;
        var now = _engine.Clock.GetUtcNow();
        var epoch = _epoch.Epoch;
        _slots.AddOrUpdate(
            key,
            _ => new Slot(standbyNodeId, uid, acked, epoch, now, SlotState.Active),
            (_, existing) => existing with
            {
                Acked = Math.Max(existing.Acked, acked),
                Epoch = epoch,
                LastContact = now,
            });
    }

    /// <summary>
    /// Re-evaluates every slot (spec 003 R2.3; ported from Highway's <c>ReplicationFeeder.EnforceCap</c>, per database):
    /// <list type="bullet">
    /// <item>no contact for longer than <c>SlotStaleAfter</c> (unless zero): dropped;</item>
    /// <item>more than <c>LagCap</c> sequences behind its database's latest (unless the cap is zero): dropped;</item>
    /// <item>more than a quarter of the cap behind: lagging; otherwise active.</item>
    /// </list>
    /// Only a move into lagging or dropped raises its event, so a standby that stays behind raises it once. Dropping
    /// holds nothing and deletes nothing: it is what the primary reports, and a dropped slot that is in contact again and
    /// within the cap is active again. Serialised, so concurrent pulls and the timer raise each event once; a slot
    /// updated by a pull meanwhile keeps the pull's update.
    /// </summary>
    internal void AgeSlots()
    {
        lock (_slotGate)
        {
            var now = _engine.Clock.GetUtcNow();
            var cap = _options.LagCap;
            var staleAfter = _options.SlotStaleAfter;
            foreach (var (key, slot) in _slots)
            {
                string? reason = null;
                var next = SlotState.Active;
                if (staleAfter > TimeSpan.Zero && now - slot.LastContact > staleAfter)
                {
                    next = SlotState.Dropped;
                    reason = $"silent for {(long)(now - slot.LastContact).TotalMilliseconds} ms, past {(long)staleAfter.TotalMilliseconds} ms";
                }
                else if (cap > 0 && LatestOf(slot.Uid) is { } latest)
                {
                    var lag = Math.Max(0, latest - slot.Acked);
                    if (lag > cap)
                    {
                        next = SlotState.Dropped;
                        reason = $"{lag} sequences behind, past the cap of {cap}";
                    }
                    else if (lag > cap / 4)
                    {
                        next = SlotState.Lagging;
                        reason = $"{lag} sequences behind, past a quarter of the cap of {cap}";
                    }
                }

                if (next == slot.State || !_slots.TryUpdate(key, slot with { State = next }, slot))
                {
                    continue; // unchanged, or a pull updated it meanwhile: the next pass sees the fresh contact
                }

                if (next is SlotState.Lagging or SlotState.Dropped)
                {
                    OnEvent?.Invoke(new ReplicationEvent(
                        next == SlotState.Dropped ? ReplicationEventKind.SlotDropped : ReplicationEventKind.SlotLagging,
                        _epoch.Epoch,
                        $"standby {slot.StandbyNodeId}, database {slot.Uid}: {reason}"));
                }
            }
        }
    }

    private void AgeSlotsQuietly()
    {
        try
        {
            AgeSlots();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The store is closing; the timer is disposed with the source.
        }
    }

    /// <summary>
    /// Every slot with its lag (R2.2, R2.4, R13.2), after aging them. Lag in sequences is against the database's latest;
    /// lag in time is the age of the oldest batch the standby lacks (zero when caught up).
    /// </summary>
    internal IReadOnlyList<ReplicationSlot> Slots()
    {
        AgeSlots();
        var result = new List<ReplicationSlot>();
        foreach (var slot in _slots.Values)
        {
            var latest = LatestOf(slot.Uid);
            var lag = latest is { } l ? Math.Max(0, l - slot.Acked) : 0;
            var lagTime = lag > 0 && ServingIfCached(slot.Uid) is { } serving ? serving.LagTime(slot.Acked) : TimeSpan.Zero;
            result.Add(new ReplicationSlot(
                slot.StandbyNodeId, slot.Uid, slot.Acked, lag, lagTime, slot.State switch
                {
                    SlotState.Active => ReplicationSlotState.Active,
                    SlotState.Lagging => ReplicationSlotState.Lagging,
                    _ => ReplicationSlotState.Dropped,
                },
                slot.LastContact));
        }

        return result;
    }

    /// <summary>The serving state of <paramref name="uid"/> if it is cached and current; never opens a database.</summary>
    private DatabaseReplication? ServingIfCached(string uid)
    {
        lock (_dbGate)
        {
            return _byUid.TryGetValue(uid, out var serving) && serving.IsCurrent ? serving : null;
        }
    }

    /// <summary>The latest sequence of a served database, read inside it; null when it is not served or is closing.</summary>
    private long? LatestOf(string uid)
    {
        var serving = ServingIfCached(uid);
        using var scope = serving?.Enter();
        return scope is null ? null : serving!.Latest;
    }

    private void Signal() => _change.Signal();

    /// <summary>Wakes every waiting pull: the role or epoch changed (R3.4), and each must answer for the new state.</summary>
    internal void SignalRoleOrEpochChange() => _change.Signal();

    private bool IsPrimary(RespWriter reply)
    {
        if (_epoch.Role == ReplicationRole.Primary)
        {
            return true;
        }

        // A fenced or standby node serves nothing (design § 8.3): CRYSTAL_NOTPRIMARY <epoch> <role>.
        reply.Error($"{ReplicationProtocol.ErrNotPrimary} {_epoch.Epoch} {_epoch.RoleName}");
        return false;
    }

    private static bool TryParseLong(byte[] bytes, out long value)
    {
        var text = Text.ASCII.GetString(bytes);
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose()
    {
        lock (_dbGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var serving in _byUid.Values)
            {
                serving.Detach();
            }

            _byUid.Clear();
        }

        _slotTimer.Dispose();
        _snapshots.Dispose();
    }

    /// <summary>A primary slot: a standby's acked position in one database (spec 003 R2.3). An observation, not a lease.</summary>
    /// <summary>A standby's slot for one database (R2.2): its position, the epoch, last contact and state.</summary>
    private sealed record Slot(string StandbyNodeId, string Uid, long Acked, long Epoch, DateTimeOffset LastContact, SlotState State);

    private enum SlotState
    {
        Active,
        Lagging,
        Dropped,
    }

    /// <summary>One built pull entry, ready to write (design § 8.2).</summary>
    private readonly struct Entry
    {
        private Entry(string uid, string status, string incarnation, long @base, long latest,
            IReadOnlyList<(ulong Seq, byte[] Data)> batches, long gapFirstAvailable, IReadOnlyList<IncarnationEnd> history)
        {
            Uid = uid;
            Status = status;
            Incarnation = incarnation;
            Base = @base;
            Latest = latest;
            Batches = batches;
            GapFirstAvailable = gapFirstAvailable;
            History = history;
        }

        public IReadOnlyList<IncarnationEnd> History { get; }

        public string Uid { get; }
        public string Status { get; }
        public string Incarnation { get; }
        public long Base { get; }
        public long Latest { get; }
        public IReadOnlyList<(ulong Seq, byte[] Data)> Batches { get; }
        public long GapFirstAvailable { get; }

        public static Entry Ok(string uid, DatabaseReplication db, IReadOnlyList<(ulong Seq, byte[] Data)> batches)
            => Of(uid, ReplicationProtocol.EntryOk, db, batches, 0);

        public static Entry GapAt(string uid, DatabaseReplication db, long firstAvailable)
            => Of(uid, ReplicationProtocol.EntryGap, db, [], firstAvailable);

        public static Entry Diverged(string uid, DatabaseReplication db)
            => Of(uid, ReplicationProtocol.EntryDiverged, db, [], 0);

        private static Entry Of(string uid, string status, DatabaseReplication db, IReadOnlyList<(ulong Seq, byte[] Data)> batches, long gapFirst)
        {
            var history = db.Current; // one read: incarnation, base and history never mixed across a promotion
            return new(uid, status, history.Id.ToString("D"), history.Base, db.Latest, batches, gapFirst, history.History);
        }

        public static Entry Gone(string uid)
            => new(uid, ReplicationProtocol.EntryGone, Guid.Empty.ToString("D"), 0, 0, [], 0, []);
    }
}
