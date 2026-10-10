// Ported from StowDB: src/Crystal/Crystal.Storage/Encoding/KeyWriter.cs
// and Highway: src/Highway.Server/Storage/Layout/KeyWriter.cs (growth to a pooled array, ToArray, Return).
// Adapted for Crystal: composite key writer (spec 002 R7).

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SystemD.Crystal.Encoding;

/// <summary>
/// Writes order-preserving composite keys into a caller-supplied buffer (typically <c>stackalloc</c>), growing to a
/// pooled array only when the key outgrows it (spec 002 R7). Call <see cref="ToArray"/> or <see cref="Return"/> when
/// done, so a pooled array goes back to the pool.
/// </summary>
public ref struct KeyWriter
{
    private Span<byte> _buffer;
    private byte[]? _pooledArray;
    private int _position;

    /// <summary>Initializes a new <see cref="KeyWriter"/> over <paramref name="destination"/>.</summary>
    public KeyWriter(Span<byte> destination)
    {
        _buffer = destination;
        _pooledArray = null;
        _position = 0;
    }

    /// <summary>The number of bytes written so far.</summary>
    public readonly int BytesWritten => _position;

    /// <summary>The written bytes. Valid until the next write, <see cref="ToArray"/> or <see cref="Return"/>.</summary>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._position];

    /// <summary>Writes a boolean (1 byte).</summary>
    public void WriteBoolean(bool value)
    {
        EnsureCapacity(1);
        KeyEncoding.EncodeBoolean(_buffer[_position..], value);
        _position += 1;
    }

    /// <summary>Writes an unsigned 32-bit integer (4 bytes big-endian).</summary>
    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        KeyEncoding.EncodeUInt32(_buffer[_position..], value);
        _position += 4;
    }

    /// <summary>Writes an unsigned 64-bit integer (8 bytes big-endian).</summary>
    public void WriteUInt64(ulong value)
    {
        EnsureCapacity(8);
        KeyEncoding.EncodeUInt64(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>Writes a signed 32-bit integer (4 bytes big-endian with sign flipped).</summary>
    public void WriteInt32(int value)
    {
        EnsureCapacity(4);
        KeyEncoding.EncodeInt32(_buffer[_position..], value);
        _position += 4;
    }

    /// <summary>Writes a signed 64-bit integer (8 bytes big-endian with sign flipped).</summary>
    public void WriteInt64(long value)
    {
        EnsureCapacity(8);
        KeyEncoding.EncodeInt64(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>Writes a double (8 bytes IEEE bit-ordered).</summary>
    public void WriteDouble(double value)
    {
        EnsureCapacity(8);
        KeyEncoding.EncodeDouble(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>Writes a decimal (25 bytes sign + normalized magnitude).</summary>
    public void WriteDecimal(decimal value)
    {
        EnsureCapacity(KeyEncoding.DecimalEncodedLength);
        KeyEncoding.EncodeDecimal(_buffer[_position..], value);
        _position += KeyEncoding.DecimalEncodedLength;
    }

    /// <summary>Writes a timestamp (8 bytes UTC ticks).</summary>
    public void WriteTimestamp(DateTimeOffset value)
    {
        EnsureCapacity(8);
        KeyEncoding.EncodeTimestamp(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>Writes a GUID (16 bytes big-endian text order).</summary>
    public void WriteGuid(Guid value)
    {
        EnsureCapacity(16);
        KeyEncoding.EncodeGuid(_buffer[_position..], value);
        _position += 16;
    }

    /// <summary>Writes an escaped string.</summary>
    public void WriteString(string value) => WriteRaw(KeyEncoding.EncodeString(value));

    /// <summary>Writes raw bytes directly.</summary>
    public void WriteRaw(scoped ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer[_position..]);
        _position += bytes.Length;
    }

    /// <summary>Inverts the last <paramref name="count"/> written bytes to descending order.</summary>
    public readonly void InvertLast(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _position);
        KeyEncoding.Invert(_buffer.Slice(_position - count, count));
    }

    /// <summary>Copies the written bytes to a new array and returns any pooled buffer. Call when done.</summary>
    public byte[] ToArray()
    {
        var result = _buffer[.._position].ToArray();
        Return();
        return result;
    }

    /// <summary>
    /// Returns any pooled array without copying. Use when <see cref="WrittenSpan"/> was already consumed; the writer
    /// must not be used afterwards.
    /// </summary>
    public void Return()
    {
        if (_pooledArray is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooledArray);
            _pooledArray = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int needed)
    {
        if (_position + needed > _buffer.Length)
        {
            Grow(needed);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int needed)
    {
        var newArray = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _position + needed));
        _buffer[.._position].CopyTo(newArray);

        if (_pooledArray is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooledArray);
        }

        _pooledArray = newArray;
        _buffer = newArray;
    }
}
