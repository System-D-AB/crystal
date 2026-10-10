// Ported from Highway: tests/Highway.Server.Tests/Storage/BoxLayoutTests.cs (spec 004 design § 2.2, task T10).
// Highway's queue spaces, box kinds and id cache are Highway's own; what carries over is the layout, the catalogue
// and the stale-id rule. Highway's reference model of a queue is carried as a pattern, in ReferenceModelTests.
// AnEpochReset_InvalidatesLookupsInFlight belongs to a standby's catalogue reload, spec 003.

using System;
using System.Linq;
using RocksDbSharp;
using SystemD.Crystal.Encoding;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Ported;

public sealed class BoxLayoutTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Seq(ulong value)
    {
        var bytes = new byte[8];
        KeyEncoding.EncodeUInt64(bytes, value);
        return bytes;
    }

    private static bool StartsWith(byte[] key, byte[] prefix) => key.AsSpan().StartsWith(prefix);

    // Ported from Highway: BoxLayoutTests.Keys_OrderNumerically_AndStayInsideTheirBoxAndSpace
    [Fact]
    public void Keys_OrderNumerically_AndStayInsideTheirBox()
    {
        var keys = new[]
        {
            BoxKeys.BuildKey(16, Seq(2)), BoxKeys.BuildKey(16, Seq(10)), BoxKeys.BuildKey(16, Seq(256)),
            BoxKeys.BuildKey(16, Seq(70_000)), BoxKeys.BuildKey(17, Seq(0)), BoxKeys.BuildKey(256, Seq(0)),
        };

        for (var i = 1; i < keys.Length; i++)
        {
            Assert.True(keys[i - 1].AsSpan().SequenceCompareTo(keys[i]) < 0, $"Key {i - 1} must sort before key {i}.");
        }

        BoxKeys.GetBoxBounds(16, out var lower16, out var upper16);
        foreach (var key in keys.Take(4))
        {
            Assert.True(StartsWith(key, lower16));
            Assert.True(key.AsSpan().SequenceCompareTo(upper16) < 0);
        }

        Assert.False(StartsWith(BoxKeys.BuildKey(17, Seq(0)), lower16), "Another box never shares a prefix.");
        Assert.Equal(17u, BoxKeys.ExtractBoxId(BoxKeys.BuildKey(17, Seq(0))));
        Assert.Equal(Seq(70_000), BoxKeys.ExtractUserKey(BoxKeys.BuildKey(16, Seq(70_000))).ToArray());

        BoxKeys.GetBoxBounds(BoxKeys.CatalogueBoxId, out var catalogue, out _);
        Assert.True(StartsWith(BoxKeys.MakeAliasKey("orders"), catalogue), "Catalogue keys live in box 0.");
        Assert.True(StartsWith(BoxKeys.MakeRecordKey(16), catalogue));
        Assert.True(StartsWith(BoxKeys.AllocatorKey, catalogue));
    }

    // Ported from Highway: BoxLayoutTests.Catalog_Allocates_Finds_Lists_Retires_AndNeverReusesAnId. Highway's
    // "cold cache" is Crystal's reopen; its "retire" is Crystal's drop.
    [Fact]
    public void Catalog_Creates_Finds_Lists_Drops_AndNeverReusesAnId()
    {
        Box orders, invoices;
        uint databaseId;
        long generation;

        using (var store = CrystalStore.Open(_dir.Path))
        {
            var db = store.CreateDatabase("shop");
            databaseId = db.Id;
            generation = db.Boxes.Generation;

            orders = db.Boxes.Create("orders");
            invoices = db.Boxes.Create("invoices");

            Assert.True(orders.Id >= BoxKeys.FirstProductBoxId);
            Assert.NotEqual(orders.Id, invoices.Id);
            Assert.Equal(generation + 2, db.Boxes.Generation);
        }

        using (var store = CrystalStore.Open(_dir.Path))
        {
            var db = store.OpenDatabase("shop");
            var found = db.Boxes.Find("orders")!.Value;
            Assert.Equal((orders.Id, orders.Name), (found.Id, found.Name));
            orders = found;

            var listed = db.Boxes.List().Select(b => (b.Name, b.Id)).OrderBy(b => b.Id).ToArray();
            Assert.Equal([("orders", orders.Id), ("invoices", invoices.Id)], listed);

            using (var batch = db.NewBatch())
            {
                batch.Put(orders, "m1"u8, "payload"u8);
                batch.Commit();
            }

            db.Boxes.Drop(orders);

            Assert.Null(db.Boxes.Find("orders"));
            Assert.Equal(generation + 3, db.Boxes.Generation);

            var again = db.Boxes.Create("orders");
            Assert.NotEqual(orders.Id, again.Id);
            Assert.True(again.Id > invoices.Id, "A dropped id is never reused, and the allocator only moves forward.");

            using var snapshot = db.Snapshot();
            Assert.Empty(snapshot.Scan(again, [], [], 10));
        }

        // Dropping deletes the box's whole range, on disk.
        BoxKeys.GetBoxBounds(orders.Id, out var lower, out var upper);
        var families = new ColumnFamilies { { "general", new ColumnFamilyOptions() }, { "delete_heavy", new ColumnFamilyOptions() } };
        using var rocks = RocksDb.Open(new DbOptions(), _dir.DatabaseFolder(databaseId), families);
        using var iterator = rocks.NewIterator(rocks.GetColumnFamily("general"));
        iterator.Seek(lower);
        Assert.False(
            iterator.Valid() && iterator.Key().AsSpan().SequenceCompareTo(upper) < 0,
            "Dropping a box deletes its whole range.");
    }

    // Ported from Highway: BoxLayoutTests.ALookupStartedBeforeARetirement_DoesNotCacheTheRetiredId (adapted). Crystal
    // has no id cache to refill; the same hazard is a handle found before a drop and used after the name is created
    // again. It must be refused, never redirected to the new box of the same name.
    [Fact]
    public void AHandleFoundBeforeADrop_IsRefused_NeverRedirectedToTheNewBox()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        db.Boxes.Create("news@billing");

        var found = db.Boxes.Find("news@billing")!.Value;      // the lookup, before the drop
        db.Boxes.Drop(found);                                  // the drop, committed
        var recreated = db.Boxes.Create("news@billing");       // the same name, a new box

        using (var batch = db.NewBatch())
        {
            batch.Put(found, "k"u8, "stale"u8);
            var refused = Assert.Throws<BoxDroppedException>(() => batch.Commit());
            Assert.Equal(found.Id, refused.BoxId);
        }

        using (var snapshot = db.Snapshot())
        {
            Assert.Throws<BoxDroppedException>(() => snapshot.Get(found, "k"u8));
            Assert.Null(snapshot.Get(recreated, "k"u8));
            Assert.Empty(snapshot.Scan(recreated, [], [], 10));
        }

        Assert.Equal(recreated, db.Boxes.Find("news@billing"));
    }
}
