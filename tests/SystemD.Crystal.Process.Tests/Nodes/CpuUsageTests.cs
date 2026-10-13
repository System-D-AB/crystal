// CPU use of a replicated pair over time: two node processes (a primary and a standby following it), a client writing
// and reading boxes for 75 seconds, then 20 seconds idle. Each node's CPU time is sampled every 5 seconds and per phase,
// and reported. The test asserts the pair stays correct (the standby ends byte-identical) and that an idle node does
// not spin (a timer, poll or retry loop that keeps working with nothing to do); the load figures are reported, not
// bounded, since they depend on the machine.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace SystemD.Crystal.Process.Tests.Nodes;

public sealed class CpuUsageTests(ITestOutputHelper output)
{
    private static readonly TimeSpan LoadPhase = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan IdlePhase = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Sample = TimeSpan.FromSeconds(5);
    private static readonly string[] Databases = ["orders", "users"];
    private static readonly string[] Boxes = ["main", "index"];

    /// <summary>An idle node may use at most this share of one core (timers, long polls and their wake-ups).</summary>
    private const double IdleBoundPercentOfOneCore = 5.0;

    [Fact]
    public void APairUnderClientLoadForOverAMinute_ReportsItsCpu_AndDoesNotSpinWhenIdle()
    {
        using var primary = TestNode.Start();
        using var standby = TestNode.Start();
        primary.Listen();
        foreach (var db in Databases)
        {
            primary.CreateDatabase(db);
            foreach (var box in Boxes)
            {
                primary.Box(db, box);
                primary.Put(db, box, "seed", "seed");
            }
        }

        standby.Listen();
        standby.Follow(primary);
        WaitUntilIdentical(primary, standby); // bootstrapped before the clock starts

        var nodes = new[] { ("primary", primary), ("standby", standby) };
        var cpu = nodes.ToDictionary(n => n.Item1, n => CpuTime(n.Item2));
        var phaseStart = new Dictionary<string, TimeSpan>(cpu);
        var kernelStart = nodes.ToDictionary(n => n.Item1, n => KernelTime(n.Item2));
        var watch = Stopwatch.StartNew();
        var nextSample = Sample;
        long writes = 0, batches = 0, reads = 0, standbyReads = 0;
        var random = new Random(2026);

        output.WriteLine($"cores: {Environment.ProcessorCount}; load {LoadPhase.TotalSeconds:F0} s, idle {IdlePhase.TotalSeconds:F0} s; " +
                         "% = share of one core (Task Manager shows it divided by the number of cores)");
        output.WriteLine("  t(s)  phase  primary%  standby%  ops/s");
        long opsAtSample = 0;

        // Load: a client writes single puts and 10-key batches to the primary, and reads both nodes by key.
        while (watch.Elapsed < LoadPhase)
        {
            var db = Databases[random.Next(Databases.Length)];
            var box = Boxes[random.Next(Boxes.Length)];
            var key = $"k{random.Next(5_000):D5}";
            switch (random.Next(10))
            {
                case 0:
                    primary.PutBatch(db, box, $"b{batches++:D6}", 10);
                    break;
                case < 6:
                    primary.Put(db, box, key, $"value {writes++} at {watch.ElapsedMilliseconds}");
                    break;
                case < 9:
                    primary.Get(db, box, key);
                    reads++;
                    break;
                default:
                    standby.Get(db, box, key); // a point read: a count would scan a box that keeps growing
                    standbyReads++;
                    break;
            }

            if (watch.Elapsed >= nextSample)
            {
                var ops = writes + batches + reads + standbyReads;
                Report("load", watch.Elapsed, Sample, ref cpu, nodes, (ops - opsAtSample) / Sample.TotalSeconds);
                opsAtSample = ops;
                nextSample += Sample;
            }
        }

        var loadCpu = nodes.ToDictionary(n => n.Item1, n => CpuTime(n.Item2) - phaseStart[n.Item1]);
        var loadKernel = nodes.ToDictionary(n => n.Item1, n => KernelTime(n.Item2) - kernelStart[n.Item1]);
        var loadElapsed = watch.Elapsed;

        // The standby catches up, then nothing happens: an idle pair should be close to 0.
        WaitUntilIdentical(primary, standby);
        cpu = nodes.ToDictionary(n => n.Item1, n => CpuTime(n.Item2));
        var idleStart = new Dictionary<string, TimeSpan>(cpu);
        var idleWatch = Stopwatch.StartNew();
        nextSample = Sample;
        while (idleWatch.Elapsed < IdlePhase)
        {
            Thread.Sleep(nextSample - idleWatch.Elapsed is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
            Report("idle", loadElapsed + idleWatch.Elapsed, Sample, ref cpu, nodes, 0);
            nextSample += Sample;
        }

        var idleElapsed = idleWatch.Elapsed;
        var idleCpu = nodes.ToDictionary(n => n.Item1, n => CpuTime(n.Item2) - idleStart[n.Item1]);

        output.WriteLine(string.Empty);
        output.WriteLine($"client: {writes} puts, {batches} batches of 10, {reads} primary reads, {standbyReads} standby reads " +
                         $"({(writes + batches + reads + standbyReads) / loadElapsed.TotalSeconds:F0} ops/s, one request at a time)");
        foreach (var (name, _) in nodes)
        {
            output.WriteLine($"{name}: load {loadCpu[name].TotalSeconds:F1} s CPU over {loadElapsed.TotalSeconds:F0} s = {Percent(loadCpu[name], loadElapsed):F1}% of one core, " +
                             $"{Percent(loadCpu[name], loadElapsed) / Environment.ProcessorCount:F2}% of the machine as Task Manager shows it; " +
                             $"idle {idleCpu[name].TotalSeconds:F2} s over {idleElapsed.TotalSeconds:F0} s = {Percent(idleCpu[name], idleElapsed):F2}%");
            output.WriteLine($"  {name} under load: kernel {loadKernel[name].TotalSeconds:F1} s, user {(loadCpu[name] - loadKernel[name]).TotalSeconds:F1} s");
        }

        foreach (var (name, _) in nodes)
        {
            Assert.True(
                Percent(idleCpu[name], idleElapsed) < IdleBoundPercentOfOneCore,
                $"the idle {name} used {Percent(idleCpu[name], idleElapsed):F1}% of a core: something keeps working with nothing to do");
        }

        WaitUntilIdentical(primary, standby); // still identical after the idle phase
    }

    private void Report(string phase, TimeSpan at, TimeSpan window, ref Dictionary<string, TimeSpan> last, (string, TestNode)[] nodes, double opsPerSecond)
    {
        var now = nodes.ToDictionary(n => n.Item1, n => CpuTime(n.Item2));
        output.WriteLine($"{at.TotalSeconds,6:F0}  {phase,-5}  {Percent(now["primary"] - last["primary"], window),8:F1}  {Percent(now["standby"] - last["standby"], window),8:F1}  {opsPerSecond,5:F0}");
        last = now;
    }

    private static double Percent(TimeSpan cpu, TimeSpan wall) => 100.0 * cpu.TotalSeconds / wall.TotalSeconds;

    private static TimeSpan CpuTime(TestNode node)
    {
        using var process = System.Diagnostics.Process.GetProcessById(node.ProcessId);
        return process.TotalProcessorTime;
    }

    private static TimeSpan KernelTime(TestNode node)
    {
        using var process = System.Diagnostics.Process.GetProcessById(node.ProcessId);
        return process.PrivilegedProcessorTime;
    }

    private static void WaitUntilIdentical(TestNode primary, TestNode standby)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Databases.All(db => Boxes.All(box => primary.Digest(db, box) == standby.Digest(db, box))))
                {
                    return;
                }
            }
            catch (NodeCommandException)
            {
                // The standby has not bootstrapped the database or box yet.
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("the standby did not become byte-identical to the primary in time");
    }
}
