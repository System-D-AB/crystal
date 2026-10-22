// Spec 003 R9.3 — the events Crystal raises so a product can drive its failover policy: role transitions, epoch
// changes, slot states, gaps, divergences, format refusals, and the equal-epoch conflict (R6.4). Crystal reports; the
// product decides (R9). Delivered through CrystalReplication.Events.

using System;

namespace SystemD.Crystal;

/// <summary>What a <see cref="ReplicationEvent"/> reports (spec 003 R9.3).</summary>
// Values are explicit and append-only (spec 015 R3.1): they reach metrics, logs and products' switches.
public enum ReplicationEventKind
{
    /// <summary>This node promoted to primary at a new epoch (R6.2).</summary>
    Promoted = 0,

    /// <summary>This node demoted from primary (it saw a higher epoch, or was asked to).</summary>
    Demoted = 1,

    /// <summary>This node fenced itself read-only (R6.5).</summary>
    Fenced = 2,

    /// <summary>This node adopted a higher epoch (R6.3).</summary>
    EpochChanged = 3,

    /// <summary>A standby slot is lagging past a fraction of the cap (R2.3).</summary>
    SlotLagging = 4,

    /// <summary>A standby slot was dropped (lag cap or staleness, R2.3).</summary>
    SlotDropped = 5,

    /// <summary>A database reported a gap and must re-bootstrap (R5.1).</summary>
    Gap = 6,

    /// <summary>A database diverged and must re-bootstrap (R5.2).</summary>
    Divergence = 7,

    /// <summary>A peer's storage format or layout did not match; nothing was served or applied (R8).</summary>
    FormatRefused = 8,

    /// <summary>Two primaries at the same epoch were detected (R6.4): both ids and endpoints are in the detail.</summary>
    Conflict = 9,

    /// <summary>
    /// This node was promoted while some of its databases were marked for re-bootstrap (spec 011 R2.4): the detail names
    /// each and why. A database marked for a divergence was given a fresh history, so its other standbys re-bootstrap
    /// from this node; the product decides whether to serve it.
    /// </summary>
    PromotedWithPendingResync = 10,

    /// <summary>
    /// A fenced primary returned to primary at the epoch it was fenced at (<see cref="CrystalReplication.Unfence"/>, spec
    /// 013 R10): it serves and accepts writes again, with no new epoch.
    /// </summary>
    Unfenced = 11,
}

/// <summary>
/// One replication event (spec 003 R9.3). A product subscribes to <see cref="CrystalReplication.Events"/> and reads
/// its policy from these; Crystal never decides failover itself. The detail is a short, data-free description (never a
/// key, value or secret).
/// </summary>
public sealed class ReplicationEvent(ReplicationEventKind kind, long epoch, string detail) : EventArgs
{
    /// <summary>What happened.</summary>
    public ReplicationEventKind Kind { get; } = kind;

    /// <summary>The node's epoch when the event was raised.</summary>
    public long Epoch { get; } = epoch;

    /// <summary>A short description (ids, endpoints, counts — never data or secrets).</summary>
    public string Detail { get; } = detail;

    /// <summary>The database UID the event concerns, if any (gaps, divergences, slots).</summary>
    public string? Uid { get; init; }

    /// <summary>The other node's id, when a peer caused the event (a conflict, an epoch or demotion from a peer; spec 011 R7.3).</summary>
    public string? PeerNodeId { get; init; }

    /// <summary>The other node's endpoint, when known (spec 011 R7.3).</summary>
    public string? PeerEndpoint { get; init; }

    /// <summary>A short, data-free rendering of the event (kind, epoch, detail).</summary>
    public override string ToString() => $"{Kind} epoch={Epoch} {Detail}";
}
