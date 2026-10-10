// Ported from Highway: tests/Highway.Server.Tests/Storage/CrashReplayTests.cs (the harness process management)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SystemD.Crystal.CrashHarness;

namespace SystemD.Crystal.Process.Tests.Support;

/// <summary>
/// Runs the crash harness (spec 004 R2.3, task T4) and audits a store it wrote. <see cref="RunAndKill"/> lets it commit
/// until it has acknowledged a chosen number of batches, then hard-kills it; <see cref="RunToCompletion"/> is the
/// reference run. <see cref="Audit"/> reopens the store in this process and checks what survived.
/// </summary>
internal static class CrashHarnessRunner
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public sealed record Run(long FirstIndex, long LastAcked);

    /// <summary>Starts the harness on <paramref name="dir"/>, waits for <paramref name="acks"/> acknowledgements, kills it.</summary>
    public static Run RunAndKill(string dir, DurabilityMode mode, int acks)
    {
        using var harness = HostProcess.Start("SystemD.Crystal.CrashHarness.dll", "write", "--dir", dir, "--mode", mode.ToString());
        var first = ReadReady(harness);
        var lastAcked = first - 1;
        while (lastAcked < first + acks - 1)
        {
            lastAcked = ReadAck(harness);
        }

        harness.Kill(Patience); // no graceful shutdown, no flush
        return new Run(first, lastAcked);
    }

    /// <summary>The reference run: exactly <paramref name="count"/> batches into a fresh store, then a clean exit.</summary>
    public static void RunToCompletion(string dir, DurabilityMode mode, long count)
    {
        using var harness = HostProcess.Start(
            "SystemD.Crystal.CrashHarness.dll", "write", "--dir", dir, "--mode", mode.ToString(),
            "--count", count.ToString(CultureInfo.InvariantCulture));
        ReadReady(harness);
        while (harness.ReadLine(Patience) is var line && line != "DONE")
        {
            if (!line.StartsWith("ACK ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected harness output '{line}'.");
            }
        }

        if (!harness.WaitForExit(Patience) || harness.ExitCode != 0)
        {
            throw new InvalidOperationException($"The reference run failed: {harness.Errors}");
        }
    }

    /// <summary>What a store holds of the stream, and whether it is sound.</summary>
    public sealed record Audit(long Batches, IReadOnlyList<string> Problems)
    {
        public bool Sound => Problems.Count == 0;
    }

    /// <summary>
    /// Reopens <paramref name="dir"/> and checks the stream: the batches present are exactly 0…n-1 (a prefix, no gap);
    /// each batch is whole (its entry and its mark both present, or neither); each value is byte-identical to the
    /// deterministic stream. Problems are listed, not thrown, so a test can print them all.
    /// </summary>
    public static Audit AuditStore(string dir)
    {
        using var store = CrystalStore.Open(dir);
        var db = store.OpenDatabase(CrashStream.Database);
        var entries = db.Boxes.Find(CrashStream.Entries)!.Value;
        var marks = db.Boxes.Find(CrashStream.Marks)!.Value;
        using var snapshot = db.Snapshot();
        var entryRows = snapshot.Scan(entries, [], [], int.MaxValue).ToList();
        var markRows = snapshot.Scan(marks, [], [], int.MaxValue).ToList();

        var problems = new List<string>();
        if (entryRows.Count != markRows.Count)
        {
            problems.Add($"{entryRows.Count} entries but {markRows.Count} marks: a batch is not whole.");
        }

        for (var i = 0; i < Math.Max(entryRows.Count, markRows.Count) && problems.Count < 20; i++)
        {
            var key = CrashStream.Key(i);
            if (i >= entryRows.Count || !entryRows[i].Key.AsSpan().SequenceEqual(key))
            {
                problems.Add($"Entry {i} is missing or out of place: the stream is not a prefix.");
                continue;
            }

            if (!entryRows[i].Value.AsSpan().SequenceEqual(CrashStream.EntryValue(i)))
            {
                problems.Add($"Entry {i} differs from the reference value.");
            }

            if (i >= markRows.Count || !markRows[i].Key.AsSpan().SequenceEqual(key))
            {
                problems.Add($"Mark {i} is missing or out of place: batch {i} is not whole.");
            }
            else if (!markRows[i].Value.AsSpan().SequenceEqual(CrashStream.MarkValue(i)))
            {
                problems.Add($"Mark {i} differs from the reference value.");
            }
        }

        return new Audit(entryRows.Count, problems);
    }

    /// <summary>Every entry and mark row of the stream in <paramref name="dir"/>, in key order.</summary>
    public static (IReadOnlyList<KeyValue> Entries, IReadOnlyList<KeyValue> Marks) Rows(string dir)
    {
        using var store = CrystalStore.Open(dir);
        var db = store.OpenDatabase(CrashStream.Database);
        using var snapshot = db.Snapshot();
        return (
            snapshot.Scan(db.Boxes.Find(CrashStream.Entries)!.Value, [], [], int.MaxValue).ToList(),
            snapshot.Scan(db.Boxes.Find(CrashStream.Marks)!.Value, [], [], int.MaxValue).ToList());
    }

    private static long ReadReady(HostProcess harness)
    {
        var line = harness.ReadLine(Patience);
        if (!line.StartsWith("READY ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The harness did not start: '{line}'. Its errors: {harness.Errors}");
        }

        return long.Parse(line["READY ".Length..], CultureInfo.InvariantCulture);
    }

    private static long ReadAck(HostProcess harness)
    {
        var line = harness.ReadLine(Patience);
        if (!line.StartsWith("ACK ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected harness output '{line}'. Its errors: {harness.Errors}");
        }

        return long.Parse(line["ACK ".Length..], CultureInfo.InvariantCulture);
    }
}
