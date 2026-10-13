// Ported from Highway: src/Highway.Server/Storage/Rocks/BatchApplier.cs and ReplicationApply.cs — the standby's
// apply of a pull page: skip by sequence, concatenate a contiguous run into one synced write, epoch check (spec 003
// R4). RocksDB-free: it works over IApplyTarget, which the Storage layer implements, so it is unit-tested without a
// live database. Catalogue reload (R4.5) and the incarnation record (R5.2) are the follower's, after a page lands.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SystemD.Crystal.Replication;

/// <summary>The database side an applier writes to (spec 003 design § 5). Implemented in <c>Storage</c> over <c>WalAccess</c>.</summary>
internal interface IApplyTarget
{
    /// <summary>The standby's applied watermark: the database's latest sequence (R2.1). 0 on a blank database.</summary>
    ulong Watermark { get; }

    /// <summary>Applies raw committed batch bytes, synced (R4.2). One batch or a concatenated page.</summary>
    void ApplyRaw(byte[] batchBytes);
}

/// <summary>The outcome of applying one page (spec 003 R4).</summary>
internal enum ApplyStatus
{
    Applied,
    EpochRefused,

    /// <summary>
    /// The page does not continue the database from its position (003 should-fix 7): a hole, a batch straddling the
    /// position, or a malformed batch. Nothing was written; the database must be re-bootstrapped.
    /// </summary>
    NotContiguous,
}

/// <summary>What one page apply did (spec 003 R4).</summary>
/// <param name="Status">Applied, or refused for a stale epoch.</param>
/// <param name="Applied">Batches written.</param>
/// <param name="Skipped">Batches at or below the watermark, dropped (R4.1).</param>
/// <param name="TouchedCatalogue">Whether any applied batch wrote box 0 (the catalogue needs reloading, R4.5).</param>
/// <param name="Watermark">The database's latest sequence after the apply (derived, R4.2): a crash cannot leave it ahead of the data.</param>
internal readonly record struct ApplyResult(ApplyStatus Status, int Applied, int Skipped, bool TouchedCatalogue, ulong Watermark);

/// <summary>
/// Applies pull pages to one standby database (spec 003 R4, design § 5): skips batches at or below the watermark so a
/// re-sent page applies nothing twice (R4.1); concatenates a contiguous run into one synced write per page, so a
/// crash keeps the whole page or none of it (R4.2); and refuses a page whose epoch is below the newest seen, writing
/// nothing (R4.3). The applied bytes are byte-identical to the primary's (R4.4).
/// </summary>
internal sealed class BatchApplier
{
    private const int HeaderSize = 12;

    private readonly IApplyTarget _target;

    public BatchApplier(IApplyTarget target, ulong initialEpoch = 0)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        SeenEpoch = initialEpoch;
    }

    /// <summary>The highest primary epoch this standby has accepted (R4.3). Seeded from the node's persisted epoch.</summary>
    public ulong SeenEpoch { get; private set; }

    /// <summary>The database's current applied watermark: its latest sequence (R2.1), derived, never a separate write.</summary>
    public ulong Watermark => _target.Watermark;

    /// <summary>The number of synced writes this applier has issued (one per non-empty page, 066 C2).</summary>
    public int SyncedWrites { get; private set; }

    /// <summary>
    /// Applies one pull page for this database. Refuses the whole page (writes nothing) when <paramref name="epoch"/>
    /// is below <see cref="SeenEpoch"/> (R4.3) or does not continue the database (003 should-fix 7); otherwise skips
    /// batches already applied and writes the rest as one synced write. <paramref name="accepted"/> runs once the page
    /// is accepted, before anything is written, also for a page with nothing new (spec 011 R2.1: the follow record is
    /// written there, so a crash after the write never leaves a record older than the data).
    /// </summary>
    public ApplyResult ApplyPage(ulong epoch, IReadOnlyList<(ulong Seq, byte[] Data)> batches, Action? accepted = null)
    {
        ArgumentNullException.ThrowIfNull(batches);

        if (epoch < SeenEpoch)
        {
            return new ApplyResult(ApplyStatus.EpochRefused, 0, 0, TouchedCatalogue: false, _target.Watermark);
        }

        if (epoch > SeenEpoch)
        {
            SeenEpoch = epoch;
        }

        var watermark = _target.Watermark;
        var fresh = new List<byte[]>(batches.Count);
        var skipped = 0;
        var touchedCatalogue = false;

        // RocksDB numbers a written batch from the standby's own next sequence, not from the batch's header, so the
        // standby's positions match the primary's only if every batch it writes starts exactly where the last one
        // ended (003 should-fix 7). A batch wholly at or below the watermark is a re-pull and skipped (R4.1); a batch
        // straddling the watermark, or a fresh batch that does not start at the expected sequence, refuses the page
        // whole, writing nothing.
        var expected = watermark + 1;
        foreach (var (seq, data) in batches)
        {
            if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(data) != seq)
            {
                return NotContiguous();
            }

            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            var last = seq + Math.Max(count, 1u) - 1;
            if (last <= watermark)
            {
                skipped++;
                continue;
            }

            if (seq != expected)
            {
                return NotContiguous(); // a hole, or a batch straddling the watermark
            }

            fresh.Add(data);
            expected = seq + count;
        }

        if (fresh.Count == 0)
        {
            accepted?.Invoke();
            return new ApplyResult(ApplyStatus.Applied, 0, skipped, TouchedCatalogue: false, _target.Watermark);
        }

        // R4.5 is decided here from the bytes, before the write, so the follower can reload the catalogue after.
        touchedCatalogue = fresh.Exists(TouchesCatalogue);

        // One synced write for the whole contiguous page (066 C2), assembled before the page is accepted.
        byte[] page;
        if (fresh.Count == 1)
        {
            page = fresh[0];
        }
        else if (!TryConcat(fresh, out page))
        {
            return NotContiguous();
        }

        accepted?.Invoke();
        _target.ApplyRaw(page);
        SyncedWrites++;
        return new ApplyResult(ApplyStatus.Applied, fresh.Count, skipped, touchedCatalogue, _target.Watermark);

        ApplyResult NotContiguous() =>
            new(ApplyStatus.NotContiguous, 0, skipped, TouchedCatalogue: false, _target.Watermark);
    }

    /// <summary>
    /// Concatenates serialized write batches into one (design § 5): header <c>[8B LE first seq][4B LE total count]</c>,
    /// then every batch's records in order. False unless each batch starts exactly where the previous ended
    /// (<c>seq + count</c>) — anything else is applied batch by batch, unchanged.
    /// </summary>
    internal static bool TryConcat(IReadOnlyList<byte[]> batches, out byte[] merged)
    {
        merged = [];
        if (batches.Count == 0)
        {
            return false;
        }

        ulong first = 0, next = 0, total = 0;
        var bytes = HeaderSize;
        for (var i = 0; i < batches.Count; i++)
        {
            var b = batches[i];
            if (b.Length < HeaderSize)
            {
                return false;
            }

            var seq = BinaryPrimitives.ReadUInt64LittleEndian(b);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
            if (i == 0)
            {
                first = seq;
            }
            else if (seq != next)
            {
                return false;
            }

            next = seq + count;
            total += count;
            bytes += b.Length - HeaderSize;
        }

        if (total > uint.MaxValue)
        {
            return false;
        }

        merged = new byte[bytes];
        BinaryPrimitives.WriteUInt64LittleEndian(merged, first);
        BinaryPrimitives.WriteUInt32LittleEndian(merged.AsSpan(8), (uint)total);
        var pos = HeaderSize;
        foreach (var b in batches)
        {
            b.AsSpan(HeaderSize).CopyTo(merged.AsSpan(pos));
            pos += b.Length - HeaderSize;
        }

        return true;
    }

    /// <summary>
    /// Whether a serialized batch writes box 0 (the catalogue), so the standby must reload it (R4.5). Box keys begin
    /// with the 4-byte big-endian box id; box 0's are <c>00 00 00 00</c>. Walks the batch's records looking at the
    /// first key's prefix; a record it cannot parse is treated as touching the catalogue (the safe answer: a needless
    /// reload, never a missed one).
    /// </summary>
    internal static bool TouchesCatalogue(byte[] batch)
    {
        var pos = HeaderSize;
        while (pos < batch.Length)
        {
            if (!TryReadFirstKeyPrefix(batch, ref pos, out var isBoxZero))
            {
                return true; // cannot parse: reload to be safe
            }

            if (isBoxZero)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadFirstKeyPrefix(byte[] b, ref int pos, out bool isBoxZero)
    {
        isBoxZero = false;
        if (pos >= b.Length)
        {
            return false;
        }

        var tag = b[pos++];
        // Crystal writes only Put (0x01), Delete (0x00) and DeleteRange (0x0F) in the default/general/delete_heavy
        // families (no column-family tags: the family is chosen by the handle, and RocksDB stores it per batch op
        // with a cf tag only when not the default). To stay safe, handle the cf-tagged variants too.
        switch (tag)
        {
            case 0x00: // Deletion: key
            case 0x07: // SingleDeletion: key
                return KeyPrefixIsBoxZero(b, ref pos, out isBoxZero);
            case 0x01: // Value: key, value
            case 0x02: // Merge: key, value
            case 0x0F: // RangeDeletion: begin, end
                if (!KeyPrefixIsBoxZero(b, ref pos, out isBoxZero))
                {
                    return false;
                }

                return SkipSlice(b, ref pos);
            case 0x04: // ColumnFamilyDeletion: cf, key
            case 0x08: // ColumnFamilySingleDeletion: cf, key
                return SkipVarint(b, ref pos) && KeyPrefixIsBoxZero(b, ref pos, out isBoxZero);
            case 0x05: // ColumnFamilyValue: cf, key, value
            case 0x06: // ColumnFamilyMerge: cf, key, value
            case 0x0E: // ColumnFamilyRangeDeletion: cf, begin, end
                if (!SkipVarint(b, ref pos) || !KeyPrefixIsBoxZero(b, ref pos, out isBoxZero))
                {
                    return false;
                }

                return SkipSlice(b, ref pos);
            case 0x03: // LogData: blob
                isBoxZero = false;
                return SkipSlice(b, ref pos);
            default:
                return false;
        }
    }

    private static bool KeyPrefixIsBoxZero(byte[] b, ref int pos, out bool isBoxZero)
    {
        isBoxZero = false;
        if (!TryReadVarint32(b, ref pos, out var len) || len > (uint)(b.Length - pos))
        {
            return false;
        }

        // The box id is the first 4 bytes of the key (box-layout § 3.2). Box 0 is 00 00 00 00.
        if (len >= 4)
        {
            isBoxZero = b[pos] == 0 && b[pos + 1] == 0 && b[pos + 2] == 0 && b[pos + 3] == 0;
        }

        pos += (int)len;
        return true;
    }

    private static bool SkipSlice(byte[] b, ref int pos)
    {
        if (!TryReadVarint32(b, ref pos, out var len) || len > (uint)(b.Length - pos))
        {
            return false;
        }

        pos += (int)len;
        return true;
    }

    private static bool SkipVarint(byte[] b, ref int pos) => TryReadVarint32(b, ref pos, out _);

    private static bool TryReadVarint32(byte[] b, ref int pos, out uint value)
    {
        value = 0;
        for (var shift = 0; shift <= 28 && pos < b.Length; shift += 7)
        {
            var x = b[pos++];
            value |= (uint)(x & 0x7F) << shift;
            if ((x & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
