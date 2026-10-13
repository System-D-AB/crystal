// Ported from Highway: tests/Highway.Storage.CrashHarness/Program.cs
// Adapted for Crystal: the public store API instead of Highway's store; each batch writes two boxes, so a test sees a
// batch whole or not at all; a durability mode argument (spec 006); resumes after the last batch already in the store,
// so one store can take several kills (spec 004 R2.3).

using System;
using System.Linq;

namespace SystemD.Crystal.CrashHarness;

/// <summary>
/// <code>
/// write --dir &lt;path&gt; [--mode Strict|Interval|Buffered] [--count N] [--backup &lt;root&gt; --backup-after N]
/// </code>
/// Opens the store at <c>--dir</c> (database <see cref="CrashStream.Database"/>, boxes <see cref="CrashStream.Entries"/> and
/// <see cref="CrashStream.Marks"/>), prints <c>READY &lt;first index&gt;</c>, then commits batch after batch, printing
/// <c>ACK &lt;index&gt;</c> after each commit returns. Batch <c>i</c> puts <see cref="CrashStream.Key"/>(i) in both boxes,
/// with <see cref="CrashStream.EntryValue"/>(i) and <see cref="CrashStream.MarkValue"/>(i). With <c>--count</c> it stops after
/// that many batches in total and prints <c>DONE</c>; without it, it writes until it is killed.
/// <para>With <c>--backup</c> (spec 010 R9.3), once batch <c>--backup-after</c> is acknowledged it starts a backup into the
/// root and keeps writing while it runs: it prints <c>BACKUPSTART &lt;last acknowledged index&gt;</c> before the backup
/// starts and <c>BACKUP &lt;set id&gt;</c> when its set exists.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args[0] != "write")
        {
            Console.Error.WriteLine("usage: write --dir <path> [--mode Strict|Interval|Buffered] [--count N]");
            return 2;
        }

        string? dir = null;
        long? count = null;
        var mode = DurabilityMode.Strict;
        string? backupRoot = null;
        long backupAfter = 0;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir":
                    dir = args[++i];
                    break;
                case "--count":
                    count = long.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--mode":
                    mode = Enum.Parse<DurabilityMode>(args[++i]);
                    break;
                case "--backup":
                    backupRoot = args[++i];
                    break;
                case "--backup-after":
                    backupAfter = long.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return 2;
            }
        }

        if (dir is null)
        {
            Console.Error.WriteLine("--dir is required");
            return 2;
        }

        using var store = CrystalStore.Open(dir, new CrystalOptions { Durability = mode });
        var db = store.ListDatabases().Any(d => d.Name == CrashStream.Database)
            ? store.OpenDatabase(CrashStream.Database)
            : store.CreateDatabase(CrashStream.Database);
        var entries = db.Boxes.Find(CrashStream.Entries) ?? db.Boxes.Create(CrashStream.Entries);
        var marks = db.Boxes.Find(CrashStream.Marks) ?? db.Boxes.Create(CrashStream.Marks);

        var first = NextIndex(db, entries);
        Say($"READY {first}");

        System.Threading.Tasks.Task? backup = null;
        for (var i = first; count is null || i < count; i++)
        {
            using (var batch = db.NewBatch())
            {
                batch.Put(entries, CrashStream.Key(i), CrashStream.EntryValue(i));
                batch.Put(marks, CrashStream.Key(i), CrashStream.MarkValue(i));
                batch.Commit(mode);
            }

            Say($"ACK {i}"); // only after the commit returned
            if (backupRoot is not null && backup is null && i >= backupAfter)
            {
                Say($"BACKUPSTART {i}");
                backup = store.BackupAsync(backupRoot).ContinueWith(
                    t => Say(t.IsCompletedSuccessfully ? $"BACKUP {t.Result.SetId}" : $"BACKUPFAILED {t.Exception?.GetBaseException().Message}"),
                    System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        backup?.Wait();

        Say("DONE");
        return 0;
    }

    /// <summary>The first index not yet written: the stream is a prefix, so the number of entries.</summary>
    private static long NextIndex(CrystalDatabase db, Box entries)
    {
        using var snapshot = db.Snapshot();
        long count = 0;
        byte[] from = [];
        while (true)
        {
            var page = snapshot.Scan(entries, from, [], 10_000).ToList();
            count += page.Count;
            if (page.Count < 10_000)
            {
                return count;
            }

            from = Successor(page[^1].Key);
        }
    }

    private static byte[] Successor(byte[] key)
    {
        var next = new byte[key.Length + 1];
        key.CopyTo(next, 0);
        return next;
    }

    private static readonly object SayLock = new();

    private static void Say(string line)
    {
        lock (SayLock) // the backup's line comes from another thread
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}
