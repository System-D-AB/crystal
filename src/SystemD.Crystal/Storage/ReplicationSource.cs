// Ported from Highway: src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs (slots, lag cap, pulls, epochs, status, the reconciliation report)
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
using System.Threading.Tasks;
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
        _snapshots = new SnapshotSessions(store.DataDirectory, engine.Clock, engine.RunHook);

        // R2.3: a standby that goes silent is dropped after SlotStaleAfter even if nobody pulls or reads the status, so its
        // drop event is raised on time. Pulls and status reads age the slots too.
        var period = options.SlotStaleAfter > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Clamp(options.SlotStaleAfter.Ticks / 4, TimeSpan.TicksPerSecond, 15 * TimeSpan.TicksPerSecond))
            : TimeSpan.FromSeconds(5);
        _slotTimer = engine.Clock.CreateTimer(_ => AgeSlotsQuietly(), null, period, period);

        // A change of the replicated set wakes a waiting pull at once (spec 011 R11.1, 003 R3.4), as a commit does.
        engine.RegistryChanged += _change.SignalUrgent;
    }

    private readonly ITimer _slotTimer;
    private readonly Lock _slotGate = new();

    public string NodeId => _nodeId;

    public string Role => _epoch.RoleName;

    public long Epoch => _epoch.Epoch;

    public string Secret => _options.Secret;

    /// <summary>Raised for each replication event (R9.3). Set by the facade; null drops events.</summary>
    internal Action<ReplicationEvent>? OnEvent { get; set; }

    /// <summary>Called with the path of a reconciliation report written by a demotion a peer caused (spec 011 R8.3).</summary>
    internal Action<string>? OnReport { get; set; }

    /// <summary>Called with the batch bytes of each pull reply sent (spec 011 R10.1).</summary>
    internal Action<long>? OnShipped { get; set; }

    /// <summary>
    /// Called for each database page served with batches (spec 014 R4.1, R3): its UID, whether it came from the tail
    /// (else the WAL, with why the tail missed), and for a tail page how long ago its oldest batch was committed.
    /// </summary>
    internal Action<string, bool, TailMiss, TimeSpan?>? OnPageServed { get; set; }

    public void ObservePeer(long peerEpoch, string peerRole, string peerNodeId, string? peerEndpoint = null)
    {
        if (peerEpoch > _epoch.Epoch)
        {
            // A higher epoch: adopt it (R6.3). A primary here demotes to standby; its tails/readers/slots are cleared.
            var transition = _epoch.ObserveHigherEpoch(peerEpoch);
            if (transition != EpochTransition.None)
            {
                // Every demotion writes the report (spec 011 R8.3), this one too: what this node committed beyond any
                // standby's acknowledgement. Written after the epoch's lock and the wait for admitted writes, and before
                // the slots are cleared below, since their acknowledged positions are the report's floor.
                string? report = null;
                try
                {
                    report = transition == EpochTransition.Demoted ? WriteReconciliationReport() : null;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The node has stood down either way: a report that could not be written must not stop the clearing
                    // and the events below (re-review 2026-10-13, M7).
                    report = null;
                    _ = ex;
                }
                if (report is not null)
                {
                    OnReport?.Invoke(report);
                }

                ClearAllHistory();
                OnEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.EpochChanged, _epoch.Epoch,
                    $"adopted epoch {peerEpoch} from node {peerNodeId}") { PeerNodeId = peerNodeId, PeerEndpoint = peerEndpoint });
                if (transition == EpochTransition.Demoted)
                {

                    OnEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.Demoted, _epoch.Epoch,
                        report is null
                            ? $"a higher epoch {peerEpoch} from node {peerNodeId} superseded this primary"
                            : $"a higher epoch {peerEpoch} from node {peerNodeId} superseded this primary; reconciliation report at {report}")
                    {
                        PeerNodeId = peerNodeId,
                        PeerEndpoint = peerEndpoint,
                    });
                }
            }

            return;
        }

        // Equal epoch from another primary is a conflict (R6.4): report it with both node ids and endpoints; never apply
        // either way. (The node-id check is moot since peers stating this node's own id are refused, spec 011 R1.3.)
        if (peerEpoch == _epoch.Epoch
            && _epoch.Role == ReplicationRole.Primary
            && string.Equals(peerRole, ReplicationProtocol.RolePrimary, StringComparison.Ordinal)
            && !string.Equals(peerNodeId, _nodeId, StringComparison.Ordinal))
        {
            OnEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.Conflict, _epoch.Epoch,
                $"two primaries at epoch {_epoch.Epoch}: this node {_nodeId} at {_options.ListenEndpoint} and peer {peerNodeId} at {peerEndpoint}")
            {
                PeerNodeId = peerNodeId,
                PeerEndpoint = peerEndpoint,
            });
        }
    }

    /// <summary>Clears every database's tail and held readers, and the slots (a new history on an epoch/role change, design § 3).</summary>
    internal void ClearAllHistory()
    {
        lock (_dbGate)
        {
            foreach (var serving in _byUid.Values)
            {
                // Entered like any call (spec 011 R6.1): a drop or close waits for the clear, and a database already
                // closing is skipped (its close releases the tail and readers itself).
                using var scope = serving.Enter();
                if (scope is null)
                {
                    continue;
                }

                _engine.RunHook("clear.entered");
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

        // Every replicated database of this node (spec 011 R8.1), not only those a standby happened to pull: one no
        // standby has pulled holds writes nobody acknowledged, from position 0.
        foreach (var replicated in _engine.ReplicatedDatabaseSet())
        {
            DatabaseSlot? slotHandle;
            try
            {
                slotHandle = _engine.OpenReplicatedByUid(_store, replicated.Uid);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A database that cannot be opened now (the open cap, a refused format) is listed as unreadable rather
                // than ending the report, or the demotion that writes it (review 2026-10-13, M7).
                databases.Add(new ReconciliationDatabase(replicated.Uid, MinAckedForUid(replicated.Uid), -1, TailUndecodableGap: true, UndecodedBatches: 0, Boxes: []));
                continue;
            }

            using var scope = slotHandle?.TryEnter();
            if (slotHandle is null || scope is null)
            {
                continue; // dropped or closing: nothing of it can be reported any more
            }

            var latest = (long)slotHandle.LatestSequence;
            var acked = MinAckedForUid(replicated.Uid);
            if (latest <= acked)
            {
                continue; // every local write was acknowledged by every standby of this database
            }

            // Read the unreplicated tail from the acked position, decode per box. A WAL that no longer reaches it is
            // reported as such (TailUndecodableGap): the batches before it are on disk but cannot be listed.
            var batches = new List<(ulong Seq, byte[] Data)>();
            var wal = new WalAccess(slotHandle.Instance);
            var status = wal.ReadPage((ulong)acked, int.MaxValue, batches);
            var (boxes, undecoded) = WalTailSummary.Describe(batches, id => NameOfBox(slotHandle, id));
            databases.Add(new ReconciliationDatabase(replicated.Uid, acked, latest, status == WalPageStatus.Gap, undecoded, boxes));
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

    public async ValueTask<bool> TryHandleAsync(string command, IReadOnlyList<byte[]> args, string peerNodeId, RespWriter reply, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case ReplicationProtocol.Databases:
                HandleDatabases(reply);
                return true;
            case ReplicationProtocol.Pull:
                await HandlePullAsync(args, peerNodeId, reply, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask HandlePullAsync(IReadOnlyList<byte[]> args, string peerNodeId, RespWriter reply, CancellationToken cancellationToken)
    {
        // Taken first, so a role or epoch change at any point of this pull ends its pacing (spec 014 review).
        var urgentMark = _change.UrgentMark;
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

        var maxBytes = (int)Math.Clamp(maxBytesRaw, 1, Math.Min(_options.PageBytes, ReplicationProtocol.MaxPageBytes));
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

        // Spec 014 R2.3: a standby near the head is answered at most once per gather window, so the commits of the window
        // ship together; one catching up is answered at once (R2.4).
        await PaceAsync(peerNodeId, urgentMark, cancellationToken).ConfigureAwait(false);

        // The mark is taken BEFORE building entries, so a change that lands after the build still wakes the wait below.
        // Build the entries once; if every database is caught up and nothing is to be sent, wait for a commit, a
        // set-version change, or an epoch or role change, up to waitMs, then build once more. The wait holds no thread
        // (spec 014 R5).
        var epoch = _epoch.Epoch;
        var mark = _change.Mark;
        var entries = BuildEntries(requests, maxBytes, out var behind);
        if (waitMs > 0 && NothingToSend(entries) && await WaitForChangeAsync(mark, waitMs, cancellationToken).ConfigureAwait(false))
        {
            entries = BuildEntries(requests, maxBytes, out behind);
        }

        _pace[peerNodeId] = new PullPace(_engine.Clock.GetTimestamp(), behind);

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

        ForgetWhatTheStandbyDoesNotHold(peerNodeId, requests, entries);

        // [epoch, setVersion, [entry…]] (design § 8.2).
        reply.ArrayHeader(3)
            .Integer(epoch)
            .Integer(_engine.ReplicatedSetVersion)
            .ArrayHeader(entries.Count);
        long shipped = 0;
        foreach (var entry in entries)
        {
            WriteEntry(reply, entry);
            foreach (var (_, data) in entry.Batches)
            {
                shipped += data.Length;
            }
        }

        OnShipped?.Invoke(shipped); // spec 011 R10.1
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

    /// <summary>The largest batch shipped in a page (spec 011 R4.1); lowered by tests, which cannot commit 32 MiB batches.</summary>
    internal int MaxShippableBatch { get; set; } = ReplicationProtocol.MaxShippableBatch;

    /// <summary>
    /// One entry per requested database (design § 4). <paramref name="maxBytes"/> is the page for the <b>whole reply</b>
    /// (R3.2, 003 addendum B4): each database is served from what the ones before it left, and a database is skipped
    /// once the page is spent (its entry carries no batches; it is served on the next pull). A reply is bounded by
    /// <paramref name="maxBytes"/> plus one batch: a batch is never split, so the last one may cross the limit. The
    /// first database served rotates from pull to pull, so one database far behind cannot starve the others.
    /// </summary>
    private List<Entry> BuildEntries(List<(string Uid, long From)> requests, int maxBytes, out bool behind)
    {
        // Whether this reply leaves the standby behind (spec 014 R2.4): a page cut short, a page read from the WAL, or
        // anything but a plain entry. Its next pull is then answered at once, not paced.
        behind = false;
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
            ServeSource source = default;
            ulong? more = null;
            var result = remaining > 0
                ? serving.Serve(from, (int)remaining, batches, out more, out source)
                : serving.Position(from);
            if (more is not null || remaining <= 0 || result != ServeResult.Served && result != ServeResult.CaughtUp
                || result == ServeResult.Served && !source.FromTail)
            {
                behind = true;
            }


            // A batch too large for any reply is never sent: the standby could not read the reply, and would ask again
            // forever (spec 011 R4.1). It re-bootstraps this database instead; the others are served as usual.
            if (batches.Exists(b => b.Data.Length > MaxShippableBatch))
            {
                entries[i] = Entry.Oversize(uid, serving);
                continue;
            }

            foreach (var (_, data) in batches)
            {
                remaining -= data.Length;
            }

            // Counted once it is known to ship (spec 014 R4.1): after the oversize check above.
            if (result == ServeResult.Served && batches.Count > 0 && OnPageServed is { } onPageServed)
            {
                TimeSpan? delay = source.FromTail && source.CommittedAt is { } committedAt ? _engine.Clock.GetElapsedTime(committedAt) : null;
                onPageServed(uid, source.FromTail, source.Miss, delay);
            }

            entries[i] = result switch
            {
                ServeResult.Diverged => Entry.Diverged(uid, serving),
                ServeResult.Gap => Entry.GapAt(uid, serving, serving.FirstAvailable()),
                _ => Entry.Ok(uid, serving, batches),
            };
        }

        foreach (var entry in entries)
        {
            behind |= entry.Status != ReplicationProtocol.EntryOk;
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

    /// <summary>
    /// A slot says what a standby holds, so it may never say more (review 2026-10-13, S2): a database the standby's PULL no
    /// longer names (it stopped following it, or is re-bootstrapping it) has no slot, and a standby told it diverged holds
    /// nothing of this log there, so its slot is at 0. Otherwise a stale position would count as caught up: in a handover
    /// (spec 011 R14) and as the reconciliation report's floor.
    /// </summary>
    private void ForgetWhatTheStandbyDoesNotHold(string standbyNodeId, List<(string Uid, long From)> requests, List<Entry> entries)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (uid, _) in requests)
        {
            named.Add(uid);
        }
        var prefix = standbyNodeId + "\u0000";
        foreach (var key in _slots.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && !named.Contains(key[prefix.Length..]))
            {
                _slots.TryRemove(key, out _);
            }
        }

        foreach (var entry in entries)
        {
            if (entry.Status == ReplicationProtocol.EntryDiverged)
            {
                UpdateSlot(standbyNodeId, entry.Uid, 0);
            }
        }
    }

    private void UpdateSlot(string standbyNodeId, string uid, long acked)
    {
        // The slot is keyed by (standby node id, uid), so two standbys at different positions do not share one slot
        // (R2.2). The standby node id is the authenticated one from HELLO. The acked value is the position the standby
        // last stated (spec 011 R8.2), forward or back. The state is left to AgeSlots, which every pull runs next: a contact within the
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
                // The position the standby last stated, not the highest ever (spec 011 R8.2): a standby that re-bootstraps
                // lower (after a divergence) holds no more than it says, and the report's floor and the lag follow it.
                Acked = acked,
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

    private ValueTask<bool> WaitForChangeAsync(long mark, int waitMs, CancellationToken cancellationToken)
    {
        _engine.RunHook("pull.waiting"); // tests: act only once a pull is really waiting
        return _change.WaitForChangeAsync(mark, TimeSpan.FromMilliseconds(waitMs), _engine.Clock, cancellationToken);
    }

    /// <summary>
    /// The gather window (spec 014 R2.3): a standby whose previous reply was neither a full page nor read from the WAL is
    /// answered no sooner than <see cref="ReplicationOptions.GatherWindow"/> after it, so the commits of the window ship in
    /// one reply. A standby catching up is answered at once (R2.4), and an urgent change (role, epoch, database set) ends
    /// the wait. Waits without holding a thread (R5).
    /// </summary>
    private async ValueTask PaceAsync(string peerNodeId, long urgentMark, CancellationToken cancellationToken)
    {
        var window = _options.GatherWindow;
        if (window <= TimeSpan.Zero || !_pace.TryGetValue(peerNodeId, out var last) || last.Behind)
        {
            return;
        }

        var left = window - _engine.Clock.GetElapsedTime(last.At);
        if (left <= TimeSpan.Zero)
        {
            return;
        }

        _engine.RunHook("pull.pacing"); // tests: act only once a pull is really pacing
        await _change.WaitForUrgentAsync(urgentMark, left, _engine.Clock, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>When a standby was last answered, and whether that reply left it behind (spec 014 R2.3, R2.4).</summary>
    private readonly record struct PullPace(long At, bool Behind);

    private readonly ConcurrentDictionary<string, PullPace> _pace = new(StringComparer.Ordinal);

    /// <summary>Wakes every waiting pull: the role or epoch changed (R3.4), and each must answer for the new state.</summary>
    internal void SignalRoleOrEpochChange() => _change.SignalUrgent();

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
        _engine.RegistryChanged -= _change.SignalUrgent;
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

        public static Entry Oversize(string uid, DatabaseReplication db)
            => Of(uid, ReplicationProtocol.EntryOversize, db, [], 0);

        private static Entry Of(string uid, string status, DatabaseReplication db, IReadOnlyList<(ulong Seq, byte[] Data)> batches, long gapFirst)
        {
            var history = db.Current; // one read: incarnation, base and history never mixed across a promotion
            return new(uid, status, history.Id.ToString("D"), history.Base, db.Latest, batches, gapFirst, history.History);
        }

        public static Entry Gone(string uid)
            => new(uid, ReplicationProtocol.EntryGone, Guid.Empty.ToString("D"), 0, 0, [], 0, []);
    }
}
