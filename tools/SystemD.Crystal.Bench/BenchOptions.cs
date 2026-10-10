using System;
using System.Collections.Generic;

namespace SystemD.Crystal.Bench;

/// <summary>Raised when the command line cannot be parsed; the caller prints usage and exits non-zero.</summary>
internal sealed class BenchUsageException(string message) : Exception(message);

/// <summary>The size of a run's cells (spec 008 R3.1): quick for a fast check, full before a release.</summary>
internal enum BenchSize
{
    Quick,
    Full,
}

/// <summary>Parsed command line (spec 008 R1.2, R1.3, design § 2).</summary>
internal sealed class BenchOptions
{
    public required string Command { get; init; }
    public required BenchSize Size { get; init; }
    public string? DataDirectory { get; init; }
    public string? OutPath { get; init; }
    public bool NoRecord { get; init; }
    public bool AllowDebug { get; init; }
    public required string CommandLine { get; init; }

    private static readonly HashSet<string> Commands =
    [
        "env", "selftest", "commit", "metrics-cost", "boxes", "churn", "soak", "replication", "quick", "full",
    ];

    public static BenchOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            return new BenchOptions { Command = "help", Size = BenchSize.Quick, CommandLine = "help" };
        }

        var command = args[0];
        if (!Commands.Contains(command))
        {
            throw new BenchUsageException($"Unknown command '{command}'.");
        }

        // quick/full set the size; a bare command defaults to quick unless --size overrides.
        var size = command == "full" ? BenchSize.Full : BenchSize.Quick;
        string? dir = null;
        string? outPath = null;
        var noRecord = false;
        var allowDebug = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--size":
                    size = ParseSize(NextValue(args, ref i, "--size"));
                    break;
                case "--dir":
                    dir = NextValue(args, ref i, "--dir");
                    break;
                case "--out":
                    outPath = NextValue(args, ref i, "--out");
                    break;
                case "--no-record":
                    noRecord = true;
                    break;
                case "--allow-debug":
                    allowDebug = true;
                    break;
                default:
                    throw new BenchUsageException($"Unknown option '{args[i]}'.");
            }
        }

        return new BenchOptions
        {
            Command = command,
            Size = size,
            DataDirectory = dir,
            OutPath = outPath,
            NoRecord = noRecord,
            AllowDebug = allowDebug,
            CommandLine = string.Join(' ', args),
        };
    }

    private static BenchSize ParseSize(string value) => value.ToLowerInvariant() switch
    {
        "quick" => BenchSize.Quick,
        "full" => BenchSize.Full,
        _ => throw new BenchUsageException($"--size must be 'quick' or 'full', not '{value}'."),
    };

    private static string NextValue(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
        {
            throw new BenchUsageException($"{option} needs a value.");
        }

        return args[++i];
    }
}
