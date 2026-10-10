// Ported from Highway: src/Highway.Server/Storage/Rocks/RocksDbBatch.cs
// Adapted for Crystal: batch atomicity, multi-box writes, read-your-writes overlay, R4 refusal (spec 002 T6).

using System;
using System.IO;
using System.Linq;
using Text = System.Text.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class BatchTests : IDisposable
{
    private readonly string _dir;

    public BatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crystal-batch-" + Guid.NewGuid().ToString("N"));
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
                // ignore
            }
        }
    }

    [Fact]
    public void EmptyBatch_SkipsWriteAndSync_R63()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");

        using var batch = db.NewBatch();
        // Committing an empty batch does not throw and writes nothing
        batch.Commit(DurabilityMode.Strict);
    }

    [Fact]
    public void ReadYourWrites_Overlay_WorksForPutsDeletesAndRanges()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");
        var box = db.Boxes.Create("mybox");

        // 1. Pre-populate committed keys
        using (var b0 = db.NewBatch())
        {
            b0.Put(box, Text.UTF8.GetBytes("k1"), Text.UTF8.GetBytes("committed1"));
            b0.Put(box, Text.UTF8.GetBytes("k2"), Text.UTF8.GetBytes("committed2"));
            b0.Put(box, Text.UTF8.GetBytes("k3"), Text.UTF8.GetBytes("committed3"));
            b0.Commit();
        }

        // 2. Open batch with pending modifications
        using var batch = db.NewBatch();

        // Pending put
        batch.Put(box, Text.UTF8.GetBytes("k4"), Text.UTF8.GetBytes("pending4"));
        // Pending overwrite
        batch.Put(box, Text.UTF8.GetBytes("k1"), Text.UTF8.GetBytes("overwritten1"));
        // Pending delete
        batch.Delete(box, Text.UTF8.GetBytes("k2"));

        // Check read-your-writes via Get
        var v1 = batch.Get(box, Text.UTF8.GetBytes("k1"));
        var v2 = batch.Get(box, Text.UTF8.GetBytes("k2"));
        var v3 = batch.Get(box, Text.UTF8.GetBytes("k3"));
        var v4 = batch.Get(box, Text.UTF8.GetBytes("k4"));

        Assert.Equal("overwritten1", Text.UTF8.GetString(v1!));
        Assert.Null(v2); // deleted in batch
        Assert.Equal("committed3", Text.UTF8.GetString(v3!)); // unmutated
        Assert.Equal("pending4", Text.UTF8.GetString(v4!)); // new put

        // Check read-your-writes via Scan
        var scanRows = batch.Scan(box, [], [], limit: 10).ToList();
        var keys = scanRows.Select(r => Text.UTF8.GetString(r.Key)).ToList();
        var vals = scanRows.Select(r => Text.UTF8.GetString(r.Value)).ToList();

        Assert.Equal(["k1", "k3", "k4"], keys);
        Assert.Equal(["overwritten1", "committed3", "pending4"], vals);

        // Range delete k3 to k5
        batch.DeleteRange(box, Text.UTF8.GetBytes("k3"), Text.UTF8.GetBytes("k5"));
        Assert.Null(batch.Get(box, Text.UTF8.GetBytes("k3")));
        Assert.Null(batch.Get(box, Text.UTF8.GetBytes("k4")));

        var scanAfterRange = batch.Scan(box, [], [], limit: 10).ToList();
        Assert.Single(scanAfterRange);
        Assert.Equal("k1", Text.UTF8.GetString(scanAfterRange[0].Key));
    }

    [Fact]
    public void MultiBoxCommit_Atomic_And_DroppedBoxRefusal_R41()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");

        var box1 = db.Boxes.Create("box1");
        var box2 = db.Boxes.Create("box2");

        using var batch = db.NewBatch();
        batch.Put(box1, Text.UTF8.GetBytes("b1_k"), Text.UTF8.GetBytes("v1"));
        batch.Put(box2, Text.UTF8.GetBytes("b2_k"), Text.UTF8.GetBytes("v2"));

        // Drop box2 BEFORE batch commits
        db.Boxes.Drop(box2);

        // Commit MUST be refused whole (R4.1)
        var ex = Assert.Throws<BoxDroppedException>(() => batch.Commit());
        Assert.Equal(box2.Id, ex.BoxId);

        // Verify NOTHING landed in box1 (atomicity: whole batch refused)
        using var snap = db.Snapshot();
        Assert.Null(snap.Get(box1, Text.UTF8.GetBytes("b1_k")));
    }
}
