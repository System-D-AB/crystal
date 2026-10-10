// Spec 003 T7 / design § 3, § 4 — the primary's per-database serving state: the in-memory tail of recent commits, the
// WAL read path, and the incarnation/base a standby checks for divergence. One per replicated database the primary
// serves. RocksDB-touching (it owns a WalAccess and wires the commit hook), so it lives in Storage; the tail and the
// pull logic above it are RocksDB-free (001 R3.2).

using System;
using System.Collections.Generic;
using System.Threading;
using SystemD.Crystal.Replication;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The primary's serving state for one replicated database (spec 003 design § 3): its <see cref="Tail"/> of recently
/// committed batches (fed from <see cref="RocksInstance.OnCommitted"/>), its WAL read path, and its incarnation and
/// base. The incarnation is a GUID set when the database first opens on a primary and again at promotion; the base is
/// the latest sequence at that moment. Neither is persisted — a restart is a new incarnation (design § 3) — so a
/// standby that applied past this base under a different incarnation knows it diverged (R5.2).
/// </summary>
internal sealed class DatabaseReplication
{
    private readonly WalAccess _wal;
    private readonly WalReaderPool _readers;
    private readonly RocksInstance _instance;
    private readonly DatabaseSlot _slot;
    private readonly TimeProvider _clock;

    /// <summary>When this database's recent sequences were committed, for lag in time (R2.4).</summary>
    public CommitTimeline Timeline { get; } = new();

    /// <summary>The age of the oldest batch a standby at <paramref name="acknowledged"/> lacks (R2.4, R12.1).</summary>
    public TimeSpan LagTime(long acknowledged) => Timeline.AgeAfter(acknowledged, _clock.GetUtcNow().UtcTicks);

    public DatabaseReplication(string uid, DatabaseSlot slot, long tailBytes, TimeProvider clock, Action? onCommit = null)
    {
        Uid = uid ?? throw new ArgumentNullException(nameof(uid));
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(clock);
        _slot = slot;
        _instance = slot.Instance;
        _wal = new WalAccess(_instance);
        _readers = new WalReaderPool(_wal, clock);
        Tail = new ReplicationTail(tailBytes);

        // Capture every committed batch into the tail (R3.5), then wake any waiting long-poll pull (design § 4). The
        // hook fires inside RocksInstance.Write after the batch has its sequence; the tail only keeps batches once a
        // pull has enabled it, so this costs nothing until a standby attaches.
        _clock = clock;
        _instance.OnCommitted = bytes =>
        {
            Tail.Add(bytes);
            Timeline.Committed(bytes, clock.GetUtcNow().UtcTicks);
            onCommit?.Invoke();
        };

        // Release the held readers while the database closes (drop, reopen, store close), before its instance goes:
        // releasing them later would call into a closed native instance.
        slot.OnClose(Detach);
    }

    private int _detached;

    /// <summary>
    /// Enters the database for one serving step, so a drop or close waits for it; null once it is closing (the caller
    /// answers as if the database were gone).
    /// </summary>
    public IDisposable? Enter() => _slot.TryEnter();

    /// <summary>The database's stable UID (R1.3) — how the wire names it.</summary>
    public string Uid { get; }

    /// <summary>Recently committed batches, served to a caught-up standby without a WAL read (R3.5).</summary>
    public ReplicationTail Tail { get; }

    /// <summary>
    /// This database's log history as one value (design § 3; tasks addendum B3): incarnation, base and history read
    /// together, never mixed across a promotion. Read from the database's slot, never fixed at the first pull: a base
    /// taken then would include every write since the open or promotion, and an old primary holding writes no standby
    /// received would pass as caught up.
    /// </summary>
    public LogIncarnation Current => _slot.Incarnation;

    /// <summary>The database's slot (the snapshot path checkpoints it, inside <see cref="Enter"/>).</summary>
    public DatabaseSlot Slot => _slot;

    /// <summary>Whether the database this serves is still the open one: false once it is dropped or reopened.</summary>
    public bool IsCurrent => !_slot.IsClosing;

    /// <summary>The database's latest committed sequence (R2.1).</summary>
    public long Latest => (long)_wal.LatestSequence();

    /// <summary>Starts capturing commits into the tail (first pull of this database). Idempotent.</summary>
    public void EnableTail() => Tail.Enable();

    /// <summary>Clears the tail and held readers (epoch or role change: a new history, design § 3).</summary>
    public void ClearTail()
    {
        Tail.Clear();
        _readers.Clear();
    }

    /// <summary>
    /// What serving <paramref name="fromSeq"/> would report, without reading anything (003 addendum B4): a pull whose
    /// page is already spent still reports a divergence at once, and otherwise an empty entry the standby pulls again.
    /// </summary>
    public ServeResult Position(long fromSeq) => fromSeq > Latest ? ServeResult.Diverged : ServeResult.CaughtUp;

    /// <summary>
    /// Fills <paramref name="into"/> with the contiguous run of batches after <paramref name="fromSeq"/>, up to
    /// <paramref name="maxBytes"/> (spec 003 design § 4): from the tail if it holds the cursor, else from the WAL.
    /// Returns the serve outcome; <paramref name="more"/> is the first sequence left out by the byte cap, or null.
    /// </summary>
    public ServeResult Serve(long fromSeq, int maxBytes, List<(ulong Seq, byte[] Data)> into, out ulong? more)
    {
        more = null;
        Tail.Enable();

        var latest = Latest;
        if (fromSeq > latest)
        {
            // The standby claims a position past the primary's latest: it has batches the primary never had (R5.2).
            return ServeResult.Diverged;
        }

        if (fromSeq == latest)
        {
            return ServeResult.CaughtUp; // nothing after the cursor
        }

        // Serve from memory when the tail holds the cursor (a standby just behind). GetUpdatesSince re-reads the live
        // WAL from its start, so this keeps a near-caught-up pull's cost off the WAL (design § 3).
        if (Tail.TryRead((ulong)fromSeq, maxBytes, into, out more))
        {
            return ServeResult.Served;
        }

        // Behind the tail: read from the WAL through a held reader, continuing one kept from the previous page when its
        // next sequence matches (design § 3), chunk after chunk until the page is full (R3.2, 003 addendum B4). The
        // reader stays in step for the next pull, so the catch-up cost stays O(pages), not O(pages²). We are here only
        // because fromSeq < latest, so there IS newer data; a reader that can serve nothing for the first chunk is
        // therefore a genuine gap (the range was discarded), not "caught up".
        var want = (ulong)fromSeq + 1;
        if (_readers.Read(fromSeq, maxBytes) is not { } chunk)
        {
            return ServeResult.Gap; // the WAL no longer reaches the cursor (R5.1)
        }

        var seq = chunk.First;
        var data = chunk.Data;

        if (chunk.First < want)
        {
            // The chunk starts at the record that CONTAINS the cursor (a group-commit merge the tail served piecemeal).
            // Trim it to start at `want`; a record that cannot be split with certainty is a gap (R3.6).
            if (!Replication.WriteBatchTrim.TryStartAt(data, want, out var trimmed))
            {
                return ServeResult.Gap;
            }

            data = trimmed;
            seq = want;
        }

        into.Add((seq, data));
        long bytes = data.Length;
        var next = chunk.Next;
        while (bytes < maxBytes && (long)next <= latest)
        {
            // The next chunk continues the reader just returned. A chunk that does not start exactly at `next`, or none
            // at all, ends this page; the next pull resumes from what was sent.
            if (_readers.Read((long)next - 1, maxBytes) is not { } following || following.First != next)
            {
                break;
            }

            into.Add((following.First, following.Data));
            bytes += following.Data.Length;
            next = following.Next;
        }

        if ((long)next <= latest)
        {
            more = next;
        }

        return ServeResult.Served;
    }

    /// <summary>The first available sequence when a gap is reported (design § 4): the WAL's earliest, or latest+1.</summary>
    public long FirstAvailable()
    {
        // Probe from 0: the first record GetUpdatesSince can serve is the WAL's earliest retained sequence. If none,
        // everything lives in SSTs past the live WAL — the standby's only path forward is a snapshot, so report
        // latest+1 (nothing to pull).
        var probe = new List<(ulong Seq, byte[] Data)>();
        if (_wal.ReadPage(0, 1, probe) == WalPageStatus.Ok && probe.Count > 0)
        {
            return (long)probe[0].Seq;
        }

        return Latest + 1;
    }

    /// <summary>Releases the commit hook and held readers so a dropped or demoted database stops serving.</summary>
    public void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0)
        {
            return;
        }

        _instance.OnCommitted = null;
        Tail.Clear();
        _readers.Dispose();
    }

    /// <summary>Held readers open now (design § 3 tests): each opened read the WAL from its start once.</summary>
    internal int ReadersOpened => _readers.Opened;

    /// <summary>Whether a held reader is pausing WAL file deletion now (design § 3 tests).</summary>
    internal bool HoldingWal => _readers.HoldingWal;
}

/// <summary>The outcome of serving one database in a pull (spec 003 design § 4).</summary>
internal enum ServeResult
{
    /// <summary>Batches were added (the page may be partial; see <c>more</c>).</summary>
    Served,

    /// <summary>The standby is at the primary's latest: an empty, caught-up entry.</summary>
    CaughtUp,

    /// <summary>The WAL no longer reaches the cursor: the standby re-bootstraps (R5.1).</summary>
    Gap,

    /// <summary>The standby's position is past the primary's latest: it diverged (R5.2).</summary>
    Diverged,
}
