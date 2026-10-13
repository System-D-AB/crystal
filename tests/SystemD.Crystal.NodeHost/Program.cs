using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SystemD.Crystal.NodeHost;

/// <summary>
/// One Crystal node in its own process (spec 004 R2.2). <c>TestNode</c> starts it, sends one JSON request per line on
/// standard input and reads one JSON reply per line on standard output.
/// </summary>
/// <remarks>
/// <code>
/// args:     --dir &lt;path&gt; --port &lt;n&gt; [--durability Strict|Interval|Buffered]
/// started:  READY {"pid":…,"port":…,"nodeId":"…"}
/// request:  {"id":1,"cmd":"put","db":"shop","box":"orders","key":"a","value":"v","durability":"Strict"}
/// reply:    {"id":1,"ok":true,"result":…}   or   {"id":1,"ok":false,"error":"TypeName","message":"…"}
/// </code>
/// Commands: <c>ping</c>, <c>create</c> (db), <c>box</c> (db, box), <c>put</c> (db, box, key, value, durability?),
/// <c>delete</c> (db, box, key), <c>get</c> (db, box, key), <c>count</c> (db, box), <c>sync</c>, <c>status</c>,
/// <c>exit</c>. Keys and values are UTF-8 text. <c>listen</c>, <c>follow</c>, <c>promote</c>, <c>demote</c> and
/// <c>fence</c> are reserved for spec 003 and refused until it exists. The port is the node's replication port,
/// chosen free by <c>TestNode</c> and reported back; nothing listens on it until spec 003.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? dir = null;
        var port = 0;
        var durability = DurabilityMode.Strict;
        string? certificate = null;
        string? trust = null;
        var requireClient = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir":
                    dir = args[++i];
                    break;
                case "--port":
                    port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--durability":
                    durability = Enum.Parse<DurabilityMode>(args[++i]);
                    break;
                case "--cert": // spec 011 T16: replication over TLS, the node's certificate (a PFX with its key)
                    certificate = args[++i];
                    break;
                case "--trust": // the CA certificate peers must chain to
                    trust = args[++i];
                    break;
                case "--require-client-cert":
                    requireClient = true;
                    break;
            }
        }

        if (dir is null)
        {
            Console.Error.WriteLine("--dir is required");
            return 2;
        }

        // Spec 003: the node binds its replication port, so a standby can pull from it and it can be promoted.
        var replication = new ReplicationOptions
        {
            Secret = "crystal-test-secret",
            ListenEndpoint = $"127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Certificate = certificate is null ? null : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certificate, null),
            TrustedCertificates = trust is null ? null : [System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(trust)],
            RequireClientCertificate = requireClient,
        };
        var store = CrystalStore.Open(dir, new CrystalOptions { Durability = durability, Replication = replication });
        var node = new Node(store);
        Say("READY " + new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["port"] = port,
            ["nodeId"] = store.NodeId,
        }.ToJsonString());

        while (Console.In.ReadLine() is { } line)
        {
            JsonObject request;
            try
            {
                request = JsonNode.Parse(line)!.AsObject();
            }
            catch (JsonException ex)
            {
                Say(new JsonObject { ["id"] = null, ["ok"] = false, ["error"] = nameof(JsonException), ["message"] = ex.Message }.ToJsonString());
                continue;
            }

            var id = request["id"]?.GetValue<long>();
            var cmd = request["cmd"]?.GetValue<string>() ?? string.Empty;
            JsonObject reply;
            try
            {
                var result = cmd == "exit" ? null : node.Run(cmd, request);
                reply = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result };
            }
            catch (Exception ex)
            {
                reply = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message };
            }

            if (cmd == "exit")
            {
                try
                {
                    store.Dispose();
                }
                catch (Exception ex)
                {
                    reply = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message };
                }

                Say(reply.ToJsonString());
                return 0;
            }

            Say(reply.ToJsonString());
        }

        store.Dispose();
        return 0;
    }

    private static void Say(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private sealed class Node(CrystalStore store)
    {
        private readonly Dictionary<string, CrystalDatabase> _databases = new(StringComparer.Ordinal);

        public JsonNode? Run(string cmd, JsonObject request)
        {
            switch (cmd)
            {
                case "ping":
                    return "pong";
                case "create":
                    _databases[Text(request, "db")] = store.CreateDatabase(Text(request, "db"));
                    return null;
                case "box":
                {
                    var db = Database(request);
                    return (db.Boxes.Find(Text(request, "box")) ?? db.Boxes.Create(Text(request, "box"))).Id;
                }

                case "put":
                {
                    var (db, box) = Box(request);
                    using var batch = db.NewBatch();
                    batch.Put(box, System.Text.Encoding.UTF8.GetBytes(Text(request, "key")), System.Text.Encoding.UTF8.GetBytes(Text(request, "value")));
                    if (request["durability"]?.GetValue<string>() is { } mode)
                    {
                        batch.Commit(Enum.Parse<DurabilityMode>(mode));
                    }
                    else
                    {
                        batch.Commit();
                    }

                    return null;
                }

                case "stepdown": // spec 011 R14: hand over to the standby named, waiting up to 30 s
                {
                    var handedOver = store.Replication.StepDownAsync(Text(request, "standby"), TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    return handedOver.Count;
                }

                case "putbatch":
                {
                    // Several keys in one batch: {prefix}-0 … {prefix}-(count-1) (spec 011 R12.2: a torn page is visible
                    // only as a batch with some of its keys missing).
                    var (db, box) = Box(request);
                    var prefix = Text(request, "prefix");
                    var count = int.Parse(Text(request, "count"), System.Globalization.CultureInfo.InvariantCulture);
                    using var batch = db.NewBatch();
                    for (var i = 0; i < count; i++)
                    {
                        batch.Put(box, System.Text.Encoding.UTF8.GetBytes($"{prefix}-{i}"), System.Text.Encoding.UTF8.GetBytes("v"));
                    }

                    batch.Commit();
                    return null;
                }

                case "delete":
                {
                    var (db, box) = Box(request);
                    using var batch = db.NewBatch();
                    batch.Delete(box, System.Text.Encoding.UTF8.GetBytes(Text(request, "key")));
                    batch.Commit();
                    return null;
                }

                case "get":
                {
                    var (db, box) = Box(request);
                    using var snapshot = db.Snapshot();
                    var value = snapshot.Get(box, System.Text.Encoding.UTF8.GetBytes(Text(request, "key")));
                    return value is null ? null : System.Text.Encoding.UTF8.GetString(value);
                }

                case "count":
                {
                    var (db, box) = Box(request);
                    using var snapshot = db.Snapshot();
                    return snapshot.Scan(box, [], [], int.MaxValue).Count();
                }

                case "digest":
                {
                    // SHA-256 over every key and value of the box, in order, each length-prefixed: equal digests mean
                    // byte-identical boxes (spec 003 R14.1).
                    var (db, box) = Box(request);
                    using var snapshot = db.Snapshot();
                    using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                    Span<byte> length = stackalloc byte[4];
                    foreach (var kv in snapshot.Scan(box, [], [], int.MaxValue))
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, kv.Key.Length);
                        sha.AppendData(length);
                        sha.AppendData(kv.Key);
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, kv.Value.Length);
                        sha.AppendData(length);
                        sha.AppendData(kv.Value);
                    }

                    return Convert.ToHexString(sha.GetHashAndReset());
                }

                case "keys":
                {
                    var (db, box) = Box(request);
                    using var snapshot = db.Snapshot();
                    var keys = new JsonArray();
                    foreach (var kv in snapshot.Scan(box, [], [], int.MaxValue))
                    {
                        keys.Add(System.Text.Encoding.UTF8.GetString(kv.Key));
                    }

                    return keys;
                }

                case "sync":
                    store.SyncAll();
                    return null;
                case "status":
                    return JsonSerializer.SerializeToNode(store.Status());
                case "listen":
                    store.Replication.Listen();
                    return store.Replication.ListenerEndpoint;
                case "follow":
                    store.Replication.Follow(Text(request, "endpoint"));
                    return null;
                case "stopfollowing":
                    store.Replication.StopFollowing();
                    return null;
                case "promote":
                    return store.Replication.Promote(request["reason"]?.GetValue<string>() ?? "test");
                case "role":
                    return store.Replication.Role.ToString();
                case "epoch":
                    return store.Replication.Epoch;
                case "demote":
                    store.Replication.Demote();
                    return null;
                case "fence":
                    store.Replication.Fence(request["reason"]?.GetValue<string>() ?? "test");
                    return null;
                case "rejoin":
                    store.Replication.ScheduleRejoin(Text(request, "endpoint"));
                    return null;
                default:
                    throw new ArgumentException($"Unknown command '{cmd}'.");
            }
        }

        private CrystalDatabase Database(JsonObject request)
        {
            // Opened by name each time (it returns the open handle): a database replication reinstalled from a snapshot
            // has a new handle, and a kept one would be refused.
            var name = Text(request, "db");
            var db = store.OpenDatabase(name);
            _databases[name] = db;
            return db;
        }

        private (CrystalDatabase Db, Box Box) Box(JsonObject request)
        {
            var db = Database(request);
            var name = Text(request, "box");
            var box = db.Boxes.Find(name) ?? throw new BoxNotFoundException(name, $"Box '{name}' does not exist.");
            return (db, box);
        }

        private static string Text(JsonObject request, string field) =>
            request[field]?.GetValue<string>() ?? throw new ArgumentException($"'{field}' is required.");
    }
}
