// Spec 003 T8 / R4, R1.6, R5.2; design § 5 — the standby side of one replicated database: it applies a primary WAL
// page (skip-by-watermark, one synced write per page, epoch check), reloads the catalogue when the page touched
// box 0 (R4.5), records the incarnation and base it followed (R5.2), and marks the database read-only so local
// writes are refused (R1.6). RocksDB-touching (WalAccess, DatabaseContext), so it lives in Storage; the apply logic
// itself is the RocksDB-free BatchApplier over the IApplyTarget this type provides.

using System;
using System.Collections.Generic;
using SystemD.Crystal.Replication;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Applies replicated pages onto one standby database (spec 003 design § 5). It owns the <see cref="BatchApplier"/>
/// and the <see cref="IApplyTarget"/> over the database's WAL, drives the catalogue reload and the incarnation record
/// after a page lands, and holds the database read-only (R1.6) for as long as it follows.
/// </summary>
internal sealed class StandbyDatabase
{
    private readonly DatabaseSlot _slot;
    private readonly string _uid;
    private bool _incarnationRecordCleared;
    private readonly string _dataDirectory;
    private readonly BatchApplier _applier;
    private readonly ApplyTarget _target;

    // The follow record, read from disk once and then kept as this object writes it (003 review): every pull checks it,
    // and while this database is followed nothing else writes it (a bootstrap writes it, then follows anew).
    private FollowRecord? _followRecord;
    private bool _followRecordLoaded;

    public StandbyDatabase(DatabaseSlot slot, string uid, string dataDirectory, long initialEpoch = 0)
    {
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _uid = uid ?? throw new ArgumentNullException(nameof(uid));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _target = new ApplyTarget(new WalAccess(slot.Instance));
        _applier = new BatchApplier(_target, (ulong)Math.Max(initialEpoch, 0));

        // R1.6: while this database follows a primary, its handle refuses local writes. The applier writes through
        // WalAccess.ApplyRaw, which does not pass the handle's commit path, so replicated applies still land.
        _slot.Context.FollowingPrimary = true;
    }

    /// <summary>The database's applied watermark: its latest sequence (R2.1).</summary>
    public ulong Watermark => _target.Watermark;

    /// <summary>The highest primary epoch accepted (R4.3).</summary>
    public ulong SeenEpoch => _applier.SeenEpoch;

    /// <summary>Synced writes issued (one per non-empty page, 066 C2) — for the catch-up tests.</summary>
    public int SyncedWrites => _applier.SyncedWrites;

    /// <summary>
    /// Applies one pull page for this database (spec 003 R4). After an applied page: reloads the catalogue if a box-0
    /// record was written (R4.5), and records the <paramref name="servedIncarnation"/> and <paramref name="servedBase"/>
    /// if they changed (R5.2, R5.5). Returns the apply result.
    /// </summary>
    public ApplyResult Apply(
        long epoch, IReadOnlyList<(ulong Seq, byte[] Data)> batches, string servedIncarnation, long servedBase,
        IReadOnlyList<IncarnationEnd>? servedHistory = null)
    {
        ArgumentNullException.ThrowIfNull(batches);

        // The database is about to hold what this node received: none of its own earlier history as a primary may be
        // continued if it is promoted later (tasks addendum, review of the fixes). Once per followed database.
        if (!_incarnationRecordCleared && batches.Count > 0)
        {
            IncarnationRecord.Delete(_dataDirectory, _uid);
            _incarnationRecordCleared = true;
        }

        var result = _applier.ApplyPage((ulong)Math.Max(epoch, 0), batches);
        if (result.Status != ApplyStatus.Applied)
        {
            return result;
        }

        // R4.5: a page that wrote box 0 changed the catalogue; reload it before the next read through the database.
        if (result.TouchedCatalogue)
        {
            _slot.Context.LoadCatalogue(DatabaseKind.Database, _uid);
        }

        // R5.2/R5.5: record the incarnation and base followed, if they changed, temp-and-rename. Written after the
        // page's synced write, so a crash between the two causes at most a needless re-bootstrap, never a wrong apply.
        // Recorded on any accepted page, even one with nothing new: a standby that is caught up keeps the incarnation it
        // follows current, so the primary's history of earlier incarnations need not reach back to it.
        if (Guid.TryParse(servedIncarnation, out var incarnation))
        {
            var current = KeptFollowRecord();
            if (current is null
                || !string.Equals(current.Incarnation, incarnation.ToString("D"), StringComparison.Ordinal)
                || current.Base != servedBase)
            {
                FollowRecord.Write(_dataDirectory, _uid, incarnation, servedBase, servedHistory);
                _followRecord = FollowRecord.Read(_dataDirectory, _uid);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether this database has diverged from the served log (spec 003 R5.2, design § 6, as amended 2026-10-11): it
    /// continues only if the log it holds (the kept incarnation in <c>follow/{uid}.json</c>, and the incarnations in its
    /// kept history up to their ends) and the served log (the served incarnation, and its history up to their ends)
    /// name a common incarnation within both ends at its position. A missing or unparsable kept record knows nothing
    /// shared (R5.5): only an empty database continues, so a crash between apply and record causes at most a needless
    /// re-bootstrap, never a missed divergence.
    /// </summary>
    public bool HasDiverged(string servedIncarnation, long servedBase, IReadOnlyList<IncarnationEnd>? history = null)
    {
        var kept = KeptFollowRecord();
        var sameIncarnation = kept is not null
            && string.Equals(kept.Incarnation, servedIncarnation, StringComparison.Ordinal);
        if (sameIncarnation)
        {
            return false; // same log: continue
        }

        var position = (long)Watermark;

        // Exact history (tasks addendum 2026-10-11; replaces R5.2's "position above the base"). What this standby holds
        // is the kept incarnation up to its position, and also each incarnation in the kept history up to that entry's
        // end. What is served is the served incarnation, and each incarnation in the served history up to its end. The
        // standby continues only if the two name a common incarnation within both ends; a batch it holds that no common
        // incarnation covers (its own writes as a primary, or batches its primary lost) means it diverged. The served
        // base is not a reason to continue: a standby that wrote as a primary holds its own batches below it.
        _ = servedBase;
        var held = kept?.AsHistory();
        if (held is null || !Guid.TryParseExact(servedIncarnation, "D", out var servedId))
        {
            return position > 0; // nothing known about what it holds: only an empty database may continue
        }

        foreach (var (heldId, heldEnd) in Shares(held.Id, held.History))
        {
            foreach (var (servedShareId, servedEnd) in Shares(servedId, history ?? []))
            {
                if (heldId == servedShareId && position <= Math.Min(heldEnd, servedEnd))
                {
                    return false;
                }
            }
        }

        return true;

        static IEnumerable<(Guid Id, long End)> Shares(Guid id, IReadOnlyList<IncarnationEnd> earlier)
        {
            yield return (id, long.MaxValue); // the log itself, whatever its position
            foreach (var e in earlier)
            {
                yield return (e.Id, e.End);
            }
        }
    }

    /// <summary>The follow record of this database: read from disk on first use, then as this object last wrote it.</summary>
    private FollowRecord? KeptFollowRecord()
    {
        if (!_followRecordLoaded)
        {
            _followRecord = FollowRecord.Read(_dataDirectory, _uid);
            _followRecordLoaded = true;
        }

        return _followRecord;
    }

    /// <summary>Stops following (promotion, T12): the database accepts local writes again.</summary>
    public void StopFollowing() => _slot.Context.FollowingPrimary = false;

    /// <summary>The IApplyTarget over the database's WAL: the derived watermark and the synced apply (R4.2).</summary>
    private sealed class ApplyTarget(WalAccess wal) : IApplyTarget
    {
        public ulong Watermark => wal.LatestSequence();

        public void ApplyRaw(byte[] batchBytes) => wal.ApplyRaw(batchBytes);
    }
}
