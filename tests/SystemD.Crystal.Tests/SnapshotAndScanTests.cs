// Ported from Highway: src/Highway.Server/Storage/Rocks/RocksDbSnapshot.cs
// Adapted for Crystal: bounded box scans and snapshot isolation (spec 002 T7, R6.4, R6.5).

using System;
using System.IO;
using System.Linq;
using Text = System.Text.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class SnapshotAndScanTests : IDisposable
{
    private readonly string _dir;

    public SnapshotAndScanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crystal-snap-" + Guid.NewGuid().ToString("N"));
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
    public void Snapshot_IsolatesReadsFromLaterWrites()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");
        var box = db.Boxes.Create("box1");

        using (var b = db.NewBatch())
        {
            b.Put(box, Text.UTF8.GetBytes("k1"), Text.UTF8.GetBytes("v1"));
            b.Commit();
        }

        // Take snapshot
        using var snapshot = db.Snapshot();

        // Write v2 after snapshot
        using (var b = db.NewBatch())
        {
            b.Put(box, Text.UTF8.GetBytes("k1"), Text.UTF8.GetBytes("v2"));
            b.Put(box, Text.UTF8.GetBytes("k2"), Text.UTF8.GetBytes("v2"));
            b.Commit();
        }

        // Snapshot still sees v1 and doesn't see k2
        var val1 = snapshot.Get(box, Text.UTF8.GetBytes("k1"));
        var val2 = snapshot.Get(box, Text.UTF8.GetBytes("k2"));
        Assert.Equal("v1", Text.UTF8.GetString(val1!));
        Assert.Null(val2);

        var rows = snapshot.Scan(box, [], [], limit: 10).ToList();
        Assert.Single(rows);
        Assert.Equal("k1", Text.UTF8.GetString(rows[0].Key));
        Assert.Equal("v1", Text.UTF8.GetString(rows[0].Value));
    }

    [Fact]
    public void Scan_NeverReturnsKeysOfAnotherBox_R65()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("testdb");

        var box1 = db.Boxes.Create("box1"); // Id 16
        var box2 = db.Boxes.Create("box2"); // Id 17 (adjacent next id!)

        // Populate both boxes
        using (var b = db.NewBatch())
        {
            for (var i = 0; i < 50; i++)
            {
                b.Put(box1, Text.UTF8.GetBytes($"key_{i:d3}"), Text.UTF8.GetBytes($"b1_val_{i}"));
                b.Put(box2, Text.UTF8.GetBytes($"key_{i:d3}"), Text.UTF8.GetBytes($"b2_val_{i}"));
            }
            b.Commit();
        }

        using var snap = db.Snapshot();

        // Scan box 1 with unbounded to/from and high limit
        var box1Rows = snap.Scan(box1, [], [], limit: 1000).ToList();
        Assert.Equal(50, box1Rows.Count);
        foreach (var row in box1Rows)
        {
            var valStr = Text.UTF8.GetString(row.Value);
            Assert.StartsWith("b1_val_", valStr);
        }

        // Scan box 2 with limit 20
        var box2Rows = snap.Scan(box2, [], [], limit: 20).ToList();
        Assert.Equal(20, box2Rows.Count);
        foreach (var row in box2Rows)
        {
            var valStr = Text.UTF8.GetString(row.Value);
            Assert.StartsWith("b2_val_", valStr);
        }

        // Bounded range inside box 1
        var rangeRows = snap.Scan(box1, Text.UTF8.GetBytes("key_010"), Text.UTF8.GetBytes("key_020"), limit: 100).ToList();
        Assert.Equal(10, rangeRows.Count);
        Assert.Equal("key_010", Text.UTF8.GetString(rangeRows.First().Key));
        Assert.Equal("key_019", Text.UTF8.GetString(rangeRows.Last().Key));
    }
}
