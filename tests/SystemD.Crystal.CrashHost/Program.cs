using System;
using System.IO;
using System.Threading;

namespace SystemD.Crystal.CrashHost;

/// <summary>
/// Runs one store operation and stops at a named step, so the test can kill the process there
/// (spec 005 R13.2, R13.5). Prints <c>AT {step}</c> when it reaches the step, then waits to be killed.
/// </summary>
/// <remarks>
/// <code>
/// create  {dir} {name} {step}           create a database
/// drop    {dir} {name} {step}           drop a database
/// rename  {dir} {name} {newName} {step} rename a database
/// install {dir} {name} {uid} {snapshot} {step} install a downloaded snapshot as a new replicated database (spec 011 R9)
/// reset   {dir} {step}                  reset the store
/// backup  {dir} {root} {step}           back up every database into the root, copying at 1 MB/s (spec 010 R9.4)
/// restore {dir} {root} {setId} {step}   restore a backup set (spec 010 R9.9)
/// hold    {dir}                         open the store, print OPEN, close it when stdin closes
/// tryopen {dir}                         print LOCKED or OPENED
/// holdroot {root}                       open a backup root, print HELD, release it when stdin closes (spec 010 R2.4)
/// </code>
/// A step of <c>none</c> runs to completion and prints <c>DONE</c>. A step ending in <c>+run</c> prints <c>AT</c> at the
/// step without stopping there, so the test kills the process at a moment after it (a backup's native copy).
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args[0];
        var dir = args[1];

        switch (command)
        {
            case "hold":
            {
                using var store = CrystalStore.Open(dir);
                Say("OPEN");
                Console.In.ReadToEnd();
                return 0;
            }

            case "holdroot":
            {
                using var root = CrystalBackupRoot.Open(dir);
                Say("HELD");
                Console.In.ReadToEnd();
                return 0;
            }

            case "tryopen":
                try
                {
                    using var store = CrystalStore.Open(dir);
                    Say("OPENED");
                }
                catch (StoreLockedException)
                {
                    Say("LOCKED");
                }

                return 0;
        }

        var stopAt = args[^1];
        var options = new CrystalOptions { Hook = step => StopIfAt(step, stopAt) };
        if (command == "reset")
        {
            CrystalStore.Reset(dir, options);
            Say("DONE");
            return 0;
        }

        using (var store = CrystalStore.Open(dir, options))
        {
            switch (command)
            {
                case "create":
                    store.CreateDatabase(args[2]);
                    break;
                case "drop":
                    store.DropDatabase(store.OpenDatabase(args[2]));
                    break;
                case "rename":
                    store.RenameDatabase(store.OpenDatabase(args[2]), args[3]);
                    break;
                case "install":
                    store.Engine.InstallBootstrap(store, args[3], args[2], args[4]);
                    break;
                case "backup":
                    Say("SET " + store.BackupAsync(args[2], new BackupOptions { RateLimitBytesPerSecond = 1 << 20 }).GetAwaiter().GetResult().SetId);
                    break;
                case "restore":
                    store.RestoreAsync(args[2], args[3]).GetAwaiter().GetResult();
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{command}'.");
            }
        }

        Say("DONE");
        return 0;
    }

    private static int _passed;

    private static void StopIfAt(string step, string stopAt)
    {
        if (stopAt.EndsWith("+run", StringComparison.Ordinal))
        {
            if (string.Equals(step, stopAt[..^"+run".Length], StringComparison.Ordinal) && Interlocked.Exchange(ref _passed, 1) == 0)
            {
                Say("AT " + step); // and carry on: the test kills the process during what follows
            }

            return;
        }

        if (string.Equals(step, stopAt, StringComparison.Ordinal))
        {
            Say("AT " + step);
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static void Say(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}
