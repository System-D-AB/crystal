// Spec 003 T9 / design § 10 — the store's replication facade: start a listener (be a primary a standby can pull
// from), follow a primary (be a standby), and promote. The full operational surface (demote, fence, rejoin, events)
// is T12; T9 fills in what the follower loop needs end to end. The RocksDB-touching machinery (source, follower)
// lives in Storage; this is the public, product-facing control.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>This node's replication role (spec 003 R6).</summary>
public enum ReplicationRole
{
    /// <summary>Serves pulls and accepts writes.</summary>
    Primary,

    /// <summary>Follows a primary; refuses local writes (R1.6).</summary>
    Standby,

    /// <summary>Serves nothing (a stood-down former primary).</summary>
    Fenced,
}

/// <summary>
/// The store's replication control (spec 003 design § 10): <c>store.Replication</c>. A node listens to be pullable
/// (primary or promotable standby), follows a primary to become a standby, and promotes to accept writes. Epoch and
/// role are node-local (<c>node.epoch</c>), never replicated.
/// </summary>
public sealed class CrystalReplication : IDisposable
{
    private readonly CrystalStore _store;
    private readonly StoreEngine _engine;
    private readonly string _nodeId;
    private const int MaxTransitions = 64;

    private readonly string _dataDirectory;
    private readonly ReplicationOptions? _options;
    private readonly ILogger? _logger;
    private readonly Storage.ReplicationEpoch _epoch;
    private readonly Observability.ReplicationMetrics _metrics;
    private readonly Lock _gate = new();
    private readonly Queue<string> _transitions = new();

    // The transition log has its own lock: Promote and Fence stop the follower while holding _gate, and the follower
    // raises events as it stops, so taking _gate here could deadlock.
    private readonly Lock _transitionsLock = new();

    private Storage.ReplicationSource? _source;
    private Replication.ReplicationListener? _listener;
    private ReplicationFollower? _follower;
    private string? _followedPrimary;
    private int _disposed;

    internal CrystalReplication(
        CrystalStore store,
        StoreEngine engine,
        string nodeId,
        string dataDirectory,
        ReplicationOptions? options,
        System.Diagnostics.Metrics.IMeterFactory? meterFactory,
        ILogger? logger)
    {
        _store = store;
        _engine = engine;
        _nodeId = nodeId;
        _dataDirectory = dataDirectory;
        _options = options;
        _logger = logger;

        // A node with a configured listen endpoint but no explicit role starts as a primary (it accepts writes until
        // told to follow); a node that follows becomes a standby.
        _epoch = Storage.ReplicationEpoch.Load(dataDirectory, Storage.ReplicationRole.Primary);

        // 003 addendum B2: local writes to replicated databases follow the role, from the role this node starts in.
        _engine.WriteGate.Set(_epoch.Role);
        _epoch.RoleChanged = _engine.WriteGate.Set;
        _metrics = new Observability.ReplicationMetrics(
            meterFactory, nodeId, () => _epoch.Epoch, () => _epoch.RoleName,
            () => Source?.Slots() ?? [],
            uid => _engine.LocalDatabasesForReconcile().FirstOrDefault(d => d.Uid == uid).Name,
            engine.TagMetricsWithDatabase);
    }

    /// <summary>This node's current role (Primary, Standby, Fenced).</summary>
    public ReplicationRole Role => Map(_epoch.Role);

    /// <summary>The serving side, once listening or promoted (tests).</summary>
    internal Storage.ReplicationSource? Source
    {
        get { lock (_gate) { return _source; } }
    }

    /// <summary>This node's current epoch (R6).</summary>
    public long Epoch => _epoch.Epoch;

    /// <summary>Replication events (R9.3): promotions, demotions, fences, epoch changes, slots, gaps, divergences, conflicts.</summary>
    public event EventHandler<ReplicationEvent>? Events;

    /// <summary>
    /// Starts the replication listener on the configured endpoint so standbys can pull from this node (R10.1). The
    /// store must have been opened with <see cref="ReplicationOptions"/>. Idempotent: a second call is a no-op.
    /// </summary>
    public void Listen()
    {
        RequireOptions();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_listener is not null)
            {
                return;
            }

            EnsureSource();
            _listener = Replication.ReplicationListener.Start(_source!, _options!, _logger);
        }
    }

    /// <summary>The endpoint the listener bound to (host:port), or null if it is not listening.</summary>
    public string? ListenerEndpoint
    {
        get
        {
            lock (_gate)
            {
                return _listener?.Endpoint.ToString();
            }
        }
    }

    /// <summary>
    /// Follows <paramref name="primaryEndpoint"/> as a standby (design § 4): starts the follower loop, which connects,
    /// reconciles the database set, and applies the primary's pages. Returns at once; progress is observable through
    /// reads and status. The node becomes a <see cref="ReplicationRole.Standby"/>.
    /// </summary>
    public void Follow(string primaryEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(primaryEndpoint);
        RequireOptions();
        lock (_gate)
        {
            ThrowIfDisposed();
            _epoch.SetRole(Storage.ReplicationRole.Standby);
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _followedPrimary = primaryEndpoint;
            _follower = new ReplicationFollower(_store, _engine, _nodeId, _epoch, _options!, primaryEndpoint, _dataDirectory, _logger, Raise);
        }
    }

    /// <summary>Stops following (keeps the current role otherwise). The database set stays as it is.</summary>
    public void StopFollowing()
    {
        lock (_gate)
        {
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _follower = null;
            _followedPrimary = null;
        }
    }

    /// <summary>
    /// Promotes this node to primary (spec 003 R6.1, R6.2): persist the new epoch **before** serving (a persist
    /// failure fails the promotion and leaves the node as it was), stop following, take the primary role, give every
    /// open database a new incarnation so a standby past the old base diverges (R5.2), and raise a Promoted event.
    /// Returns the new epoch.
    /// </summary>
    public long Promote(string reason)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            // Order (003 addendum B3): stop applying, then begin every open database's new incarnation at what this node
            // received, then persist the epoch and take the primary role, which opens local writes. A base taken after
            // that could include this node's own new writes, and a rejoining old primary would pass as caught up.
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _follower = null;
            _followedPrimary = null;
            if (_options is not null)
            {
                EnsureSource();
                _source!.ClearAllHistory(); // a new epoch is a new history (design § 3)
            }

            // A new incarnation per open database at promotion (R5.2), and the epoch and role (which open writes) in the
            // same step under the store gate, so no database opens in between without a history.
            var newEpoch = 0L;
            if (!_engine.BeginNewIncarnations(() => _epoch.TryPromote(Storage.ReplicationRole.Primary, out newEpoch)))
            {
                throw new InvalidOperationException(
                    "Promotion failed: the new epoch could not be persisted to node.epoch (R6.2). The node did not " +
                    "become a primary; check the disk and retry.");
            }

            Raise(new ReplicationEvent(ReplicationEventKind.Promoted, newEpoch, $"promoted: {reason}"));
            return newEpoch;
        }
    }

    /// <summary>
    /// Demotes this node from primary to standby (spec 003 R9.1): stops serving as a primary and stops following. The
    /// product typically follows a new primary next, or schedules a rejoin. The epoch is unchanged (a demotion is not
    /// a new history on its own; adopting a higher epoch is). Raises a Demoted event.
    /// </summary>
    public void Demote()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_epoch.Role != Storage.ReplicationRole.Primary)
            {
                return;
            }

            // R9.2: before standing down, record what this node committed beyond any standby's acknowledgement.
            var report = _source?.WriteReconciliationReport();
            _epoch.SetRole(Storage.ReplicationRole.Standby);
            _source?.ClearAllHistory();
            LastReconciliationReport = report;
            Raise(new ReplicationEvent(ReplicationEventKind.Demoted, _epoch.Epoch,
                report is null ? "demoted by request" : $"demoted by request; reconciliation report at {report}"));
        }
    }

    /// <summary>The path of the reconciliation report the last demote wrote (spec 003 R9.2), or null if none/nothing unreplicated.</summary>
    public string? LastReconciliationReport { get; private set; }

    /// <summary>
    /// Fences this node read-only at once (spec 003 R6.5): it serves no pages and accepts no writes until it is
    /// promoted, demoted or restarted. Raises a Fenced event. Idempotent.
    /// </summary>
    public void Fence(string reason)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_epoch.Role == Storage.ReplicationRole.Fenced)
            {
                return;
            }

            _epoch.SetRole(Storage.ReplicationRole.Fenced);

            // A fenced node follows nobody either (R6.5): its follower would otherwise reconnect without pause.
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _follower = null;
            _followedPrimary = null;
            _source?.ClearAllHistory();
            Raise(new ReplicationEvent(ReplicationEventKind.Fenced, _epoch.Epoch, $"fenced: {reason}"));
        }
    }

    /// <summary>
    /// Schedules a rejoin (spec 003 R7.8, R9.1): records that on the next start this node wipes its replicated
    /// databases and bootstraps from <paramref name="primaryEndpoint"/>. The marker survives a failed download;
    /// whether and when to schedule it is the product's decision.
    /// </summary>
    public void ScheduleRejoin(string primaryEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(primaryEndpoint);
        lock (_gate)
        {
            ThrowIfDisposed();
            Storage.RejoinMarker.Write(_dataDirectory, primaryEndpoint);
        }
    }

    private void EnsureSource()
    {
        if (_source is null)
        {
            _source = new Storage.ReplicationSource(_store, _engine, _nodeId, _epoch, _options!)
            {
                OnEvent = Raise,
            };
        }
    }

    private void Raise(ReplicationEvent e)
    {
        _metrics.Observe(e.Kind);
        Log(e);
        lock (_transitionsLock)
        {
            _transitions.Enqueue(e.ToString());
            while (_transitions.Count > MaxTransitions)
            {
                _transitions.Dequeue();
            }
        }

        Events?.Invoke(this, e);
    }

    private void Log(ReplicationEvent e)
    {
        if (_logger is null)
        {
            return;
        }

        switch (e.Kind)
        {
            case ReplicationEventKind.Promoted: Observability.CrystalLog.ReplicationPromoted(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.Demoted: Observability.CrystalLog.ReplicationDemoted(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.Fenced: Observability.CrystalLog.ReplicationFenced(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.EpochChanged: Observability.CrystalLog.ReplicationEpochChanged(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.SlotLagging: Observability.CrystalLog.ReplicationSlotLagging(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.SlotDropped: Observability.CrystalLog.ReplicationSlotDropped(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.Gap: Observability.CrystalLog.ReplicationGap(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.Divergence: Observability.CrystalLog.ReplicationDivergence(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.FormatRefused: Observability.CrystalLog.ReplicationFormatRefused(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.Conflict: Observability.CrystalLog.ReplicationConflict(_logger, e.Epoch, e.Detail); break;
        }
    }

    /// <summary>
    /// This node's replication status (spec 003 R13.2): role, epoch, the primary it follows (if any), and the recent
    /// transition log. Surfaced in <see cref="CrystalStore.Status"/> and the protocol's <c>STATUS</c>.
    /// </summary>
    public ReplicationStatus Status()
    {
        string[] transitions;
        lock (_transitionsLock)
        {
            transitions = [.. _transitions];
        }

        Storage.ReplicationSource? source;
        ReplicationStatus status;
        lock (_gate)
        {
            source = _source;
            status = new ReplicationStatus(
                _epoch.RoleName,
                _epoch.Epoch,
                _followedPrimary,
                _listener?.Endpoint.ToString(),
                transitions,
                []);
        }

        // Read outside the lock: aging slots may raise events, which must not wait on it.
        return source is null ? status : status with { Slots = source.Slots() };
    }

    private void RequireOptions()
    {
        if (_options is null)
        {
            throw new InvalidOperationException(
                "Replication is not configured: open the store with CrystalOptions.Replication to listen or follow.");
        }
    }

    private static ReplicationRole Map(Storage.ReplicationRole role) => role switch
    {
        Storage.ReplicationRole.Primary => ReplicationRole.Primary,
        Storage.ReplicationRole.Standby => ReplicationRole.Standby,
        _ => ReplicationRole.Fenced,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Stops the listener and the follower loop and releases their resources. Called by the store on close.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown best effort.
        }

        _listener?.Dispose();
        _source?.Dispose();
        _metrics.Dispose();
    }
}

/// <summary>
/// A node's replication status (spec 003 R13.2): role, epoch, the primary it follows, its listener endpoint, and a
/// bounded log of recent transitions. In <see cref="CrystalStore.Status"/> and the protocol's <c>STATUS</c>.
/// </summary>
/// <param name="Role">This node's role: <c>primary</c>, <c>standby</c> or <c>fenced</c>.</param>
/// <param name="Epoch">This node's current epoch.</param>
/// <param name="FollowedPrimary">The endpoint this node follows as a standby, or null.</param>
/// <param name="ListenerEndpoint">The endpoint this node's listener is bound to, or null if not listening.</param>
/// <param name="RecentTransitions">A bounded log of recent role/epoch/slot transitions (newest last).</param>
/// <param name="Slots">On a primary, each standby's slot per database (R2.2, R2.4); empty otherwise.</param>
public sealed record ReplicationStatus(
    string Role,
    long Epoch,
    string? FollowedPrimary,
    string? ListenerEndpoint,
    IReadOnlyList<string> RecentTransitions,
    IReadOnlyList<ReplicationSlot> Slots);

/// <summary>A standby slot's state (spec 003 R2.3).</summary>
public enum ReplicationSlotState
{
    /// <summary>In contact and within a quarter of the lag cap.</summary>
    Active,

    /// <summary>More than a quarter of the lag cap behind.</summary>
    Lagging,

    /// <summary>
    /// Past the lag cap, or silent past <c>SlotStaleAfter</c>. An observation, never a hold: the standby resumes on its
    /// next contact if the log still reaches it, or meets a gap and re-bootstraps.
    /// </summary>
    Dropped,
}

/// <summary>
/// One standby's slot for one database, on a primary (spec 003 R2.2, R2.4, R13.2).
/// </summary>
/// <param name="Standby">The standby's node id.</param>
/// <param name="DatabaseUid">The database's UID.</param>
/// <param name="Acknowledged">The position the standby last reported.</param>
/// <param name="LagSequences">How far it is behind the database's latest sequence.</param>
/// <param name="LagTime">
/// The age of the oldest batch it lacks: what a failover now would lose. Zero when caught up; resolution 100 ms; a lower
/// bound when that batch is older than the few minutes of commit times kept.
/// </param>
/// <param name="State">Active, lagging or dropped.</param>
/// <param name="LastContact">When it last pulled.</param>
public sealed record ReplicationSlot(
    string Standby,
    string DatabaseUid,
    long Acknowledged,
    long LagSequences,
    TimeSpan LagTime,
    ReplicationSlotState State,
    DateTimeOffset LastContact);
