// Spec 003 R1.4 (should-fix 2 of the 2026-10-10 review): a standby's set of replicated databases follows the primary's
// through every change of names — a swap, a drop and a new database under the same name, a chain of renames, a rename
// and a new database under the old name — matching the primary by UID and by data, never by name alone.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class ReconcileTests
{
    private static CrystalOptions Options(string? listen = null) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public void ASwap_OnThePrimary_IsAppliedOnTheStandby()
    {
        using var pair = Pair.Start("a", "b");
        pair.Primary.SwapDatabases(pair.Primary.OpenDatabase("a"), pair.Primary.OpenDatabase("b"));
        pair.WaitUntilMatched();
    }

    [Fact]
    public void ADropAndANewDatabaseUnderTheSameName_ReplaceItOnTheStandby()
    {
        using var pair = Pair.Start("a", "b");
        var oldUid = pair.Primary.OpenDatabase("a").Uid;
        pair.Primary.DropDatabase(pair.Primary.OpenDatabase("a"));
        var fresh = pair.Primary.CreateDatabase("a", replicated: true);
        Put(fresh, "new-a", "1");

        pair.WaitUntilMatched();
        Assert.DoesNotContain(pair.Standby.ListDatabases(), d => d.Uid == oldUid);
    }

    [Fact]
    public void AChainOfRenames_IsAppliedOnTheStandby()
    {
        using var pair = Pair.Start("a", "b");
        pair.Primary.RenameDatabase(pair.Primary.OpenDatabase("b"), "c");
        pair.Primary.RenameDatabase(pair.Primary.OpenDatabase("a"), "b");
        pair.WaitUntilMatched();
    }

    [Fact]
    public void ARenameAndANewDatabaseUnderTheOldName_AreBothAppliedOnTheStandby()
    {
        using var pair = Pair.Start("a");
        pair.Primary.RenameDatabase(pair.Primary.OpenDatabase("a"), "x");
        var fresh = pair.Primary.CreateDatabase("a", replicated: true);
        Put(fresh, "new-a", "1");
        pair.WaitUntilMatched();
    }

    [Fact]
    public void ANameClash_IsFollowedOnceTheStandbyFreesTheName_WithNoChangeOnThePrimary()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options());
        var replicated = primary.CreateDatabase("c", replicated: true);
        Put(replicated, "from-primary", "1");
        var own = standby.CreateDatabase("c", replicated: false); // the standby's own database holds the name
        primary.Replication.Listen();
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        Thread.Sleep(1000);
        Assert.Single(standby.ListDatabases(), d => d.Name == "c" && !d.Replicated);

        standby.DropDatabase(own); // the product frees the name; the primary changes nothing

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30)
               && !standby.ListDatabases().Any(d => d.Name == "c" && d.Uid == replicated.Uid))
        {
            Thread.Sleep(50);
        }

        Assert.Contains(standby.ListDatabases(), d => d.Name == "c" && d.Uid == replicated.Uid);
    }

    [Fact]
    public void AnInstall_NeverTakesANameAnotherDatabaseHolds()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options());
        using var standby = CrystalStore.Open(sdir.Path, Options());
        var source = primary.CreateDatabase("taken", replicated: true);
        var holder = standby.CreateDatabase("taken", replicated: true);
        Put(holder, "the-holder", "1");
        var snapshot = System.IO.Path.Combine(sdir.Path, "bootstrap-test");
        new SystemD.Crystal.Storage.WalAccess(source.Slot.Instance).Checkpoint(snapshot);

        Assert.Throws<DatabaseNameTakenException>(() => standby.Engine.InstallBootstrap(standby, source.Uid, "taken", snapshot));

        var stillThere = standby.OpenDatabase("taken");
        Assert.Equal(holder.Uid, stillThere.Uid);
        Assert.Equal(["the-holder"], Rows(stillThere));
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Pair : IDisposable
    {
        private readonly TempStoreDirectory _pdir = new();
        private readonly TempStoreDirectory _sdir = new();

        private Pair()
        {
            Primary = CrystalStore.Open(_pdir.Path, Options(listen: "127.0.0.1:0"));
            Standby = CrystalStore.Open(_sdir.Path, Options());
        }

        public CrystalStore Primary { get; }

        public CrystalStore Standby { get; }

        public static Pair Start(params string[] names)
        {
            var pair = new Pair();
            foreach (var name in names)
            {
                var db = pair.Primary.CreateDatabase(name, replicated: true);
                Put(db, $"of-{name}", name);
            }

            pair.Primary.Replication.Listen();
            pair.Standby.Replication.Follow(pair.Primary.Replication.ListenerEndpoint!);
            pair.WaitUntilMatched();
            return pair;
        }

        /// <summary>Until the standby holds the primary's databases under the same names, UIDs and data.</summary>
        public void WaitUntilMatched()
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (Describe(Standby).SequenceEqual(Describe(Primary)))
                {
                    return;
                }

                Thread.Sleep(50);
            }

            Assert.Equal(Describe(Primary), Describe(Standby));
        }

        private static List<string> Describe(CrystalStore store)
        {
            try
            {
                return store.ListDatabases()
                    .OrderBy(d => d.Name, StringComparer.Ordinal)
                    .Select(d => $"{d.Name}={d.Uid}:{string.Join(",", Rows(store.OpenDatabase(d.Name)))}")
                    .ToList();
            }
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException or NullReferenceException or CrystalFormatException)
            {
                return ["(changing)"];
            }
        }

        public void Dispose()
        {
            Standby.Dispose();
            Primary.Dispose();
            _sdir.Dispose();
            _pdir.Dispose();
        }
    }

    private static void Put(CrystalDatabase db, string key, string value)
    {
        var box = db.Boxes.Find("box") ?? db.Boxes.Create("box");
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    private static List<string> Rows(CrystalDatabase db)
    {
        if (db.Boxes.Find("box") is not { } box)
        {
            return [];
        }

        using var snapshot = db.Snapshot();
        return snapshot.Scan(box, [], [], int.MaxValue).Select(kv => Text.UTF8.GetString(kv.Key)).ToList();
    }
}
