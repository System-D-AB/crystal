// Spec 003 R11.1 (task T2, completed 2026-10-10) — a replicated database keeps its WAL by age and size, within a
// store-wide budget: each replicated database's size limit, set when it opens, is an equal share of the budget, capped at
// the per-database size and never below 1 MiB. RocksDB refuses to change the limit on an open database, so a database
// already open keeps its share until it next opens.

using System;
using System.IO;
using System.Linq;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class WalBudgetTests
{
    private const long MiB = 1024 * 1024;

    private static CrystalOptions Options(long perDatabase, long budget) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            WalBytesPerDatabase = perDatabase,
            WalBudgetBytes = budget,
        },
    };

    [Fact]
    public void EachReplicatedDatabase_GetsAnEqualShareOfTheBudget_CappedAtItsOwnLimit()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(perDatabase: 100 * MiB, budget: 120 * MiB));

        var a = store.CreateDatabase("a", replicated: true);
        Assert.Equal(100, WalSizeLimitMb(dir, a)); // alone: its own limit, under the budget

        var b = store.CreateDatabase("b", replicated: true);
        Assert.Equal(60, WalSizeLimitMb(dir, b));  // two share 120

        var own = store.CreateDatabase("own", replicated: false);
        Assert.Equal(0, WalSizeLimitMb(dir, own)); // not replicated: no retention, and it takes no share
        var c = store.CreateDatabase("c", replicated: true);
        Assert.Equal(40, WalSizeLimitMb(dir, c));  // three share 120
    }

    [Fact]
    public void AShare_IsNeverBelowOneMiB()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(perDatabase: 100 * MiB, budget: 2 * MiB));
        var databases = Enumerable.Range(0, 3).Select(i => store.CreateDatabase($"db{i}", replicated: true)).ToList();

        Assert.Equal(2, WalSizeLimitMb(dir, databases[0])); // alone: the whole 2 MiB budget
        Assert.Equal(1, WalSizeLimitMb(dir, databases[1])); // two share 2 MiB
        Assert.Equal(1, WalSizeLimitMb(dir, databases[2])); // three would get 0.67 MiB: floored at 1 MiB
    }

    [Fact]
    public void ADatabaseOpenedLater_GetsTheCurrentShare()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path, Options(perDatabase: 100 * MiB, budget: 120 * MiB)))
        {
            store.CreateDatabase("a", replicated: true);
            store.CreateDatabase("b", replicated: true);
            store.CreateDatabase("c", replicated: true);
        }

        using (var store = CrystalStore.Open(dir.Path, Options(perDatabase: 100 * MiB, budget: 120 * MiB)))
        {
            Assert.Equal(40, WalSizeLimitMb(dir, store.OpenDatabase("b")));
        }
    }

    /// <summary>The WAL size limit RocksDB holds for the database, from the newest OPTIONS file it wrote.</summary>
    private static long WalSizeLimitMb(TempStoreDirectory dir, CrystalDatabase db)
    {
        var folder = dir.DatabaseFolder(db.Id);
        var newest = Directory.GetFiles(folder, "OPTIONS-*")
            .OrderBy(f => long.Parse(Path.GetFileName(f)["OPTIONS-".Length..], System.Globalization.CultureInfo.InvariantCulture))
            .Last();
        var line = File.ReadLines(newest).First(l => l.Trim().StartsWith("WAL_size_limit_MB=", StringComparison.Ordinal));
        return long.Parse(line.Trim()["WAL_size_limit_MB=".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }
}
