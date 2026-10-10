// Spec 003 T16 / R14.1, R14.2 — two-process failover and hard kills, on 004's TestNode (rewritten 2026-10-10 to the
// task's done criteria: 20 failovers in a row, compared byte for byte, half of them with writes the old primary never
// replicated; 20 hard kills of a standby while the primary writes, each leaving a whole prefix of the stream).

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Nodes;

public sealed class FailoverTests
{
    private const int FailoverCycles = 20;
    private const int Kills = 20;
    private static readonly string[] Databases = ["alpha", "beta", "gamma"];
    private static readonly TimeSpan CatchUp = TimeSpan.FromSeconds(60);

    /// <summary>
    /// R14.1: kill the primary, promote the standby, lose nothing that was replicated, and have the old primary rejoin
    /// as a standby of the new one and end byte-identical — 20 times in a row, the roles swapping each time. On every
    /// other cycle the old primary first commits writes that never reach the standby, and the new primary writes more
    /// than that before the old one rejoins: the rejoining node must be re-bootstrapped (divergence, R5.2), never left
    /// holding writes the new primary never had.
    /// </summary>
    [Fact]
    public void Failover_TwentyTimes_LosesNothingReplicated_AndTheOldPrimaryRejoinsByteIdentical()
    {
        using var a = TestNode.Start();
        using var b = TestNode.Start();

        a.Listen();
        foreach (var db in Databases)
        {
            a.CreateDatabase(db);
            a.Box(db, "box");
            a.Put(db, "box", "k0", "v0");
        }

        b.Listen();
        b.Follow(a);
        WaitUntilIdentical(a, b);

        var primary = a;
        var standby = b;
        for (var cycle = 0; cycle < FailoverCycles; cycle++)
        {
            for (var i = 0; i < 9; i++)
            {
                primary.Put(Databases[i % 3], "box", $"c{cycle:D2}-{i}", $"replicated in cycle {cycle}");
            }

            primary.SyncAll();
            WaitUntilIdentical(primary, standby);
            var replicated = Databases.ToDictionary(d => d, d => standby.Digest(d, "box"));

            var unreplicated = cycle % 2 == 1;
            if (unreplicated)
            {
                // The standby stops receiving; the primary commits writes that will never reach it.
                standby.StopFollowing();
                foreach (var db in Databases)
                {
                    primary.Put(db, "box", $"lost-c{cycle:D2}", "only on the old primary");
                }

                primary.SyncAll();
            }

            primary.Kill();
            standby.StopFollowing();
            standby.Promote();
            Assert.Equal("Primary", standby.Role());

            // Nothing that was replicated is lost: the promoted node holds exactly what it had, byte for byte.
            foreach (var db in Databases)
            {
                Assert.Equal(replicated[db], standby.Digest(db, "box"));
            }

            // The new primary writes more than the old one may hold beyond it, so positions alone cannot tell them apart.
            for (var i = 0; i < 6; i++)
            {
                standby.Put(Databases[i % 3], "box", $"c{cycle:D2}-new-{i}", $"written by the new primary in cycle {cycle}");
            }

            // The restarted node listens again, so that once it is promoted in a later cycle its standby can reach it.
            primary.Restart();
            primary.Listen();
            primary.Follow(standby);
            WaitUntilIdentical(standby, primary);

            foreach (var db in Databases)
            {
                Assert.Equal(standby.Digest(db, "box"), primary.Digest(db, "box"));
                Assert.DoesNotContain(primary.Keys(db, "box"), k => k.StartsWith("lost-", StringComparison.Ordinal));
            }

            (primary, standby) = (standby, primary);
        }
    }

    /// <summary>
    /// R14.2: a standby hard-killed while it applies the primary's stream comes back holding a whole prefix of that
    /// stream — every batch whole, none missing before the last it holds — and re-follows to byte-identical. 20 kills,
    /// each at a different moment, while the primary keeps writing.
    /// </summary>
    [Fact]
    public async Task TwentyHardKillsOfAFollowingStandby_EachLeaveAWholePrefix_ThatReFollowsToByteIdentical()
    {
        using var primary = TestNode.Start();
        using var standby = TestNode.Start();

        primary.Listen();
        primary.CreateDatabase("db");
        primary.Box("db", "box");
        standby.Follow(primary);

        var written = 0;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            // One row per batch, keys in order: a whole prefix is k00000 … k(n-1) with no gap.
            while (!stop.IsCancellationRequested)
            {
                primary.Put("db", "box", $"k{written:D5}", $"v{written:D5}");
                Interlocked.Increment(ref written);
            }
        });

        try
        {
            var random = new Random(20261010);
            for (var kill = 0; kill < Kills; kill++)
            {
                Thread.Sleep(50 + random.Next(250));
                standby.Kill();
                standby.Restart();

                // Whatever survived is a whole prefix of the stream, in order, with no holes. Killed before its first
                // bootstrap finished, the standby holds no database at all: the empty prefix.
                string[] keys;
                try
                {
                    keys = standby.Keys("db", "box");
                }
                catch (NodeCommandException ex) when (ex.Error is "DatabaseNotFoundException" or "BoxNotFoundException")
                {
                    keys = [];
                }

                for (var i = 0; i < keys.Length; i++)
                {
                    Assert.Equal($"k{i:D5}", keys[i]);
                }

                standby.Follow(primary);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(30));
        }

        primary.SyncAll();
        WaitUntilIdentical(primary, standby, "db");
        Assert.Equal(primary.Digest("db", "box"), standby.Digest("db", "box"));
        Assert.True(Volatile.Read(ref written) > Kills, "the primary kept writing through the kills");
    }

    private static void WaitUntilIdentical(TestNode primary, TestNode standby) =>
        WaitUntilIdentical(primary, standby, Databases);

    private static void WaitUntilIdentical(TestNode primary, TestNode standby, params string[] databases)
    {
        var deadline = DateTime.UtcNow.Add(CatchUp);
        while (DateTime.UtcNow < deadline)
        {
            if (databases.All(Identical))
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            "the standby did not become byte-identical to the primary in time. " + string.Join("; ", databases.Select(db =>
                $"{db}: primary [{Describe(primary, db)}], standby [{Describe(standby, db)}]")) +
            $". Standby status: {Safe(() => standby.Status().ToJsonString())}");

        static string Describe(TestNode node, string db) => Safe(() => string.Join(",", node.Keys(db, "box")));

        static string Safe(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        bool Identical(string db)
        {
            try
            {
                return standby.Digest(db, "box") == primary.Digest(db, "box");
            }
            catch (NodeCommandException)
            {
                return false; // still bootstrapping the database
            }
        }
    }
}
