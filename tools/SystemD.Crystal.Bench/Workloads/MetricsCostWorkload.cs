using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `metrics-cost` (spec 008 R5.2) — the cost of observability: the commit workload's Buffered and Strict cells at 1
/// and 16 writers, run with no MeterListener and then with a listener on every SystemD.Crystal instrument. Reports
/// throughput and latency of both and the difference, and allocations per commit with and without the listener.
/// Closes 007 T8.
/// </summary>
internal sealed class MetricsCostWorkload : IWorkload
{
    public string Name => "metrics-cost";

    public WorkloadResult Run(WorkloadContext context)
    {
        var modes = new[] { DurabilityMode.Buffered, DurabilityMode.Strict };
        var writerCounts = new[] { 1, 16 };

        var rows = new List<IReadOnlyList<string>>();
        var cells = new List<CellResult>();

        foreach (var mode in modes)
        {
            foreach (var writers in writerCounts)
            {
                var without = RunCell(context, mode, writers, withListener: false);
                var with = RunCell(context, mode, writers, withListener: true);
                cells.Add(without);
                cells.Add(with);

                var diffPct = without.OpsPerSecMedian > 0
                    ? (without.OpsPerSecMedian - with.OpsPerSecMedian) / without.OpsPerSecMedian * 100
                    : 0;

                var allocWithout = AllocPerCommit(context, mode, withListener: false);
                var allocWith = AllocPerCommit(context, mode, withListener: true);

                rows.Add([
                    mode.ToString(),
                    writers.ToString(),
                    $"{without.OpsPerSecMedian:N0}",
                    $"{with.OpsPerSecMedian:N0}",
                    $"{diffPct:N1}%",
                    $"{without.P99Us:N1}",
                    $"{with.P99Us:N1}",
                    $"{allocWithout:N0}",
                    $"{allocWith:N0}",
                ]);
            }
        }

        var table = new BenchTable(
            "commit with and without a MeterListener",
            ["Durability", "Writers", "No listener/s", "Listener/s", "Δ throughput", "p99 µs (none)", "p99 µs (listener)", "alloc B/commit (none)", "alloc B/commit (listener)"],
            rows,
            cells);

        return new WorkloadResult(Name, [table], new VerificationOutcome(true, "metrics-cost compares throughput, latency and allocation with and without a listener"));
    }

    private static CellResult RunCell(WorkloadContext context, DurabilityMode mode, int writers, bool withListener)
    {
        var label = withListener ? "listener" : "no-listener";
        var spec = CellSpec.For(context.Size, "metrics-cost", $"{label} {mode}", mode, writers);

        return Cell.Run(spec, () =>
        {
            var temp = TempStore.Create(context.DataParent);
            var db = temp.Store.CreateDatabase("bench");
            MeterListener? listener = withListener ? MakeListener(temp.Store.NodeId) : null;

            PrepareWriter prepare = index =>
            {
                var box = db.Boxes.Create($"w{index}");
                return new State(db, box, mode, index);
            };

            OneOperation operation = s =>
            {
                var st = (State)s;
                using var batch = st.Db.NewBatch();
                batch.Put(st.Box, st.NextKey(), Values.Make(st.Index, st.Seq, 100));
                batch.Commit(st.Mode);
            };

            // Dispose both the listener and the store when the cell's repetition ends.
            return (prepare, operation, new Composite(temp, listener));
        });
    }

    private static double AllocPerCommit(WorkloadContext context, DurabilityMode mode, bool withListener)
    {
        using var temp = TempStore.Create(context.DataParent);
        var db = temp.Store.CreateDatabase("alloc");
        using var listener = withListener ? MakeListener(temp.Store.NodeId) : null;

        return Cell.AllocatedBytesPerOp(
            _ =>
            {
                var box = db.Boxes.Create("w0");
                return new State(db, box, mode, 0);
            },
            s =>
            {
                var st = (State)s;
                using var batch = st.Db.NewBatch();
                batch.Put(st.Box, st.NextKey(), Values.Make(0, st.Seq, 100));
                batch.Commit(st.Mode);
            },
            operations: 5_000);
    }

    private static MeterListener MakeListener(string nodeId)
    {
        var listener = new MeterListener();
        long counter = 0;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "SystemD.Crystal" && MeterTaggedWithNode(instrument.Meter, nodeId))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        // Callbacks do no work beyond a local add, so the cost measured is Crystal's recording, not the listener's.
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref counter, m));
        listener.SetMeasurementEventCallback<int>((_, m, _, _) => Interlocked.Add(ref counter, m));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref counter));
        listener.Start();
        return listener;
    }

    private static bool MeterTaggedWithNode(Meter meter, string nodeId)
    {
        if (meter.Tags is null)
        {
            return false;
        }

        foreach (var tag in meter.Tags)
        {
            if (tag.Key == "crystal.node" && tag.Value is string s && s == nodeId)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class State(CrystalDatabase db, Box box, DurabilityMode mode, int index)
    {
        public CrystalDatabase Db => db;
        public Box Box => box;
        public DurabilityMode Mode => mode;
        public int Index => index;
        public long Seq { get; private set; }

        public byte[] NextKey()
        {
            var key = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(0, 4), index);
            BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(4, 8), Seq++);
            return key;
        }
    }

    private sealed class Composite(IDisposable first, IDisposable? second) : IDisposable
    {
        public void Dispose()
        {
            second?.Dispose();
            first.Dispose();
        }
    }
}
