// Ported from StowDB: tests/Stow.Storage.Measurements/Program.cs (branch rocksdb). Carried: one command per
// measurement and the explicit output path with no silent fallback. Changed: Crystal's commands and options (§ 2),
// the exit code gated on verification (R1.5), and the Debug refusal (R4.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Bench.Workloads;

namespace SystemD.Crystal.Bench;

/// <summary>
/// The benchmark harness entry point (spec 008 R1, design § 2). Parses one command and its options, runs the
/// workloads, appends the results, and sets the exit code to 0 only if every workload passed its verification (R1.5).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        BenchOptions options;
        try
        {
            options = BenchOptions.Parse(args);
        }
        catch (BenchUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return 2;
        }

        if (options.Command == "help")
        {
            PrintUsage(Console.Out);
            return 0;
        }

        // The data directory parent (the disk under test): default the system temp folder (R1.3).
        var dataParent = options.DataDirectory ?? Path.Combine(Path.GetTempPath(), "crystal-bench");

        // env just prints the conditions and exits (R1.2, R4.1).
        if (options.Command == "env")
        {
            PrintEnvironment(dataParent);
            return 0;
        }

        // Resolve the results log before running: an explicit --out, or engineering/measurements.md under the repo
        // root, which is found by walking up to the folder holding the solution. No silent fallback (design § 2).
        string? outPath = null;
        if (!options.NoRecord)
        {
            outPath = options.OutPath;
            if (outPath is null)
            {
                var repoRoot = RepositoryRoot.Find();
                if (repoRoot is null)
                {
                    Console.Error.WriteLine(
                        "Could not find the repository root (the folder holding SystemD.Crystal.slnx) to place " +
                        "engineering/measurements.md. Pass --out <path>, or --no-record to print only.");
                    return 2;
                }

                outPath = Path.Combine(repoRoot, "engineering", "measurements.md");
            }
        }

        // The Debug refusal (R4.3): a Debug build runs a workload only with --allow-debug, and then its results are
        // marked "DEBUG — not representative".
        if (Conditions.IsDebug && options.Command is not ("selftest") && !options.AllowDebug)
        {
            Console.Error.WriteLine(
                "This is a Debug build; its numbers are not representative. Build with -c Release, or pass " +
                "--allow-debug to run anyway (results are marked DEBUG).");
            return 2;
        }

        IReadOnlyList<IWorkload> workloads;
        try
        {
            workloads = SelectWorkloads(options.Command);
        }
        catch (BenchUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return 2;
        }

        var conditions = CaptureConditions(dataParent);
        var context = new WorkloadContext(dataParent, options.Size);

        var sections = new List<WorkloadResult>();
        var allPassed = true;
        foreach (var workload in workloads)
        {
            Console.WriteLine($"Running '{workload.Name}' ({options.Size})...");
            WorkloadResult result;
            try
            {
                result = workload.Run(context);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"'{workload.Name}' threw: {ex}");
                allPassed = false;
                continue;
            }

            sections.Add(result);
            Console.WriteLine(result.Verification.Passed
                ? $"  PASSED — {result.Verification.Summary}"
                : $"  FAILED — {result.Verification.Summary}");
            allPassed &= result.Verification.Passed;
        }

        if (outPath is not null && sections.Count > 0)
        {
            Results.Append(outPath, options.CommandLine, conditions, sections);
            Console.WriteLine($"Recorded {sections.Count} section(s) to {outPath}");
        }

        return allPassed ? 0 : 1;
    }

    private static Conditions CaptureConditions(string dataParent)
    {
        // Open and close a throwaway store so RocksDbSharp is loaded before we read its version (R4.1).
        try
        {
            using var warm = TempStore.Create(dataParent);
        }
        catch (Exception)
        {
            // If even opening a store fails, Capture still reports what it can; the workloads will surface the error.
        }

        return Conditions.Capture(dataParent);
    }

    private static void PrintEnvironment(string dataParent)
    {
        var conditions = CaptureConditions(dataParent);
        Console.WriteLine("Crystal benchmark harness — conditions");
        Console.WriteLine($"  date            {conditions.DateUtc}");
        Console.WriteLine($"  build           {conditions.BuildConfiguration}");
        Console.WriteLine($"  os              {conditions.Os}");
        Console.WriteLine($"  processor       {conditions.Processor}");
        Console.WriteLine($"  logical cores   {conditions.LogicalCores}");
        Console.WriteLine($"  memory          {conditions.Memory}");
        Console.WriteLine($"  data drive      {conditions.DataDrive}");
        Console.WriteLine($"  file system     {conditions.FileSystem}");
        Console.WriteLine($"  .NET            {conditions.DotnetVersion}");
        Console.WriteLine($"  Crystal         {conditions.CrystalVersion}");
        Console.WriteLine($"  RocksDB         {conditions.RocksDbVersion}");
    }

    private static IReadOnlyList<IWorkload> SelectWorkloads(string command)
    {
        var all = new IWorkload[]
        {
            new CommitWorkload(),
            new MetricsCostWorkload(),
            new BoxesWorkload(),
            new ChurnWorkload(),
            new SoakWorkload(),
            new ReplicationWorkload(),
        };

        return command switch
        {
            "selftest" => [new SelfTest()],
            "commit" => [new CommitWorkload()],
            "metrics-cost" => [new MetricsCostWorkload()],
            "boxes" => [new BoxesWorkload()],
            "churn" => [new ChurnWorkload()],
            "soak" => [new SoakWorkload()],
            "replication" => [new ReplicationWorkload()],
            "quick" or "full" => all,
            _ => throw new BenchUsageException($"Unknown command '{command}'."),
        };
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Crystal benchmark harness (spec 008).");
        w.WriteLine();
        w.WriteLine("usage: dotnet run -c Release --project tools/SystemD.Crystal.Bench -- <command> [options]");
        w.WriteLine();
        w.WriteLine("commands:");
        w.WriteLine("  env            print the conditions and exit");
        w.WriteLine("  selftest       prove the read-back verification works");
        w.WriteLine("  commit         durability: commits/s and latency per mode and writer count");
        w.WriteLine("  metrics-cost   the cost of observability (with and without a MeterListener)");
        w.WriteLine("  boxes          catalogue cost (create/find/rename/swap/drop/list)");
        w.WriteLine("  churn          profiles under constant insert and delete");
        w.WriteLine("  soak           sustained writes with Status() figures over time");
        w.WriteLine("  replication    cost of replication over a socket (50 dbs, bootstrap, catch-up)");
        w.WriteLine("  quick          every workload in its quick size");
        w.WriteLine("  full           every workload in its full size");
        w.WriteLine();
        w.WriteLine("options:");
        w.WriteLine("  --size quick|full   measured durations (default quick; quick/full set it)");
        w.WriteLine("  --dir <path>        data directory parent, the disk under test (default: system temp)");
        w.WriteLine("  --out <path>        results log (default: <repo>/engineering/measurements.md)");
        w.WriteLine("  --no-record         print, do not append");
        w.WriteLine("  --allow-debug       run from a Debug build, marked as such");
    }
}
