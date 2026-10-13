// Ported from Highway: src/Highway.Server/Resp/RespReader.cs — the stateless, incremental request reader (an array
// of bulk strings), with Crystal's frame caps (spec 003 R10.5, R10.8). The reply reader is new (Highway's replicas
// used a client library; Crystal has none, 001 R3.3): a standby reads the primary's RESP2 replies with it.

using System;
using System.Buffers;
using System.Collections.Generic;

namespace SystemD.Crystal.Replication.Resp;

/// <summary>Outcome of one attempt to read a RESP frame off a buffer (spec 003 R10.2).</summary>
internal enum RespReadStatus
{
    /// <summary>A whole frame was parsed; <c>consumed</c> marks its end.</summary>
    Complete,

    /// <summary>The buffer holds only part of a frame — read more bytes and retry. Never an error.</summary>
    Incomplete,

    /// <summary>The bytes are not a valid RESP frame; the connection must be closed (R10.8).</summary>
    ProtocolError,
}

/// <summary>The RESP2 type of a parsed reply value (spec 003 design § 8.1).</summary>
internal enum RespType
{
    SimpleString,
    Error,
    Integer,
    BulkString,
    NullBulkString,
    Array,
    NullArray,
}

/// <summary>
/// One parsed RESP2 reply value (spec 003 R10.2, design § 8.1): a simple string, error, integer, bulk string (or
/// null), or an array (or null) of values. Requests are read with <see cref="RespReader.TryReadRequest"/>, which is
/// the fast path for "an array of bulk strings"; replies with <see cref="RespReader.TryReadReply"/>.
/// </summary>
internal sealed class RespValue
{
    private RespValue(RespType type)
    {
        Type = type;
    }

    public RespType Type { get; }

    /// <summary>The text of a simple string or error, or null.</summary>
    public string? Text { get; private init; }

    /// <summary>The value of an integer.</summary>
    public long Integer { get; private init; }

    /// <summary>The bytes of a bulk string, or null for a null bulk string.</summary>
    public byte[]? Bytes { get; private init; }

    /// <summary>The elements of an array, or null for a null array.</summary>
    public IReadOnlyList<RespValue>? Items { get; private init; }

    public static RespValue OfSimpleString(string text) => new(RespType.SimpleString) { Text = text };

    public static RespValue OfError(string text) => new(RespType.Error) { Text = text };

    public static RespValue OfInteger(long value) => new(RespType.Integer) { Integer = value };

    public static RespValue OfBulkString(byte[] bytes) => new(RespType.BulkString) { Bytes = bytes };

    public static RespValue NullBulk { get; } = new(RespType.NullBulkString);

    public static RespValue OfArray(IReadOnlyList<RespValue> items) => new(RespType.Array) { Items = items };

    public static RespValue NullArrayValue { get; } = new(RespType.NullArray);
}

/// <summary>
/// Reads RESP2 frames off a <see cref="ReadOnlySequence{T}"/>, one frame at a time, stateless across calls: the
/// caller advances its buffer only on <see cref="RespReadStatus.Complete"/>, so feeding a frame one byte at a time
/// parses identically to feeding it whole. A declared length above <c>maxFrameBytes</c>, or any malformed frame,
/// is a <see cref="RespReadStatus.ProtocolError"/> (spec 003 R10.8).
/// </summary>
internal static class RespReader
{
    /// <summary>
    /// Reads one request frame — an array of bulk strings, the command first (spec 003 R10.2). A null or empty array
    /// is a protocol error (a request must name a command).
    /// </summary>
    public static RespReadStatus TryReadRequest(
        in ReadOnlySequence<byte> buffer,
        int maxFrameBytes,
        out IReadOnlyList<byte[]> args,
        out SequencePosition consumed,
        out string? error)
    {
        args = [];
        consumed = buffer.Start;
        error = null;
        var reader = new SequenceReader<byte>(buffer);

        if (!TryReadTypedLength(ref reader, (byte)'*', maxFrameBytes, out var count, out var status, out error))
        {
            return status;
        }

        if (count < 0)
        {
            error = "a request frame must be a non-empty array of bulk strings, not a null array";
            return RespReadStatus.ProtocolError;
        }

        if (count == 0)
        {
            error = "a request frame must name a command; received an empty array";
            return RespReadStatus.ProtocolError;
        }

        var elements = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadBulkString(ref reader, maxFrameBytes, out var value, out status, out error) || value is null)
            {
                if (status == RespReadStatus.Complete)
                {
                    error = "a request argument cannot be a null bulk string";
                    status = RespReadStatus.ProtocolError;
                }

                return status;
            }

            elements[i] = value;
        }

        args = elements;
        consumed = reader.Position;
        return RespReadStatus.Complete;
    }

    /// <summary>Reads one reply frame of any RESP2 type Crystal uses (spec 003 R10.2, design § 8).</summary>
    public static RespReadStatus TryReadReply(
        in ReadOnlySequence<byte> buffer,
        int maxFrameBytes,
        out RespValue? value,
        out SequencePosition consumed,
        out string? error)
    {
        value = null;
        consumed = buffer.Start;
        error = null;
        var reader = new SequenceReader<byte>(buffer);
        var status = TryReadValue(ref reader, maxFrameBytes, depth: 0, out value, out error);
        if (status == RespReadStatus.Complete)
        {
            consumed = reader.Position;
        }

        return status;
    }

    /// <summary>
    /// The deepest nesting of arrays a reply may have. Crystal's replies nest at most five deep (a PULL reply's entry's
    /// batches); a deeper reply is refused rather than followed down the stack (003 should-fix 4: a peer sending
    /// <c>*1</c> a million times would otherwise overflow the stack and end the process).
    /// </summary>
    public const int MaxDepth = 16;

    /// <summary>
    /// Array elements reserved up front at most; more grow as they arrive. A declared count is a peer's claim, so it never
    /// sizes an allocation on its own (003 should-fix 4).
    /// </summary>
    private const int MaxReservedElements = 1024;

    private static RespReadStatus TryReadValue(
        ref SequenceReader<byte> reader, int maxFrameBytes, int depth, out RespValue? value, out string? error)
    {
        value = null;
        error = null;
        if (depth > MaxDepth)
        {
            error = $"RESP reply nests deeper than {MaxDepth} arrays";
            return RespReadStatus.ProtocolError;
        }

        if (!reader.TryPeek(out var prefix))
        {
            return RespReadStatus.Incomplete;
        }

        switch (prefix)
        {
            case (byte)'+':
                return ReadLineValue(ref reader, maxFrameBytes, RespValue.OfSimpleString, out value, out error);
            case (byte)'-':
                return ReadLineValue(ref reader, maxFrameBytes, RespValue.OfError, out value, out error);
            case (byte)':':
            {
                reader.Advance(1);
                if (!TryReadLine(ref reader, maxFrameBytes, out var line, out var status, out error))
                {
                    return status;
                }

                if (!TryParseInt64(line, out var n))
                {
                    error = "RESP integer is not a base-10 integer";
                    return RespReadStatus.ProtocolError;
                }

                value = RespValue.OfInteger(n);
                return RespReadStatus.Complete;
            }

            case (byte)'$':
            {
                var status = TryReadBulkString(ref reader, maxFrameBytes, out var bytes, out var s, out error);
                if (!status)
                {
                    return s;
                }

                value = bytes is null ? RespValue.NullBulk : RespValue.OfBulkString(bytes);
                return RespReadStatus.Complete;
            }

            case (byte)'*':
            {
                if (!TryReadTypedLength(ref reader, (byte)'*', maxFrameBytes, out var count, out var status, out error))
                {
                    return status;
                }

                if (count < 0)
                {
                    value = RespValue.NullArrayValue;
                    return RespReadStatus.Complete;
                }

                // Every element takes at least three bytes (a type byte and CRLF), so a count larger than a third of the
                // frame cap cannot be honest.
                if (count > maxFrameBytes / 3)
                {
                    error = $"RESP array of {count} elements cannot fit in a {maxFrameBytes}-byte frame";
                    return RespReadStatus.ProtocolError;
                }

                var items = new List<RespValue>((int)Math.Min(count, MaxReservedElements));
                for (var i = 0; i < count; i++)
                {
                    var itemStatus = TryReadValue(ref reader, maxFrameBytes, depth + 1, out var item, out error);
                    if (itemStatus != RespReadStatus.Complete)
                    {
                        return itemStatus;
                    }

                    items.Add(item!);
                }

                value = RespValue.OfArray(items);
                return RespReadStatus.Complete;
            }

            default:
                error = $"unexpected RESP type byte '{Printable(prefix)}'";
                return RespReadStatus.ProtocolError;
        }
    }

    private static RespReadStatus ReadLineValue(
        ref SequenceReader<byte> reader, int maxFrameBytes, Func<string, RespValue> make, out RespValue? value, out string? error)
    {
        value = null;
        reader.Advance(1); // the type byte
        if (!TryReadLine(ref reader, maxFrameBytes, out var line, out var status, out error))
        {
            return status;
        }

        value = make(System.Text.Encoding.UTF8.GetString(line));
        return RespReadStatus.Complete;
    }

    /// <summary>Reads a <c>$&lt;len&gt;\r\n&lt;bytes&gt;\r\n</c> bulk string; <paramref name="value"/> is null for <c>$-1</c>.</summary>
    private static bool TryReadBulkString(
        ref SequenceReader<byte> reader, int maxFrameBytes,
        out byte[]? value, out RespReadStatus status, out string? error)
    {
        value = null;
        if (!TryReadTypedLength(ref reader, (byte)'$', maxFrameBytes, out var len, out status, out error))
        {
            return false;
        }

        if (len < 0)
        {
            // A null bulk string: valid as a reply value, rejected as a request argument by the caller.
            status = RespReadStatus.Complete;
            return true;
        }

        if (reader.Remaining < len + 2)
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        var payload = new byte[len];
        if (!reader.TryCopyTo(payload))
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        reader.Advance(len);
        if (!TryConsumeCrlf(ref reader, out status, out error))
        {
            return false;
        }

        value = payload;
        status = RespReadStatus.Complete;
        return true;
    }

    private static bool TryReadTypedLength(
        ref SequenceReader<byte> reader, byte typeByte, int maxFrameBytes,
        out long length, out RespReadStatus status, out string? error)
    {
        length = 0;
        error = null;
        if (!reader.TryPeek(out var prefix))
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        if (prefix != typeByte)
        {
            error = $"expected RESP type byte '{(char)typeByte}' but found '{Printable(prefix)}'";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        reader.Advance(1);
        if (!TryReadLine(ref reader, maxFrameBytes, out var line, out status, out error))
        {
            return false;
        }

        if (line.Length == 0)
        {
            error = "missing length after RESP type byte";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        if (!TryParseInt64(line, out length))
        {
            error = "RESP length is not a base-10 integer";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        if (length > maxFrameBytes)
        {
            error = $"RESP frame declares {length} bytes, exceeding the {maxFrameBytes}-byte limit";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        if (length < -1)
        {
            // -1 is RESP's null; any other negative length is malformed (spec 011 R11.7), never read as null.
            error = $"RESP length {length} is negative";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        status = RespReadStatus.Complete;
        return true;
    }

    private static bool TryReadLine(
        ref SequenceReader<byte> reader, int maxFrameBytes,
        out ReadOnlySpan<byte> line, out RespReadStatus status, out string? error)
    {
        line = default;
        error = null;
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineSeq, (byte)'\r', advancePastDelimiter: false))
        {
            if (reader.Remaining > maxFrameBytes + 2)
            {
                error = $"RESP line exceeds the {maxFrameBytes}-byte limit with no terminator";
                status = RespReadStatus.ProtocolError;
                return false;
            }

            status = RespReadStatus.Incomplete;
            return false;
        }

        reader.Advance(1); // the CR
        if (!reader.TryRead(out var lf))
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        if (lf != (byte)'\n')
        {
            error = "RESP line CR was not followed by LF";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        line = lineSeq.IsSingleSegment ? lineSeq.FirstSpan : lineSeq.ToArray();
        status = RespReadStatus.Complete;
        return true;
    }

    private static bool TryConsumeCrlf(ref SequenceReader<byte> reader, out RespReadStatus status, out string? error)
    {
        error = null;
        if (reader.Remaining < 2)
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        reader.TryRead(out var cr);
        reader.TryRead(out var lf);
        if (cr != (byte)'\r' || lf != (byte)'\n')
        {
            error = "expected CRLF terminating a bulk string";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        status = RespReadStatus.Complete;
        return true;
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        var negative = false;
        var i = 0;
        if (digits.Length > 0 && digits[0] == (byte)'-')
        {
            negative = true;
            i = 1;
            if (digits.Length == 1)
            {
                return false;
            }
        }

        // Checked against the signed range (spec 011 R11.7): a number past 64 bits is a protocol error, never a silently
        // wrapped value. The magnitude is kept unsigned, as long.MinValue's is one more than long.MaxValue.
        var limit = negative ? (ulong)long.MaxValue + 1 : long.MaxValue;
        ulong magnitude = 0;
        for (; i < digits.Length; i++)
        {
            var d = digits[i];
            if (d < (byte)'0' || d > (byte)'9')
            {
                return false;
            }

            var digit = (ulong)(d - (byte)'0');
            if (magnitude > (limit - digit) / 10)
            {
                return false;
            }

            magnitude = (magnitude * 10) + digit;
        }

        value = negative ? (long)(0 - magnitude) : (long)magnitude;
        return true;
    }

    private static char Printable(byte b) => b is >= 0x20 and < 0x7f ? (char)b : '?';
}
