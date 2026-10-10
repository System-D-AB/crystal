// Ported from Highway: src/Highway.Server/Storage/Boxes/BoxKeys.cs
// Adapted for Crystal: box prefix [box_id : 4 bytes BE][userKey], reserved ids, range bounds (spec 002 R1).

using System;
using System.Buffers.Binary;
using System.IO;
using SystemD.Crystal.Encoding;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Physical key layout for boxes (spec 002 R1, box-layout § 3.2).
/// Key = [box_id : 4 bytes big-endian][user key].
/// Box 0 is the catalogue; ids 1–15 are reserved for Crystal; products' boxes start at 16.
/// </summary>
internal static class BoxKeys
{
    public const uint CatalogueBoxId = 0;
    public const uint FirstProductBoxId = 16;
    public const int PrefixLength = 4;

    public static readonly byte[] FormatKey = [0x00, 0x00, 0x00, 0x00, 0x05];
    public static readonly byte[] AllocatorKey = [0x00, 0x00, 0x00, 0x00, 0x03];
    public static readonly byte[] GenerationKey = [0x00, 0x00, 0x00, 0x00, 0x04];

    /// <summary>
    /// The key-encoder version (spec 002 R7.4), at box 0 space 0x06. Checked on open: data written with a
    /// different encoder version is refused, never misread. A change to any encoding's bytes changes this.
    /// </summary>
    public static readonly byte[] EncoderVersionKey = [0x00, 0x00, 0x00, 0x00, 0x06];

    /// <summary>
    /// The database's identity record (spec 009 R1.1), at box 0 space 0x07: its UID, kind and creation time, written
    /// once with the rest of the catalogue and never changed.
    /// </summary>
    public static readonly byte[] IdentityKey = [0x00, 0x00, 0x00, 0x00, 0x07];

    /// <summary>Prepends the 4-byte big-endian box id prefix to <paramref name="userKey"/>.</summary>
    public static byte[] BuildKey(uint boxId, ReadOnlySpan<byte> userKey)
    {
        var prefixed = new byte[PrefixLength + userKey.Length];
        BinaryPrimitives.WriteUInt32BigEndian(prefixed.AsSpan(0, PrefixLength), boxId);
        userKey.CopyTo(prefixed.AsSpan(PrefixLength));
        return prefixed;
    }

    /// <summary>Writes the 4-byte box id prefix followed by <paramref name="userKey"/> into <paramref name="destination"/>.</summary>
    public static void BuildKey(Span<byte> destination, uint boxId, ReadOnlySpan<byte> userKey)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination[..PrefixLength], boxId);
        userKey.CopyTo(destination[PrefixLength..]);
    }

    /// <summary>Returns the lower and upper scan bounds for <paramref name="boxId"/>.</summary>
    public static void GetBoxBounds(uint boxId, out byte[] lowerBound, out byte[] upperBound)
    {
        lowerBound = new byte[PrefixLength];
        BinaryPrimitives.WriteUInt32BigEndian(lowerBound, boxId);

        // The last 4-byte id is never issued (DatabaseContext.MaxBoxId), so every box has the exclusive upper bound
        // [box_id + 1]. No finite key would bound every key under [FF FF FF FF].
        ArgumentOutOfRangeException.ThrowIfEqual(boxId, uint.MaxValue);
        upperBound = new byte[PrefixLength];
        BinaryPrimitives.WriteUInt32BigEndian(upperBound, boxId + 1);
    }

    /// <summary>The exclusive upper bound of <paramref name="boxId"/>'s range: <c>[box_id + 1]</c>.</summary>
    public static byte[] UpperBound(uint boxId)
    {
        GetBoxBounds(boxId, out _, out var upperBound);
        return upperBound;
    }

    /// <summary>Extracts the box id from a prefixed key.</summary>
    public static uint ExtractBoxId(ReadOnlySpan<byte> prefixedKey)
    {
        if (prefixedKey.Length < PrefixLength)
        {
            throw new ArgumentException("Key is too short to contain a box id prefix.", nameof(prefixedKey));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(prefixedKey[..PrefixLength]);
    }

    /// <summary>Extracts the product's user key from a prefixed key.</summary>
    public static ReadOnlySpan<byte> ExtractUserKey(ReadOnlySpan<byte> prefixedKey)
    {
        if (prefixedKey.Length < PrefixLength)
        {
            throw new ArgumentException("Key is too short to contain a box id prefix.", nameof(prefixedKey));
        }

        return prefixedKey[PrefixLength..];
    }

    /// <summary>Builds the box 0 alias key for <paramref name="boxName"/>: [0][0x01][escapedName].</summary>
    public static byte[] MakeAliasKey(string boxName)
    {
        var escaped = KeyEncoding.EncodeString(boxName);
        var key = new byte[5 + escaped.Length];
        key[4] = 0x01; // TagAlias
        escaped.CopyTo(key.AsSpan(5));
        return key;
    }

    /// <summary>Builds the box 0 record key for <paramref name="boxId"/>: [0][0x02][box_id:4].</summary>
    public static byte[] MakeRecordKey(uint boxId)
    {
        var key = new byte[9];
        key[4] = 0x02; // TagRecord
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(5, 4), boxId);
        return key;
    }
}
