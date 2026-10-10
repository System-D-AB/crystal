// Ported from StowDB: tests/Stow.Storage.Measurements/MeasurementArtifact.cs (branch rocksdb) — the percentile-by-
// index with clamp is carried as-is. The method is the CORRECTED one this spec requires (design § 3, R3): StowDB's
// WriteThroughputMeasurement ran fixed counts on thread-pool Task.Run with a ConcurrentBag; this uses fixed-time
// cells, dedicated threads behind a barrier, and per-writer latency arrays merged after the run.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench;

/// <summary>One timed configuration's result (spec 008 R3, design § 3). Numbers carry the durability mode (R4.2).</summary>
internal sealed record CellResult(
    string Workload,
    string Configuration,
    DurabilityMode? Durability,
    int Writers,
    double OpsPerSecMedian,
    double OpsPerSecMin,
    double OpsPerSecMax,
    double P50Us,
    double P99Us,
    double MaxUs,
    long Operations,
    double? AllocatedBytesPerOp);

/// <summary>What one writer thread does, bound to its own state before the barrier.</summary>
internal delegate void OneOperation(object writerState);

/// <summary>How to prepare a writer's state (its box handle, counters, random) on its own thread.</summary>
internal delegate object PrepareWriter(int writerIndex);

/// <summary>The parameters of a cell (spec 008 R3.1, R3.3).</summary>
internal sealed record CellSpec(
    string Workload,
    string Configuration,
    DurabilityMode? Durability,
    int Writers,
    TimeSpan WarmUp,
    TimeSpan Measured,
    int Repetitions)
{
    public static CellSpec For(
        BenchSize size,
        string workload,
        string configuration,
        DurabilityMode? durability,
        int writers)
    {
        var warmUp = size == BenchSize.Quick ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(1);
        var measured = size == BenchSize.Quick ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10);
        var reps = size == BenchSize.Quick ? 3 : 5;
        return new CellSpec(workload, configuration, durability, writers, warmUp, measured, reps);
    }
}

/// <summary>
/// Runs a cell (spec 008 R3): a warm-up, then a fixed measured window, with concurrent writers on dedicated threads
/// started together behind a barrier. Each repetition is a fresh setup (R3.5); the median repetition by throughput
/// supplies the reported latencies.
/// </summary>
internal static class Cell
{
    /// <summary>
    /// Runs <paramref name="spec"/>. <paramref name="setUp"/> builds per-repetition shared state (e.g. a fresh store)
    /// and returns a per-writer prepare function and the one-operation body; <paramref name="tearDown"/> disposes it.
    /// </summary>
    public static CellResult Run(
        CellSpec spec,
        Func<(PrepareWriter Prepare, OneOperation Operation, IDisposable? State)> setUp)
    {
        var throughputs = new List<double>(spec.Repetitions);
        var perRepThroughput = new List<(double Ops, long[][] Latencies, long Ops2)>(spec.Repetitions);

        for (var rep = 0; rep < spec.Repetitions; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var (prepare, operation, state) = setUp();
            try
            {
                var (ops, latencies) = RunOnce(spec, prepare, operation);
                var totalOps = latencies.Sum(a => (long)a.Length);
                var opsPerSec = ops / spec.Measured.TotalSeconds;
                throughputs.Add(opsPerSec);
                perRepThroughput.Add((opsPerSec, latencies, totalOps));
            }
            finally
            {
                state?.Dispose();
            }
        }

        // Median repetition by throughput supplies the latencies (R3.3).
        var sortedByThroughput = perRepThroughput.OrderBy(r => r.Ops).ToList();
        var medianRep = sortedByThroughput[sortedByThroughput.Count / 2];
        var totalOperationsOfMedian = medianRep.Ops2;

        var allLatencies = Merge(medianRep.Latencies);
        Array.Sort(allLatencies);

        return new CellResult(
            spec.Workload,
            spec.Configuration,
            spec.Durability,
            spec.Writers,
            OpsPerSecMedian: Median(throughputs),
            OpsPerSecMin: throughputs.Min(),
            OpsPerSecMax: throughputs.Max(),
            P50Us: TicksToMicros(Percentile(allLatencies, 0.50)),
            P99Us: TicksToMicros(Percentile(allLatencies, 0.99)),
            MaxUs: allLatencies.Length == 0 ? 0 : TicksToMicros(allLatencies[^1]),
            Operations: totalOperationsOfMedian,
            AllocatedBytesPerOp: null);
    }

    /// <summary>Runs the writers once for the warm-up plus the measured window; returns measured ops and per-writer latency ticks.</summary>
    private static (long MeasuredOps, long[][] Latencies) RunOnce(CellSpec spec, PrepareWriter prepare, OneOperation operation)
    {
        var barrier = new Barrier(spec.Writers + 1);
        var stop = new ManualResetEventSlim(false);
        var measuring = new ManualResetEventSlim(false);
        var threads = new Thread[spec.Writers];
        var latencyLists = new long[spec.Writers][];
        var measuredOps = new long[spec.Writers];

        for (var w = 0; w < spec.Writers; w++)
        {
            var index = w;
            threads[w] = new Thread(() =>
            {
                var state = prepare(index);
                var latencies = new long[1024];
                var count = 0;
                long measured = 0;

                barrier.SignalAndWait();   // all writers + the timer start together

                while (!stop.IsSet)
                {
                    var start = Stopwatch.GetTimestamp();
                    operation(state);
                    var elapsed = Stopwatch.GetTimestamp() - start;

                    var inWindow = measuring.IsSet;
                    if (inWindow)
                    {
                        if (count == latencies.Length)
                        {
                            Array.Resize(ref latencies, latencies.Length * 2);
                        }

                        latencies[count++] = elapsed;
                        measured++;
                    }
                }

                latencyLists[index] = latencies[..count];
                measuredOps[index] = measured;
            })
            {
                IsBackground = true,
                Name = $"{spec.Workload}-writer-{w}",
            };
            threads[w].Start();
        }

        barrier.SignalAndWait();           // release the writers
        Thread.Sleep(spec.WarmUp);
        measuring.Set();                   // begin counting
        Thread.Sleep(spec.Measured);
        stop.Set();                        // end

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return (measuredOps.Sum(), latencyLists);
    }

    /// <summary>
    /// Measures bytes allocated per operation on a single writer thread (spec 008 R3.6), run outside the timed cell.
    /// </summary>
    public static double AllocatedBytesPerOp(PrepareWriter prepare, OneOperation operation, int operations)
    {
        var done = 0L;
        double bytes = 0;
        var thread = new Thread(() =>
        {
            var state = prepare(0);
            // Warm up a little so first-call JIT does not count.
            for (var i = 0; i < Math.Min(100, operations); i++)
            {
                operation(state);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < operations; i++)
            {
                operation(state);
            }

            var after = GC.GetAllocatedBytesForCurrentThread();
            bytes = after - before;
            done = operations;
        })
        { IsBackground = true };
        thread.Start();
        thread.Join();

        return done == 0 ? 0 : bytes / done;
    }

    private static long[] Merge(long[][] perWriter)
    {
        var total = perWriter.Sum(a => a.Length);
        var all = new long[total];
        var offset = 0;
        foreach (var writer in perWriter)
        {
            Array.Copy(writer, 0, all, offset, writer.Length);
            offset += writer.Length;
        }

        return all;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Percentile by index into the sorted array (StowDB's MeasurementArtifact.Percentile).</summary>
    private static long Percentile(long[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)(sorted.Length * q);
        if (index >= sorted.Length)
        {
            index = sorted.Length - 1;
        }

        return sorted[index];
    }

    private static double TicksToMicros(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
}
