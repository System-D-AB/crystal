// Spec 015 R2.4 (task T8) — the primary hard-killed while it writes, its standby promoted, the old primary rejoining. The
// failover test of spec 003 (FailoverTests) kills a primary at rest; this kills one with writes in flight, at a random
// moment, five times with the roles swapping, so a kill lands between a commit and its shipping, mid-page and mid-batch.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Nodes;

public sealed class PrimaryKillMidLoadTests
{
    private const int Kills = 5;
    private const int KeysPerBatch = 5;
    private static readonly TimeSpan CatchUp = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task APrimaryKilledMidLoad_ItsStandbyHoldsAWholePrefix_AndTheOldPrimaryRejoinsByteIdentical()
    {
        using var a = TestNode.Start();
        using var b = TestNode.Start();
        a.Listen();
        b.Listen();
        a.CreateDatabase("db");
        a.Box("db", "box");
        b.Follow(a);
        WaitUntilIdentical(a, b);

        var primary = a;
        var standby = b;
        var random = new Random(20261022);
        var diverged = 0;
        for (var kill = 0; kill < Kills; kill++)
        {
            // The new primary continues the stream from what it holds: batch numbers follow on, so every node's box is a
            // prefix of one stream of whole five-key batches.
            var next = primary.Count("db", "box") / KeysPerBatch;
            var first = next;
            using var stop = new CancellationTokenSource();
            var killing = 0;
            var writer = Task.Run(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        primary.PutBatch("db", "box", $"b{next:D5}", KeysPerBatch);
                        Interlocked.Increment(ref next);
                    }
                }
                catch (Exception) when (Volatile.Read(ref killing) == 1)
                {
                    // The primary was killed under the writer: expected. The flag is set before the kill, so the writer's
                    // failure is recognised however the kill and the failure are ordered (on Linux the failure can arrive
                    // before Kill returns, while the process still shows as running).
                }
            });

            // Kill once the writer has committed a random number of batches and is writing the next: mid-load by
            // construction, however fast or loaded the machine is (a fixed sleep was not, on a busy run).
            var killAfter = first + random.Next(3, 20);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref next) >= killAfter || writer.IsCompleted, TimeSpan.FromSeconds(30)),
                $"kill {kill + 1}: the writer did not reach {killAfter - first} batches in 30 s");
            Volatile.Write(ref killing, 1);
            primary.Kill();
            await stop.CancelAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(next - first > 1, $"kill {kill + 1}: the writer committed {next - first} batches, so the kill was not mid-load");

            standby.StopFollowing();
            standby.Promote();
            Assert.Equal("Primary", standby.Role());
            AssertWholePrefix(standby, $"kill {kill + 1}, the promoted standby");
            var promotedHolds = standby.Count("db", "box");

            // The old primary may hold batches that never reached the standby: rejoining, it is re-bootstrapped if it
            // diverged, and ends holding exactly what the new primary holds.
            primary.Restart();
            AssertWholePrefix(primary, $"kill {kill + 1}, the killed primary");
            if (primary.Count("db", "box") > promotedHolds)
            {
                diverged++; // it committed batches that never shipped: rejoining must discard them
            }

            primary.Listen();
            primary.Follow(standby);
            WaitUntilIdentical(standby, primary);
            Assert.Equal(standby.Digest("db", "box"), primary.Digest("db", "box"));

            (primary, standby) = (standby, primary);
        }

        Assert.True(diverged > 0, "no kill left the old primary holding batches its standby never received");
    }

    /// <summary>Keys sort as b00000-0 … b00000-4, b00001-0 …: a whole prefix of whole batches is exactly that sequence.</summary>
    private static void AssertWholePrefix(TestNode node, string who)
    {
        var keys = node.Keys("db", "box");
        Assert.True(keys.Length % KeysPerBatch == 0, $"{who}: a batch was torn, {keys.Length} keys is not a whole number of batches");
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.Equal($"b{i / KeysPerBatch:D5}-{i % KeysPerBatch}", keys[i]);
        }
    }

    private static void WaitUntilIdentical(TestNode primary, TestNode standby)
    {
        // A monotonic clock: a wall-clock jump (a suspended VM, an NTP step) must not end the wait early (spec 015 T8).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < CatchUp)
        {
            try
            {
                if (standby.Digest("db", "box") == primary.Digest("db", "box"))
                {
                    return;
                }
            }
            catch (NodeCommandException)
            {
                // still bootstrapping the database
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            "the standby did not become byte-identical to the primary in time. " +
            $"Primary: {primary.Count("db", "box")} keys, replication {primary.ReplicationStatus()}. " +
            $"Standby: {SafeCount(standby)} keys, replication {standby.ReplicationStatus()}.");

        static string SafeCount(TestNode node)
        {
            try
            {
                return node.Count("db", "box").ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                return $"? ({ex.Message})";
            }
        }
    }
}
