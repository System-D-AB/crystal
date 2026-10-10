using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `boxes` (spec 008 R5.3) — catalogue cost: the time per Create, Find, Rename, Swap, Drop and List at 10, 1 000 and
/// (full) 100 000 live boxes, and the time to drop and rename a box of 0, 10 000 and (full) 1 000 000 keys. Each size
/// is verified against a model (List equals the model). Replaces 002 T12's unit-test numbers.
/// </summary>
internal sealed class BoxesWorkload : IWorkload
{
    public string Name => "boxes";

    public WorkloadResult Run(WorkloadContext context)
    {
        var sizes = context.Size == BenchSize.Quick ? new[] { 10, 1_000 } : [10, 1_000, 100_000];
        var keyCounts = context.Size == BenchSize.Quick ? new[] { 0, 10_000 } : [0, 10_000, 1_000_000];

        var cells = new List<CellResult>();
        var callRows = new List<IReadOnlyList<string>>();
        var sizeRows = new List<IReadOnlyList<string>>();
        var callJson = new List<IReadOnlyDictionary<string, object>>();
        var sizeJson = new List<IReadOnlyDictionary<string, object>>();
        var verifiedKeys = 0L;
        var allPassed = true;

        foreach (var size in sizes)
        {
            using var temp = TempStore.Create(context.DataParent);
            var db = temp.Store.CreateDatabase("bench");
            var model = new HashSet<string>(StringComparer.Ordinal);

            // Grow the catalogue to `size` live boxes.
            for (var i = 0; i < size; i++)
            {
                var name = $"box_{i:d7}";
                db.Boxes.Create(name);
                model.Add(name);
            }

            // Time each catalogue call over at least 100 operations at this size (R5.3).
            var createUs = TimeCreate(db, model, size, 100);
            var findUs = TimeFind(db, size, 100);
            var renameUs = TimeRename(db, model, size, 100);
            var swapUs = TimeSwap(db, size, 100);
            var listUs = TimeList(db, 100);
            var dropUs = TimeDrop(db, model, size, 100);

            callRows.Add([size.ToString("N0"), Fmt(createUs), Fmt(findUs), Fmt(renameUs), Fmt(swapUs), Fmt(dropUs), Fmt(listUs)]);
            callJson.Add(new Dictionary<string, object>
            {
                ["liveBoxes"] = size,
                ["createUs"] = createUs,
                ["findUs"] = findUs,
                ["renameUs"] = renameUs,
                ["swapUs"] = swapUs,
                ["dropUs"] = dropUs,
                ["listUs"] = listUs,
            });

            // Verify: List equals the model.
            var live = db.Boxes.List().Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            var matches = live.SetEquals(model);
            allPassed &= matches;
        }

        // Drop and rename against box size (R5.3).
        foreach (var keys in keyCounts)
        {
            using var temp = TempStore.Create(context.DataParent);
            var db = temp.Store.CreateDatabase("bench");
            var box = db.Boxes.Create("sized");
            FillBox(db, box, keys);
            verifiedKeys += keys;

            var renameSw = Stopwatch.StartNew();
            var renamed = db.Boxes.Rename(box, "sized_renamed");
            renameSw.Stop();

            var dropSw = Stopwatch.StartNew();
            db.Boxes.Drop(renamed);
            dropSw.Stop();

            sizeRows.Add([keys.ToString("N0"), Fmt(renameSw.Elapsed.TotalMilliseconds * 1000), Fmt(dropSw.Elapsed.TotalMilliseconds * 1000)]);
            sizeJson.Add(new Dictionary<string, object>
            {
                ["keysInBox"] = keys,
                ["renameUs"] = renameSw.Elapsed.TotalMilliseconds * 1000,
                ["dropUs"] = dropSw.Elapsed.TotalMilliseconds * 1000,
            });
        }

        var callTable = new BenchTable(
            "per-call cost by live-box count (µs, median of ≥100 calls)",
            ["Live boxes", "Create", "Find", "Rename", "Swap", "Drop", "List"],
            callRows,
            cells,
            callJson);

        var sizeTable = new BenchTable(
            "rename and drop by box size (µs) — alias-only, so size-independent",
            ["Keys in box", "Rename", "Drop"],
            sizeRows,
            cells,
            sizeJson);

        var summary = allPassed
            ? $"verified the catalogue (List equals the model) at {string.Join(", ", sizes.Select(s => s.ToString("N0")))} boxes; filled {verifiedKeys:N0} keys"
            : "FAILED: List did not equal the model at some size";

        return new WorkloadResult(Name, [callTable, sizeTable], new VerificationOutcome(allPassed, summary));
    }

    private static double TimeCreate(CrystalDatabase db, HashSet<string> model, int startIndex, int operations)
    {
        var times = new double[operations];
        for (var i = 0; i < operations; i++)
        {
            var name = $"created_{startIndex}_{i}";
            var sw = Stopwatch.GetTimestamp();
            db.Boxes.Create(name);
            times[i] = Micros(sw);
            model.Add(name);
        }

        return Median(times);
    }

    private static double TimeFind(CrystalDatabase db, int size, int operations)
    {
        var times = new double[operations];
        var random = new Random(1);
        for (var i = 0; i < operations; i++)
        {
            var name = $"box_{random.Next(size):d7}";
            var sw = Stopwatch.GetTimestamp();
            _ = db.Boxes.Find(name);
            times[i] = Micros(sw);
        }

        return Median(times);
    }

    private static double TimeRename(CrystalDatabase db, HashSet<string> model, int size, int operations)
    {
        var times = new double[operations];
        for (var i = 0; i < operations; i++)
        {
            var from = $"box_{i:d7}";
            var to = $"renamed_{i:d7}";
            var handle = db.Boxes.Find(from);
            if (handle is null)
            {
                times[i] = 0;
                continue;
            }

            var sw = Stopwatch.GetTimestamp();
            db.Boxes.Rename(handle.Value, to);
            times[i] = Micros(sw);
            model.Remove(from);
            model.Add(to);
            // rename back so later indices stay valid
            db.Boxes.Rename(db.Boxes.Find(to)!.Value, from);
            model.Remove(to);
            model.Add(from);
        }

        return Median(times);
    }

    private static double TimeSwap(CrystalDatabase db, int size, int operations)
    {
        var times = new double[operations];
        for (var i = 0; i + 1 < operations * 2 && i / 2 < operations; i += 2)
        {
            var a = db.Boxes.Find($"box_{i % size:d7}");
            var b = db.Boxes.Find($"box_{(i + 1) % size:d7}");
            if (a is null || b is null || a.Value.Id == b.Value.Id)
            {
                continue;
            }

            var sw = Stopwatch.GetTimestamp();
            db.Boxes.Swap(a.Value, b.Value);
            times[i / 2] = Micros(sw);
            db.Boxes.Swap(a.Value, b.Value); // swap back
        }

        return Median(times.Where(t => t > 0).DefaultIfEmpty(0).ToArray());
    }

    private static double TimeList(CrystalDatabase db, int operations)
    {
        var times = new double[operations];
        for (var i = 0; i < operations; i++)
        {
            var sw = Stopwatch.GetTimestamp();
            _ = db.Boxes.List();
            times[i] = Micros(sw);
        }

        return Median(times);
    }

    private static double TimeDrop(CrystalDatabase db, HashSet<string> model, int size, int operations)
    {
        // Create disposable boxes to drop, so the catalogue size returns to `size` after.
        var times = new double[operations];
        for (var i = 0; i < operations; i++)
        {
            var name = $"todrop_{size}_{i}";
            var box = db.Boxes.Create(name);
            model.Add(name);
            var sw = Stopwatch.GetTimestamp();
            db.Boxes.Drop(box);
            times[i] = Micros(sw);
            model.Remove(name);
        }

        return Median(times);
    }

    private static void FillBox(CrystalDatabase db, Box box, int keys)
    {
        const int perBatch = 50_000;
        var written = 0;
        while (written < keys)
        {
            using var batch = db.NewBatch();
            var upTo = Math.Min(written + perBatch, keys);
            for (var i = written; i < upTo; i++)
            {
                var key = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(key, i);
                batch.Put(box, key, Values.Make(0, i, 16));
            }

            batch.Commit(DurabilityMode.Buffered);
            written = upTo;
        }
    }

    private static double Micros(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency;

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static string Fmt(double micros) => micros < 1 ? $"{micros:N3}" : $"{micros:N1}";
}
