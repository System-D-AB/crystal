// Spec 011 R4 — size limits never stop replication silently. A batch too large for any reply is reported "oversize" and
// that database alone re-bootstraps (its snapshot carries the batch), while the others keep following (R4.1). The
// request cap after AUTH is sized from the databases a store can open, so a PULL naming all of them is accepted (R4.2).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class SizeLimitTests
{
    private const string Secret = "test-secret";

    private static CrystalOptions Options(string? listen = null, int maxOpen = 256) => new()
    {
        MaxOpenDatabases = maxOpen,
        Replication = new ReplicationOptions
        {
            Secret = Secret,
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public void AnOversizeBatch_ReBootstrapsItsDatabaseAlone_AndTheOthersKeepApplying()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var big = Created(primary, "big");
        var small = Created(primary, "small");
        primary.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options());
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => Rows(standby, "big").Count == 1 && Rows(standby, "small").Count == 1, TimeSpan.FromSeconds(30));

        // The cap lowered for the test (no test commits a 32 MiB batch): a 10 KB batch is now too large to ship.
        primary.Replication.Source!.MaxShippableBatch = 4096;
        Put(big, "large", new string('x', 10_000));
        Put(small, "after", "1");

        WaitUntil(() => Rows(standby, "big").Contains("large=" + new string('x', 10_000)) && Rows(standby, "small").Contains("after=1"),
            TimeSpan.FromSeconds(60));
        Assert.Contains("large=" + new string('x', 10_000), Rows(standby, "big"));
        Assert.Contains("after=1", Rows(standby, "small"));

        EventDelivery.Drain(standby);
        lock (events)
        {
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.Gap && e.Uid == big.Uid && e.Detail.Contains("oversize", StringComparison.Ordinal));
            Assert.DoesNotContain(events, e => e.Kind is ReplicationEventKind.Gap or ReplicationEventKind.Divergence && e.Uid == small.Uid);
        }
    }

    [Fact]
    public async Task APullNamingEveryDatabaseTheStoreCanOpen_IsAccepted()
    {
        // 1 000 databases: about 75 KB of (uid, position) pairs, past the old fixed 64 KiB cap.
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0", maxOpen: 1000));
        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        var client = new ReplicationClient(tcp.GetStream(), "standby-1", Secret);
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        var pull = new List<byte[]> { B(ReplicationProtocol.Pull), B("1048576"), B("0") };
        for (var i = 0; i < 1000; i++)
        {
            pull.Add(B(Guid.NewGuid().ToString("D")));
            pull.Add(B(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var reply = await client.RequestAsync(pull, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RespType.Array, reply.Type);
        var entries = reply.Items![2].Items!;
        Assert.Equal(1000, entries.Count); // one entry per database named (each "gone": no such database here)
    }

    private static CrystalDatabase Created(CrystalStore store, string name)
    {
        var db = store.CreateDatabase(name, replicated: true);
        db.Boxes.Create("box");
        Put(db, "seed", "0");
        return db;
    }

    private static void Put(CrystalDatabase db, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(db.Boxes.Find("box")!.Value, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    private static List<string> Rows(CrystalStore store, string name)
    {
        try
        {
            var db = store.OpenDatabase(name);
            using var snapshot = db.Snapshot();
            return snapshot.Scan(db.Boxes.Find("box")!.Value, [], [], int.MaxValue)
                .Select(kv => Text.UTF8.GetString(kv.Key) + "=" + Text.UTF8.GetString(kv.Value)).ToList();
        }
        catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException or CrystalFormatException or NullReferenceException or NotPrimaryException)
        {
            return [];
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
        {
            Thread.Sleep(50);
        }
    }

    private static byte[] B(string text) => Text.UTF8.GetBytes(text);
}
