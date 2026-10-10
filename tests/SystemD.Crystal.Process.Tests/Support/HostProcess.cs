using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SystemD.Crystal.Process.Tests.Support;

/// <summary>
/// A child program started with <c>dotnet &lt;program&gt;.dll</c> from beside the tests (spec 004 R2.2, R2.3). Its standard
/// output is read on a thread of its own into a queue, so every read here has a real timeout: a test never hangs on a
/// child that died or stopped talking. Standard error is kept for failure messages.
/// </summary>
internal sealed class HostProcess : IDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly BlockingCollection<string> _lines = new();
    private readonly StringBuilder _errors = new();
    private readonly Thread _reader;

    private HostProcess(System.Diagnostics.Process process)
    {
        _process = process;
        _reader = new Thread(ReadOutput) { IsBackground = true, Name = "host-output" };
        _reader.Start();
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_errors)
                {
                    _errors.AppendLine(e.Data);
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public string Errors
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToString();
            }
        }
    }

    /// <summary>Starts <paramref name="program"/> (a dll name beside the tests) with <paramref name="args"/>.</summary>
    public static HostProcess Start(string program, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, program));
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException($"Could not start {program}.");
        return new HostProcess(process);
    }

    /// <summary>The next line of output, or an exception naming what the child said on standard error.</summary>
    public string ReadLine(TimeSpan timeout)
    {
        if (_lines.TryTake(out var line, timeout))
        {
            return line;
        }

        var state = _process.HasExited ? $"exited with code {_process.ExitCode}" : "still running but silent";
        throw new TimeoutException($"No output within {timeout.TotalSeconds:0.#} s; the child {state}. Its errors: {Errors}");
    }

    /// <summary>The next line, or null once the child's output has ended.</summary>
    public string? TryReadLine(TimeSpan timeout) => _lines.TryTake(out var line, timeout) ? line : null;

    public void WriteLine(string line)
    {
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    /// <summary>Kills the process and everything it started, with no chance to flush or close: a hard kill.</summary>
    public void Kill(TimeSpan timeout)
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            throw new TimeoutException($"Process {_process.Id} did not die within {timeout.TotalSeconds:0.#} s of being killed.");
        }
    }

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit((int)timeout.TotalMilliseconds);

    public int ExitCode => _process.ExitCode;

    private void ReadOutput()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
            {
                _lines.Add(line);
            }
        }
        catch (Exception)
        {
            // The process died mid-line; the queue ends.
        }
        finally
        {
            _lines.CompleteAdding();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Never started, or already gone.
        }

        _process.Dispose();
    }
}
