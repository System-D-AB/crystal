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
// Values are explicit and append-only (spec 015 R3.1): they reach metrics, logs and products' switches.
public enum ReplicationRole
{
    /// <summary>Serves pulls and accepts writes.</summary>
    Primary = 0,

    /// <summary>Follows a primary; refuses local writes (R1.6).</summary>
    Standby = 1,

    /// <summary>Serves nothing (a stood-down former primary).</summary>
    Fenced = 2,
}

/// <summary>
/// The store's replication control (spec 003 design § 10): <c>store.Replication</c>. A node listens to be pullable
/// (primary or promotable standby), follows a primary to become a standby, and promotes to accept writes. Epoch and
/// role are node-local (<c>node.epoch</c>), never replicated.
/// </summary>
/// <remarks>
/// <b>Blocking</b> (playbook § 7, spec 011 R11.10): <c>Promote</c>, <c>Demote</c>, <c>Fence</c>, <c>Follow</c>,
/// <c>StopFollowing</c> and <c>Dispose</c> are synchronous and block the calling thread until the follower has stopped
/// (at most one pull's long poll, <c>LongPollMax</c>), the node's epoch file is written, and the local writes already
/// admitted have ended. They take this object's lock, so other members called meanwhile (<c>Status</c> among them)
/// wait for them. Events are delivered on Crystal's own thread, never inside these calls.
/// </remarks>
public sealed class CrystalReplication
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
    private ReplicationEventPump? _events;
    private readonly Lock _eventsGate = new();

    /// <summary>The event thread, started with the first event (review 2026-10-13, M6: a store that raises none has none).</summary>
    private ReplicationEventPump? EventPump(bool create)
    {
        if (Volatile.Read(ref _events) is { } pump || !create)
        {
            return _events;
        }

        lock (_eventsGate)
        {
            return _events ??= Volatile.Read(ref _disposed) == 0 ? new ReplicationEventPump(e => Events?.Invoke(this, e), _logger) : null;
        }
    }
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
        _epoch.RoleChanged = role =>
        {
            _engine.WriteGate.Set(role);

            // Waiting pulls answer for the new state at once (R3.4). Read without the facade's lock: this runs inside
            // the epoch's lock, and taking ours here could deadlock with a promotion holding ours and waiting for it.
            Volatile.Read(ref _source)?.SignalRoleOrEpochChange();
        };

        // Standing down returns only once the local writes admitted before it have ended, waited for outside the epoch's
        // lock (spec 011 R5.1): an admitted database change may be waiting for the store gate a promotion holds while the
        // promotion waits for the epoch's lock.
        _epoch.AfterRoleChanged = role =>
        {
            if (role != Storage.ReplicationRole.Primary)
            {
                _engine.WriteGate.WaitForAdmittedWrites();
            }
        };
        _metrics = new Observability.ReplicationMetrics(
            meterFactory, nodeId, () => _epoch.Epoch, () => _epoch.RoleName,
            () => Source?.Slots() ?? [],
            () => Followed(),
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

    /// <summary>
    /// Replication events (R9.3): promotions, demotions, fences, epoch changes, slots, gaps, divergences, conflicts.
    /// Handlers run on Crystal's own event thread, one at a time, in the order the events occurred (spec 011 R5.2), so a
    /// handler may run after the call that caused its event has returned, and may itself call any member of this class.
    /// A handler that throws is logged (event 2014) and does not stop later events. Disposing the store delivers the
    /// events already queued, waiting at most 5 s.
    /// </summary>
    public event EventHandler<ReplicationEvent>? Events;

    /// <summary>Waits until every event raised so far has been delivered to the handlers (tests).</summary>
    internal bool DrainEventsForTests(TimeSpan timeout) => EventPump(create: false)?.Drain(timeout) ?? true;

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
            _listener = Replication.ReplicationListener.Start(
                _source!, _options!, _logger, Replication.ReplicationProtocol.RequestCapFor(_engine.MaxOpenDatabases), () => _metrics.FrameRefused("primary"));
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
        _ = ReplicationFollower.ParseEndpoint(primaryEndpoint); // a bad endpoint is refused now (spec 011 R11.5), not looped on
        RequireOptions();
        lock (_gate)
        {
            ThrowIfDisposed();
            _epoch.SetRole(Storage.ReplicationRole.Standby);
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _followedPrimary = primaryEndpoint;
            _follower = new ReplicationFollower(_store, _engine, _nodeId, _epoch, _options!, primaryEndpoint, _dataDirectory, _logger, Raise, _metrics);
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
    /// Promotes this node to primary (spec 003 R6.1, R6.2): stop following, persist the new epoch **before** serving,
    /// take the primary role, give every open database a new incarnation so a standby past the old base diverges
    /// (R5.2), and raise a Promoted event. Returns the new epoch.
    /// </summary>
    /// <exception cref="System.IO.IOException">
    /// The new epoch could not be written to <c>node.epoch</c>. The node did not become a primary: its epoch and role
    /// are unchanged, but it no longer follows. Fix the disk and promote again, or follow a primary.
    /// </exception>
    public long Promote(string reason) => PromoteCore(reason, minimumEpoch: 0);

    /// <summary>
    /// Promotes this node to primary at <c>max(epoch + 1, <paramref name="minimumEpoch"/>)</c>, in one persisted epoch
    /// write (spec 013 R6): the promotion lands above every epoch the product has heard of — a promotion announce, a
    /// client that saw a newer primary — without promoting in a loop. Otherwise as <see cref="Promote(string)"/>.
    /// </summary>
    /// <param name="reason">Why, for the Promoted event and the log.</param>
    /// <param name="minimumEpoch">The lowest epoch to promote to; at most 2^62.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumEpoch"/> is negative or above 2^62: no real history reaches it, so it is taken as a garbled
    /// or hostile value rather than an epoch to adopt, and refused before anything changes.
    /// </exception>
    /// <exception cref="System.IO.IOException">As <see cref="Promote(string)"/>.</exception>
    public long Promote(string reason, long minimumEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumEpoch);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumEpoch, MaximumEpochFloor);
        return PromoteCore(reason, minimumEpoch);
    }

    /// <summary>The highest floor <see cref="Promote(string, long)"/> accepts (2^62): far above any real history.</summary>
    public const long MaximumEpochFloor = 1L << 62;

    private long PromoteCore(string reason, long minimumEpoch)
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
            System.IO.IOException? failure = null;
            if (!_engine.BeginNewIncarnations(() => _epoch.TryPromote(Storage.ReplicationRole.Primary, out newEpoch, out failure, minimumEpoch), out var pendingResync))
            {
                // A typed error a failover controller must handle (playbook § 7): the disk, not the caller, is at fault.
                throw new System.IO.IOException(
                    "Promotion failed: the new epoch could not be written to node.epoch (R6.2), so the node did not " +
                    "become a primary; its epoch and role are unchanged, but it no longer follows. Fix the disk and " +
                    "promote again, or follow a primary. Cause: " + failure?.Message,
                    failure);
            }

            Raise(new ReplicationEvent(ReplicationEventKind.Promoted, newEpoch, $"promoted: {reason}"));
            if (pendingResync.Count > 0)
            {
                Raise(new ReplicationEvent(
                    ReplicationEventKind.PromotedWithPendingResync,
                    newEpoch,
                    "promoted with databases marked for re-bootstrap: " +
                    string.Join(", ", pendingResync.Select(p => $"'{p.Name}' ({p.Reason})"))));
            }

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

            // Stand down first: shutting the write gate waits for the local writes already admitted, so the report
            // below holds every write this node committed (003 review, race windows). Then, R9.2: record what this
            // node committed beyond any standby's acknowledgement.
            _epoch.SetRole(Storage.ReplicationRole.Standby);
            // Every replicated database is reported (spec 011 R8.1), so a primary that never listened reports too.
            if (_options is not null)
            {
                EnsureSource();
            }

            var report = _source?.WriteReconciliationReport();
            _source?.ClearAllHistory();
            LastReconciliationReport = report;
            Raise(new ReplicationEvent(ReplicationEventKind.Demoted, _epoch.Epoch,
                report is null ? "demoted by request" : $"demoted by request; reconciliation report at {report}"));
        }
    }

    /// <summary>
    /// Hands the primary role over without losing a write (spec 011 R14): refuses local writes to replicated databases at
    /// once, keeps serving, waits until the standby <paramref name="standbyNodeId"/> has acknowledged the latest position
    /// of every replicated database, then demotes this node (an empty reconciliation report) and returns each database's
    /// final position. The product then promotes that standby and points this node at it with <see cref="Follow"/>.
    /// </summary>
    /// <remarks>
    /// <b>Yields a thread</b> while it waits (it polls the standby's acknowledgements on the store's clock every 20 ms).
    /// If the standby has not caught up within <paramref name="timeout"/>, or <paramref name="cancellationToken"/> fires,
    /// writes are accepted again and the node stays the primary. A role change meanwhile (a fence, a higher epoch) ends it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This node is not the primary, a step-down is already running, or the
    /// role or epoch changed while it waited.</exception>
    /// <exception cref="TimeoutException">The standby did not catch up in time; the node is still the primary.</exception>
    public async Task<IReadOnlyList<SteppedDownDatabase>> StepDownAsync(string standbyNodeId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(standbyNodeId);
        RequireOptions();
        long epoch;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_epoch.Role != Storage.ReplicationRole.Primary)
            {
                throw new InvalidOperationException(
                    $"Cannot step down: this node is {_epoch.RoleName}, not the primary. Only a primary hands over (spec 011 R14.5).");
            }

            if (!_engine.WriteGate.BeginStepDown())
            {
                throw new InvalidOperationException("Cannot step down: a step-down is already running on this node (spec 011 R14.5).");
            }

            epoch = _epoch.Epoch;
            EnsureSource();
        }

        try
        {
            // Writes admitted before the step-down end first; from then on nothing local is written (R14.1).
            await Task.Run(_engine.WriteGate.WaitForAdmittedWrites, cancellationToken).ConfigureAwait(false);
            var positions = _engine.ReplicatedDatabaseSet()
                .Select(d => new SteppedDownDatabase(d.Uid, d.Name, LatestOf(d.Uid)))
                .ToList();

            // Wait for the standby to acknowledge every one (R14.2, R14.6), on the store's clock.
            using var deadline = new CancellationTokenSource(timeout, _engine.Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            while (true)
            {
                if (_epoch.Role != Storage.ReplicationRole.Primary || _epoch.Epoch != epoch)
                {
                    throw new InvalidOperationException(
                        $"The step-down ended: this node became {_epoch.RoleName} at epoch {_epoch.Epoch} while it waited (spec 011 R14.4).");
                }

                var behind = Behind(standbyNodeId, positions);
                if (behind.Count == 0)
                {
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), _engine.Clock, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The standby {standbyNodeId} did not catch up within {timeout}: still behind on {string.Join(", ", behind)}. " +
                        "Writes are accepted again and this node is still the primary (spec 011 R14.3).");
                }
            }

            // Caught up: stand down as Demote does. The report is empty, as every write was acknowledged.
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_epoch.Role != Storage.ReplicationRole.Primary || _epoch.Epoch != epoch)
                {
                    throw new InvalidOperationException(
                        $"The step-down ended: this node became {_epoch.RoleName} at epoch {_epoch.Epoch} while it waited (spec 011 R14.4).");
                }

                _epoch.SetRole(Storage.ReplicationRole.Standby);
                var report = _source?.WriteReconciliationReport();
                _source?.ClearAllHistory();
                LastReconciliationReport = report;
                Raise(new ReplicationEvent(ReplicationEventKind.Demoted, _epoch.Epoch,
                    report is null ? $"stepped down to {standbyNodeId}" : $"stepped down to {standbyNodeId}; reconciliation report at {report}")
                {
                    PeerNodeId = standbyNodeId,
                });
            }

            return positions;
        }
        finally
        {
            // Writes resume unless the node stood down, whose role now refuses them on its own (R14.3).
            _engine.WriteGate.EndStepDown();
        }
    }

    /// <summary>The databases on which <paramref name="standbyNodeId"/> has not acknowledged the position given, by name.</summary>
    private List<string> Behind(string standbyNodeId, IReadOnlyList<SteppedDownDatabase> positions)
    {
        var slots = Volatile.Read(ref _source)?.Slots() ?? [];
        var behind = new List<string>();
        foreach (var db in positions)
        {
            // Exactly the position (spec 011 R14.2): a slot above it states batches this primary never had, so it is a
            // standby that diverged, not one that caught up.
            var acked = slots.FirstOrDefault(s => s.Standby == standbyNodeId && s.DatabaseUid == db.Uid);
            if (acked is null || acked.Acknowledged != db.Position)
            {
                behind.Add(db.Name);
            }
        }

        return behind;
    }

    private long LatestOf(string uid)
    {
        var slot = _engine.OpenReplicatedByUid(_store, uid);
        using var scope = slot?.TryEnter();
        return scope is null ? 0 : (long)slot!.LatestSequence;
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

            // Spec 013 R10: a primary fenced here may lift the fence at this same epoch; anything else must promote.
            _fencedPrimaryAtEpoch = _epoch.Role == Storage.ReplicationRole.Primary ? _epoch.Epoch : null;
            _epoch.SetRole(Storage.ReplicationRole.Fenced);

            // A fenced node follows nobody either (R6.5): its follower would otherwise reconnect without pause.
            _follower?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _follower = null;
            _followedPrimary = null;
            _source?.ClearAllHistory();
            Raise(new ReplicationEvent(ReplicationEventKind.Fenced, _epoch.Epoch, $"fenced: {reason}"));
        }
    }

    // The epoch a primary was fenced at by this instance (spec 013 R10); null if it was not a primary when it was fenced.
    private long? _fencedPrimaryAtEpoch;

    /// <summary>
    /// Lifts a fence at the same epoch (spec 013 R10): a primary fenced by <see cref="Fence"/> returns to primary at the
    /// epoch it was fenced at, serving and accepting writes again, and raises an <see cref="ReplicationEventKind.Unfenced"/>
    /// event. The backstop a product needs when a primary that lost every peer and client fenced itself and contact
    /// returned: no new epoch, so its standbys continue where they were.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Refused, with the reason, unless this node is fenced, was a primary when it was fenced, and has seen no higher epoch
    /// since: a fenced standby returning as primary at the same epoch would be a second primary at one epoch (R6.4), and a
    /// node that has seen a higher epoch is outranked. A restart lifts a fence by failing closed to a standby (spec 003
    /// R6.5). In each of these cases <see cref="Promote(string)"/>, which takes a new epoch, is the way back.
    /// </exception>
    public void Unfence(string reason)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_epoch.Role != Storage.ReplicationRole.Fenced)
            {
                throw new InvalidOperationException($"Unfence refused: this node is not fenced (its role is {_epoch.RoleName}).");
            }

            if (_fencedPrimaryAtEpoch is not { } fencedAt)
            {
                throw new InvalidOperationException(
                    "Unfence refused: this node was not a primary when it was fenced, so serving as one at this epoch would " +
                    "make a second primary at it. Promote it instead, which takes a new epoch.");
            }

            if (_epoch.Epoch != fencedAt)
            {
                throw new InvalidOperationException(
                    $"Unfence refused: this node was fenced at epoch {fencedAt} and has since seen epoch {_epoch.Epoch}; " +
                    "another node has been primary. Promote it instead, which takes a new epoch.");
            }

            _fencedPrimaryAtEpoch = null;
            _epoch.SetRole(Storage.ReplicationRole.Primary);
            Raise(new ReplicationEvent(ReplicationEventKind.Unfenced, _epoch.Epoch, $"unfenced: {reason}"));
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
        _ = ReplicationFollower.ParseEndpoint(primaryEndpoint); // refused now, not at the next start (spec 011 R11.5)
        lock (_gate)
        {
            ThrowIfDisposed();
            Storage.RejoinMarker.Write(_dataDirectory, primaryEndpoint);
        }
    }

    /// <summary>
    /// At open, a scheduled rejoin follows the endpoint it names (spec 011 R11.4, 003 R7.8): the node becomes a standby
    /// and every replicated database re-bootstraps from that primary, without the product calling <see cref="Follow"/>.
    /// Without replication options, or with a marker naming no valid endpoint, nothing starts (the marker stays).
    /// </summary>
    internal void ResumeScheduledRejoin()
    {
        if (_options is null || Storage.RejoinMarker.Read(_dataDirectory) is not { } endpoint)
        {
            return;
        }

        try
        {
            _ = ReplicationFollower.ParseEndpoint(endpoint);
        }
        catch (ArgumentException)
        {
            _logger?.LogError("[replication] the scheduled rejoin names no valid endpoint ('{Endpoint}'); not following", endpoint);
            return;
        }

        Follow(endpoint);
    }

    /// <summary>The databases followed now, named (spec 011 R10.4); read lock-free from the follower's last publication.</summary>
    private IReadOnlyList<FollowedDatabase> Followed()
    {
        var positions = Volatile.Read(ref _follower)?.Positions ?? [];
        if (positions.Count == 0)
        {
            return [];
        }

        var names = _engine.LocalDatabasesForReconcile().ToDictionary(d => d.Uid, d => d.Name, StringComparer.Ordinal);
        return [.. positions.Select(p => new FollowedDatabase(
            p.Uid, names.TryGetValue(p.Uid, out var name) ? name : p.Uid, p.Position, p.PrimaryLatest, Math.Max(0, p.PrimaryLatest - p.Position)))];
    }

    private void EnsureSource()
    {
        if (_source is null)
        {
            _source = new Storage.ReplicationSource(_store, _engine, _nodeId, _epoch, _options!)
            {
                OnEvent = Raise,
                OnReport = path => LastReconciliationReport = path,
                OnShipped = bytes => _metrics.BytesShipped(bytes),
                OnPageServed = (uid, fromTail, miss, delay) => _metrics.PageServed(uid, fromTail, miss == Replication.TailMiss.Landing, delay),
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

        // Delivered on the event thread (spec 011 R5.2): the raising thread may hold the facade's lock or be the
        // follower that a handler's Promote would wait for.
        EventPump(create: true)?.Post(e);
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
            case ReplicationEventKind.PromotedWithPendingResync: Observability.CrystalLog.ReplicationPromotedWithPendingResync(_logger, e.Epoch, e.Detail); break;
            case ReplicationEventKind.Unfenced: Observability.CrystalLog.ReplicationUnfenced(_logger, e.Epoch, e.Detail); break;
        }
    }

    /// <summary>
    /// This node's replication status (spec 003 R13.2): role, epoch, the primary it follows (if any), and the recent
    /// transition log. Read here, not in <see cref="CrystalStore.Status"/>; the protocol's <c>STATUS</c> reports the same.
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
                Role,
                _epoch.Epoch,
                _followedPrimary,
                _listener?.Endpoint.ToString(),
                transitions,
                []);
        }

        // Read outside the lock: aging slots may raise events, which must not wait on it.
        var follower = Volatile.Read(ref _follower);
        status = status with
        {
            Followed = Followed(),
            ConnectedToPrimary = follower?.Connected ?? false,
            LastReplyFromPrimary = follower?.LastReply,
        };
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

    /// <summary>
    /// Stops the listener and the follower loop and releases their resources. Called by the store on close; the store owns
    /// its replication, so this is not public (spec 015 R3.1): disposing it would stop replication under an open store.
    /// </summary>
    internal void Dispose()
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
        lock (_eventsGate)
        {
            _events?.Dispose();
        }
        _metrics.Dispose();
    }
}

/// <summary>
/// A node's replication status (spec 003 R13.2): role, epoch, the primary it follows, its listener endpoint, and a
/// bounded log of recent transitions. From <see cref="CrystalReplication.Status"/>; the protocol's <c>STATUS</c> reports the same.
/// </summary>
/// <param name="Role">This node's role, as <see cref="CrystalReplication.Role"/>.</param>
/// <param name="Epoch">This node's current epoch.</param>
/// <param name="FollowedPrimary">The endpoint this node follows as a standby, or null.</param>
/// <param name="ListenerEndpoint">The endpoint this node's listener is bound to, or null if not listening.</param>
/// <param name="RecentTransitions">A bounded log of recent role/epoch/slot transitions (newest last).</param>
/// <param name="Slots">On a primary, each standby's slot per database (R2.2, R2.4); empty otherwise.</param>
public sealed record ReplicationStatus(
    ReplicationRole Role,
    long Epoch,
    string? FollowedPrimary,
    string? ListenerEndpoint,
    IReadOnlyList<string> RecentTransitions,
    IReadOnlyList<ReplicationSlot> Slots)
{
    /// <summary>The databases this node follows as a standby, with position and lag (spec 011 R10.4); empty on a primary.</summary>
    public IReadOnlyList<FollowedDatabase> Followed { get; init; } = [];

    /// <summary>
    /// Whether this node, as a standby, holds an authenticated connection to the primary it follows now (spec 013 R8.2):
    /// false on a primary, while reconnecting, and when the primary is unreachable. With
    /// <see cref="LastReplyFromPrimary"/>, what a product needs to tell a follower parked in a long poll from one whose
    /// primary vanished.
    /// </summary>
    public bool ConnectedToPrimary { get; init; }

    /// <summary>
    /// When this node, as a standby, last read a reply from its primary: a page, an empty long-poll reply, a snapshot
    /// chunk, the handshake (spec 013 R8.1); null until the first. While the link is healthy it is never older than
    /// <see cref="ReplicationOptions.LongPollMax"/> plus one round trip, the longest a caught-up pull waits for its reply
    /// (R8.3); older means the link is in trouble. It describes the connection, which serves every followed database.
    /// </summary>
    public DateTimeOffset? LastReplyFromPrimary { get; init; }
}

/// <summary>A database handed over by <see cref="CrystalReplication.StepDownAsync"/> (spec 011 R14.2): its final position.</summary>
/// <param name="Uid">The database's UID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Position">Its latest position when writes stopped, which the standby acknowledged.</param>
public sealed record SteppedDownDatabase(string Uid, string Name, long Position);

/// <summary>
/// A database this standby follows (spec 011 R10.4, 003 R13.2): its position, the primary's latest position as the last
/// reply stated it, and the difference.
/// </summary>
/// <param name="Uid">The database's UID.</param>
/// <param name="Name">Its name on this node.</param>
/// <param name="Position">The position applied here.</param>
/// <param name="PrimaryLatest">The primary's latest position, from the last reply that named it.</param>
/// <param name="Lag">Sequences this standby lacks: <c>PrimaryLatest - Position</c>, never below zero.</param>
public sealed record FollowedDatabase(string Uid, string Name, long Position, long PrimaryLatest, long Lag);

/// <summary>A standby slot's state (spec 003 R2.3).</summary>
// Values are explicit and append-only (spec 015 R3.1): they reach metrics, logs and products' switches.
public enum ReplicationSlotState
{
    /// <summary>In contact and within a quarter of the lag cap.</summary>
    Active = 0,

    /// <summary>More than a quarter of the lag cap behind.</summary>
    Lagging = 1,

    /// <summary>
    /// Past the lag cap, or silent past <c>SlotStaleAfter</c>. An observation, never a hold: the standby resumes on its
    /// next contact if the log still reaches it, or meets a gap and re-bootstraps.
    /// </summary>
    Dropped = 2,
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
