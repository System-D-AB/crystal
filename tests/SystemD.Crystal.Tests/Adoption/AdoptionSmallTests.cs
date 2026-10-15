// Spec 012 T7, T8, T9, T12, T13, T14 — key range builders (a property test per builder), batch size accounting, the
// key reader, open-iterator accounting, the key size limit, and the added storage status.

using System;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Encoding;
using SystemD.Crystal.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Tests.Adoption;

public sealed class AdoptionSmallTests
{
    // ------------------------------------------------------------------ R4: key ranges

    private static bool In(byte[] key, KeyRange range) =>
        (range.From.Length == 0 || key.AsSpan().SequenceCompareTo(range.From) >= 0)
        && (range.To.Length == 0 || key.AsSpan().SequenceCompareTo(range.To) < 0)
        && !(range.From.Length > 0 && range.To.Length > 0 && range.From.AsSpan().SequenceCompareTo(range.To) >= 0);

    private static byte[] RandomKey(Random random)
    {
        var key = new byte[random.Next(0, 4)];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(random.Next(4) switch { 0 => 0x00, 1 => 0xFF, _ => random.Next(256) });
        }

        return key;
    }

    [Fact]
    public void EachBuilder_HoldsExactlyTheKeysItsPredicateNames()
    {
        var random = new Random(12_04);
        for (var round = 0; round < 3000; round++)
        {
            var a = RandomKey(random);
            var key = RandomKey(random);
            var b = RandomKey(random);

            Assert.Equal(key.AsSpan().SequenceEqual(a), In(key, KeyRanges.Point(a)));
            Assert.Equal(key.AsSpan().StartsWith(a), In(key, KeyRanges.Prefix(a)));

            foreach (var lowerKind in new[] { KeyBoundKind.Unbounded, KeyBoundKind.Inclusive, KeyBoundKind.Exclusive })
            {
                foreach (var upperKind in new[] { KeyBoundKind.Unbounded, KeyBoundKind.Inclusive, KeyBoundKind.Exclusive })
                {
                    var range = KeyRanges.Between(new KeyBound(lowerKind, a), new KeyBound(upperKind, b));
                    var aboveLower = lowerKind switch
                    {
                        KeyBoundKind.Unbounded => true,
                        KeyBoundKind.Inclusive => key.AsSpan().SequenceCompareTo(a) >= 0,
                        _ => key.AsSpan().SequenceCompareTo(a) > 0,
                    };
                    var belowUpper = upperKind switch
                    {
                        KeyBoundKind.Unbounded => true,
                        KeyBoundKind.Inclusive => key.AsSpan().SequenceCompareTo(b) <= 0,
                        _ => key.AsSpan().SequenceCompareTo(b) < 0,
                    };
                    Assert.True(
                        (aboveLower && belowUpper) == In(key, range),
                        $"{lowerKind} {Convert.ToHexString(a)} .. {upperKind} {Convert.ToHexString(b)}: {Convert.ToHexString(key)}");
                }
            }
        }
    }

    [Fact]
    public void PrefixEnd_IncrementsTheLastNonFFByte_AndIsTheBoxsEndWhenThereIsNone()
    {
        Assert.Equal([0x01, 0x03], KeyRanges.PrefixEnd([0x01, 0x02]));
        Assert.Equal([0x02], KeyRanges.PrefixEnd([0x01, 0xFF, 0xFF]));
        Assert.Empty(KeyRanges.PrefixEnd([0xFF, 0xFF]));
        Assert.Empty(KeyRanges.PrefixEnd([]));
        Assert.Equal(KeyRange.Empty, KeyRanges.Between(KeyBound.Unbounded, KeyBound.Exclusive([])));
    }

    [Fact]
    public void AScanOverABuiltRange_ReturnsExactlyTheKeysThePredicateSelects()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        var random = new Random(12_05);
        var keys = Enumerable.Range(0, 400).Select(_ => RandomKey(random)).Where(k => k.Length > 0)
            .DistinctBy(Convert.ToHexString).ToList();
        using (var batch = db.NewBatch())
        {
            foreach (var key in keys)
            {
                batch.Put(box, key, []);
            }

            batch.Commit();
        }

        using var snapshot = db.Snapshot();
        for (var round = 0; round < 300; round++)
        {
            var range = random.Next(3) switch
            {
                0 => KeyRanges.Prefix(RandomKey(random)),
                1 => KeyRanges.Point(RandomKey(random)),
                _ => KeyRanges.Between(
                    new KeyBound((KeyBoundKind)random.Next(3), RandomKey(random)),
                    new KeyBound((KeyBoundKind)random.Next(3), RandomKey(random))),
            };
            var expected = keys.Where(k => In(k, range)).Select(Convert.ToHexString).Order(StringComparer.Ordinal);
            Assert.Equal(expected, snapshot.Scan(box, range, int.MaxValue).Select(kv => Convert.ToHexString(kv.Key)));
        }
    }

    // ------------------------------------------------------------------ R5: batch accounting

    [Fact]
    public void ABatch_CountsItsOperationsAndBytes_WithoutThePrefix_AndResetsAfterCommitAndDispose()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");

        var batch = db.NewBatch();
        batch.Put(box, "key"u8, "value"u8);      // 3 + 5
        batch.Delete(box, "gone"u8);             // 4
        batch.DeleteRange(box, "a"u8, "bb"u8);  // 1 + 2
        Assert.Equal(3, batch.Count);
        Assert.Equal(15, batch.Bytes);
        batch.Commit();
        Assert.Equal((0, 0L), (batch.Count, batch.Bytes));

        batch.Put(box, "k"u8, "v"u8);
        batch.Dispose();
        Assert.Equal((0, 0L), (batch.Count, batch.Bytes));

        // A refused commit keeps the batch as it was.
        using var refused = db.NewBatch();
        refused.Put(box, "k"u8, "vv"u8);
        db.Boxes.Drop(box);
        Assert.Throws<BoxDroppedException>(() => refused.Commit());
        Assert.Equal((1, 3L), (refused.Count, refused.Bytes));
    }

    // ------------------------------------------------------------------ R6: key reader

    [Fact]
    public void EveryTypeRoundTrips_InBothOrders_AtItsBoundaries()
    {
        Span<byte> buffer = stackalloc byte[512];
        foreach (var descending in new[] { false, true })
        {
            var writer = new KeyWriter(buffer);
            void Field(ref KeyWriter w, int before)
            {
                if (descending)
                {
                    w.InvertLast(w.BytesWritten - before);
                }
            }

            var mark = writer.BytesWritten; writer.WriteBoolean(true); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteUInt32(uint.MaxValue); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteUInt64(0); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteInt32(int.MinValue); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteInt64(long.MaxValue); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteDouble(-0.0); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteDouble(double.NaN); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteDecimal(-79228162514264337593543950335m); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteTimestamp(DateTimeOffset.MaxValue); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteGuid(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10")); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteString("a\0bÿ\0"); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteString(""); Field(ref writer, mark);
            mark = writer.BytesWritten; writer.WriteInt32(42); Field(ref writer, mark);
            var key = writer.ToArray();

            var reader = new KeyReader(key);
            Assert.True(reader.ReadBoolean(descending));
            Assert.Equal(uint.MaxValue, reader.ReadUInt32(descending));
            Assert.Equal(0UL, reader.ReadUInt64(descending));
            Assert.Equal(int.MinValue, reader.ReadInt32(descending));
            Assert.Equal(long.MaxValue, reader.ReadInt64(descending));
            Assert.Equal(0.0, reader.ReadDouble(descending));
            Assert.True(double.IsNaN(reader.ReadDouble(descending)));
            Assert.Equal(-79228162514264337593543950335m, reader.ReadDecimal(descending));
            Assert.Equal(DateTimeOffset.MaxValue, reader.ReadTimestamp(descending));
            Assert.Equal(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"), reader.ReadGuid(descending));
            Assert.Equal("a\0bÿ\0", reader.ReadString(descending));
            Assert.Equal("", reader.ReadString(descending));
            Assert.Equal(42, reader.ReadInt32(descending));
            Assert.True(reader.Exhausted);
            Assert.Equal(key.Length, reader.Position);
        }
    }

    [Fact]
    public void AKeyTruncatedAtAnyOffset_ThrowsFormatException_AndNeverReadsPastIt()
    {
        Span<byte> buffer = stackalloc byte[128];
        var writer = new KeyWriter(buffer);
        writer.WriteInt64(7);
        writer.WriteString("value\0x");
        var mark = writer.BytesWritten;
        writer.WriteString("down");
        writer.InvertLast(writer.BytesWritten - mark);
        writer.WriteGuid(Guid.NewGuid());
        var key = writer.ToArray();

        for (var length = 0; length < key.Length; length++)
        {
            var truncated = key[..length];
            Assert.Throws<FormatException>(() =>
            {
                var reader = new KeyReader(truncated);
                reader.ReadInt64();
                reader.ReadString();
                reader.ReadString(descending: true);
                reader.ReadGuid();
            });
        }
    }

    [Fact]
    public void AFieldThatDoesNotDecode_IsAFormatException_AndTheReaderStaysBeforeIt()
    {
        var ticks = new byte[8];
        KeyEncoding.EncodeInt64(ticks, long.MaxValue); // beyond DateTimeOffset's range
        var reader = new KeyReader(ticks);
        try
        {
            reader.ReadTimestamp();
            Assert.Fail("an out-of-range timestamp was read");
        }
        catch (FormatException)
        {
        }

        Assert.Equal(0, reader.Position);
        Assert.Equal(long.MaxValue, reader.ReadInt64()); // the same bytes, read as what they are
    }

    // ------------------------------------------------------------------ R8: open iterators

    [Fact]
    public void OpenIterators_AreCounted_UntilDisposed()
    {
        using var metrics = new MetricsCapture();
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { MeterFactory = metrics });
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "a"u8, []);
            batch.Put(box, "b"u8, []);
            batch.Commit();
        }

        using var snapshot = db.Snapshot();
        Assert.Equal(0, store.Engine.OpenIterators);
        using (var scan = snapshot.Scan(box, [], [], 10).GetEnumerator())
        {
            Assert.True(scan.MoveNext());
            Assert.Equal(1, store.Engine.OpenIterators);
            Assert.Equal(1, Assert.Single(metrics.Collect("crystal.scan.open")).Value);
        }

        Assert.Equal(0, store.Engine.OpenIterators);

        var abandoned = snapshot.Scan(box, [], [], 10).GetEnumerator();
        abandoned.MoveNext(); // never disposed: stays counted
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(1, store.Engine.OpenIterators);
        abandoned.Dispose();
        Assert.Equal(0, store.Engine.OpenIterators);
    }

    // ------------------------------------------------------------------ R9: key size limit

    [Fact]
    public void KeysAboveTheLimit_AreRefusedWhenStaged_AndReadsFindNothing()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { MaxKeyBytes = 8 });
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        var long9 = new byte[9];

        using var batch = db.NewBatch();
        var refusal = Assert.Throws<KeyTooLargeException>(() => batch.Put(box, long9, []));
        Assert.Equal(("box", 9, 8), (refusal.BoxName, refusal.KeyBytes, refusal.Limit));
        Assert.Throws<KeyTooLargeException>(() => batch.Delete(box, long9));
        Assert.Throws<KeyTooLargeException>(() => batch.DeleteRange(box, ReadOnlySpan<byte>.Empty, long9));
        Assert.Equal(0, batch.Count); // nothing staged
        batch.Put(box, new byte[8], []);
        batch.Commit();

        using var snapshot = db.Snapshot();
        Assert.Null(snapshot.Get(box, long9));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrystalStore.Open(dir.Path + "-x", new CrystalOptions { MaxKeyBytes = 0 }));
    }

    // ------------------------------------------------------------------ R11: storage status

    [Fact]
    public void StorageStatus_ReportsFilesPerLevel_ActiveMemtableBytes_AndTheDelayedWriteRate()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "flushed"u8, new byte[1000]);
            batch.Commit();
        }

        db.Slot.Instance.Flush();
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "in-memory"u8, new byte[1000]);
            batch.Commit();
        }

        var storage = store.Status().Databases.Single().Storage;
        Assert.Equal(7, storage.FilesPerLevel.Count);
        Assert.True(storage.FilesPerLevel.Sum() >= 1);
        Assert.Equal(storage.SstFiles, storage.FilesPerLevel.Sum());
        Assert.True(storage.ActiveMemtableBytes > 0);
        Assert.Equal(0, storage.DelayedWriteRate);

        // Positional construction still compiles and defaults the new properties (R11.2).
        var positional = new StorageStatus(0, 0, 0, 0, 0, 0, false);
        Assert.Empty(positional.FilesPerLevel);

        // Value equality survives the array: equal figures, equal statuses.
        Assert.Equal(storage with { FilesPerLevel = [.. storage.FilesPerLevel] }, storage);
        Assert.NotEqual(storage with { FilesPerLevel = [9, 9, 9, 9, 9, 9, 9] }, storage);
    }
}
