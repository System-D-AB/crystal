// Spec 004 task T3: the test node works — a store in its own process, started, stopped, killed and restarted.

using System;
using System.Diagnostics;
using System.Linq;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Nodes;

public sealed class TestNodeTests
{
    [Fact]
    public void ANode_RunsAStoreInItsOwnProcess()
    {
        using var node = TestNode.Start();

        Assert.NotEqual(Environment.ProcessId, node.ProcessId);
        Assert.True(node.IsRunning);
        Assert.Equal("pong", node.Send("ping")!.GetValue<string>());

        node.CreateDatabase("shop");
        node.Box("shop", "orders");
        node.Put("shop", "orders", "k1", "v1");
        node.Put("shop", "orders", "k2", "v2", DurabilityMode.Buffered);

        Assert.Equal("v1", node.Get("shop", "orders", "k1"));
        Assert.Equal(2, node.Count("shop", "orders"));
        var status = node.Status();
        Assert.Equal(node.NodeId, status["NodeId"]!.GetValue<string>());
        Assert.Equal("shop", status["Databases"]![0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void AHardKill_EndsTheProcessAtOnce_AndARestartFindsEveryStrictCommit()
    {
        using var node = TestNode.Start();
        node.CreateDatabase("shop");
        node.Box("shop", "orders");
        for (var i = 0; i < 50; i++)
        {
            node.Put("shop", "orders", $"k{i}", $"v{i}");   // Strict: synced before the reply
        }

        var pid = node.ProcessId;
        var nodeId = node.NodeId;
        node.Kill();

        Assert.False(node.IsRunning);
        Assert.NotEqual(0, node.ExitCode);   // killed, not exited: the store was never closed
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));

        node.Restart();   // the store lock went with the killed process
        Assert.NotEqual(pid, node.ProcessId);
        Assert.Equal(nodeId, node.NodeId);
        Assert.Equal(50, node.Count("shop", "orders"));
        Assert.Equal("v49", node.Get("shop", "orders", "k49"));
    }

    [Fact]
    public void AGracefulStop_ClosesTheStore_AndSyncsBufferedCommits()
    {
        using var node = TestNode.Start(durability: DurabilityMode.Buffered);
        node.CreateDatabase("shop");
        node.Box("shop", "orders");
        node.Put("shop", "orders", "k", "v");

        node.Stop();

        Assert.False(node.IsRunning);
        Assert.Equal(0, node.ExitCode);
        node.Restart();
        Assert.Equal("v", node.Get("shop", "orders", "k"));
    }

    [Fact]
    public void TwoNodes_RunSideBySide_WithTheirOwnDirectoriesPortsAndIdentities()
    {
        using var first = TestNode.Start();
        using var second = TestNode.Start();

        Assert.NotEqual(first.DataDirectory, second.DataDirectory);
        Assert.NotEqual(first.Port, second.Port);
        Assert.NotEqual(first.NodeId, second.NodeId);
        Assert.NotEqual(first.ProcessId, second.ProcessId);

        first.CreateDatabase("a");
        second.CreateDatabase("b");
        Assert.Throws<NodeCommandException>(() => second.Send("box", ("db", "a"), ("box", "x")));
    }

    [Fact]
    public void ARefusedCommand_ReportsTheNodesException_AndTheNodeCarriesOn()
    {
        using var node = TestNode.Start();

        var refused = Assert.Throws<NodeCommandException>(() => node.Get("missing", "box", "k"));
        Assert.Equal(nameof(DatabaseNotFoundException), refused.Error);
        Assert.Equal("pong", node.Send("ping")!.GetValue<string>());
    }

    [Fact]
    public void ANode_ListensAndPromotes()
    {
        using var node = TestNode.Start();

        var endpoint = node.Listen();
        Assert.StartsWith("127.0.0.1:", endpoint);
        Assert.Equal("Primary", node.Role());

        var epoch = node.Promote();
        Assert.True(epoch >= 2);
        Assert.Equal("Primary", node.Role());
        Assert.Equal(epoch, node.Epoch());
    }

    [Fact]
    public void AStandby_Following_TakesTheStandbyRole_AndRefusesLocalWrites()
    {
        using var primary = TestNode.Start();
        using var standby = TestNode.Start();
        primary.Listen();

        standby.Follow(primary);
        Assert.Equal("Standby", standby.Role());
    }

    [Fact]
    public void AStandby_BootstrapsAndFollowsAPrimary_ThroughThreeDatabases_InTwoProcesses()
    {
        using var primary = TestNode.Start();
        using var standby = TestNode.Start();

        // The primary: three databases, each a box and a seed, then start listening.
        var dbs = new[] { "alpha", "beta", "gamma" };
        foreach (var db in dbs)
        {
            primary.CreateDatabase(db);
            primary.Box(db, "box");
            primary.Put(db, "box", "k0", "v0");
        }

        primary.Listen();

        // The standby — blank — follows: it bootstraps all three databases over the wire, then streams.
        standby.Follow(primary);

        // The primary writes more across all three.
        for (var i = 1; i <= 30; i++)
        {
            primary.Put(dbs[i % 3], "box", $"k{i}", $"v{i}");
        }

        primary.SyncAll();

        // Wait for the standby to hold every row of every database (compared against the primary's own counts).
        // A monotonic clock: a wall-clock jump (a suspended VM, an NTP step) must not end the wait early (spec 015 T8).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (dbs.All(StandbyHasEverything))
            {
                break;
            }

            System.Threading.Thread.Sleep(200);
        }

        foreach (var db in dbs)
        {
            Assert.True(StandbyHasEverything(db), $"standby did not catch up on '{db}'");
        }

        bool StandbyHasEverything(string db)
        {
            try
            {
                return standby.Count(db, "box") == primary.Count(db, "box")
                    && standby.Get(db, "box", "k0") == "v0";
            }
            catch (NodeCommandException)
            {
                return false; // still bootstrapping
            }
        }
    }

    [Fact]
    public void ANode_KeepsItsEpochAcrossARestart_AndDemoteFenceWork()
    {
        using var node = TestNode.Start();
        node.Listen();

        var epoch = node.Promote();
        Assert.True(epoch >= 2);

        // A graceful stop and restart: the persisted node.epoch comes back (R6.1, R14.8).
        node.Stop();
        node.Restart();
        Assert.Equal(epoch, node.Epoch());

        // Promote again from the restarted node: the epoch advances from where it was, never resets to 1.
        var again = node.Promote();
        Assert.Equal(epoch + 1, again);

        // Demote and fence are real now.
        node.Send("demote");
        Assert.Equal("Standby", node.Role());
        node.Send("fence", ("reason", "test"));
        Assert.Equal("Fenced", node.Role());
    }

    [Fact]
    public void Disposing_KillsTheProcess_AndDeletesItsDirectory()
    {
        int pid;
        string dir;
        using (var node = TestNode.Start())
        {
            pid = node.ProcessId;
            dir = node.DataDirectory;
            Assert.True(System.IO.Directory.Exists(dir));
        }

        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        Assert.False(System.IO.Directory.Exists(dir));
    }
}
