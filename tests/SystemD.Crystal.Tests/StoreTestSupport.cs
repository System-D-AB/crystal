using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

/// <summary>A store directory under the temp folder, deleted when disposed.</summary>
internal sealed class TempStoreDirectory : IDisposable
{
    public TempStoreDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crystal-" + Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public string DatabaseFolder(uint id) => System.IO.Path.Combine(Path, StoreEngine.DatabaseFolderName(id));

    public void Dispose()
    {
        try
        {
            StoreEngine.DeleteDirectoryWithRetries(Path);
        }
        catch (IOException)
        {
            // Best effort in test cleanup.
        }
    }
}

/// <summary>
/// Runs the crash host program (spec 005 R13.2, R13.5) and kills it at a named step.
/// </summary>
internal static class CrashHost
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs the host with <paramref name="args"/>. If it prints <c>AT {step}</c>, kills the whole process there and
    /// returns the step; otherwise returns its last line once it exits.
    /// </summary>
    public static string RunAndKillAtStep(params string[] args) => RunAndKillAfter(TimeSpan.Zero, args);

    /// <summary>
    /// As <see cref="RunAndKillAtStep"/>, but kills <paramref name="delay"/> after the host prints <c>AT {step}</c>: with a
    /// step ending in <c>+run</c> the host carries on past it, so the kill lands in what follows (spec 010 R9.4).
    /// </summary>
    public static string RunAndKillAfter(TimeSpan delay, params string[] args)
    {
        using var process = Start(args);
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            lines.Add(line);
            if (line.StartsWith("AT ", StringComparison.Ordinal))
            {
                Thread.Sleep(delay);
                Assert.False(process.HasExited, "The crash host finished before it was killed.");
                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(Patience), "The crash host did not die when killed.");
                return line[3..];
            }
        }

        Assert.True(process.WaitForExit(Patience), "The crash host did not exit.");
        Assert.True(process.ExitCode == 0, $"The crash host failed: {process.StandardError.ReadToEnd()}");
        return lines.LastOrDefault() ?? string.Empty;
    }

    /// <summary>Starts the host and waits for its first line.</summary>
    public static Process StartAndWaitFor(string expectedLine, params string[] args)
    {
        var process = Start(args);
        var line = process.StandardOutput.ReadLine();
        Assert.True(line == expectedLine, $"Expected '{expectedLine}' from the crash host, got '{line}': {(line is null ? process.StandardError.ReadToEnd() : string.Empty)}");
        return process;
    }

    private static Process Start(string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "SystemD.Crystal.CrashHost.dll"));
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Could not start the crash host.");
    }
}

/// <summary>Store options whose hook signals each step and can hold the thread that reaches one.</summary>
internal sealed class StepGate
{
    private readonly Dictionary<string, ManualResetEventSlim> _reached = [];
    private readonly Dictionary<string, ManualResetEventSlim> _held = [];
    private readonly object _sync = new();

    /// <summary>Makes the thread that reaches <paramref name="step"/> wait there until <see cref="Release"/>.</summary>
    public void HoldAt(string step)
    {
        lock (_sync)
        {
            _held[step] = new ManualResetEventSlim(false);
        }
    }

    public void Release(string step) => Event(_held, step).Set();

    public bool WaitReached(string step, TimeSpan timeout) => Event(_reached, step).Wait(timeout);

    public bool HasReached(string step) => Event(_reached, step).IsSet;

    public CrystalOptions Options(int maxOpenDatabases = 256) => new()
    {
        MaxOpenDatabases = maxOpenDatabases,
        Hook = OnStep,
    };

    private void OnStep(string step)
    {
        Event(_reached, step).Set();
        ManualResetEventSlim? hold;
        lock (_sync)
        {
            _held.TryGetValue(step, out hold);
        }

        hold?.Wait();
    }

    private ManualResetEventSlim Event(Dictionary<string, ManualResetEventSlim> events, string step)
    {
        lock (_sync)
        {
            if (!events.TryGetValue(step, out var e))
            {
                e = new ManualResetEventSlim(false);
                events[step] = e;
            }

            return e;
        }
    }
}

/// <summary>Runs work on a dedicated thread, independent of the thread pool (playbook § 4, starved pool).</summary>
internal sealed class DedicatedThread
{
    private readonly Thread _thread;
    private Exception? _error;

    private DedicatedThread(Action work)
    {
        _thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _error = ex;
            }
        })
        {
            IsBackground = true,
        };
        _thread.Start();
    }

    public static DedicatedThread Run(Action work) => new(work);

    public bool Join(TimeSpan timeout) => _thread.Join(timeout);

    /// <summary>Waits for the thread and rethrows anything it threw.</summary>
    public void Complete(TimeSpan timeout)
    {
        Assert.True(_thread.Join(timeout), "The thread did not finish.");
        if (_error is not null)
        {
            throw new AggregateException(_error);
        }
    }
}
