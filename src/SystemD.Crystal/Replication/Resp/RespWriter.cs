// Ported from Highway: src/Highway.Server/Commands/Runtime/RespWriter.cs — the subset of RESP2 Crystal uses (spec
// 003 R10.2): simple string, error, integer, bulk string, null bulk, array, null array. Highway's writer used a
// vendored Garnet formatter (RespWriteUtils) with unsafe pointer writes; Crystal writes the bytes itself, safely,
// so it depends on no client library (001 R3.3). The reply shapes are Crystal's (design § 8.2).

using System;
using System.Collections.Generic;
using System.Globalization;
using Utf8 = System.Text.Encoding;

namespace SystemD.Crystal.Replication.Resp;

/// <summary>
/// Builds a RESP2 reply or request as bytes (spec 003 R10.2). Append the pieces of a frame in order — a top-level
/// value, or an array header followed by its elements — then take <see cref="ToArray"/>. Binary-safe: bulk strings
/// carry raw bytes (node ids, UIDs, names, batch bytes, file data, JSON). Not thread-safe; one writer per frame.
/// </summary>
internal sealed class RespWriter
{
    private byte[] _buffer;
    private int _length;

    public RespWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
    }

    /// <summary>The bytes written so far, as a new array.</summary>
    public byte[] ToArray()
    {
        var exact = new byte[_length];
        Array.Copy(_buffer, exact, _length);
        return exact;
    }

    /// <summary>The number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>Writes a simple string: <c>+&lt;s&gt;\r\n</c>.</summary>
    public RespWriter SimpleString(string s)
    {
        WriteByte((byte)'+');
        WriteAscii(s);
        WriteCrlf();
        return this;
    }

    /// <summary>Writes an error: <c>-&lt;message&gt;\r\n</c> (spec 003 § 8.3; e.g. <c>CRYSTAL_AUTH</c>).</summary>
    public RespWriter Error(string message)
    {
        WriteByte((byte)'-');
        WriteUtf8(message);
        WriteCrlf();
        return this;
    }

    /// <summary>Writes a signed 64-bit integer: <c>:&lt;n&gt;\r\n</c>.</summary>
    public RespWriter Integer(long value)
    {
        WriteByte((byte)':');
        WriteAscii(value.ToString(CultureInfo.InvariantCulture));
        WriteCrlf();
        return this;
    }

    /// <summary>Writes a bulk string: <c>$&lt;len&gt;\r\n&lt;bytes&gt;\r\n</c>.</summary>
    public RespWriter BulkString(ReadOnlySpan<byte> bytes)
    {
        WriteByte((byte)'$');
        WriteAscii(bytes.Length.ToString(CultureInfo.InvariantCulture));
        WriteCrlf();
        WriteBytes(bytes);
        WriteCrlf();
        return this;
    }

    /// <summary>Writes a bulk string from UTF-8 text.</summary>
    public RespWriter BulkString(string text) => BulkString(Utf8.UTF8.GetBytes(text));

    /// <summary>Writes a null bulk string: <c>$-1\r\n</c>.</summary>
    public RespWriter NullBulkString()
    {
        WriteAscii("$-1");
        WriteCrlf();
        return this;
    }

    /// <summary>Writes an array header: <c>*&lt;count&gt;\r\n</c>. The caller then writes the elements.</summary>
    public RespWriter ArrayHeader(int count)
    {
        WriteByte((byte)'*');
        WriteAscii(count.ToString(CultureInfo.InvariantCulture));
        WriteCrlf();
        return this;
    }

    /// <summary>Writes a null array: <c>*-1\r\n</c>.</summary>
    public RespWriter NullArray()
    {
        WriteAscii("*-1");
        WriteCrlf();
        return this;
    }

    /// <summary>Writes a complete request: an array of bulk-string arguments (spec 003 R10.2).</summary>
    public RespWriter Request(IReadOnlyList<byte[]> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArrayHeader(args.Count);
        foreach (var arg in args)
        {
            BulkString(arg);
        }

        return this;
    }

    private void WriteByte(byte b)
    {
        EnsureCapacity(1);
        _buffer[_length++] = b;
    }

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    private void WriteAscii(string s)
    {
        EnsureCapacity(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            _buffer[_length++] = (byte)s[i];
        }
    }

    private void WriteUtf8(string s) => WriteBytes(Utf8.UTF8.GetBytes(s));

    private void WriteCrlf()
    {
        EnsureCapacity(2);
        _buffer[_length++] = (byte)'\r';
        _buffer[_length++] = (byte)'\n';
    }

    private void EnsureCapacity(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        var newSize = _buffer.Length * 2;
        while (newSize < _length + extra)
        {
            newSize *= 2;
        }

        Array.Resize(ref _buffer, newSize);
    }
}
