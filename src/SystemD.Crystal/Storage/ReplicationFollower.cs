// Spec 003 T9 / design § 4 — the standby follower loop, ported from Highway's ReplicaPuller (one connection, HELLO →
// PULL → apply) and generalised to a store: one connection, one PULL for every followed database, epoch adoption,
// reconcile on a new set version, reconnect with backoff. RocksDB-touching (it opens databases and applies through
// StandbyDatabase), so it lives in Storage; the pure pieces (the reconcile diff, the applier) are in Replication.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The standby's follower (spec 003 design § 4): one connection to the primary, one <c>PULL</c> carrying every
/// followed database and its position, applying each entry through a <see cref="StandbyDatabase"/>. It adopts a higher
/// epoch, reconciles its set on a new set version, and reconnects with backoff on connection loss. One per store;
/// started by <c>Follow</c>, stopped by <c>StopFollowing</c> or dispose.
/// </summary>
internal sealed class ReplicationFollower : IAsyncDisposable
{
    // Reconnect backoff (design § 4): 0.2, 0.5, 1, 2, 5 s, then capped at 5 s, forever, cancellable.
    private static readonly int[] BackoffMs = [200, 500, 1_000, 2_000, 5_000];

    // A format mismatch is retried rarely (R8.2): the primary must be upgraded, not merely restarted.
    private static readonly TimeSpan FormatRetry = TimeSpan.FromMinutes(5);

    private bool _formatRefused;

    private static bool IsFormatRefusal(ReplicationProtocolException ex) =>
        ex.Message.Contains(ReplicationProtocol.ErrFormat, StringComparison.Ordinal);

    private readonly CrystalStore _store;
    private readonly StoreEngine _engine;
    private readonly string _nodeId;
    private readonly ReplicationEpoch _epoch;
    private readonly ReplicationOptions _options;
    private readonly string _primaryEndpoint;
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, StandbyDatabase> _followed = new(StringComparer.Ordinal);

    // Bootstraps that failed, retried with backoff on the follower's own schedule (R7.2: a failed download starts
    // again), not only when the primary's set version next changes.
    private static readonly TimeSpan[] BootstrapRetry =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Dictionary<string, (string Name, DateTimeOffset Due, int Attempt)> _pendingBootstraps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clashesLogged = new(StringComparer.Ordinal);
    private readonly Task _loop;

    private long _lastSetVersion = -1;

    private readonly Action<ReplicationEvent>? _onEvent;

    public ReplicationFollower(
        CrystalStore store,
        StoreEngine engine,
        string nodeId,
        ReplicationEpoch epoch,
        ReplicationOptions options,
        string primaryEndpoint,
        string dataDirectory,
        ILogger? logger,
        Action<ReplicationEvent>? onEvent = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _primaryEndpoint = primaryEndpoint ?? throw new ArgumentNullException(nameof(primaryEndpoint));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _onEvent = onEvent;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>The follower's background loop task (for tests to observe faults; it swallows transient errors).</summary>
    public Task Loop => _loop;

    private async Task RunAsync(CancellationToken ct)
    {
        // A pending rejoin (R7.8): mark every replicated database for re-bootstrap so reconcile re-syncs them all by
        // the one bootstrap path, then consume the marker (a failed download leaves the resync markers, which the
        // next reconcile honours — the rejoin is fulfilled by them).
        if (RejoinMarker.Read(_dataDirectory) is not null)
        {
            foreach (var local in _engine.LocalDatabasesForReconcile())
            {
                if (local.Replicated)
                {
                    ResyncMarker.Write(_dataDirectory, local.Uid, "rejoin");
                }
            }

            RejoinMarker.Clear(_dataDirectory);
        }

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(ct).ConfigureAwait(false);
                if (_epoch.Role != ReplicationRole.Standby)
                {
                    return; // promoted, demoted elsewhere or fenced: this loop has nothing to follow any more
                }

                attempt = 0; // a clean return (a stale or stood-down primary) resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ReplicationProtocolException ex) when (IsFormatRefusal(ex))
            {
                // R8.2/R14.5: the primary's format or layout differs (or is missing). Download and apply nothing;
                // keep waiting, retrying rarely, so this node bootstraps on its own once the primary is upgraded.
                if (!_formatRefused)
                {
                    _formatRefused = true;
                    _logger?.LogError("[replication] primary format differs ({Error}); not replicating until it is upgraded", ex.Message);
                    _onEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.FormatRefused, _epoch.Epoch, ex.Message));
                }

                try
                {
                    await Task.Delay(FormatRetry, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Connection lost, the primary refused, or anything unexpected while applying or installing: back off and
                // reconnect (design § 4). The follower never stops on its own; only StopFollowing, promotion or fencing
                // ends it (tasks addendum: a single unexpected exception used to end it for the life of the process).
                if (ex is not (IOException or SocketException or ReplicationProtocolException or ObjectDisposedException))
                {
                    _logger?.LogError(ex, "[replication] the follower failed unexpectedly; it reconnects and goes on");
                }

                _formatRefused = false;
                var delay = BackoffMs[Math.Min(attempt, BackoffMs.Length - 1)];
                attempt++;
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        var (host, port) = ParseEndpoint(_primaryEndpoint);
        await using var stream = await ReplicationTls.ConnectAsync(host, port, _options, ct).ConfigureAwait(false);

        var client = new ReplicationClient(stream, _nodeId, _options.Secret);
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, _epoch.Epoch, ct).ConfigureAwait(false);

        if (client.PrimaryEpoch < 1 || client.PrimaryEpoch > ReplicationProtocol.MaxEpoch)
        {
            throw new ReplicationProtocolException($"the primary stated epoch {client.PrimaryEpoch}, outside 1 to {ReplicationProtocol.MaxEpoch}");
        }

        // Adopt the primary's epoch if it is newer (R6.3), before serving any page.
        if (client.PrimaryEpoch > _epoch.Epoch)
        {
            _epoch.BumpTo(client.PrimaryEpoch);
        }

        // The set version each PULL reply states. DATABASES is asked for only when it differs from the one last
        // reconciled (design § 4 step 3), never before every pull: a catch-up costs one round trip per page (003
        // addendum B4). Null until the first reply, so a new connection always reconciles first.
        long? statedSetVersion = null;
        while (!ct.IsCancellationRequested && _epoch.Role == ReplicationRole.Standby)
        {
            var setVersion = statedSetVersion is { } stated && stated == _lastSetVersion
                ? stated
                : await ReconcileIfChangedAsync(client, ct).ConfigureAwait(false);

            RetryDueBootstraps();
            var followed = CurrentFollowed();
            if (followed.Count == 0)
            {
                // Nothing to follow yet (reconcile may be a no-op until bootstrap, T10). Pull with no databases simply
                // long-polls on the primary; wait a beat so an idle standby does not spin.
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var pullReply = await PullAsync(client, followed, ct).ConfigureAwait(false);
            if (pullReply is null)
            {
                return; // the primary is no longer a primary; reconnect
            }

            var (pageEpoch, replySetVersion, entries) = pullReply.Value;
            statedSetVersion = replySetVersion;
            if (pageEpoch < 1 || pageEpoch > ReplicationProtocol.MaxEpoch)
            {
                throw new ReplicationProtocolException($"the primary stated epoch {pageEpoch}, outside 1 to {ReplicationProtocol.MaxEpoch}");
            }

            // A newer epoch on the reply is adopted; an older one means a stale primary — refuse the reply (R4.3).
            if (pageEpoch > _epoch.Epoch)
            {
                _epoch.BumpTo(pageEpoch);
            }
            else if (pageEpoch < _epoch.Epoch)
            {
                return; // stale primary: drop this connection, reconnect
            }

            var appliedAnything = ApplyEntries(pageEpoch, client.PrimaryNodeId, entries);
            _engine.RunHook("follower.applied");

            // No sleep when something was applied (catch-up runs flat out); the empty PULL already long-polled on the
            // primary, so a caught-up loop does not spin either.
            if (!appliedAnything)
            {
                _ = setVersion; // (kept for a future "resync on set-version churn" refinement)
            }
        }
    }

    /// <summary>Reads <c>DATABASES</c>, and if the set version changed since last time, applies the reconcile plan.</summary>
    private async Task<long> ReconcileIfChangedAsync(ReplicationClient client, CancellationToken ct)
    {
        var reply = await client.RequestAsync([Bytes(ReplicationProtocol.Databases)], ct).ConfigureAwait(false);
        if (reply.Type == RespType.Error)
        {
            throw new ReplicationProtocolException(reply.Text ?? "DATABASES refused");
        }

        if (reply.Type != RespType.Array || reply.Items is not { Count: 2 } top)
        {
            throw new ReplicationProtocolException("DATABASES reply was not a 2-element array");
        }

        var setVersion = top[0].Integer;
        var primarySet = ParseDatabaseSet(top[1]);

        if (setVersion != _lastSetVersion)
        {
            // The set version counts as handled only when every step took effect; otherwise the next pull asks again.
            if (Reconcile(primarySet))
            {
                _lastSetVersion = setVersion;
            }
        }

        return setVersion;
    }

    /// <summary>
    /// Brings the standby's followed set in line with the primary's (design § 2). Create-and-bootstrap is T10 (it
    /// needs the snapshot download), so here a listed UID the standby already has is followed and kept named; a
    /// replicated UID the primary no longer lists is dropped; a name difference is renamed; a clash with one of the
    /// standby's own databases is logged and left unfollowed. A UID the standby does not have yet is recorded as
    /// pending — T10 bootstraps it, then it joins the followed set.
    /// </summary>
    private bool Reconcile(IReadOnlyList<ReplicatedDatabase> primarySet)
    {
        var local = _engine.LocalDatabasesForReconcile();
        var localByUid = local.Where(d => d.Replicated).ToDictionary(d => d.Uid, StringComparer.Ordinal);
        var plan = DatabaseSetReconcile.Plan(
            primarySet,
            local);

        // Applied by UID, never by name, in an order where no step meets a name another still holds (003 should-fix 2):
        // 1. drop what the primary no longer lists, freeing its names;
        // 2. move every database to be renamed to a temporary name, then to the primary's name, so a swap or a chain of
        //    renames never meets a name still held;
        // 3. bootstrap the new ones last, under names now free.
        var complete = true;
        foreach (var step in plan.Where(s => s.Action == ReconcileAction.Drop))
        {
            complete &= TryDropByUid(step.Uid);
        }

        var renames = plan.Where(s => s.Action == ReconcileAction.Rename).ToList();
        foreach (var step in renames)
        {
            complete &= TryRenameByUid(step.Uid, ReconcileTemporaryName(step.Uid));
        }

        foreach (var step in renames)
        {
            complete &= TryRenameByUid(step.Uid, step.ToName!);
        }

        foreach (var step in plan.Where(s => s.Action == ReconcileAction.NameClash))
        {
            // Not complete: reconcile runs again on every pull until the product frees the name, then follows it. Logged
            // once per database, not on every pull.
            if (_clashesLogged.Add(step.Uid))
            {
                _logger?.LogWarning(
                    "[replication] the primary's database '{Name}' clashes with one of this node's own; not followed until the name is freed",
                    step.ToName);
            }

            complete = false;
        }

        foreach (var step in plan.Where(s => s.Action == ReconcileAction.CreateAndBootstrap))
        {
            TryBootstrap(step.Uid, step.ToName!); // a failure is retried on its own schedule
        }

        // Follow every listed UID the standby already has open (or can open), stopping local writes on it (R1.6). A
        // UID carrying a resync marker (a gap/divergence that did not finish, or found at open) is re-bootstrapped by
        // the one path instead of followed stale (R5.3, R5.4).
        foreach (var remote in primarySet)
        {
            if (_followed.ContainsKey(remote.Uid) || !localByUid.ContainsKey(remote.Uid))
            {
                continue;
            }

            if (ResyncMarker.Exists(_dataDirectory, remote.Uid))
            {
                TryBootstrap(remote.Uid, remote.Name);
                continue;
            }

            if (_engine.OpenReplicatedByUid(_store, remote.Uid) is { } slot)
            {
                _followed[remote.Uid] = new StandbyDatabase(slot, remote.Uid, _dataDirectory, _epoch.Epoch);
            }
        }

        return complete;
    }

    /// <summary>
    /// The name a database holds between the two steps of a reconcile rename. Not reserved (a name starting with '_'
    /// would make the registry refuse to open after a crash between the steps), and unique by UID; the next reconcile
    /// renames it on to the primary's name.
    /// </summary>
    internal static string ReconcileTemporaryName(string uid) => "~reconcile-" + uid;

    /// <summary>
    /// Bootstraps a new replicated database over its own connection (design § 7): download the snapshot, verify it,
    /// install it under the primary's UID, record the follow point, and add it to the followed set. A failure leaves
    /// nothing installed (the download goes to a sibling directory); it is retried on the next reconcile.
    /// </summary>
    private void TryBootstrap(string uid, string name)
    {
        var bootstrapDir = Path.Combine(_dataDirectory, $"bootstrap-{Guid.NewGuid():N}");
        try
        {
            var begin = DownloadSnapshot(uid, bootstrapDir);
            _engine.RunHook("bootstrap.downloaded");
            var handle = _engine.InstallBootstrap(_store, uid, name, bootstrapDir);

            // Record the follow point from the snapshot's incarnation and base (R7.7), clear any resync marker (the
            // re-bootstrap this fulfils, R5.4), then follow the database.
            FollowRecord.Write(_dataDirectory, uid, begin.Incarnation, begin.Base);
            ResyncMarker.Clear(_dataDirectory, uid);
            if (_engine.OpenReplicatedByUid(_store, uid) is { } slot)
            {
                _followed[uid] = new StandbyDatabase(slot, uid, _dataDirectory, _epoch.Epoch);
            }

            _logger?.LogInformation("[replication] bootstrapped a replicated database from a snapshot ({Files} files)", begin.Manifest.Count);
            _ = handle;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            TryDeleteDirectory(bootstrapDir);
            var attempt = _pendingBootstraps.TryGetValue(uid, out var pending) ? pending.Attempt + 1 : 0;
            var wait = BootstrapRetry[Math.Min(attempt, BootstrapRetry.Length - 1)];
            _pendingBootstraps[uid] = (name, _engine.Clock.GetUtcNow() + wait, attempt);
            _logger?.LogWarning("[replication] snapshot bootstrap failed ({Error}); retrying in {Wait}", ex.Message, wait);
            return;
        }

        _pendingBootstraps.Remove(uid);
    }

    /// <summary>Retries every failed bootstrap whose backoff has passed (R7.2, R7.4).</summary>
    private void RetryDueBootstraps()
    {
        if (_pendingBootstraps.Count == 0)
        {
            return;
        }

        var now = _engine.Clock.GetUtcNow();
        foreach (var (uid, pending) in _pendingBootstraps.ToList())
        {
            if (pending.Due <= now && !_followed.ContainsKey(uid))
            {
                TryBootstrap(uid, pending.Name);
            }
        }
    }

    private SnapshotBegin DownloadSnapshot(string uid, string bootstrapDir)
    {
        // Its own connection (design § 7): a snapshot download must not block the pull loop's connection.
        var (host, port) = ParseEndpoint(_primaryEndpoint);
        using var stream = ReplicationTls.ConnectAsync(host, port, _options, _cts.Token).GetAwaiter().GetResult();
        var client = new ReplicationClient(stream, _nodeId, _options.Secret);
        client.HandshakeAsync(ReplicationProtocol.RoleStandby, _epoch.Epoch, _cts.Token).GetAwaiter().GetResult();
        return SnapshotBootstrap.DownloadAsync(client, uid, bootstrapDir, _cts.Token).GetAwaiter().GetResult();
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private IReadOnlyList<(string Uid, StandbyDatabase Db)> CurrentFollowed() =>
        _followed.Select(kv => (kv.Key, kv.Value)).ToList();

    private async Task<(long Epoch, long SetVersion, IReadOnlyList<PullEntry> Entries)?> PullAsync(
        ReplicationClient client, IReadOnlyList<(string Uid, StandbyDatabase Db)> followed, CancellationToken ct)
    {
        // PULL <maxBytes> <waitMs> (<uid> <from>)…
        var args = new List<byte[]>
        {
            Bytes(ReplicationProtocol.Pull),
            Bytes(_options.PageBytes.ToString(CultureInfo.InvariantCulture)),
            Bytes(((int)_options.LongPollMax.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var (uid, db) in followed)
        {
            args.Add(Bytes(uid));
            args.Add(Bytes(((long)db.Watermark).ToString(CultureInfo.InvariantCulture)));
        }

        var reply = await client.RequestAsync(args, ct).ConfigureAwait(false);
        if (reply.Type == RespType.Error)
        {
            if ((reply.Text ?? string.Empty).StartsWith(ReplicationProtocol.ErrNotPrimary, StringComparison.Ordinal))
            {
                return null; // the peer is not a primary (fenced/demoted): reconnect
            }

            throw new ReplicationProtocolException(reply.Text ?? "PULL refused");
        }

        if (reply.Type != RespType.Array || reply.Items is not { Count: 3 } top)
        {
            throw new ReplicationProtocolException("PULL reply was not a 3-element array");
        }

        var entries = ParseEntries(top[2]);
        return (top[0].Integer, top[1].Integer, entries);
    }

    private bool ApplyEntries(long pageEpoch, string primaryName, IReadOnlyList<PullEntry> entries)
    {
        var appliedAnything = false;
        foreach (var entry in entries)
        {
            if (!_followed.TryGetValue(entry.Uid, out var db))
            {
                continue; // an entry for a database we are not following yet (awaiting bootstrap)
            }

            switch (entry.Status)
            {
                case var s when s == ReplicationProtocol.EntryOk:
                    // Divergence check before applying (design § 6): a different served incarnation with this standby
                    // past the served base means it holds batches the primary's log never had (R5.2). Re-bootstrap.
                    if (db.HasDiverged(entry.Incarnation, entry.Base, entry.History))
                    {
                        MarkAndReBootstrap(entry.Uid, "divergence");
                        break;
                    }

                    var result = db.Apply(pageEpoch, entry.Batches, entry.Incarnation, entry.Base, entry.History);
                    if (result.Status == ApplyStatus.Applied && result.Applied > 0)
                    {
                        appliedAnything = true;
                    }

                    break;
                case var s when s == ReplicationProtocol.EntryGap:
                    MarkAndReBootstrap(entry.Uid, "gap");
                    break;
                case var s when s == ReplicationProtocol.EntryDiverged:
                    MarkAndReBootstrap(entry.Uid, "diverged (primary reported from above its latest)");
                    break;
                case var s when s == ReplicationProtocol.EntryGone:
                    // The primary dropped it; reconcile will drop it locally on the next set-version change.
                    break;
            }
        }

        _ = primaryName;
        return appliedAnything;
    }

    /// <summary>
    /// Marks <paramref name="uid"/> for re-bootstrap (R5.3) and re-bootstraps it now by the one bootstrap path (R5.4):
    /// stop following it, download a fresh snapshot, install, record the follow point, follow again. The marker
    /// survives a crash so a restart re-bootstraps it; it is cleared only on a successful install. Only this database
    /// is affected — the others keep following.
    /// </summary>
    private void MarkAndReBootstrap(string uid, string reason)
    {
        ResyncMarker.Write(_dataDirectory, uid, reason);
        _logger?.LogWarning("[replication] database re-bootstrap required ({Reason}); re-syncing it alone", reason);
        var kind = reason.StartsWith("gap", StringComparison.Ordinal) ? ReplicationEventKind.Gap : ReplicationEventKind.Divergence;
        _onEvent?.Invoke(new ReplicationEvent(kind, _epoch.Epoch, $"re-bootstrapping a database ({reason})") { Uid = uid });

        if (_followed.Remove(uid, out var db))
        {
            db.StopFollowing();
        }

        // Resolve the primary's name for this UID from the last DATABASES we parsed; if unknown, the next reconcile
        // picks it up (the marker persists). We re-use the name the database already has locally.
        var name = NameForUid(uid);
        if (name is not null)
        {
            TryBootstrap(uid, name);
        }
    }

    private string? NameForUid(string uid)
    {
        foreach (var local in _engine.LocalDatabasesForReconcile())
        {
            if (string.Equals(local.Uid, uid, StringComparison.Ordinal))
            {
                return local.Name;
            }
        }

        return null;
    }

    /// <summary>Renames the replicated database <paramref name="uid"/> to <paramref name="name"/>; true if it now has it.</summary>
    private bool TryRenameByUid(string uid, string name)
    {
        try
        {
            if (_engine.OpenReplicatedByUid(_store, uid) is not { } slot)
            {
                return false;
            }

            if (!string.Equals(slot.Handle.Name, name, StringComparison.Ordinal))
            {
                _engine.RenameDatabase(slot.Handle, name); // the engine, not the store: a standby's own reconcile (R1.4)
            }

            return true;
        }
        catch (Exception ex) when (ex is DatabaseNotFoundException or DatabaseNameTakenException or ArgumentException)
        {
            _logger?.LogWarning("[replication] reconcile rename could not complete ({Error}); tried again on the next pull", ex.Message);
            return false;
        }
    }

    /// <summary>Drops the replicated database <paramref name="uid"/>; true once it is gone.</summary>
    private bool TryDropByUid(string uid)
    {
        try
        {
            if (_engine.OpenReplicatedByUid(_store, uid) is not { } slot)
            {
                return true; // already gone
            }

            if (_followed.Remove(uid, out var db))
            {
                db.StopFollowing();
            }

            _engine.DropDatabase(slot.Handle); // the engine, not the store: a standby's own reconcile (R1.4)
            return true;
        }
        catch (DatabaseNotFoundException)
        {
            return true; // already gone
        }
    }

    // ---------------------------------------------------------------- parsing

    private static IReadOnlyList<ReplicatedDatabase> ParseDatabaseSet(RespValue list)
    {
        var set = new List<ReplicatedDatabase>();
        if (list.Items is null)
        {
            return set;
        }

        foreach (var row in list.Items)
        {
            if (row.Items is { Count: 3 } f)
            {
                set.Add(new ReplicatedDatabase(Text.UTF8.GetString(f[0].Bytes ?? []), Text.UTF8.GetString(f[1].Bytes ?? []), f[2].Integer));
            }
        }

        return set;
    }

    private static IReadOnlyList<PullEntry> ParseEntries(RespValue list)
    {
        var entries = new List<PullEntry>();
        if (list.Items is null)
        {
            return entries;
        }

        foreach (var row in list.Items)
        {
            // [uid, status, incarnation, base, latest, batchesOrGap, history]
            if (row.Items is not { Count: 7 } f)
            {
                throw new ReplicationProtocolException("a PULL entry was not a 7-element array");
            }

            var uid = Text.UTF8.GetString(f[0].Bytes ?? []);
            var status = Text.UTF8.GetString(f[1].Bytes ?? []);
            var incarnation = Text.UTF8.GetString(f[2].Bytes ?? []);
            var @base = f[3].Integer;
            var batches = new List<(ulong Seq, byte[] Data)>();
            if (status == ReplicationProtocol.EntryOk && f[5].Items is { } rows)
            {
                foreach (var b in rows)
                {
                    if (b.Items is { Count: 2 } pair)
                    {
                        batches.Add(((ulong)pair[0].Integer, pair[1].Bytes ?? []));
                    }
                }
            }

            var history = new List<IncarnationEnd>();
            foreach (var h in f[6].Items ?? [])
            {
                if (h.Items is not { Count: 2 } pair || !Guid.TryParseExact(Text.UTF8.GetString(pair[0].Bytes ?? []), "D", out var id))
                {
                    throw new ReplicationProtocolException("a PULL entry's history was not [[incarnation, end]…]");
                }

                history.Add(new IncarnationEnd(id, pair[1].Integer));
            }

            entries.Add(new PullEntry(uid, status, incarnation, @base, batches, history));
        }

        return entries;
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(endpoint.AsSpan(colon + 1), out var port))
        {
            throw new ArgumentException($"'{endpoint}' is not a valid host:port endpoint.", nameof(endpoint));
        }

        var host = endpoint[..colon].Trim('[', ']');
        return (host.Length == 0 ? "127.0.0.1" : host, port);
    }

    private static byte[] Bytes(string s) => Text.UTF8.GetBytes(s);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // Cancelled or faulted on shutdown; nothing to surface.
        }

        foreach (var db in _followed.Values)
        {
            db.StopFollowing();
        }

        _cts.Dispose();
    }

    /// <summary>One parsed PULL entry (design § 8.2).</summary>
    private readonly record struct PullEntry(
        string Uid, string Status, string Incarnation, long Base, IReadOnlyList<(ulong Seq, byte[] Data)> Batches,
        IReadOnlyList<IncarnationEnd> History);
}
