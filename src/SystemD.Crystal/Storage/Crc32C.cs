// Spec 003 R7.2 — CRC-32C (Castagnoli), to verify each snapshot file the standby downloads matches the primary's.
// Implemented here rather than taken from a package: Crystal depends only on its one backend (001 R3.3), and the
// framework's System.IO.Hashing ships CRC-32 (ISO-HDLC), not the Castagnoli variant the design names. Table-driven,
// polynomial 0x1EDC6F41 reflected to 0x82F63B78 — the same CRC-32C RocksDB and iSCSI use.

using System;

namespace SystemD.Crystal.Storage;

/// <summary>
/// CRC-32C (Castagnoli) over a byte stream (spec 003 R7.2). Feed with <see cref="Append"/>; read with
/// <see cref="Value"/>. Standalone one-shot via <see cref="Compute(ReadOnlySpan{byte})"/>. The reflected form matches
/// the checksum RocksDB stamps in its SST footers, so a Crystal checksum is comparable to any CRC-32C reference.
/// </summary>
internal struct Crc32C
{
    // Reflected Castagnoli polynomial (0x82F63B78), the lookup table built once.
    private static readonly uint[] Table = BuildTable();

    private uint _crc = 0xFFFFFFFFu;

    public Crc32C()
    {
    }

    /// <summary>The checksum of everything appended so far (finalised each read, so appending after a read is fine).</summary>
    public readonly uint Value => _crc ^ 0xFFFFFFFFu;

    /// <summary>Folds <paramref name="data"/> into the running checksum.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        _crc = crc;
    }

    /// <summary>The CRC-32C of <paramref name="data"/> in one call.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32C();
        crc.Append(data);
        return crc.Value;
    }

    private static uint[] BuildTable()
    {
        const uint poly = 0x82F63B78u; // reflected 0x1EDC6F41
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
