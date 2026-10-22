// Spec 015 R4.5 (decision D2) — the Highway features of spec 013 taken for 1.0, so Highway runs on Crystal's own
// mechanisms instead of its workarounds: a database's latest position (013 R5) and box handles from the catalogue
// listing (013 R9). The replication ones (013 R6, R8, R10) and offline compaction (013 R4) have tests of their own.

using System;
using System.Collections.Generic;
using System.Linq;
using Text = System.Text.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests.Adoption;

public sealed class HighwayFeatureTests
{
    [Fact]
    public void LatestPosition_MovesWithEveryCommit_AndIsKnownWithNoStandby()
    {
        // 013 R5: Highway proved "an idle poll writes nothing" by reading the position before and after, and showed a
        // primary's position on its dashboard; with no standby attached there was none to read.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        var before = Position(store, db);
        Assert.Equal((long)db.Slot.LatestSequence, before); // the position replication speaks in

        using (var batch = db.NewBatch())
        {
            batch.Put(box, Text.UTF8.GetBytes("a"), Text.UTF8.GetBytes("1"));
            batch.Put(box, Text.UTF8.GetBytes("b"), Text.UTF8.GetBytes("2"));
            batch.Commit(DurabilityMode.Strict);
        }

        var after = Position(store, db);
        Assert.True(after > before, $"A commit did not move the position ({before} → {after}).");

        using (var empty = db.NewBatch())
        {
            empty.Commit(DurabilityMode.Strict); // writes nothing (spec 002 R6.3)
        }

        Assert.Equal(after, Position(store, db));
    }

    [Fact]
    public void List_GivesEveryLiveBoxWithAUsableHandle_AndAnotherDatabaseRefusesThem()
    {
        // 013 R9: a product walking every box gets handles in one call, not List() and then Find() for each.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("shop");
        db.Boxes.Create("a");
        var dropped = db.Boxes.Create("b");
        db.Boxes.Create("c");
        db.Boxes.Drop(dropped);

        var listed = db.Boxes.List();
        var handles = listed.Select(i => i.Handle).ToList();

        Assert.Equal(["a", "c"], handles.Select(h => h.Name).Order(StringComparer.Ordinal));
        Assert.Equal(listed.Select(i => i.Id), handles.Select(h => h.Id));
        Assert.Equal(db.Boxes.Find("a"), handles.Single(h => h.Name == "a")); // the same handle Find issues
        foreach (var box in handles)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes(box.Name));
            batch.Commit(DurabilityMode.Strict);
            using var snapshot = db.Snapshot();
            Assert.Equal(box.Name, Text.UTF8.GetString(snapshot.Get(box, Text.UTF8.GetBytes("k"))!));
        }

        // Box ids repeat across databases: a handle from this listing addresses this database only.
        var other = store.CreateDatabase("other");
        other.Boxes.Create("a");
        using var foreign = other.NewBatch();
        Assert.Throws<BoxNotInDatabaseException>(() => foreign.Put(handles[0], Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v")));
    }

    [Fact]
    public void Compact_ChangesNoData_AndDropsWhatWasDeleted()
    {
        // 013 R4.3: live keys, point deletes and range deletes in several boxes of two databases read back exactly the
        // same after an offline compaction; the database whose data was mostly deleted shrinks.
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            var shop = store.CreateDatabase("shop");
            Put(shop, shop.Boxes.Create("orders"));
            Put(shop, shop.Boxes.Create("queue", BoxProfile.DeleteHeavy));
            var billing = store.CreateDatabase("billing");
            Put(billing, billing.Boxes.Create("invoices"));
        }

        // A second session: opening flushes what the first wrote into table files, so the deletions below are real
        // tombstones over table-file data, which is what an offline compaction exists to drop.
        Dictionary<string, string> before;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var shop = store.OpenDatabase("shop");
            Delete(shop, shop.Boxes.Find("orders")!.Value, deleteFrom: 0, deleteTo: 1500, rangeFrom: 1500, rangeTo: 1800);
            Delete(shop, shop.Boxes.Find("queue")!.Value, deleteFrom: 200, deleteTo: 1900, rangeFrom: 0, rangeTo: 100);
            var billing = store.OpenDatabase("billing");
            Delete(billing, billing.Boxes.Find("invoices")!.Value, deleteFrom: 0, deleteTo: 10, rangeFrom: 10, rangeTo: 20);
            before = Contents(store);
        }

        var result = CrystalStore.Compact(dir.Path);

        Assert.Equal(["billing", "shop"], result.Databases.Select(d => d.Name).Order(StringComparer.Ordinal));
        var shopResult = result.Databases.Single(d => d.Name == "shop");
        Assert.True(shopResult.BytesAfter < shopResult.BytesBefore,
            $"shop, mostly deleted, did not shrink: {shopResult.BytesBefore} → {shopResult.BytesAfter} bytes");
        using var reopened = CrystalStore.Open(dir.Path);
        Assert.Equal(before, Contents(reopened));

        // Compacted end to end: nothing is left in level 0, where flushes put data. (Size alone proves little: RocksDB
        // also drops deleted data in flushes and deletion-triggered compactions of its own.)
        foreach (var database in reopened.Status().Databases)
        {
            Assert.True(database.Storage.FilesPerLevel[0] == 0,
                $"{database.Name} still has {database.Storage.FilesPerLevel[0]} level-0 files after an end-to-end compaction");
        }
    }

    [Fact]
    public void Compact_IsRefusedWhileOpen_RefusesAnUnknownDatabase_AndCompactsOnlyTheOnesNamed()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop").Boxes.Create("orders");
            store.CreateDatabase("billing").Boxes.Create("invoices");
            Assert.Throws<StoreLockedException>(() => CrystalStore.Compact(dir.Path)); // offline only (013 R4.1)
        }

        Assert.Throws<DatabaseNotFoundException>(() => CrystalStore.Compact(dir.Path, new CompactOptions { Databases = ["shop", "nope"] }));

        var result = CrystalStore.Compact(dir.Path, new CompactOptions { Databases = ["billing"] });
        Assert.Equal("billing", Assert.Single(result.Databases).Name);
    }

    private static void Put(CrystalDatabase db, Box box)
    {
        var random = new Random(box.Name.Length);
        using var batch = db.NewBatch();
        for (var i = 0; i < 2000; i++)
        {
            var value = new byte[1024];
            random.NextBytes(value); // incompressible, so sizes mean data
            batch.Put(box, Key(i), value);
        }

        batch.Commit(DurabilityMode.Strict);
    }

    private static void Delete(CrystalDatabase db, Box box, int deleteFrom, int deleteTo, int rangeFrom, int rangeTo)
    {
        using var batch = db.NewBatch();
        for (var i = deleteFrom; i < deleteTo; i++)
        {
            batch.Delete(box, Key(i));
        }

        batch.DeleteRange(box, Key(rangeFrom), Key(rangeTo));
        batch.Commit(DurabilityMode.Strict);
    }

    private static byte[] Key(int i) => Text.UTF8.GetBytes($"k{i:D5}");

    /// <summary>Everything every database holds, as "database/box/key" → value.</summary>
    private static Dictionary<string, string> Contents(CrystalStore store)
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var info in store.ListDatabases())
        {
            var db = store.OpenDatabase(info.Name);
            using var snapshot = db.Snapshot();
            foreach (var box in db.Boxes.List().Select(i => i.Handle))
            {
                foreach (var row in snapshot.Scan(box, [], [], int.MaxValue))
                {
                    contents[$"{info.Name}/{box.Name}/{Text.UTF8.GetString(row.Key)}"] = Convert.ToBase64String(row.Value);
                }
            }
        }

        return contents;
    }

    private static long Position(CrystalStore store, CrystalDatabase db) =>
        store.Status().Databases.Single(d => d.Id == db.Id).LatestPosition;
}
