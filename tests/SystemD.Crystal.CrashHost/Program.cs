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
/// hold    {dir}                         open the store, print OPEN, close it when stdin closes
/// tryopen {dir}                         print LOCKED or OPENED
/// </code>
/// A step of <c>none</c> runs to completion and prints <c>DONE</c>.
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
                default:
                    throw new ArgumentException($"Unknown command '{command}'.");
            }
        }

        Say("DONE");
        return 0;
    }

    private static void StopIfAt(string step, string stopAt)
    {
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
