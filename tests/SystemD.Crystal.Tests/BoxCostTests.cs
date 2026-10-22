// Cost and complexity tests for box operations (spec 002 T12, R9, R10.4).
// Spec 015 T2: shown structurally (the database's RocksDB sequence counts the entries an operation writes; the scan-steps
// histogram counts the iterator steps a scan takes), never by wall-clock thresholds, which measure the machine's disk
// and failed or hung on a slow Linux runner. The 100 000-box run is a long cost probe (spec 004 R4.1): tagged
// `Category=LongProbe`, it is left out of CI's everyday run and run by the release gate.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    public void Cost_AtScales_10_And_1000_Boxes_WriteTheSameEntries_R91()
    {
        // R9.1: each catalogue change writes only its own keys (R2.3), so its cost does not grow with the number of boxes;
        // find and list read the in-memory catalogue and write nothing (R2.4).
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("costdb");

        Grow(db, 10);
        var at10 = Lifecycle(db, "probe10");

        Grow(db, 1000);
        var at1000 = Lifecycle(db, "probe1000");

        Assert.Equal(at10, at1000);
        Assert.Equal(0UL, at10["find"]);
        Assert.Equal(0UL, at10["list"]);
        _output.WriteLine("[Cost R9.1] entries per operation: " + string.Join(", ", at10.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public void Rename_OfMillionKeyBox_WritesWhatAnEmptyBoxRenameWrites_R91()
    {
        // R9.1: a rename moves only the alias; it writes the same catalogue entries whatever the box holds.
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("renamecostdb");

        var empty = db.Boxes.Create("empty");
        var huge = db.Boxes.Create("huge");
        Fill(db, huge, 1_000_000);

        var emptyRename = Entries(db, () => db.Boxes.Rename(empty, "empty_renamed"));
        var hugeRename = Entries(db, () => db.Boxes.Rename(huge, "huge_renamed"));

        Assert.Equal(emptyRename, hugeRename);
        _output.WriteLine($"[Cost R9.1] rename writes {hugeRename} entries for an empty box and for a 1 000 000-key box");
    }

    [Fact]
    public void Drop_OfPopulatedBox_WritesWhatAnEmptyBoxDropWrites_R92()
    {
        // R9.2: a drop is one range delete, not a delete per key: the same entries for an empty box and a full one.
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("dropcostdb");

        var emptyBox = db.Boxes.Create("empty_box");
        var populatedBox = db.Boxes.Create("populated_box");
        Fill(db, populatedBox, 10_000);

        var emptyDrop = Entries(db, () => db.Boxes.Drop(emptyBox));
        var populatedDrop = Entries(db, () => db.Boxes.Drop(populatedBox));

        Assert.Equal(emptyDrop, populatedDrop);
        Assert.Null(db.Boxes.Find("populated_box"));
        Assert.Null(db.Boxes.Find("empty_box"));
        _output.WriteLine($"[Cost R9.2] drop writes {populatedDrop} entries for an empty box and for a 10 000-key box");
    }

    [Fact]
    public void Scan_BoxIsolation_StepsOnlyOverItsOwnKeys_R93()
    {
        // R9.3: a scan of one box never reads another box's keys: 10 keys between two boxes of 5 000 take at most 11 native
        // iterator steps (the keys and the step that finds the end of the box), not thousands.
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("scancostdb");

        var boxBefore = db.Boxes.Create("box_before");
        var targetBox = db.Boxes.Create("target_box");
        var boxAfter = db.Boxes.Create("box_after");
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

        var steps = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Only this store's meter: the test classes run in parallel, each with stores of its own.
            if (instrument.Name == "crystal.scan.steps"
                && instrument.Meter.Tags?.Any(t => t.Key == "crystal.node" && Equals(t.Value, store.NodeId)) == true)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => steps.Enqueue(value));
        listener.Start();

        using (var snap = db.Snapshot())
        {
            Assert.Equal(10, snap.Scan(targetBox, [], [], limit: 100).Count());
        }

        var recorded = Assert.Single(steps);
        Assert.InRange(recorded, 10, 11);
        _output.WriteLine($"[Cost R9.3] a scan of 10 keys between 10 000 others took {recorded} steps");
    }

    [Fact]
    [Trait("Category", "LongProbe")]
    public void Cost_At100000Boxes_WriteTheSameEntriesAsAt10_R91()
    {
        // R9.1 at 100 000 boxes: a long cost probe (spec 004 R4.1), run by the release gate, not by CI's everyday run.
        // Creating the boxes is 100 000 synced catalogue writes, so its length is the disk's; the result is structural.
        using var store = CrystalStore.Open(_dir);
        var db = store.CreateDatabase("bigdb");

        Grow(db, 10);
        var at10 = Lifecycle(db, "probe10");

        var watch = Stopwatch.StartNew();
        Grow(db, 100_000);
        _output.WriteLine($"[Cost R9.1 @ 100 000 boxes] created in {watch.Elapsed.TotalSeconds:F0} s (recorded, not asserted)");
        var at100000 = Lifecycle(db, "probe100000");

        Assert.Equal(at10, at100000);
        Assert.Equal(100_000, db.Boxes.List().Count(b => b.Name.StartsWith("grow_", StringComparison.Ordinal)));
    }

    /// <summary>Creates boxes until the database holds <paramref name="count"/> named <c>grow_*</c>.</summary>
    private static void Grow(CrystalDatabase db, int count)
    {
        var existing = db.Boxes.List().Count(b => b.Name.StartsWith("grow_", StringComparison.Ordinal));
        for (var i = existing; i < count; i++)
        {
            db.Boxes.Create($"grow_{i:d6}");
        }
    }

    /// <summary>The entries each catalogue operation writes, on a fresh pair of boxes named after <paramref name="tag"/>.</summary>
    private static Dictionary<string, ulong> Lifecycle(CrystalDatabase db, string tag)
    {
        var costs = new Dictionary<string, ulong>();
        Box probe = default;
        costs["create"] = Entries(db, () => probe = db.Boxes.Create(tag));
        var partner = db.Boxes.Create(tag + "-partner");
        costs["find"] = Entries(db, () => Assert.NotNull(db.Boxes.Find(tag)));
        costs["list"] = Entries(db, () => db.Boxes.List());
        costs["rename"] = Entries(db, () => probe = db.Boxes.Rename(probe, tag + "-renamed"));
        costs["swap"] = Entries(db, () => db.Boxes.Swap(probe, partner));
        costs["drop"] = Entries(db, () => db.Boxes.Drop(probe));
        db.Boxes.Drop(partner);
        return costs;
    }

    private static ulong Entries(CrystalDatabase db, Action operation)
    {
        var before = db.Slot.LatestSequence;
        operation();
        return db.Slot.LatestSequence - before;
    }

    private static void Fill(CrystalDatabase db, Box box, int keys)
    {
        var value = Text.UTF8.GetBytes("x");
        for (var start = 0; start < keys; start += 100_000)
        {
            using var batch = db.NewBatch();
            for (var i = start; i < Math.Min(keys, start + 100_000); i++)
            {
                batch.Put(box, Text.UTF8.GetBytes($"k_{i:d7}"), value);
            }

            batch.Commit(DurabilityMode.Buffered);
        }
    }
}
