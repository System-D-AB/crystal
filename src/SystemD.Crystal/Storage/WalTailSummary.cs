// Ported from Highway: src/Highway.Server/Storage/Rocks/WalTailSummary.cs — decodes a WAL tail into per-box operation
// counts for the demote reconciliation report (spec 003 R9.2): what a demoted primary committed beyond the highest
// position any standby acknowledged, by box, so an operator can see what a failover may have lost. Crystal's keys are
// [box:4 big-endian][user key] (box-layout § 3.2), far simpler than Highway's per-space queue keys. WAL compression
// must stay off for this to parse (001-2 R4.5), which Crystal enforces.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace SystemD.Crystal.Storage;

/// <summary>One box's operations in the unreplicated tail (spec 003 R9.2).</summary>
internal sealed record BoxTailOps(uint BoxId, string BoxName)
{
    public int Puts { get; set; }
    public int Deletes { get; set; }
    public long PutBytes { get; set; }
}

/// <summary>
/// Decodes the unreplicated WAL tail of one database into per-box put/delete counts (spec 003 R9.2). Reads the stable
/// RocksDB <c>WriteBatch</c> record format (12-byte header, then tagged records); Crystal writes only Put, Delete and
/// DeleteRange. An unknown tag stops that batch's decode and is reported as undecoded, never guessed. Pure: it works
/// over batch bytes a <see cref="WalAccess"/> read, with a box-id→name lookup from the catalogue.
/// </summary>
internal static class WalTailSummary
{
    private const int HeaderSize = 12;

    /// <summary>
    /// Summarises every batch in <paramref name="batches"/> as per-box lines, resolving box ids to names via
    /// <paramref name="boxName"/>. The result is ordered by box id; a batch with an unknown record tag is counted
    /// under <c>Undecoded</c> and never guessed at.
    /// </summary>
    public static (IReadOnlyList<BoxTailOps> Boxes, int Undecoded) Describe(
        IReadOnlyList<(ulong Seq, byte[] Data)> batches, Func<uint, string?> boxName)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(boxName);

        var byBox = new Dictionary<uint, BoxTailOps>();
        var undecoded = 0;
        foreach (var (_, data) in batches)
        {
            if (!TryDecodeBatch(data, byBox, boxName))
            {
                undecoded++;
            }
        }

        return (byBox.Values.OrderBy(b => b.BoxId).ToList(), undecoded);
    }

    private static bool TryDecodeBatch(byte[] data, Dictionary<uint, BoxTailOps> byBox, Func<uint, string?> boxName)
    {
        if (data.Length < HeaderSize)
        {
            return false;
        }

        var pos = HeaderSize;
        while (pos < data.Length)
        {
            var tag = data[pos++];
            bool hasCf, hasValue, isDelete;
            switch (tag)
            {
                case 0x00: hasCf = false; hasValue = false; isDelete = true; break;  // Deletion
                case 0x07: hasCf = false; hasValue = false; isDelete = true; break;  // SingleDeletion
                case 0x01: hasCf = false; hasValue = true; isDelete = false; break;  // Value (Put)
                case 0x02: hasCf = false; hasValue = true; isDelete = false; break;  // Merge
                case 0x0F: hasCf = false; hasValue = true; isDelete = true; break;   // RangeDeletion (begin,end)
                case 0x04: hasCf = true; hasValue = false; isDelete = true; break;   // CF Deletion
                case 0x08: hasCf = true; hasValue = false; isDelete = true; break;   // CF SingleDeletion
                case 0x05: hasCf = true; hasValue = true; isDelete = false; break;   // CF Value
                case 0x06: hasCf = true; hasValue = true; isDelete = false; break;   // CF Merge
                case 0x0E: hasCf = true; hasValue = true; isDelete = true; break;    // CF RangeDeletion
                default: return false; // unknown — stop, report
            }

            if (hasCf && !TryReadVarint32(data, ref pos, out _))
            {
                return false;
            }

            if (!TryReadSlice(data, ref pos, out var keyStart, out var keyLen))
            {
                return false;
            }

            var valueLen = 0;
            if (hasValue && !TryReadSlice(data, ref pos, out _, out valueLen))
            {
                return false;
            }

            var box = keyLen >= BoxKeys.PrefixLength
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(keyStart, BoxKeys.PrefixLength))
                : uint.MaxValue;

            if (!byBox.TryGetValue(box, out var ops))
            {
                byBox[box] = ops = new BoxTailOps(box, box == BoxKeys.CatalogueBoxId ? "catalogue" : boxName(box) ?? $"box {box}");
            }

            if (isDelete)
            {
                ops.Deletes++;
            }
            else
            {
                ops.Puts++;
                ops.PutBytes += valueLen;
            }
        }

        return true;
    }

    private static bool TryReadVarint32(byte[] data, ref int pos, out uint value)
    {
        value = 0;
        for (var shift = 0; shift <= 28 && pos < data.Length; shift += 7)
        {
            var b = data[pos++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadSlice(byte[] data, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (!TryReadVarint32(data, ref pos, out var len) || pos + len > data.Length)
        {
            return false;
        }

        start = pos;
        length = (int)len;
        pos += (int)len;
        return true;
    }
}
