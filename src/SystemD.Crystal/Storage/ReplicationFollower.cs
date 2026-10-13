// Ported from Highway: src/Highway.Server/Storage/Rocks/ReplicaPuller.cs (the pull loop, gap → resync marker, format refusals, bootstrap retry)
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

    // Snapshot downloads running beside the pull loop (spec 011 R3.1), at most MaxConcurrentBootstraps at once. Started
    // and finished only on the loop, which alone changes the followed set (R3.5).
    private readonly Dictionary<string, (string Name, string Directory, Task<SnapshotBegin> Download)> _downloads = new(StringComparer.Ordinal);
    private SemaphoreSlim? _downloadSlots;
    private readonly HashSet<string> _clashesLogged = new(StringComparer.Ordinal);
    private readonly Task _loop;

    private long _lastSetVersion = -1;

    private readonly Action<ReplicationEvent>? _onEvent;
    private readonly Observability.ReplicationMetrics? _metrics;

    // The primary's latest position per database, from the last reply (spec 011 R10.4), and what is published to readers
    // on other threads (Status, the lag gauge): replaced whole after each reply, never changed in place.
    private readonly Dictionary<string, long> _primaryLatest = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Uid, long Position, long PrimaryLatest)> _positions = [];

    /// <summary>Each followed database's position and its primary's latest, as of the last reply (spec 011 R10.4).</summary>
    public IReadOnlyList<(string Uid, long Position, long PrimaryLatest)> Positions => Volatile.Read(ref _positions);

    private void PublishPositions()
    {
        var positions = new List<(string Uid, long Position, long PrimaryLatest)>(_followed.Count);
        foreach (var (uid, db) in _followed)
        {
            var position = (long)db.Watermark;
            positions.Add((uid, position, _primaryLatest.TryGetValue(uid, out var latest) ? Math.Max(latest, position) : position));
        }

        Volatile.Write(ref _positions, positions);
    }

    /// <summary>Adopts a newer epoch a primary stated (003 R6.3), raising EpochChanged when it moved (spec 011 R10.3).</summary>
    private void AdoptEpoch(long epoch, string from)
    {
        if (epoch > _epoch.Epoch && _epoch.BumpTo(epoch) == epoch)
        {
            _onEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.EpochChanged, epoch, $"adopted epoch {epoch} from {from}"));
        }
    }

    public ReplicationFollower(
        CrystalStore store,
        StoreEngine engine,
        string nodeId,
        ReplicationEpoch epoch,
        ReplicationOptions options,
        string primaryEndpoint,
        string dataDirectory,
        ILogger? logger,
        Action<ReplicationEvent>? onEvent = null,
        Observability.ReplicationMetrics? metrics = null)
    {
        _metrics = metrics;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _primaryEndpoint = primaryEndpoint ?? throw new ArgumentNullException(nameof(primaryEndpoint));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _onEvent = onEvent;
        _downloadSlots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentBootstraps));
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
        // Each connection reconciles first (spec 011 R11.3): the primary at this endpoint may be another node whose
        // registry generation happens to equal the last one reconciled.
        _lastSetVersion = -1;
        var (host, port) = ParseEndpoint(_primaryEndpoint);
        await using var stream = await ReplicationTls.ConnectAsync(host, port, _options, ct, _options.RequestTimeout).ConfigureAwait(false);

        var client = new ReplicationClient(stream, _nodeId, _options.Secret, () => _metrics?.FrameRefused("standby"), _options.RequestTimeout);
        await client.HandshakeAsync(_epoch.RoleName, _epoch.Epoch, ct).ConfigureAwait(false);

        if (client.PrimaryEpoch < 1 || client.PrimaryEpoch > ReplicationProtocol.MaxEpoch)
        {
            throw new ReplicationProtocolException($"the primary stated epoch {client.PrimaryEpoch}, outside 1 to {ReplicationProtocol.MaxEpoch}");
        }

        // Adopt the primary's epoch if it is newer (R6.3), before serving any page.
        AdoptEpoch(client.PrimaryEpoch, $"primary {client.PrimaryNodeId}");

        // Two primaries at one epoch (spec 011 R7.1): nothing is taken from this one; the backoff retries, in case the
        // product resolves it.
        CheckForAnotherPrimaryAtTheSameEpoch(client.PrimaryNodeId, client.PrimaryEpoch, client.PrimaryRole);

        // Connected and, when this method ends however it ends, lost (spec 011 R10.2): node id and endpoint only (R10.9).
        if (_logger is not null)
        {
            Observability.CrystalLog.ReplicationConnected(_logger, _epoch.Epoch, $"following {client.PrimaryNodeId} at {_primaryEndpoint}");
        }

        try
        {
            await PumpAsync(client, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_logger is not null)
            {
                Observability.CrystalLog.ReplicationDisconnected(_logger, _epoch.Epoch, $"no longer following {client.PrimaryNodeId} at {_primaryEndpoint}");
            }
        }
    }

    private async Task PumpAsync(ReplicationClient client, CancellationToken ct)
    {

        // The set version each PULL reply states. DATABASES is asked for only when it differs from the one last
        // reconciled (design § 4 step 3), never before every pull: a catch-up costs one round trip per page (003
        // addendum B4). Null until the first reply, so a new connection always reconciles first.
        long? statedSetVersion = null;
        while (!ct.IsCancellationRequested && _epoch.Role == ReplicationRole.Standby)
        {
            var setVersion = statedSetVersion is { } stated
                && (stated == _lastSetVersion || (stated == _failedSetVersion && _engine.Clock.GetUtcNow() < _reconcileAgainAt))
                ? stated
                : await ReconcileIfChangedAsync(client, ct).ConfigureAwait(false);

            FinishBootstraps();
            RetryDueBootstraps();
            var followed = CurrentFollowed();
            if (followed.Count == 0 && _downloads.Count > 0)
            {
                // Nothing to follow until a download ends: wait for the first to finish, or a beat.
                var pause = Task.Delay(200, ct);
                var running = _downloads.Values.Select(d => (Task)d.Download).Append(pause).ToList();
                await Task.WhenAny(running).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                continue;
            }

            // With nothing to follow, the PULL names no database and simply long-polls on the primary until its set changes
            // (spec 011 R11.6): one request per wait, and DATABASES only when the reply states a new set version.

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
                AdoptEpoch(pageEpoch, $"a page from primary {client.PrimaryNodeId}");
            }
            else if (pageEpoch < _epoch.Epoch)
            {
                return; // stale primary: drop this connection, reconnect
            }

            var appliedAnything = ApplyEntries(pageEpoch, client.PrimaryNodeId, entries);
            PublishPositions();
            _engine.RunHook("follower.applied");

            // No sleep when something was applied (catch-up runs flat out); the empty PULL already long-polled on the
            // primary, so a caught-up loop does not spin either.
            if (!appliedAnything)
            {
                _ = setVersion; // (kept for a future "resync on set-version churn" refinement)
            }
        }
    }

    private bool _conflictReported;

    // The primary's set as last parsed: a download that finishes after the primary dropped or renamed its database is
    // checked against it before it is installed (review 2026-10-13, S3).
    private IReadOnlyList<ReplicatedDatabase>? _primarySet;
    private DateTimeOffset _reconcileAgainAt = DateTimeOffset.MinValue;
    private long _failedSetVersion = -1;

    /// <summary>
    /// The name the primary lists <paramref name="uid"/> under now, or null when it no longer lists it (dropped). Without
    /// a parsed set yet, <paramref name="fallback"/>.
    /// </summary>
    private string? CurrentName(string uid, string fallback)
    {
        if (_primarySet is null)
        {
            return fallback;
        }

        foreach (var remote in _primarySet)
        {
            if (string.Equals(remote.Uid, uid, StringComparison.Ordinal))
            {
                return remote.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Compares the primary just authenticated with the last one followed (spec 011 R7.1): a different node stating the
    /// same epoch is a second primary at that epoch (003 R6.4). Reports it once, with both ids and endpoints, and refuses
    /// the connection, so nothing is taken from it. Otherwise records it as the primary followed.
    /// </summary>
    private void CheckForAnotherPrimaryAtTheSameEpoch(string nodeId, long epoch, string role)
    {
        // Only a peer that says it is a primary: every node of a healthy cluster shares the epoch, so a standby or a
        // fenced node at it is no second primary (it refuses the pulls by itself).
        if (!string.Equals(role, ReplicationProtocol.RolePrimary, StringComparison.Ordinal))
        {
            return;
        }

        var last = PrimaryRecord.Read(_dataDirectory);
        if (last is not null && last.Epoch == epoch && !string.Equals(last.NodeId, nodeId, StringComparison.Ordinal))
        {
            if (!_conflictReported)
            {
                _conflictReported = true;
                _onEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.Conflict, epoch,
                    $"two primaries at epoch {epoch}: {last.NodeId} at {last.Endpoint}, followed before, and {nodeId} at {_primaryEndpoint}")
                {
                    PeerNodeId = nodeId,
                    PeerEndpoint = _primaryEndpoint,
                });
            }

            throw new ReplicationProtocolException(
                $"{ReplicationProtocol.ErrEpoch} {nodeId} states epoch {epoch}, at which this node followed {last.NodeId}: two primaries; nothing is taken");
        }

        if (last is null || last.Epoch != epoch || !string.Equals(last.NodeId, nodeId, StringComparison.Ordinal)
            || !string.Equals(last.Endpoint, _primaryEndpoint, StringComparison.Ordinal))
        {
            PrimaryRecord.Write(_dataDirectory, nodeId, epoch, _primaryEndpoint);
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
        _primarySet = primarySet;

        if (setVersion != _lastSetVersion)
        {
            // The set version counts as handled only when every step took effect; otherwise it is asked again, but at
            // most once per long poll (review 2026-10-13, M8: a name that stays taken would cost a DATABASES a commit).
            if (Reconcile(primarySet))
            {
                _lastSetVersion = setVersion;
            }
            else
            {
                _failedSetVersion = setVersion;
                _reconcileAgainAt = _engine.Clock.GetUtcNow() + _options.LongPollMax;
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
                _followed[remote.Uid] = new StandbyDatabase(slot, remote.Uid, _dataDirectory, _epoch.Epoch, _engine.RunHook);
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
    /// Starts bootstrapping a replicated database over its own connection (design § 7), beside the pull loop (spec 011
    /// R3.1): the snapshot downloads into a sibling directory while every other database keeps following;
    /// <see cref="FinishBootstraps"/> installs it on the loop. A database already downloading is left to it.
    /// </summary>
    private void TryBootstrap(string uid, string name)
    {
        if (_downloads.ContainsKey(uid))
        {
            return;
        }

        var bootstrapDir = Path.Combine(_dataDirectory, $"bootstrap-{Guid.NewGuid():N}");
        var ct = _cts.Token;
        var slots = _downloadSlots!;
        var download = Task.Run(
            async () =>
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await DownloadSnapshotAsync(uid, bootstrapDir, ct).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            },
            ct);
        _downloads[uid] = (name, bootstrapDir, download);
    }

    /// <summary>
    /// Installs every download that has finished (on the loop, R3.5): install it under the primary's UID, record the
    /// follow point, and add it to the followed set. A failed download or install leaves nothing installed and is
    /// retried on its own schedule.
    /// </summary>
    private void FinishBootstraps()
    {
        foreach (var (uid, (listedName, bootstrapDir, download)) in _downloads.Where(d => d.Value.Download.IsCompleted).ToList())
        {
            _downloads.Remove(uid);

            // The primary's set may have changed while it downloaded (review 2026-10-13, S3): a database it dropped is
            // not installed, and a renamed one is installed under its name now.
            var current = download.IsCompletedSuccessfully ? CurrentName(uid, listedName) : listedName;
            if (current is null)
            {
                TryDeleteDirectory(bootstrapDir);
                _pendingBootstraps.Remove(uid);
                _logger?.LogInformation("[replication] a snapshot downloaded for a database the primary no longer lists was discarded");
                _engine.RunHook("bootstrap.discarded");
                continue;
            }

            var name = current;
            try
            {
                var begin = download.GetAwaiter().GetResult(); // completed: its failure, if any, is thrown here
                _engine.RunHook("bootstrap.downloaded");
                _engine.InstallBootstrap(_store, uid, name, bootstrapDir);

                // Record the follow point from the snapshot's incarnation and base (R7.7), clear any resync marker (the
                // re-bootstrap this fulfils, R5.4), then follow the database.
                FollowRecord.Write(_dataDirectory, uid, begin.Incarnation, begin.Base);
                ResyncMarker.Clear(_dataDirectory, uid);
                if (_engine.OpenReplicatedByUid(_store, uid) is { } slot)
                {
                    _followed[uid] = new StandbyDatabase(slot, uid, _dataDirectory, _epoch.Epoch, _engine.RunHook);
                }

                _logger?.LogInformation("[replication] bootstrapped a replicated database from a snapshot ({Files} files)", begin.Manifest.Count);
                _metrics?.Bootstrapped(); // spec 011 R10.1
                _pendingBootstraps.Remove(uid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
            {
                TryDeleteDirectory(bootstrapDir);
                var attempt = _pendingBootstraps.TryGetValue(uid, out var pending) ? pending.Attempt + 1 : 0;
                var wait = BootstrapRetry[Math.Min(attempt, BootstrapRetry.Length - 1)];
                _pendingBootstraps[uid] = (name, _engine.Clock.GetUtcNow() + wait, attempt);
                _logger?.LogWarning("[replication] snapshot bootstrap failed ({Error}); retrying in {Wait}", ex.Message, wait);
                _engine.RunHook("bootstrap.failed");
            }
        }
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
            if (pending.Due <= now && !_followed.ContainsKey(uid) && !_downloads.ContainsKey(uid))
            {
                // Retried under the primary's current name, and not at all once it no longer lists the database (S3).
                if (CurrentName(uid, pending.Name) is { } name)
                {
                    TryBootstrap(uid, name);
                }
                else
                {
                    _pendingBootstraps.Remove(uid);
                }
            }
        }
    }

    private async Task<SnapshotBegin> DownloadSnapshotAsync(string uid, string bootstrapDir, CancellationToken ct)
    {
        // Its own connection (design § 7), and a new one to resume on if it is lost (spec 011 R3.3).
        var streams = new List<Stream>();
        try
        {
            return await SnapshotBootstrap.DownloadAsync(
                async c =>
                {
                    var (host, port) = ParseEndpoint(_primaryEndpoint);
                    var stream = await ReplicationTls.ConnectAsync(host, port, _options, c, _options.RequestTimeout).ConfigureAwait(false);
                    streams.Add(stream);
                    var client = new ReplicationClient(stream, _nodeId, _options.Secret, () => _metrics?.FrameRefused("standby"), _options.RequestTimeout);
                    await client.HandshakeAsync(_epoch.RoleName, _epoch.Epoch, c).ConfigureAwait(false);
                    return client;
                },
                uid,
                bootstrapDir,
                ct,
                checkBegin: CheckSnapshotEpoch,
                hook: _engine.RunHook).ConfigureAwait(false);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A snapshot is taken only from a primary at least as new as the newest epoch seen (spec 011 R3.4, as a page is,
    /// 003 R4.3); a newer epoch is adopted, as from a page.
    /// </summary>
    private void CheckSnapshotEpoch(SnapshotBegin begin)
    {
        var own = _epoch.Epoch;
        switch (SnapshotBootstrap.JudgeEpoch(begin.Epoch, own))
        {
            case SnapshotBootstrap.EpochVerdict.Refuse:
                throw new ReplicationProtocolException(
                    $"{ReplicationProtocol.ErrEpoch} the snapshot is from epoch {begin.Epoch}, not at or above this node's {own}; nothing is installed");
            case SnapshotBootstrap.EpochVerdict.Adopt when _epoch.BumpTo(begin.Epoch) == begin.Epoch:
                _onEvent?.Invoke(new ReplicationEvent(ReplicationEventKind.EpochChanged, begin.Epoch,
                    $"adopted epoch {begin.Epoch} from the primary's snapshot"));
                break;
        }
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

        var reply = await client.RequestAsync(args, ct, extraWait: _options.LongPollMax).ConfigureAwait(false); // it may wait on the primary
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

            _primaryLatest[entry.Uid] = entry.Latest; // 003 R3.7

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

                        // The batches skipped (already held) come first in a page; the rest were written (spec 011 R10.1).
                        _metrics?.PageApplied(entry.Batches.Skip(result.Skipped).Sum(b => (long)b.Data.Length));
                    }
                    else if (result.Status == ApplyStatus.NotContiguous)
                    {
                        // Nothing was written; continuing would misalign every position after it (003 should-fix 7).
                        MarkAndReBootstrap(entry.Uid, "gap (a page that does not continue the database)");
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
                case var s when s == ReplicationProtocol.EntryOversize:
                    // A batch too large for any reply (spec 011 R4.1): the snapshot carries it.
                    MarkAndReBootstrap(entry.Uid, "gap (oversize batch)");
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

            entries.Add(new PullEntry(uid, status, incarnation, @base, f[4].Integer, batches, history));
        }

        return entries;
    }

    /// <summary>
    /// Parses <c>host:port</c> (an IPv6 host in brackets). Throws <see cref="ArgumentException"/> naming the expected form
    /// for anything else, so <c>Follow</c> refuses a bad endpoint at once (spec 011 R11.5) instead of looping on it.
    /// </summary>
    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var colon = endpoint?.LastIndexOf(':') ?? -1;
        if (endpoint is null || colon < 0
            || !int.TryParse(endpoint.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"'{endpoint}' is not a replication endpoint: expected host:port, such as 127.0.0.1:7700 or [::1]:7700, with a port from 1 to 65535.",
                nameof(endpoint));
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

        // Downloads still running were cancelled with the loop: wait for them, then remove what they wrote.
        foreach (var (_, bootstrapDir, download) in _downloads.Values)
        {
            try
            {
                await download.ConfigureAwait(false);
            }
            catch
            {
                // Cancelled or failed; the folder goes either way.
            }

            TryDeleteDirectory(bootstrapDir);
        }

        _downloadSlots?.Dispose();
        _cts.Dispose();
    }

    /// <summary>One parsed PULL entry (design § 8.2).</summary>
    private readonly record struct PullEntry(
        string Uid, string Status, string Incarnation, long Base, long Latest, IReadOnlyList<(ulong Seq, byte[] Data)> Batches,
        IReadOnlyList<IncarnationEnd> History);
}
