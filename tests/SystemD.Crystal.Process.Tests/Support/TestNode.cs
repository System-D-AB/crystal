using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SystemD.Crystal.Process.Tests.Support;

/// <summary>
/// A Crystal node in its own process (spec 004 R2.2, task T3): its own data directory and a free port, controlled by
/// start, stop, hard-kill, restart and status, and driven through the store's public API (create, put, get, count,
/// sync). The replication operations (promote, follow, demote, fence) are reserved: they fail until spec 003 builds
/// replication, and 003 fills them in here.
/// </summary>
internal sealed class TestNode : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly TempDirectory? _ownedDirectory;
    private readonly DurabilityMode _durability;
    private HostProcess? _process;
    private long _nextId;

    private TestNode(string dataDirectory, TempDirectory? ownedDirectory, int port, DurabilityMode durability)
    {
        DataDirectory = dataDirectory;
        _ownedDirectory = ownedDirectory;
        Port = port;
        _durability = durability;
    }

    /// <summary>The node's data directory; deleted on dispose if this node created it.</summary>
    public string DataDirectory { get; }

    /// <summary>The node's replication port, free when the node was created (nothing listens on it until spec 003).</summary>
    public int Port { get; }

    /// <summary>The store's node id, as the node reported it when it started.</summary>
    public string NodeId { get; private set; } = string.Empty;

    /// <summary>The operating-system process id while running.</summary>
    public int ProcessId => _process?.Id ?? throw new InvalidOperationException("The node is not running.");

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The exit code of the last process, once it has exited: 0 after <see cref="Stop"/>, never 0 after <see cref="Kill"/>.</summary>
    public int ExitCode => _process is { HasExited: true } p ? p.ExitCode : throw new InvalidOperationException("The node is running.");

    /// <summary>Starts a node in a new temporary directory (or <paramref name="dataDirectory"/>), on a free port.</summary>
    public static TestNode Start(string? dataDirectory = null, DurabilityMode durability = DurabilityMode.Strict)
    {
        var owned = dataDirectory is null ? new TempDirectory("crystal-node") : null;
        var node = new TestNode(dataDirectory ?? owned!.Path, owned, FreePort.Next(), durability);
        try
        {
            node.Launch();
            return node;
        }
        catch
        {
            node.Dispose();
            throw;
        }
    }

    /// <summary>Starts the node again on the same directory and port, after a stop or a kill.</summary>
    public void Restart()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Stop or kill the node before restarting it.");
        }

        _process?.Dispose();
        Launch();
    }

    /// <summary>A graceful stop: the node closes its store (final syncs included) and exits.</summary>
    public void Stop()
    {
        var process = _process ?? throw new InvalidOperationException("The node is not running.");
        Send("exit");
        if (!process.WaitForExit(CommandTimeout))
        {
            throw new TimeoutException("The node did not exit after 'exit'.");
        }
    }

    /// <summary>A hard kill: the process and its tree die at once, with no close, no sync, no flush.</summary>
    public void Kill()
    {
        var process = _process ?? throw new InvalidOperationException("The node is not running.");
        process.Kill(CommandTimeout);
    }

    /// <summary>The store's <c>Status()</c>, as JSON.</summary>
    public JsonNode Status() => Send("status") ?? throw new InvalidOperationException("The node returned no status.");

    public void CreateDatabase(string db) => Send("create", ("db", db));

    public uint Box(string db, string box) => Send("box", ("db", db), ("box", box))!.GetValue<uint>();

    public void Put(string db, string box, string key, string value, DurabilityMode? durability = null) =>
        Send("put", ("db", db), ("box", box), ("key", key), ("value", value), ("durability", durability?.ToString()));

    public string? Get(string db, string box, string key) => Send("get", ("db", db), ("box", box), ("key", key))?.GetValue<string>();

    public int Count(string db, string box) => Send("count", ("db", db), ("box", box))!.GetValue<int>();

    /// <summary>A SHA-256 of every key and value of the box: equal digests mean byte-identical boxes.</summary>
    public string Digest(string db, string box) => Send("digest", ("db", db), ("box", box))!.GetValue<string>();

    /// <summary>Every key of the box, in order.</summary>
    public string[] Keys(string db, string box) =>
        Send("keys", ("db", db), ("box", box))!.AsArray().Select(k => k!.GetValue<string>()).ToArray();

    public void SyncAll() => Send("sync");

    /// <summary>Promote this node to primary (spec 003); returns the new epoch.</summary>
    public long Promote() => Send("promote")!.GetValue<long>();

    /// <summary>Start this node's replication listener so standbys can pull from it; returns the bound endpoint.</summary>
    public string Listen() => Send("listen")!.GetValue<string>();

    /// <summary>Follow <paramref name="primary"/> as a standby (spec 003).</summary>
    public void Follow(TestNode primary) => Send("follow", ("endpoint", $"127.0.0.1:{primary.Port}"));

    /// <summary>Stop following a primary (spec 003).</summary>
    public void StopFollowing() => Send("stopfollowing");

    /// <summary>Schedule a rejoin of <paramref name="primary"/> on the next start (spec 003 R7.8).</summary>
    public void ScheduleRejoin(TestNode primary) => Send("rejoin", ("endpoint", $"127.0.0.1:{primary.Port}"));

    /// <summary>This node's replication role.</summary>
    public string Role() => Send("role")!.GetValue<string>();

    /// <summary>This node's replication epoch.</summary>
    public long Epoch() => Send("epoch")!.GetValue<long>();

    /// <summary>
    /// Sends one command and returns its result. A refusal by the node throws <see cref="NodeCommandException"/> with the
    /// exception type and message the node reported.
    /// </summary>
    public JsonNode? Send(string cmd, params (string Name, string? Value)[] fields)
    {
        var process = _process ?? throw new InvalidOperationException("The node is not running.");
        var id = ++_nextId;
        var request = new JsonObject { ["id"] = id, ["cmd"] = cmd };
        foreach (var (name, value) in fields)
        {
            if (value is not null)
            {
                request[name] = value;
            }
        }

        process.WriteLine(request.ToJsonString());
        var reply = JsonNode.Parse(process.ReadLine(CommandTimeout))!.AsObject();
        if (reply["id"]?.GetValue<long>() != id)
        {
            throw new InvalidOperationException($"The node answered request {reply["id"]} to request {id}.");
        }

        if (reply["ok"]!.GetValue<bool>())
        {
            return reply["result"];
        }

        throw new NodeCommandException(cmd, reply["error"]!.GetValue<string>(), reply["message"]!.GetValue<string>());
    }

    private void Launch()
    {
        _process = HostProcess.Start(
            "SystemD.Crystal.NodeHost.dll",
            "--dir", DataDirectory, "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--durability", _durability.ToString());
        var ready = _process.ReadLine(StartTimeout);
        if (!ready.StartsWith("READY ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The node did not start: '{ready}'. Its errors: {_process.Errors}");
        }

        var info = JsonNode.Parse(ready["READY ".Length..])!.AsObject();
        NodeId = info["nodeId"]!.GetValue<string>();
    }

    public void Dispose()
    {
        _process?.Dispose();
        _ownedDirectory?.Dispose();
    }
}

/// <summary>A command the node refused, with the exception it reported.</summary>
internal sealed class NodeCommandException(string command, string error, string message)
    : Exception($"The node refused '{command}': {error}: {message}")
{
    public string Command { get; } = command;

    public string Error { get; } = error;

    public string NodeMessage { get; } = message;
}
