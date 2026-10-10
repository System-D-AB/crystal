// Ported from StowDB: src/Crystal/Crystal.Storage/Encoding/OrderPreservingEncoder.cs
// Adapted for Crystal: order-preserving encoders and decoders for primitive types (spec 002 R7).

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace SystemD.Crystal.Encoding;

/// <summary>
/// Order-preserving key encoders and decoders (spec 002 R7, box-layout § 5).
/// Byte order strictly equals value order.
/// </summary>
public static class KeyEncoding
{
    /// <summary>
    /// The encoding format version. Every encoded key is produced by a known version, so an
    /// encoding change is a deliberate, visible event rather than a silently unreadable index.
    /// </summary>
    public const int Version = 1;

    // --- Booleans ---

    /// <summary>Encodes a boolean as 1 byte (0x00 false, 0x01 true).</summary>
    public static void EncodeBoolean(Span<byte> destination, bool value)
    {
        destination[0] = value ? (byte)1 : (byte)0;
    }

    /// <summary>Decodes a boolean from 1 byte.</summary>
    public static bool DecodeBoolean(ReadOnlySpan<byte> source) => source[0] != 0;

    // --- Unsigned integers ---

    /// <summary>Encodes a 32-bit unsigned integer as 4 bytes big-endian.</summary>
    public static void EncodeUInt32(Span<byte> destination, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    /// <summary>Decodes a 32-bit unsigned integer from 4 bytes big-endian.</summary>
    public static uint DecodeUInt32(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt32BigEndian(source);

    /// <summary>Encodes a 64-bit unsigned integer as 8 bytes big-endian.</summary>
    public static void EncodeUInt64(Span<byte> destination, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(destination, value);

    /// <summary>Decodes a 64-bit unsigned integer from 8 bytes big-endian.</summary>
    public static ulong DecodeUInt64(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt64BigEndian(source);

    // --- Signed integers ---

    /// <summary>Encodes a signed 32-bit integer as 4 bytes big-endian with sign bit flipped.</summary>
    public static void EncodeInt32(Span<byte> destination, int value) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value ^ 0x8000_0000u);

    /// <summary>Decodes a signed 32-bit integer.</summary>
    public static int DecodeInt32(ReadOnlySpan<byte> source) =>
        (int)(BinaryPrimitives.ReadUInt32BigEndian(source) ^ 0x8000_0000u);

    /// <summary>Encodes a signed 64-bit integer as 8 bytes big-endian with sign bit flipped.</summary>
    public static void EncodeInt64(Span<byte> destination, long value) =>
        BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)value ^ 0x8000_0000_0000_0000ul);

    /// <summary>Decodes a signed 64-bit integer.</summary>
    public static long DecodeInt64(ReadOnlySpan<byte> source) =>
        (long)(BinaryPrimitives.ReadUInt64BigEndian(source) ^ 0x8000_0000_0000_0000ul);

    // --- Doubles ---

    /// <summary>Encodes a double with IEEE bits ordered (-0.0 normalized to 0.0, NaN ordered).</summary>
    public static void EncodeDouble(Span<byte> destination, double value)
    {
        long bits;
        if (double.IsNaN(value))
        {
            bits = 0x7FF8_0000_0000_0000L;
        }
        else
        {
            bits = BitConverter.DoubleToInt64Bits(value == 0.0 ? 0.0 : value);
        }

        if (bits >= 0)
        {
            bits ^= long.MinValue;
        }
        else
        {
            bits = ~bits;
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)bits);
    }

    /// <summary>Decodes a double.</summary>
    public static double DecodeDouble(ReadOnlySpan<byte> source)
    {
        var bits = (long)BinaryPrimitives.ReadUInt64BigEndian(source);
        if ((bits & long.MinValue) != 0)
        {
            bits ^= long.MinValue;
        }
        else
        {
            bits = ~bits;
        }

        return BitConverter.Int64BitsToDouble(bits);
    }

    // --- Timestamps ---

    /// <summary>Encodes a timestamp as UTC ticks stored as a signed 64-bit integer.</summary>
    public static void EncodeTimestamp(Span<byte> destination, DateTimeOffset value) =>
        EncodeInt64(destination, value.UtcTicks);

    /// <summary>Decodes a timestamp from UTC ticks.</summary>
    public static DateTimeOffset DecodeTimestamp(ReadOnlySpan<byte> source) =>
        new(DecodeInt64(source), TimeSpan.Zero);

    // --- GUIDs ---

    /// <summary>Encodes a GUID in RFC 4122 big-endian text order (16 bytes).</summary>
    public static void EncodeGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out _))
        {
            throw new ArgumentException("The destination is shorter than the 16 bytes of a GUID.", nameof(destination));
        }
    }

    /// <summary>Decodes a GUID from 16 bytes big-endian text order.</summary>
    public static Guid DecodeGuid(ReadOnlySpan<byte> source) =>
        new(source[..16], bigEndian: true);

    // --- Strings ---

    /// <summary>
    /// UTF-8 that throws on invalid input instead of substituting U+FFFD, in both directions. Two different strings
    /// therefore never encode to the same bytes (R7.2), and damaged bytes are refused, never decoded as something else.
    /// </summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Encodes a string in order-preserving format: UTF-8 with 0x00 escaped as 0x00 0xFF,
    /// terminated by 0x00 0x01.
    /// </summary>
    public static byte[] EncodeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("The string is not valid UTF-16: it has an unpaired surrogate (R7.2).", nameof(value), ex);
        }

        using var ms = new MemoryStream(utf8.Length + 4);
        foreach (var b in utf8)
        {
            if (b == 0x00)
            {
                ms.WriteByte(0x00);
                ms.WriteByte(0xFF);
            }
            else
            {
                ms.WriteByte(b);
            }
        }

        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        return ms.ToArray();
    }

    /// <summary>Encodes a string into <paramref name="destination"/>.</summary>
    public static void EncodeString(Span<byte> destination, string value, out int bytesWritten)
    {
        var encoded = EncodeString(value);
        encoded.CopyTo(destination);
        bytesWritten = encoded.Length;
    }

    /// <summary>
    /// Decodes an escaped string from <paramref name="source"/>, reporting <paramref name="bytesConsumed"/>.
    /// </summary>
    public static string DecodeString(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        using var ms = new MemoryStream(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var b = source[i++];
            if (b == 0x00)
            {
                if (i >= source.Length)
                {
                    throw new FormatException("Truncated escaped string: trailing 0x00 without escape or terminator.");
                }

                var next = source[i++];
                if (next == 0x01)
                {
                    bytesConsumed = i;
                    try
                    {
                        return StrictUtf8.GetString(ms.ToArray());
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new FormatException("The escaped string is not valid UTF-8.", ex);
                    }
                }

                if (next == 0xFF)
                {
                    ms.WriteByte(0x00);
                }
                else
                {
                    throw new FormatException($"Invalid escape byte 0x{next:x2} following 0x00.");
                }
            }
            else
            {
                ms.WriteByte(b);
            }
        }

        throw new FormatException("Escaped string missing terminating 0x00 0x01 sequence.");
    }

    // --- Decimals ---

    private static readonly BigInteger Ten = 10;
    private static readonly BigInteger MaxDecimalMantissa = (BigInteger.One << 96) - 1;
    private static readonly BigInteger[] PowersOfTen = InitializePowersOfTen();

    private static BigInteger[] InitializePowersOfTen()
    {
        var powers = new BigInteger[29];
        powers[0] = BigInteger.One;
        for (var i = 1; i <= 28; i++)
        {
            powers[i] = powers[i - 1] * 10;
        }

        return powers;
    }

    /// <summary>Length in bytes of an encoded decimal (25 bytes: 1 sign byte + 24 magnitude bytes).</summary>
    public const int DecimalEncodedLength = 25;

    /// <summary>
    /// Encodes a decimal with sign byte and normalized 24-byte magnitude.
    /// Format: 25 bytes total.
    /// </summary>
    public static void EncodeDecimal(Span<byte> destination, decimal value)
    {
        var bits = decimal.GetBits(value);
        var isNegative = (bits[3] & 0x80000000) != 0;
        var scale = (byte)((bits[3] >> 16) & 0x7F);

        var low = (uint)bits[0];
        var mid = (uint)bits[1];
        var high = (uint)bits[2];

        var mantissa = (new BigInteger(high) << 64) | (new BigInteger(mid) << 32) | new BigInteger(low);

        if (mantissa.IsZero)
        {
            destination[0] = 0x01; // Zero sign
            destination.Slice(1, 24).Clear();
            return;
        }

        var normalizedMagnitude = mantissa * PowersOfTen[28 - scale];
        var temp = normalizedMagnitude.ToByteArray(isUnsigned: true, isBigEndian: true);
        var magBytes = new byte[24];
        temp.CopyTo(magBytes.AsSpan(24 - temp.Length));

        if (!isNegative)
        {
            destination[0] = 0x02; // Positive
            magBytes.CopyTo(destination[1..]);
        }
        else
        {
            destination[0] = 0x00; // Negative
            for (var i = 0; i < 24; i++)
            {
                destination[1 + i] = (byte)~magBytes[i];
            }
        }
    }

    /// <summary>Decodes a decimal from 25 bytes.</summary>
    public static decimal DecodeDecimal(ReadOnlySpan<byte> source)
    {
        var signByte = source[0];
        if (signByte == 0x01)
        {
            return 0m;
        }

        var isNegative = signByte == 0x00;
        var magSpan = source.Slice(1, 24);
        Span<byte> buffer = stackalloc byte[24];

        if (isNegative)
        {
            for (var i = 0; i < 24; i++)
            {
                buffer[i] = (byte)~magSpan[i];
            }
        }
        else
        {
            magSpan.CopyTo(buffer);
        }

        var magnitude = new BigInteger(buffer, isUnsigned: true, isBigEndian: true);
        var scale = 28;

        while (scale > 0 && ((magnitude % Ten).IsZero || magnitude > MaxDecimalMantissa))
        {
            magnitude /= Ten;
            scale--;
        }

        var low = (uint)(magnitude & uint.MaxValue);
        var mid = (uint)((magnitude >> 32) & uint.MaxValue);
        var high = (uint)((magnitude >> 64) & uint.MaxValue);

        return new decimal((int)low, (int)mid, (int)high, isNegative, (byte)scale);
    }

    // --- Descending order ---

    /// <summary>Inverts bytes in-place to reverse sort order.</summary>
    public static void Invert(Span<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0xFF;
        }
    }
}
