// Ported from Highway: src/Highway.Server/Observability/HighwayMetrics.cs (replication instruments)
// Spec 003 R13.1 — replication instruments on 007's meter (SystemD.Crystal), under crystal.replication.*. Counters
// cost a branch when nobody listens; the role and epoch are observable gauges read only when a listener collects.
// No tag carries a key, a value or a secret (the architecture gate). Fed from CrystalReplication's event sink.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.Metrics;

namespace SystemD.Crystal.Observability;

/// <summary>
/// The replication instruments of one store (spec 003 R13.1), on the shared meter <see cref="CrystalMetrics.MeterName"/>.
/// Role transitions, slots, gaps, divergences, bootstraps and conflicts are counters; role and epoch are observable
/// gauges. One per store, owned by <see cref="CrystalReplication"/>; disposed with it.
/// </summary>
internal sealed class ReplicationMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _promotions;
    private readonly Counter<long> _demotions;
    private readonly Counter<long> _fences;
    private readonly Counter<long> _epochChanges;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _gaps;
    private readonly Counter<long> _divergences;
    private readonly Counter<long> _slotDrops;
    private readonly Counter<long> _formatRefusals;
    private readonly Counter<long> _bootstraps;
    private readonly Counter<long> _pagesApplied;
    private readonly Counter<long> _bytesApplied;
    private readonly Counter<long> _framesRefused;
    private readonly Counter<long> _bytesShipped;
    private readonly Func<IReadOnlyList<FollowedDatabase>> _followed;

    private readonly Func<long> _epoch;
    private readonly Func<string> _role;
    private readonly Func<IReadOnlyList<ReplicationSlot>> _slots;
    private readonly Func<string, string?> _databaseName;
    private readonly bool _tagWithDatabase;

    /// <param name="factory">The host's meter factory, or null for a meter of the store's own.</param>
    /// <param name="nodeId">The node id every instrument is tagged with.</param>
    /// <param name="epoch">This node's epoch, read when a listener collects.</param>
    /// <param name="role">This node's role, read when a listener collects.</param>
    /// <param name="slots">The primary's slots (empty elsewhere), read only when a listener collects.</param>
    /// <param name="followed">The databases a standby follows with their lag (empty elsewhere), read when a listener collects.</param>
    /// <param name="databaseName">A database's name from its UID, for tags when <paramref name="tagWithDatabase"/>.</param>
    /// <param name="tagWithDatabase">Lag per database, tagged with its name (007 R1.3), instead of the store's worst.</param>
    public ReplicationMetrics(
        IMeterFactory? factory, string nodeId, Func<long> epoch, Func<string> role,
        Func<IReadOnlyList<ReplicationSlot>> slots, Func<IReadOnlyList<FollowedDatabase>> followed, Func<string, string?> databaseName, bool tagWithDatabase)
    {
        _followed = followed ?? throw new ArgumentNullException(nameof(followed));
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        _role = role ?? throw new ArgumentNullException(nameof(role));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _databaseName = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
        _tagWithDatabase = tagWithDatabase;

        var options = new MeterOptions(CrystalMetrics.MeterName)
        {
            Version = CrystalVersion.Value,
            Tags = [new KeyValuePair<string, object?>("crystal.node", nodeId)],
            Scope = this,
        };
        _meter = factory?.Create(options) ?? new Meter(options);

        _promotions = _meter.CreateCounter<long>("crystal.replication.promotions", "{promotion}", "Promotions to primary (R6.2).");
        _demotions = _meter.CreateCounter<long>("crystal.replication.demotions", "{demotion}", "Demotions from primary.");
        _fences = _meter.CreateCounter<long>("crystal.replication.fences", "{fence}", "Self-fences (R6.5).");
        _epochChanges = _meter.CreateCounter<long>("crystal.replication.epoch_changes", "{change}", "Higher epochs adopted (R6.3).");
        _conflicts = _meter.CreateCounter<long>("crystal.replication.conflicts", "{conflict}", "Equal-epoch conflicts detected (R6.4).");
        _gaps = _meter.CreateCounter<long>("crystal.replication.gaps", "{gap}", "Databases that hit a gap and re-bootstrapped (R5.1).");
        _divergences = _meter.CreateCounter<long>("crystal.replication.divergences", "{divergence}", "Databases that diverged and re-bootstrapped (R5.2).");
        _slotDrops = _meter.CreateCounter<long>("crystal.replication.slot_drops", "{drop}", "Standby slots dropped (lag cap or staleness, R2.3).");
        _formatRefusals = _meter.CreateCounter<long>("crystal.replication.format_refusals", "{refusal}", "Peers refused for a format mismatch (R8).");
        _bootstraps = _meter.CreateCounter<long>("crystal.replication.bootstraps", "{bootstrap}", "Snapshot bootstraps installed (R7).");
        _pagesApplied = _meter.CreateCounter<long>("crystal.replication.pages_applied", "{page}", "Pull pages applied on a standby (R4).");
        _bytesApplied = _meter.CreateCounter<long>("crystal.replication.bytes_applied", "By", "WAL bytes applied on a standby (R4).");
        _bytesShipped = _meter.CreateCounter<long>("crystal.replication.bytes_shipped", "By", "Batch bytes a primary sent in pull replies (spec 011 R10.1, 003 R13.1).");
        _meter.CreateObservableGauge("crystal.replication.standby_lag", StandbyLag, "{sequence}",
            "Sequences this standby lacks of its primary's latest, from the last reply (spec 011 R10.4): the worst, or per database when tagged.");
        _framesRefused = _meter.CreateCounter<long>("crystal.replication.frames_refused", "{frame}", "Frames refused for exceeding a size cap, by the side that refused them (spec 011 R4.3).");

        _meter.CreateObservableGauge("crystal.replication.epoch", () => _epoch(), "{epoch}", "This node's current replication epoch (R6).");
        _meter.CreateObservableGauge("crystal.replication.role", RoleCode, "{role}", "This node's role: 0 primary, 1 standby, 2 fenced.");
        _meter.CreateObservableGauge("crystal.replication.slots", SlotsByState, "{slot}",
            "Standby slots on this primary, by state (R2.2, R13.1).");
        _meter.CreateObservableGauge("crystal.replication.lag", LagSequences, "{sequence}",
            "Sequences the furthest-behind standby lacks, of the slots not dropped (R2.4).");
        _meter.CreateObservableGauge("crystal.replication.lag_time", LagSeconds, "s",
            "Age of the oldest batch the furthest-behind standby lacks, of the slots not dropped: what a failover would lose (R2.4, R12.1).");
    }

    private IEnumerable<Measurement<long>> SlotsByState()
    {
        var slots = _slots();
        foreach (var state in new[] { ReplicationSlotState.Active, ReplicationSlotState.Lagging, ReplicationSlotState.Dropped })
        {
            var count = 0L;
            foreach (var slot in slots)
            {
                if (slot.State == state)
                {
                    count++;
                }
            }

            yield return new Measurement<long>(count, new KeyValuePair<string, object?>("crystal.slot.state", state.ToString().ToLowerInvariant()));
        }
    }

    private IEnumerable<Measurement<long>> LagSequences() =>
        Worst(s => s.LagSequences, v => v);

    private IEnumerable<Measurement<double>> LagSeconds() =>
        Worst(s => (long)s.LagTime.TotalMilliseconds, v => v / 1000.0);

    /// <summary>The worst lag over slots not dropped: one value for the store, or one per database when tagged.</summary>
    private IEnumerable<Measurement<T>> Worst<T>(Func<ReplicationSlot, long> of, Func<long, T> to)
        where T : struct
    {
        var worst = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var slot in _slots())
        {
            if (slot.State == ReplicationSlotState.Dropped)
            {
                continue;
            }

            var key = _tagWithDatabase ? slot.DatabaseUid : string.Empty;
            worst[key] = Math.Max(worst.TryGetValue(key, out var w) ? w : 0, of(slot));
        }

        if (!_tagWithDatabase)
        {
            yield return new Measurement<T>(to(worst.TryGetValue(string.Empty, out var all) ? all : 0));
            yield break;
        }

        foreach (var (uid, value) in worst)
        {
            yield return new Measurement<T>(to(value),
                new KeyValuePair<string, object?>("crystal.database", _databaseName(uid) ?? uid));
        }
    }

    /// <summary>Pages and bytes a standby applied (R13.1), counted by the follower.</summary>
    public void PageApplied(long bytes)
    {
        _pagesApplied.Add(1);
        _bytesApplied.Add(bytes);
    }

    /// <summary>A snapshot bootstrap installed (R13.1).</summary>
    public void Bootstrapped() => _bootstraps.Add(1);

    /// <summary>Batch bytes a primary put in a pull reply (spec 011 R10.1).</summary>
    public void BytesShipped(long bytes)
    {
        if (bytes > 0)
        {
            _bytesShipped.Add(bytes);
        }
    }

    private IEnumerable<Measurement<long>> StandbyLag()
    {
        var followed = _followed();
        if (!_tagWithDatabase)
        {
            yield return new Measurement<long>(followed.Count == 0 ? 0 : followed.Max(f => f.Lag));
            yield break;
        }

        foreach (var f in followed)
        {
            yield return new Measurement<long>(f.Lag, new KeyValuePair<string, object?>("crystal.database", f.Name));
        }
    }

    /// <summary>A frame refused for exceeding a cap (spec 011 R4.3), by the side that refused it.</summary>
    public void FrameRefused(string side) =>
        _framesRefused.Add(1, new KeyValuePair<string, object?>("crystal.side", side));

    /// <summary>Counts an event raised through <see cref="CrystalReplication.Events"/> (R13.1).</summary>
    public void Observe(ReplicationEventKind kind)
    {
        switch (kind)
        {
            case ReplicationEventKind.Promoted: _promotions.Add(1); break;
            case ReplicationEventKind.Demoted: _demotions.Add(1); break;
            case ReplicationEventKind.Fenced: _fences.Add(1); break;
            case ReplicationEventKind.EpochChanged: _epochChanges.Add(1); break;
            case ReplicationEventKind.Conflict: _conflicts.Add(1); break;
            case ReplicationEventKind.Gap: _gaps.Add(1); break;
            case ReplicationEventKind.Divergence: _divergences.Add(1); break;
            case ReplicationEventKind.SlotDropped: _slotDrops.Add(1); break;
            case ReplicationEventKind.FormatRefused: _formatRefusals.Add(1); break;
            default: break; // SlotLagging has no counter of its own; it is a gauge-level state
        }
    }

    private IEnumerable<Measurement<long>> RoleCode()
    {
        var code = _role() switch
        {
            Replication.ReplicationProtocol.RolePrimary => 0L,
            Replication.ReplicationProtocol.RoleStandby => 1L,
            _ => 2L,
        };
        yield return new Measurement<long>(code);
    }

    public void Dispose() => _meter.Dispose();
}
