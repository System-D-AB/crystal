// Ported from StowDB: src/Crystal/Crystal.Storage/Encoding/KeyWriter.cs (KeyReader, the cursor) and the read paths of
// OrderPreservingEncoder.cs.
// Adapted for Crystal: no field markers, no FieldSpec or SlotValue; one typed method per encoder of KeyEncoding; a
// descending field is read by inverting into a stack buffer, and a descending string is un-inverted byte by byte as it
// is walked, never by copying the rest of the key (spec 012 R6).

using System;
using System.Buffers;
using System.Text;

namespace SystemD.Crystal.Encoding;

/// <summary>
/// Reads, in sequence, the fields a <see cref="KeyWriter"/> wrote (spec 012 R6): each method reads one field,
/// ascending, or descending when the writer inverted it (<see cref="KeyWriter.InvertLast"/>). Reading what the writer
/// wrote returns the value and consumes exactly the bytes written. A truncated or malformed key throws
/// <see cref="FormatException"/> naming the type and the offset; the reader never reads past its span.
/// </summary>
public ref struct KeyReader
{
    private readonly ReadOnlySpan<byte> _key;
    private int _position;

    /// <summary>Initializes a reader at the start of <paramref name="key"/>.</summary>
    public KeyReader(ReadOnlySpan<byte> key)
    {
        _key = key;
        _position = 0;
    }

    /// <summary>The offset of the next byte to read.</summary>
    public readonly int Position => _position;

    /// <summary>Whether every byte has been read.</summary>
    public readonly bool Exhausted => _position >= _key.Length;

    /// <summary>The bytes not yet read.</summary>
    public readonly ReadOnlySpan<byte> Remaining => _key[_position..];

    /// <summary>Reads a boolean (1 byte).</summary>
    public bool ReadBoolean(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[1];
        Fill(buffer, descending, "boolean");
        bool value;
        try
        {
            value = KeyEncoding.DecodeBoolean(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid boolean at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads an unsigned 32-bit integer (4 bytes).</summary>
    public uint ReadUInt32(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[4];
        Fill(buffer, descending, "UInt32");
        uint value;
        try
        {
            value = KeyEncoding.DecodeUInt32(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid UInt32 at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads an unsigned 64-bit integer (8 bytes).</summary>
    public ulong ReadUInt64(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[8];
        Fill(buffer, descending, "UInt64");
        ulong value;
        try
        {
            value = KeyEncoding.DecodeUInt64(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid UInt64 at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads a signed 32-bit integer (4 bytes).</summary>
    public int ReadInt32(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[4];
        Fill(buffer, descending, "Int32");
        int value;
        try
        {
            value = KeyEncoding.DecodeInt32(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid Int32 at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads a signed 64-bit integer (8 bytes).</summary>
    public long ReadInt64(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[8];
        Fill(buffer, descending, "Int64");
        long value;
        try
        {
            value = KeyEncoding.DecodeInt64(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid Int64 at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads a double (8 bytes). An encoded NaN reads as NaN; −0.0 was written as 0.0.</summary>
    public double ReadDouble(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[8];
        Fill(buffer, descending, "double");
        double value;
        try
        {
            value = KeyEncoding.DecodeDouble(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid double at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads a decimal (25 bytes).</summary>
    public decimal ReadDecimal(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[KeyEncoding.DecimalEncodedLength];
        Fill(buffer, descending, "decimal");
        decimal value;
        try
        {
            value = KeyEncoding.DecodeDecimal(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid decimal at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads a timestamp (8 bytes, UTC).</summary>
    public DateTimeOffset ReadTimestamp(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[8];
        Fill(buffer, descending, "timestamp");
        DateTimeOffset value;
        try
        {
            value = KeyEncoding.DecodeTimestamp(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid timestamp at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>Reads a GUID (16 bytes).</summary>
    public Guid ReadGuid(bool descending = false)
    {
        Span<byte> buffer = stackalloc byte[16];
        Fill(buffer, descending, "GUID");
        Guid value;
        try
        {
            value = KeyEncoding.DecodeGuid(buffer);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The key holds no valid GUID at offset {_position}.", ex);
        }

        _position += buffer.Length; // only once the field decoded
        return value;
    }

    /// <summary>
    /// Reads an escaped string, through its terminator. Descending, the escape <c>0x00 0xFF</c> and the terminator
    /// <c>0x00 0x01</c> appear inverted, as <c>0xFF 0x00</c> and <c>0xFF 0xFE</c>.
    /// </summary>
    public string ReadString(bool descending = false)
    {
        var start = _position;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, _key.Length - start));
        try
        {
            var length = 0;
            var i = start;
            while (i < _key.Length)
            {
                var b = Byte(i++, descending);
                if (b != 0x00)
                {
                    rented[length++] = b;
                    continue;
                }

                if (i >= _key.Length)
                {
                    throw Malformed("string", start, "it ends inside an escape");
                }

                var next = Byte(i++, descending);
                if (next == 0x01)
                {
                    string value;
                    try
                    {
                        value = KeyEncoding.StrictUtf8.GetString(rented, 0, length);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new FormatException($"The string at offset {start} is not valid UTF-8.", ex);
                    }

                    _position = i;
                    return value;
                }

                if (next != 0xFF)
                {
                    throw Malformed("string", start, $"0x00 is followed by 0x{next:x2}, neither an escape nor the terminator");
                }

                rented[length++] = 0x00;
            }

            throw Malformed("string", start, "it has no terminator");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Reads <paramref name="count"/> bytes as they are.</summary>
    public ReadOnlySpan<byte> ReadRaw(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _key.Length - _position)
        {
            throw Malformed("raw bytes", _position, $"{count} bytes were asked for and {_key.Length - _position} remain");
        }

        var raw = _key.Slice(_position, count);
        _position += count;
        return raw;
    }

    private readonly byte Byte(int index, bool descending) => descending ? (byte)~_key[index] : _key[index];

    /// <summary>Copies the next <c>buffer.Length</c> bytes into <paramref name="buffer"/>, un-inverted when descending; the caller consumes them once they decode.</summary>
    private void Fill(scoped Span<byte> buffer, bool descending, string type)
    {
        if (buffer.Length > _key.Length - _position)
        {
            throw Malformed(type, _position, $"it needs {buffer.Length} bytes and {_key.Length - _position} remain");
        }

        _key.Slice(_position, buffer.Length).CopyTo(buffer);
        if (descending)
        {
            KeyEncoding.Invert(buffer);
        }
    }

    private static FormatException Malformed(string type, int offset, string why) =>
        new($"The key holds no whole {type} at offset {offset}: {why}.");
}
