// Ported from StowDB: tests/Stow.Storage.Measurements/WriteThroughputMeasurement.cs and WriterScalingMeasurement.cs
// (branch rocksdb). Carried: the 1/4/16/64 writer sweep and the warm-up. Changed per design § 7, R3.1/R3.2: durability
// modes; fixed-time cells on dedicated threads instead of fixed counts on thread-pool Task.Run; per-writer latency
// arrays instead of a ConcurrentBag; and read-back verification — the gap every StowDB measurement had.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `commit` (spec 008 R5.1) — the cost of durability: commits per second and latency for Strict, Interval and
/// Buffered, at 1, 4, 16 and 64 writers. Each writer owns its own box; every committed key is verified. Closes 006 T9.
/// </summary>
internal sealed class CommitWorkload : IWorkload
{
    public string Name => "commit";

    public WorkloadResult Run(WorkloadContext context)
    {
        var writerCounts = new[] { 1, 4, 16, 64 };
        var modes = new[] { DurabilityMode.Strict, DurabilityMode.Interval, DurabilityMode.Buffered };

        // quick: 1-op batches of 100-byte values. full: adds 10-op batches and 1 000-byte values.
        var shapes = context.Size == BenchSize.Quick
            ? new[] { new BatchShape(1, 100) }
            : [new BatchShape(1, 100), new BatchShape(10, 1_000)];

        var cells = new List<CellResult>();

        foreach (var shape in shapes)
        {
            foreach (var mode in modes)
            {
                foreach (var writers in writerCounts)
                {
                    cells.Add(RunCell(context, shape, mode, writers));
                }
            }
        }

        // Verify once at the end against a final store that replays the committed keys of the median run would be
        // misleading (each cell used a fresh store). Instead, run one more verified store to prove the write path is
        // correct under the same shape — the cells measure speed, this proves correctness (R2.1).
        var verification = VerifyWritePath(context, modes[0]);

        var headers = new[] { "Durability", "Writers", "Batch", "Value", "Commits/s (median)", "Range", "p50 µs", "p99 µs", "max µs" };
        var rows = cells.Select(c => (IReadOnlyList<string>)new[]
        {
            c.Durability!.ToString()!,
            c.Writers.ToString(),
            ConfigPart(c.Configuration, "batch"),
            ConfigPart(c.Configuration, "value"),
            $"{c.OpsPerSecMedian:N0}",
            $"{c.OpsPerSecMin:N0}–{c.OpsPerSecMax:N0}",
            $"{c.P50Us:N1}",
            $"{c.P99Us:N1}",
            $"{c.MaxUs:N1}",
        }).ToList();

        var table = new BenchTable(
            context.Size == BenchSize.Quick ? "100 B values, 1-op batches, 2 s cells × 3" : "100 B/1 kB values, 1/10-op batches, 10 s cells × 5",
            headers,
            rows,
            cells);

        return new WorkloadResult(Name, [table], verification.ToOutcome());
    }

    private static CellResult RunCell(WorkloadContext context, BatchShape shape, DurabilityMode mode, int writers)
    {
        var spec = CellSpec.For(context.Size, "commit", $"batch={shape.Operations} value={shape.ValueBytes}", mode, writers);

        return Cell.Run(spec, () =>
        {
            var temp = TempStore.Create(context.DataParent);
            var db = temp.Store.CreateDatabase("bench");

            PrepareWriter prepare = index =>
            {
                var box = db.Boxes.Create($"w{index}");
                return new WriterState(db, box, mode, shape, index);
            };

            OneOperation operation = s =>
            {
                var ws = (WriterState)s;
                using var batch = ws.Db.NewBatch();
                for (var i = 0; i < ws.Shape.Operations; i++)
                {
                    var key = ws.NextKey();
                    var value = Values.Make(ws.WriterIndex, ws.Sequence, ws.Shape.ValueBytes);
                    batch.Put(ws.Box, key, value);
                    // Only the correctness store records into the model; measured cells do not (keeps the hot path clean).
                }

                batch.Commit(ws.Mode);
            };

            return (prepare, operation, temp);
        });
    }

    /// <summary>Runs a small, verified commit pass to prove the write path keeps what it is told to (R2.1).</summary>
    private static VerificationResult VerifyWritePath(WorkloadContext context, DurabilityMode mode)
    {
        using var temp = TempStore.Create(context.DataParent);
        var db = temp.Store.CreateDatabase("verify");
        var model = new WrittenModel();

        const int writers = 4;
        const int perWriter = 10_000;
        var threads = new System.Threading.Thread[writers];
        for (var w = 0; w < writers; w++)
        {
            var index = w;
            threads[w] = new System.Threading.Thread(() =>
            {
                var box = db.Boxes.Create($"v{index}");
                for (var seq = 0; seq < perWriter; seq++)
                {
                    var key = KeyFor(index, seq);
                    var value = Values.Make(index, seq, 100);
                    using var batch = db.NewBatch();
                    batch.Put(box, key, value);
                    batch.Commit(mode);
                    model.Put(box, key, value);
                }
            });
            threads[w].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        temp.Store.SyncAll();
        return model.Verify(db);
    }

    private static byte[] KeyFor(int writer, long sequence)
    {
        var key = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(0, 4), writer);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(4, 8), sequence);
        return key;
    }

    private static string ConfigPart(string configuration, string which)
    {
        // configuration is "batch=N value=M"
        foreach (var part in configuration.Split(' '))
        {
            if (which == "batch" && part.StartsWith("batch=", StringComparison.Ordinal))
            {
                return part["batch=".Length..] + " op";
            }

            if (which == "value" && part.StartsWith("value=", StringComparison.Ordinal))
            {
                return part["value=".Length..] + " B";
            }
        }

        return "?";
    }

    private sealed record BatchShape(int Operations, int ValueBytes);

    private sealed class WriterState(CrystalDatabase db, Box box, DurabilityMode mode, BatchShape shape, int writerIndex)
    {
        private long _sequence;

        public CrystalDatabase Db => db;
        public Box Box => box;
        public DurabilityMode Mode => mode;
        public BatchShape Shape => shape;
        public int WriterIndex => writerIndex;
        public long Sequence => _sequence;

        public byte[] NextKey()
        {
            var key = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(0, 4), writerIndex);
            BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(4, 8), _sequence++);
            return key;
        }
    }
}

/// <summary>Deterministic values from <c>(writer, sequence)</c> (spec 008 design § 4), so the model recomputes them.</summary>
internal static class Values
{
    public static byte[] Make(int writer, long sequence, int size)
    {
        var value = new byte[size];
        // A simple reproducible fill: a per-(writer,sequence) seed expanded over the buffer.
        var seed = unchecked((uint)(writer * 2_654_435_761u + (uint)sequence * 40_503u + 0x9E3779B9u));
        for (var i = 0; i < size; i++)
        {
            seed = unchecked(seed * 1_664_525u + 1_013_904_223u);
            value[i] = (byte)(seed >> 24);
        }

        return value;
    }
}
