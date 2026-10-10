// Spec 003 T10 / R7; design § 7 — snapshot bootstrap end to end over a real socket: a BLANK standby acquires the
// primary's databases by snapshot (the follower's reconcile CreateAndBootstrap path), verifying size and CRC-32C per
// file, installing atomically, recording the follow point, then following to byte-identical. Also the primary
// session surface (BEGIN/GET/END) and CRC verification directly. This is the scenario T9 deferred: the standby
// starts with nothing and ends holding three databases identical to the primary.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class SnapshotBootstrapTests
{
    private static readonly string[] DbNames = ["alpha", "beta", "gamma"];

    private static CrystalOptions Options(string? listen) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(500),
        },
    };

    private static void Write(CrystalDatabase db, Box box, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    [Fact]
    public void ABlankStandby_BootstrapsThreeDatabases_OverTheWire_AndEndsByteIdentical()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();

        // 1. Primary: three databases, each a box and several rows, listening.
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var primaryDbs = new List<CrystalDatabase>();
        var primaryBoxes = new List<Box>();
        foreach (var name in DbNames)
        {
            var db = primary.CreateDatabase(name, replicated: true);
            var box = db.Boxes.Create("box");
            for (var i = 0; i < 20; i++)
            {
                Write(db, box, $"k{i:D4}", $"v{i:D4}");
            }

            primaryDbs.Add(db);
            primaryBoxes.Add(box);
        }

        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;

        // 2. Standby: BLANK — no databases at all. Follow the primary; reconcile must bootstrap all three.
        using var standby = CrystalStore.Open(sdir.Path, Options(listen: null));
        Assert.Empty(standby.ListDatabases());
        standby.Replication.Follow(endpoint);

        // 3. Primary keeps writing while the standby bootstraps and catches up.
        for (var i = 20; i < 60; i++)
        {
            var d = i % 3;
            Write(primaryDbs[d], primaryBoxes[d], $"k{i:D4}", $"v{i:D4}");
        }

        primary.SyncAll();

        // 4. Wait until the standby has all three databases, caught up, and byte-identical.
        WaitUntil(() =>
        {
            var names = standby.ListDatabases().Select(d => d.Name).ToHashSet();
            if (!DbNames.All(names.Contains))
            {
                return false;
            }

            for (var d = 0; d < DbNames.Length; d++)
            {
                var sdb = standby.OpenDatabase(DbNames[d]);
                if (sdb.Slot.LatestSequence < primaryDbs[d].Slot.LatestSequence)
                {
                    return false;
                }
            }

            return true;
        }, TimeSpan.FromSeconds(60));

        for (var d = 0; d < DbNames.Length; d++)
        {
            AssertByteIdentical(primaryDbs[d], standby.OpenDatabase(DbNames[d]));
        }
    }

    [Fact]
    public async Task Snapshot_DownloadAndVerify_ProducesTheManifestFiles()
    {
        using var pdir = new TempStoreDirectory();
        using var dest = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        for (var i = 0; i < 50; i++)
        {
            Write(db, box, $"k{i:D4}", $"v{i:D4}");
        }

        db.Slot.Instance.Flush(); // force SSTs so the checkpoint has files beyond the WAL
        primary.Replication.Listen();
        var endpoint = primary.Replication.ListenerEndpoint!;

        var (host, port) = Split(endpoint);
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(host, port);
        using var stream = tcp.GetStream();
        var client = new ReplicationClient(stream, "standby-node", "test-secret");
        await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);

        var downloadDir = Path.Combine(dest.Path, "dl");
        var begin = await SnapshotBootstrap.DownloadAsync(client, db.Uid, downloadDir, CancellationToken.None);

        Assert.NotEmpty(begin.Manifest);
        Assert.Equal(db.Uid, begin.Uid);
        foreach (var f in begin.Manifest)
        {
            var path = Path.Combine(downloadDir, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), f.RelativePath);
            Assert.Equal(f.Size, new FileInfo(path).Length);
        }
    }

    private static (string Host, int Port) Split(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return (endpoint[..colon].Trim('[', ']'), int.Parse(endpoint[(colon + 1)..]));
    }

    [Fact]
    public void Crc32C_DetectsCorruption()
    {
        var data = Text.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        var good = Crc32C.Compute(data);

        var corrupted = (byte[])data.Clone();
        corrupted[10] ^= 0x01;
        Assert.NotEqual(good, Crc32C.Compute(corrupted));

        // A known CRC-32C vector: "123456789" → 0xE3069283.
        Assert.Equal(0xE3069283u, Crc32C.Compute(Text.UTF8.GetBytes("123456789")));
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (DatabaseNotFoundException)
            {
                // A database still being bootstrapped; keep waiting.
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("the standby did not reach the expected state in time");
    }

    private static void AssertByteIdentical(CrystalDatabase primary, CrystalDatabase standby)
    {
        using var ps = primary.Snapshot();
        using var ss = standby.Snapshot();
        var pbox = primary.Boxes.Find("box")!.Value;
        var sbox = standby.Boxes.Find("box")!.Value;

        var pRows = ps.Scan(pbox, [], [], int.MaxValue).Select(kv => Convert.ToHexString(kv.Key) + "=" + Convert.ToHexString(kv.Value)).ToList();
        var sRows = ss.Scan(sbox, [], [], int.MaxValue).Select(kv => Convert.ToHexString(kv.Key) + "=" + Convert.ToHexString(kv.Value)).ToList();
        Assert.Equal(pRows, sRows);
    }
}
