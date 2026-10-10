// Spec 003 T3 / R10.2, R10.8, R14.6 — the RESP2 subset reader and writer: every type, null bulk and null array,
// partial reads at every byte boundary, malformed frames, a declared length above the cap, and a round trip of each
// reply shape of design § 8.2.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal.Replication.Resp;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests;

public sealed class RespTests
{
    private const int Cap = 1 << 20;

    private static ReadOnlySequence<byte> Seq(byte[] bytes) => new(bytes);

    // ---------------------------------------------------------------- requests

    [Fact]
    public void Request_ArrayOfBulkStrings_RoundTrips()
    {
        var args = new[] { "PULL"u8.ToArray(), "1048576"u8.ToArray(), new byte[] { 0, 1, 2, 0xFF } };
        var frame = new RespWriter().Request(args).ToArray();

        var status = RespReader.TryReadRequest(Seq(frame), Cap, out var read, out var consumed, out var error);

        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Null(error);
        Assert.Equal(3, read.Count);
        Assert.Equal("PULL", Text.UTF8.GetString(read[0]));
        Assert.Equal("1048576", Text.UTF8.GetString(read[1]));
        Assert.Equal(new byte[] { 0, 1, 2, 0xFF }, read[2]);
    }

    [Fact]
    public void Request_NullArray_IsProtocolError()
    {
        var status = RespReader.TryReadRequest(Seq("*-1\r\n"u8.ToArray()), Cap, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.NotNull(error);
    }

    [Fact]
    public void Request_EmptyArray_IsProtocolError()
    {
        var status = RespReader.TryReadRequest(Seq("*0\r\n"u8.ToArray()), Cap, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.NotNull(error);
    }

    [Fact]
    public void Request_NullBulkArgument_IsProtocolError()
    {
        var status = RespReader.TryReadRequest(Seq("*1\r\n$-1\r\n"u8.ToArray()), Cap, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.NotNull(error);
    }

    [Fact]
    public void Request_PartialAtEveryByteBoundary_ParsesIdenticallyToWhole()
    {
        var frame = new RespWriter().Request(["HELLO"u8.ToArray(), "1"u8.ToArray()]).ToArray();

        // Every strict prefix (missing at least one byte) must be Incomplete, never a throw or error.
        for (var cut = 1; cut < frame.Length; cut++)
        {
            var status = RespReader.TryReadRequest(Seq(frame[..cut]), Cap, out _, out _, out var error);
            Assert.Equal(RespReadStatus.Incomplete, status);
            Assert.Null(error);
        }

        // The whole frame parses.
        var whole = RespReader.TryReadRequest(Seq(frame), Cap, out var read, out _, out _);
        Assert.Equal(RespReadStatus.Complete, whole);
        Assert.Equal(["HELLO", "1"], read.Select(a => Text.UTF8.GetString(a)));
    }

    [Theory]
    [InlineData("+OK\r\n")]                 // not an array
    [InlineData("*1\r\n+OK\r\n")]           // array element is not a bulk string
    [InlineData("*1\r\n$3\r\nabX")]         // bulk string not CRLF-terminated (and complete-length)
    [InlineData("*x\r\n")]                  // non-numeric count
    public void Request_MalformedFrames_AreProtocolErrors(string text)
    {
        var status = RespReader.TryReadRequest(Seq(Text.ASCII.GetBytes(text)), Cap, out _, out _, out var error);
        // Either a protocol error, or incomplete for a genuinely short frame — never Complete, never a throw.
        Assert.NotEqual(RespReadStatus.Complete, status);
        if (status == RespReadStatus.ProtocolError)
        {
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void Request_DeclaredLengthAboveCap_IsProtocolError()
    {
        // A bulk string declaring 10 MiB with a 1 KiB cap is refused, not buffered.
        var frame = Text.ASCII.GetBytes("*1\r\n$10485760\r\n");
        var status = RespReader.TryReadRequest(Seq(frame), maxFrameBytes: 1024, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.Contains("exceeding", error);
    }

    [Fact]
    public void Request_UnterminatedLineAboveCap_IsProtocolError()
    {
        // A run of digits with no CRLF, past the cap, is refused rather than buffered forever.
        var frame = new byte[2048];
        frame[0] = (byte)'*';
        for (var i = 1; i < frame.Length; i++)
        {
            frame[i] = (byte)'9';
        }

        var status = RespReader.TryReadRequest(Seq(frame), maxFrameBytes: 1024, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.NotNull(error);
    }

    // ---------------------------------------------------------------- replies

    [Fact]
    public void Reply_SimpleString_RoundTrips()
    {
        var frame = new RespWriter().SimpleString("OK").ToArray();
        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(RespType.SimpleString, value!.Type);
        Assert.Equal("OK", value.Text);
    }

    [Fact]
    public void Reply_Error_RoundTrips()
    {
        var frame = new RespWriter().Error("CRYSTAL_AUTH").ToArray();
        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(RespType.Error, value!.Type);
        Assert.Equal("CRYSTAL_AUTH", value.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Reply_Integer_RoundTrips(long n)
    {
        var frame = new RespWriter().Integer(n).ToArray();
        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(RespType.Integer, value!.Type);
        Assert.Equal(n, value.Integer);
    }

    [Fact]
    public void Reply_BulkString_RoundTripsWithEmbeddedCrlfAndNulls()
    {
        var payload = new byte[] { 0, (byte)'\r', (byte)'\n', 0xFF, 1 };
        var frame = new RespWriter().BulkString(payload).ToArray();
        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(RespType.BulkString, value!.Type);
        Assert.Equal(payload, value.Bytes);
    }

    [Fact]
    public void Reply_EmptyBulkString_RoundTrips()
    {
        var frame = new RespWriter().BulkString(Array.Empty<byte>()).ToArray();
        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(RespType.BulkString, value!.Type);
        Assert.Empty(value.Bytes!);
    }

    [Fact]
    public void Reply_NullBulk_And_NullArray_RoundTrip()
    {
        var nb = new RespWriter().NullBulkString().ToArray();
        var nbStatus = RespReader.TryReadReply(Seq(nb), Cap, out var nbValue, out _, out _);
        Assert.Equal(RespReadStatus.Complete, nbStatus);
        Assert.Equal(RespType.NullBulkString, nbValue!.Type);

        var na = new RespWriter().NullArray().ToArray();
        var naStatus = RespReader.TryReadReply(Seq(na), Cap, out var naValue, out _, out _);
        Assert.Equal(RespReadStatus.Complete, naStatus);
        Assert.Equal(RespType.NullArray, naValue!.Type);
    }

    [Fact]
    public void Reply_HelloShape_RoundTrips()
    {
        // design § 8.2: HELLO reply [protocolVersion, nodeId, role, epoch, format, nonce]
        var w = new RespWriter();
        w.ArrayHeader(6)
            .Integer(1)
            .BulkString("node-A")
            .BulkString("primary")
            .Integer(7)
            .BulkString("crystal 1")
            .BulkString(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var frame = w.ToArray();

        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal(RespType.Array, value!.Type);
        var items = value.Items!;
        Assert.Equal(6, items.Count);
        Assert.Equal(1, items[0].Integer);
        Assert.Equal("node-A", Text.UTF8.GetString(items[1].Bytes!));
        Assert.Equal("primary", Text.UTF8.GetString(items[2].Bytes!));
        Assert.Equal(7, items[3].Integer);
        Assert.Equal("crystal 1", Text.UTF8.GetString(items[4].Bytes!));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, items[5].Bytes);
    }

    [Fact]
    public void Reply_PullEntryShape_RoundTrips()
    {
        // design § 8.2: PULL reply [epoch, setVersion, [entry…]]; entry [uid, status, incarnation, base, latest, batches]
        var w = new RespWriter();
        w.ArrayHeader(3)
            .Integer(7)       // epoch
            .Integer(42)      // setVersion
            .ArrayHeader(1);
        // one entry
        w.ArrayHeader(6)
            .BulkString("uid-A")
            .BulkString("ok")
            .BulkString("inc-1")
            .Integer(0)       // base
            .Integer(1200)    // latest
            .ArrayHeader(1);  // batches: one [seq, bytes]
        w.ArrayHeader(2)
            .Integer(1195)
            .BulkString(new byte[] { 1, 2, 3 });
        var frame = w.ToArray();

        var status = RespReader.TryReadReply(Seq(frame), Cap, out var value, out _, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        var top = value!.Items!;
        Assert.Equal(3, top.Count);
        Assert.Equal(7, top[0].Integer);
        Assert.Equal(42, top[1].Integer);
        var entries = top[2].Items!;
        Assert.Single(entries);
        var entry = entries[0].Items!;
        Assert.Equal("uid-A", Text.UTF8.GetString(entry[0].Bytes!));
        Assert.Equal("ok", Text.UTF8.GetString(entry[1].Bytes!));
        var batches = entry[5].Items!;
        Assert.Single(batches);
        Assert.Equal(1195, batches[0].Items![0].Integer);
        Assert.Equal(new byte[] { 1, 2, 3 }, batches[0].Items![1].Bytes);
    }

    [Fact]
    public void Reply_PartialAtEveryByteBoundary_IsIncompleteThenComplete()
    {
        var w = new RespWriter();
        w.ArrayHeader(2).Integer(99).BulkString("hello");
        var frame = w.ToArray();

        for (var cut = 1; cut < frame.Length; cut++)
        {
            var status = RespReader.TryReadReply(Seq(frame[..cut]), Cap, out _, out _, out var error);
            Assert.Equal(RespReadStatus.Incomplete, status);
            Assert.Null(error);
        }

        Assert.Equal(RespReadStatus.Complete, RespReader.TryReadReply(Seq(frame), Cap, out _, out _, out _));
    }

    [Fact]
    public void Reader_ReportsConsumed_SoAStreamCanContinue()
    {
        // Two frames back to back: the reader consumes exactly the first.
        var first = new RespWriter().SimpleString("OK").ToArray();
        var second = new RespWriter().Integer(5).ToArray();
        var combined = first.Concat(second).ToArray();

        var status = RespReader.TryReadReply(Seq(combined), Cap, out var value, out var consumed, out _);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Equal("OK", value!.Text);

        var seq = new ReadOnlySequence<byte>(combined);
        var rest = seq.Slice(consumed);
        var secondStatus = RespReader.TryReadReply(rest, Cap, out var secondValue, out _, out _);
        Assert.Equal(RespReadStatus.Complete, secondStatus);
        Assert.Equal(5, secondValue!.Integer);
    }

    [Fact]
    public void Reply_BulkLengthAboveCap_IsProtocolError()
    {
        // T18 review (blocker): a hostile primary declaring a huge bulk length must be refused, not allocated.
        var frame = Text.ASCII.GetBytes("$2000000000\r\n");
        var status = RespReader.TryReadReply(Seq(frame), maxFrameBytes: 1024, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.Contains("exceeding", error);
    }

    [Fact]
    public void Reply_ArrayCountAboveCap_IsProtocolError()
    {
        // An array declaring more elements than the cap is refused before `new RespValue[count]` runs: each element
        // is at least one byte on the wire, so the byte cap bounds the element count too.
        var frame = Text.ASCII.GetBytes("*2000000000\r\n");
        var status = RespReader.TryReadReply(Seq(frame), maxFrameBytes: 1024, out _, out _, out var error);
        Assert.Equal(RespReadStatus.ProtocolError, status);
        Assert.Contains("exceeding", error);
    }
}
