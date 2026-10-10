// Spec 003 T6 / R3.6 — trimming a serialized WriteBatch to begin at a cursor inside it (a group-commit record a
// standby stands in the middle of). Ported from Highway's ReplicationTailTests.Trim case, to xUnit assertions.
// The real RocksDbSharp.WriteBatch builds the record so the trim is checked against the format RocksDB actually emits.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RocksDbSharp;
using SystemD.Crystal.Replication;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class WriteBatchTrimTests
{
    [Fact]
    public void StartsAMergedRecordMidway_AndRefusesWhatItCannotParse()
    {
        using var wb = new WriteBatch();
        wb.Put(new byte[] { 1 }, new byte[] { 10 });
        wb.Delete(new byte[] { 2 });
        wb.PutLogData(new byte[] { 0xAA }, 1);                       // consumes no sequence
        wb.DeleteRange(new byte[] { 3 }, 1, new byte[] { 4 }, 1);
        wb.Put(new byte[] { 5 }, new byte[] { 50, 51 });
        var data = wb.ToBytes();
        BinaryPrimitives.WriteUInt64LittleEndian(data, 100);         // as if written at 100..103

        // Trim to 102: the DeleteRange (102) and the last Put (103) remain.
        Assert.True(WriteBatchTrim.TryStartAt(data, 102, out var rest));
        Assert.Equal(102UL, BinaryPrimitives.ReadUInt64LittleEndian(rest));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(8)));
        using (var parsed = new WriteBatch(rest, rest.Length))
        {
            Assert.Equal(2, parsed.Count());
        }

        // Trim to the first sequence returns the whole batch unchanged.
        Assert.True(WriteBatchTrim.TryStartAt(data, 100, out var whole));
        Assert.Equal(data, whole);

        // Out of range both ways.
        Assert.False(WriteBatchTrim.TryStartAt(data, 104, out _)); // past the batch
        Assert.False(WriteBatchTrim.TryStartAt(data, 99, out _));  // before it

        // An unknown record tag is refused, never guessed (the caller answers with a gap).
        var unknown = (byte[])data.Clone();
        unknown[12] = 0x11; // BlobIndex: not something Crystal writes
        Assert.False(WriteBatchTrim.TryStartAt(unknown, 101, out _));
    }

    [Fact]
    public void AnUnknownRecordInTheKeptRange_IsRefused_EvenWhenItWouldParseAsASlice()
    {
        // A batch of two records: a Put (consumes seq 200) and then an unknown tag (0xF0) followed by a well-formed
        // varint-length-prefixed field, so that *mis*reading it as a known one-slice record would parse cleanly and
        // report a plausible count. The trim keeps both records (cursor at the first). Only refusing the unknown tag
        // gives the right answer; an accepting parser would hand out a batch it cannot vouch for.
        var body = new List<byte>();
        // record 1: Put (0x01), key len 1 + key, value len 1 + value
        body.Add(0x01);
        body.Add(1); body.Add(0xAA);        // key
        body.Add(1); body.Add(0xBB);        // value
        var afterPut = body.Count;
        // record 2: unknown tag (0xF0), then a 2-byte length-prefixed field (varint 2, two bytes)
        body.Add(0xF0);
        body.Add(2); body.Add(0x01); body.Add(0x02);

        var data = new byte[12 + body.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(data, 200);               // first sequence
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 2);       // claimed count: 2
        body.CopyTo(data, 12);

        // Trim to 200 (keep everything): the unknown record is in range and must be refused.
        Assert.False(WriteBatchTrim.TryStartAt(data, 200, out _));

        // Sanity: a batch with the unknown record replaced by a real Delete trims cleanly, so the refusal above is the
        // unknown tag's doing, not a malformed length.
        var ok = (byte[])data.Clone();
        ok[12 + afterPut] = 0x00;           // 0xF0 -> Delete (key only): the trailing [2, 01, 02] is len-2 key 01 02
        Assert.True(WriteBatchTrim.TryStartAt(ok, 200, out var whole));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(8)));
    }

    [Fact]
    public void SingleRecordBatch_TrimToItsOwnSequence_IsTheWholeBatch()
    {
        using var wb = new WriteBatch();
        wb.Put(new byte[] { 7 }, new byte[] { 70 });
        var data = wb.ToBytes();
        BinaryPrimitives.WriteUInt64LittleEndian(data, 500);

        Assert.True(WriteBatchTrim.TryStartAt(data, 500, out var whole));
        Assert.Equal(data, whole);

        Assert.False(WriteBatchTrim.TryStartAt(data, 501, out _));
    }
}
