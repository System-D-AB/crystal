// Spec 012 T4, R2 — range size estimates: non-zero for written data, 0 for an untouched box, proportional to the share
// of a uniform range, isolated per box, several ranges in one call equal to one at a time; refused for a reversed
// range, a dropped box and another database's handle; answered on a standby and on a failed database.

using System;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests.Adoption;

public sealed class ApproximateBytesTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();
    private readonly CrystalStore _store;
    private readonly CrystalDatabase _db;

    public ApproximateBytesTests()
    {
        _store = CrystalStore.Open(_dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = "test-secret" } });
        _db = _store.CreateDatabase("db");
    }

    public void Dispose()
    {
        Assert.Equal(0, _store.Engine.OpenIterators); // spec 012 R8.3: no scan left open
        _store.Dispose();
        _dir.Dispose();
    }

    private static byte[] Key(int i)
    {
        var key = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(key, i);
        return key;
    }

    private void Fill(Box box, int count, int valueBytes = 64)
    {
        var value = new byte[valueBytes];
        for (var i = 0; i < count; i += 1000)
        {
            using var batch = _db.NewBatch();
            for (var j = i; j < Math.Min(count, i + 1000); j++)
            {
                Random.Shared.NextBytes(value);
                batch.Put(box, Key(j), value);
            }

            batch.Commit(DurabilityMode.Buffered);
        }

        _db.Slot.Instance.Flush();
    }

    [Fact]
    public void WrittenData_EstimatesNonZero_AnUntouchedBoxZero_AndAnEmptyRangeZero()
    {
        var full = _db.Boxes.Create("full");
        var untouched = _db.Boxes.Create("untouched");
        Fill(full, 10_000);

        Assert.True(_db.ApproximateBytes(full, [], []) > 0);
        Assert.Equal(0L, _db.ApproximateBytes(untouched, [], []));
        Assert.Equal(0L, _db.ApproximateBytes(full, Key(5), Key(5)));
    }

    [Fact]
    public void HalfOfAUniformRange_EstimatesAboutHalfOfTheWhole()
    {
        var box = _db.Boxes.Create("box");
        Fill(box, 200_000, valueBytes: 32);

        var whole = (double)_db.ApproximateBytes(box, [], []);
        var half = (double)_db.ApproximateBytes(box, [], Key(100_000));
        Assert.InRange(half / whole, 0.40, 0.60);
    }

    [Fact]
    public void AnotherBoxsData_DoesNotChangeABoxsEstimate()
    {
        var a = _db.Boxes.Create("a");
        var b = _db.Boxes.Create("b");
        Fill(a, 20_000);
        var before = _db.ApproximateBytes(a, [], []);

        Fill(b, 50_000);

        Assert.Equal(before, _db.ApproximateBytes(a, [], []));
        Assert.True(_db.ApproximateBytes(b, [], []) > before);
    }

    [Fact]
    public void SeveralRangesInOneCall_EqualOneCallPerRange()
    {
        var box = _db.Boxes.Create("box");
        Fill(box, 50_000);
        KeyRange[] ranges =
        [
            KeyRange.All,
            new(Key(0), Key(10_000)),
            new(Key(10_000), Key(10_000)),
            new(Key(40_000), []),
            KeyRanges.Prefix([0x00, 0x00]),
        ];

        var together = _db.ApproximateBytes(box, ranges);

        Assert.Equal(ranges.Select(r => _db.ApproximateBytes(box, r.From, r.To)), together);
        Assert.Equal(0L, together[2]);
    }

    [Fact]
    public void AReversedRange_ADroppedBox_AndAForeignHandle_AreRefused()
    {
        var box = _db.Boxes.Create("box");
        Assert.Equal("from", Assert.Throws<ArgumentException>(() => _db.ApproximateBytes(box, Key(9), Key(1))).ParamName);
        Assert.Throws<ArgumentException>(() => _db.ApproximateBytes(box, [new KeyRange(Key(9), Key(1))]));
        Assert.Equal(0L, _db.ApproximateBytes(box, KeyRange.Empty));

        var other = _store.CreateDatabase("other").Boxes.Create("box");
        Assert.Throws<BoxNotInDatabaseException>(() => _db.ApproximateBytes(other, [], []));

        _db.Boxes.Drop(box);
        Assert.Throws<BoxDroppedException>(() => _db.ApproximateBytes(box, [], []));
    }

    [Fact]
    public void ItIsAnsweredOnAStandby_AndOnAFailedDatabase()
    {
        var box = _db.Boxes.Create("box");
        Fill(box, 5_000);
        var expected = _db.ApproximateBytes(box, [], []);

        _store.Replication.Demote();
        Assert.Equal(expected, _db.ApproximateBytes(box, [], []));

        // A failed database, in a store of its own: closing it reports the failure, as it must.
        using var dir = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path);
        var local = store.CreateDatabase("local");
        var localBox = local.Boxes.Create("box");
        local.Slot.Instance.OnBeforeWrite = _ => throw new IOException("injected: the write failed");
        using (var batch = local.NewBatch())
        {
            batch.Put(localBox, "k"u8, "v"u8);
            Assert.Throws<DatabaseFailedException>(() => batch.Commit());
        }

        Assert.True(local.Slot.Context.Failed);
        _ = local.ApproximateBytes(localBox, [], []); // a read: answered
        Assert.Throws<AggregateException>(() => store.Dispose());
    }
}
