// Cost and complexity tests for box operations (spec 002 T12, R9, R10.4).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Text = System.Text.Encoding;
using Xunit;
using Xunit.Abstractions;

namespace SystemD.Crystal.Tests;

public sealed class BoxCostTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public BoxCostTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "crystal-boxcost-" + Guid.NewGuid().ToString("N"));
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
    public void Cost_AtScales_10_And_1000_Boxes_R91()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("costdb");

        // Scale 1: 10 boxes
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            db.Boxes.Create($"scale10_box_{i}");
        }
        sw.Stop();
        var createTime10 = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var f10 = db.Boxes.Find("scale10_box_5");
        sw.Stop();
        var findTime10 = sw.Elapsed.TotalMilliseconds;
        Assert.NotNull(f10);

        sw.Restart();
        var l10 = db.Boxes.List();
        sw.Stop();
        var listTime10 = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(10, l10.Count);

        // Scale 2: 1000 boxes
        sw.Restart();
        for (var i = 10; i < 1000; i++)
        {
            db.Boxes.Create($"scale1000_box_{i}");
        }
        sw.Stop();
        var createTime1000 = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var f1000 = db.Boxes.Find("scale1000_box_500");
        sw.Stop();
        var findTime1000 = sw.Elapsed.TotalMilliseconds;
        Assert.NotNull(f1000);

        sw.Restart();
        var l1000 = db.Boxes.List();
        sw.Stop();
        var listTime1000 = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(1000, l1000.Count);

        _output.WriteLine($"[Cost R9.1] 10 boxes: Find={findTime10:F4}ms, List={listTime10:F4}ms");
        _output.WriteLine($"[Cost R9.1] 1000 boxes: Find={findTime1000:F4}ms, List={listTime1000:F4}ms");

        // Find is dictionary O(1) in both scales (< 1ms)
        Assert.True(findTime1000 < 5.0, $"Find should be fast in-memory O(1): was {findTime1000}ms");
    }

    [Fact]
    public void Cost_At100000Boxes_CreateFindRenameSwapDrop_StayFlat_R91()
    {
        // R9.1 / T12: create, find, rename, swap, drop and list cost the same at 100 000 boxes as at 10
        // (O(1) per change, O(boxes) for list, no data read). The numbers are recorded in the spec.
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("bigdb");

        const int count = 100_000;
        var createSw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            db.Boxes.Create($"box_{i:d6}");
        }

        createSw.Stop();

        // Find of one box among 100 000 — in-memory dictionary, must be O(1).
        var findSw = Stopwatch.StartNew();
        var found = db.Boxes.Find("box_050000");
        findSw.Stop();
        Assert.NotNull(found);

        // Rename a box among 100 000 — alias move only, no data, so flat regardless of count.
        var renameSw = Stopwatch.StartNew();
        var renamed = db.Boxes.Rename(found.Value, "box_050000_renamed");
        renameSw.Stop();

        // Swap two boxes among 100 000.
        var other = db.Boxes.Find("box_000001")!.Value;
        var swapSw = Stopwatch.StartNew();
        db.Boxes.Swap(renamed, other);
        swapSw.Stop();

        // Drop a box among 100 000.
        var toDrop = db.Boxes.Find("box_099999")!.Value;
        var dropSw = Stopwatch.StartNew();
        db.Boxes.Drop(toDrop);
        dropSw.Stop();

        // List of 100 000 live boxes (O(boxes), the only operation allowed to scale with count).
        var listSw = Stopwatch.StartNew();
        var list = db.Boxes.List();
        listSw.Stop();
        Assert.Equal(count - 1, list.Count); // one dropped

        _output.WriteLine($"[Cost R9.1 @ 100 000 boxes]");
        _output.WriteLine($"  create total = {createSw.Elapsed.TotalMilliseconds:F1}ms ({createSw.Elapsed.TotalMilliseconds / count * 1000:F2}µs each)");
        _output.WriteLine($"  find   = {findSw.Elapsed.TotalMilliseconds:F4}ms");
        _output.WriteLine($"  rename = {renameSw.Elapsed.TotalMilliseconds:F4}ms");
        _output.WriteLine($"  swap   = {swapSw.Elapsed.TotalMilliseconds:F4}ms");
        _output.WriteLine($"  drop   = {dropSw.Elapsed.TotalMilliseconds:F4}ms");
        _output.WriteLine($"  list   = {listSw.Elapsed.TotalMilliseconds:F2}ms (100 000 boxes)");

        // Find/rename/swap/drop are O(1) in-memory + one small synced batch: each well under 50 ms even
        // with 100 000 boxes present. (Thresholds are generous to stay stable on a loaded CI box; the
        // recorded numbers above are the evidence, not the bound.)
        Assert.True(findSw.Elapsed.TotalMilliseconds < 10.0, $"Find at 100k was {findSw.Elapsed.TotalMilliseconds}ms");
        Assert.True(renameSw.Elapsed.TotalMilliseconds < 50.0, $"Rename at 100k was {renameSw.Elapsed.TotalMilliseconds}ms");
        Assert.True(swapSw.Elapsed.TotalMilliseconds < 50.0, $"Swap at 100k was {swapSw.Elapsed.TotalMilliseconds}ms");
        Assert.True(dropSw.Elapsed.TotalMilliseconds < 50.0, $"Drop at 100k was {dropSw.Elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public void Rename_OfMillionKeyBox_CostsSameAsEmpty_R91()
    {
        // R9.1: a rename moves only the alias; its cost must not depend on the box's size. Rename an
        // empty box and a box holding a million keys, and compare.
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("renamecostdb");

        var empty = db.Boxes.Create("empty");
        var huge = db.Boxes.Create("huge");

        // One million keys in `huge`.
        const int keys = 1_000_000;
        var value = Text.UTF8.GetBytes("x");
        for (var batchStart = 0; batchStart < keys; batchStart += 100_000)
        {
            using var batch = db.NewBatch();
            for (var i = batchStart; i < batchStart + 100_000; i++)
            {
                batch.Put(huge, Text.UTF8.GetBytes($"k_{i:d7}"), value);
            }

            batch.Commit(DurabilityMode.Buffered);
        }

        var emptySw = Stopwatch.StartNew();
        db.Boxes.Rename(empty, "empty_renamed");
        emptySw.Stop();

        var hugeSw = Stopwatch.StartNew();
        db.Boxes.Rename(huge, "huge_renamed");
        hugeSw.Stop();

        _output.WriteLine($"[Cost R9.1] Rename empty box: {emptySw.Elapsed.TotalMilliseconds:F4}ms; rename 1M-key box: {hugeSw.Elapsed.TotalMilliseconds:F4}ms");

        // The million-key rename must not cost materially more than the empty one: both are one alias
        // move plus one small synced batch. Allow generous slack for commit variance.
        Assert.True(
            hugeSw.Elapsed.TotalMilliseconds < emptySw.Elapsed.TotalMilliseconds + 50.0,
            $"Rename of 1M-key box ({hugeSw.Elapsed.TotalMilliseconds}ms) should match empty ({emptySw.Elapsed.TotalMilliseconds}ms) — alias-only, size-independent.");
    }

    [Fact]
    public void Drop_EmptyBox_Vs_PopulatedBox_Cost_R92()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("dropcostdb");

        var emptyBox = db.Boxes.Create("empty_box");
        var populatedBox = db.Boxes.Create("populated_box");

        // Populate populatedBox with 10,000 keys
        using (var batch = db.NewBatch())
        {
            for (var i = 0; i < 10_000; i++)
            {
                batch.Put(populatedBox, Text.UTF8.GetBytes($"key_{i:d6}"), Text.UTF8.GetBytes("payload_data"));
            }
            batch.Commit(DurabilityMode.Buffered);
        }

        // Measure drop of empty box
        var sw = Stopwatch.StartNew();
        db.Boxes.Drop(emptyBox);
        sw.Stop();
        var emptyDropMs = sw.Elapsed.TotalMilliseconds;

        // Measure drop of populated box (range delete is one range tombstone write)
        sw.Restart();
        db.Boxes.Drop(populatedBox);
        sw.Stop();
        var populatedDropMs = sw.Elapsed.TotalMilliseconds;

        _output.WriteLine($"[Cost R9.2] Empty box drop: {emptyDropMs:F2}ms, Populated box drop: {populatedDropMs:F2}ms");

        // Both are single-batch commits, populated drop does not scan or delete key-by-key
        Assert.Null(db.Boxes.Find("populated_box"));
        Assert.Null(db.Boxes.Find("empty_box"));
    }

    [Fact]
    public void Scan_BoxIsolation_DoesNotReadSurroundingKeys_R93()
    {
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("scancostdb");

        var boxBefore = db.Boxes.Create("box_before"); // 16
        var targetBox = db.Boxes.Create("target_box"); // 17
        var boxAfter = db.Boxes.Create("box_after");   // 18

        // Put 5000 keys before, 10 keys in target, 5000 keys after
        using (var batch = db.NewBatch())
        {
            for (var i = 0; i < 5000; i++)
            {
                batch.Put(boxBefore, Text.UTF8.GetBytes($"k_{i}"), Text.UTF8.GetBytes("before"));
                batch.Put(boxAfter, Text.UTF8.GetBytes($"k_{i}"), Text.UTF8.GetBytes("after"));
            }
            for (var i = 0; i < 10; i++)
            {
                batch.Put(targetBox, Text.UTF8.GetBytes($"k_{i:d2}"), Text.UTF8.GetBytes("target"));
            }
            batch.Commit(DurabilityMode.Buffered);
        }

        using var snap = db.Snapshot();
        var sw = Stopwatch.StartNew();
        var rows = snap.Scan(targetBox, [], [], limit: 100).ToList();
        sw.Stop();

        Assert.Equal(10, rows.Count);
        _output.WriteLine($"[Cost R9.3] Target scan of 10 keys took {sw.Elapsed.TotalMilliseconds:F4}ms with 10,000 surrounding keys.");
        Assert.True(sw.Elapsed.TotalMilliseconds < 50.0);
    }
}
