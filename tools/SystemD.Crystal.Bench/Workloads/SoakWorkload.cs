// Ported from StowDB: tests/Stow.Storage.Measurements/CompactionSoakMeasurement.cs (branch rocksdb). Carried: the
// per-interval throughput and size series. Changed per design § 5, R5.5: time-based (not a fixed 100k-doc count);
// through Crystal's public API and Status() rather than raw RocksDB; and real stall detection via
// Status().Storage.WriteStopped — StowDB's left "stalls = 0 // Check RocksDB stats if available"; plus verification.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `soak` (spec 008 R5.5) — sustained writes: Buffered commits of 1 000-byte values for a fixed time, reporting per
/// 10 % interval the writes per second, the Status() storage figures (SST files and size, pending compaction,
/// memtable deletes) and whether a write stall was seen. Verifies the final key count and a random sample of values.
/// </summary>
internal sealed class SoakWorkload : IWorkload
{
    public string Name => "soak";

    public WorkloadResult Run(WorkloadContext context)
    {
        var duration = context.Size == BenchSize.Quick ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10);

        using var temp = TempStore.Create(context.DataParent);
        var db = temp.Store.CreateDatabase("soak");
        var box = db.Boxes.Create("soak");

        var rows = new List<IReadOnlyList<string>>();
        var jsonObjects = new List<IReadOnlyDictionary<string, object>>();
        var intervalLength = duration / 10;
        long written = 0;
        var stallSeenEver = false;

        var total = Stopwatch.StartNew();
        for (var interval = 0; interval < 10; interval++)
        {
            var intervalStart = Stopwatch.StartNew();
            long intervalWrites = 0;
            while (intervalStart.Elapsed < intervalLength)
            {
                using var batch = db.NewBatch();
                batch.Put(box, SeqKey(written), Values.Make(0, written, 1_000));
                batch.Commit(DurabilityMode.Buffered);
                written++;
                intervalWrites++;
            }

            var status = temp.Store.Status();
            var dbStatus = status.Databases.FirstOrDefault(d => d.Id == db.Id);
            var storage = dbStatus?.Storage;
            var stall = storage?.WriteStopped ?? false;
            stallSeenEver |= stall;

            var writesPerSec = intervalWrites / intervalStart.Elapsed.TotalSeconds;
            rows.Add([
                $"{(interval + 1) * 10}%",
                $"{writesPerSec:N0}",
                $"{storage?.SstFiles ?? 0:N0}",
                FormatBytes(storage?.SstSize ?? 0),
                FormatBytes(storage?.PendingCompactionBytes ?? 0),
                $"{storage?.MemtableDeletes ?? 0:N0}",
                stall ? "yes" : "no",
            ]);

            jsonObjects.Add(new Dictionary<string, object>
            {
                ["percent"] = (interval + 1) * 10,
                ["writesPerSec"] = writesPerSec,
                ["sstFiles"] = storage?.SstFiles ?? 0,
                ["sstSizeBytes"] = storage?.SstSize ?? 0,
                ["pendingCompactionBytes"] = storage?.PendingCompactionBytes ?? 0,
                ["memtableDeletes"] = storage?.MemtableDeletes ?? 0,
                ["stall"] = stall ? "yes" : "no",
            });
        }

        total.Stop();
        temp.Store.SyncAll();

        // Verify: the final key count, and a random sample of 1 000 values.
        var verified = Verify(db, box, written, out var summary);

        var table = new BenchTable(
            $"Buffered, 1 kB values, {duration.TotalSeconds:N0} s",
            ["Progress", "Writes/s", "SST files", "SST size", "Pending compaction", "Memtable deletes", "Stall"],
            rows,
            [],
            jsonObjects);

        return new WorkloadResult(Name, [table], new VerificationOutcome(verified, summary));
    }

    private static bool Verify(CrystalDatabase db, Box box, long written, out string summary)
    {
        using var snap = db.Snapshot();

        // Count by paging the whole box.
        long count = 0;
        byte[] from = [];
        const int page = 50_000;
        while (true)
        {
            var batch = snap.Scan(box, from, [], page).ToList();
            if (batch.Count == 0)
            {
                break;
            }

            count += batch.Count;
            if (batch.Count < page)
            {
                break;
            }

            var last = batch[^1].Key;
            from = new byte[last.Length + 1];
            last.CopyTo(from, 0);
        }

        if (count != written)
        {
            summary = $"FAILED: expected {written:N0} keys, found {count:N0}";
            return false;
        }

        // Sample 1 000 random keys and check their values.
        var random = new Random(7);
        var sample = Math.Min(1_000, written);
        for (var i = 0; i < sample; i++)
        {
            var n = random.NextInt64(written);
            var actual = snap.Get(box, SeqKey(n));
            var expected = Values.Make(0, n, 1_000);
            if (actual is null || !actual.AsSpan().SequenceEqual(expected))
            {
                summary = $"FAILED: value for key {n} differs or is missing";
                return false;
            }
        }

        summary = $"verified {written:N0} keys and a sample of {sample:N0} values";
        return true;
    }

    private static byte[] SeqKey(long n)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, n);
        return key;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
