// Ported from Highway: src/Highway.Server/Storage/Boxes/Catalog.cs
// Adapted for Crystal: box catalogue persistence, lifecycle, rename, swap (spec 002 T4, T5, T5a, R2, R3, R4).

using System;
using System.IO;
using System.Linq;
using Text = System.Text.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class BoxCatalogTests : IDisposable
{
    private readonly string _dir;

    public BoxCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crystal-boxcat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // ignore temp cleanup
            }
        }
    }

    [Fact]
    public void T4_Catalogue_SurvivesCloseAndReopenExactly()
    {
        long generationAfterCreates;

        using (var store = CrystalStore.Open(_dir))
        {
            var db = store.CreateDatabase("testdb");

            var b1 = db.Boxes.Create("orders", BoxProfile.General);
            var b2 = db.Boxes.Create("events", BoxProfile.DeleteHeavy);

            Assert.Equal(16u, b1.Id);
            Assert.Equal(17u, b2.Id);

            var list = db.Boxes.List();
            Assert.Equal(2, list.Count);
            generationAfterCreates = db.Boxes.Generation;
        }

        // Reopen store and database
        using (var store = CrystalStore.Open(_dir))
        {
            var db = store.OpenDatabase("testdb");

            Assert.Equal(generationAfterCreates, db.Boxes.Generation);

            var b1 = db.Boxes.Find("orders");
            var b2 = db.Boxes.Find("events");

            Assert.NotNull(b1);
            Assert.NotNull(b2);
            Assert.Equal(16u, b1.Value.Id);
            Assert.Equal(17u, b2.Value.Id);

            var list = db.Boxes.List();
            Assert.Equal(2, list.Count);

            var info1 = list.First(b => b.Name == "orders");
            var info2 = list.First(b => b.Name == "events");

            Assert.Equal(16u, info1.Id);
            Assert.Equal(BoxProfile.General, info1.Profile);
            Assert.Equal(17u, info2.Id);
            Assert.Equal(BoxProfile.DeleteHeavy, info2.Profile);

            // New box allocated after reopen continues sequentially
            var b3 = db.Boxes.Create("audit");
            Assert.Equal(18u, b3.Id);
        }
    }

    [Fact]
    public void T5_Create_Find_List_Drop_Recreate()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");

        // 1. Create duplicate throws
        db.Boxes.Create("queue1");
        Assert.Throws<BoxNameTakenException>(() => db.Boxes.Create("queue1"));

        // 2. Put keys in queue1
        var b1 = db.Boxes.Find("queue1")!.Value;
        using (var batch = db.NewBatch())
        {
            batch.Put(b1, Text.UTF8.GetBytes("msg1"), Text.UTF8.GetBytes("hello"));
            batch.Put(b1, Text.UTF8.GetBytes("msg2"), Text.UTF8.GetBytes("world"));
            batch.Commit();
        }

        // Verify keys exist
        using (var snap = db.Snapshot())
        {
            Assert.NotNull(snap.Get(b1, Text.UTF8.GetBytes("msg1")));
            Assert.NotNull(snap.Get(b1, Text.UTF8.GetBytes("msg2")));
        }

        // 3. Drop queue1
        db.Boxes.Drop(b1);
        Assert.Null(db.Boxes.Find("queue1"));

        // 4. Old handle is refused after drop (R4.3)
        using (var batch = db.NewBatch())
        {
            batch.Put(b1, Text.UTF8.GetBytes("msg3"), Text.UTF8.GetBytes("fail"));
            Assert.Throws<BoxDroppedException>(() => batch.Commit());
        }

        // 5. Recreate queue1 with same name gets NEW ID (R2.2, R3.5)
        var b2 = db.Boxes.Create("queue1");
        Assert.NotEqual(b1.Id, b2.Id);
        Assert.True(b2.Id > b1.Id);

        // Keys of the dropped box are gone (R3.4)
        using (var snap = db.Snapshot())
        {
            Assert.Null(snap.Get(b2, Text.UTF8.GetBytes("msg1")));
            Assert.Null(snap.Get(b2, Text.UTF8.GetBytes("msg2")));
        }
    }

    [Fact]
    public void T5a_RenameAndSwap_AliasingAndHandleSurvivability()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");

        var boxA = db.Boxes.Create("box_a");
        var boxB = db.Boxes.Create("box_b");

        // Write to boxA
        using (var batch = db.NewBatch())
        {
            batch.Put(boxA, Text.UTF8.GetBytes("key1"), Text.UTF8.GetBytes("val1"));
            batch.Commit();
        }

        // 1. Rename boxA to box_c
        var boxC = db.Boxes.Rename(boxA, "box_c");
        Assert.Equal(boxA.Id, boxC.Id);
        Assert.Equal("box_c", boxC.Name);
        Assert.Null(db.Boxes.Find("box_a"));
        Assert.Equal(boxA.Id, db.Boxes.Find("box_c")?.Id);

        // Handle taken before rename (boxA) still writes to the box (R3.10)
        using (var batch = db.NewBatch())
        {
            batch.Put(boxA, Text.UTF8.GetBytes("key2"), Text.UTF8.GetBytes("val2"));
            batch.Commit();
        }

        // Read using new handle (boxC) sees the writes
        using (var snap = db.Snapshot())
        {
            var val1 = snap.Get(boxC, Text.UTF8.GetBytes("key1"));
            var val2 = snap.Get(boxC, Text.UTF8.GetBytes("key2"));
            Assert.Equal("val1", Text.UTF8.GetString(val1!));
            Assert.Equal("val2", Text.UTF8.GetString(val2!));
        }

        // 2. Swap box_c and box_b
        db.Boxes.Swap(boxC, boxB);

        // After swap, alias box_c points to boxB.Id, alias box_b points to boxC.Id
        Assert.Equal(boxB.Id, db.Boxes.Find("box_c")?.Id);
        Assert.Equal(boxC.Id, db.Boxes.Find("box_b")?.Id);

        // But original handles still address their original IDs (R3.10)
        using (var snap = db.Snapshot())
        {
            // boxA (and boxC) have Id=16, which is now aliased as "box_b"
            var val = snap.Get(boxA, Text.UTF8.GetBytes("key1"));
            Assert.Equal("val1", Text.UTF8.GetString(val!));
        }
    }
}
