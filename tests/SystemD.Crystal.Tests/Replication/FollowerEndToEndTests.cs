// Spec 003 T9 / design § 4 — the follower loop end to end over a real TCP socket (two stores in one process; the
// cross-process TestNode form awaits T10's snapshot bootstrap to acquire the databases). A standby follows a primary
// through writes to three databases and ends byte-identical. The three standby databases are pre-aligned by a
// checkpoint of the primary's folders — what T10's snapshot bootstrap does over the wire — so this isolates the
// follower's job: connect, handshake, reconcile (a no-op here), PULL all three, apply, byte-identical.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class FollowerEndToEndTests
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination), overwrite: true);
        }
    }

    private static void Write(CrystalDatabase db, Box box, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    [Fact]
    public void AStandby_FollowsAPrimary_ThroughThreeDatabases_AndEndsByteIdentical()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();

        // 1. Primary: three databases, each a box and a seed row. Record each UID and aligned sequence; checkpoint
        //    each folder so the standby can start physically aligned (T10 will do this over the wire).
        var checkpoints = new List<string>();
        var uids = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var seedPrimary = CrystalStore.Open(pdir.Path, Options(listen: null)))
            {
                foreach (var name in DbNames)
                {
                    var db = seedPrimary.CreateDatabase(name, replicated: true);
                    var box = db.Boxes.Create("box");
                    Write(db, box, "seed", "0");
                    uids[name] = db.Uid;
                    var ckpt = Path.Combine(Path.GetTempPath(), "crystal-ckpt-" + Guid.NewGuid().ToString("N"));
                    new WalAccess(db.Slot.Instance).Checkpoint(ckpt);
                    checkpoints.Add(ckpt);
                }
            }

            // 2. Standby: create the same three databases WITH THE PRIMARY'S UIDs (what T10's bootstrap does, so the
            //    wire — which names databases by UID — reaches them), then — with the store closed — replace each
            //    database folder with the primary's checkpoint to align them byte-for-byte.
            var standbyIds = new uint[DbNames.Length];
            using (var seedStandby = CrystalStore.Open(sdir.Path, Options(listen: null)))
            {
                for (var i = 0; i < DbNames.Length; i++)
                {
                    standbyIds[i] = seedStandby.Engine.CreateDatabaseWithUid(seedStandby, DbNames[i], replicated: true, uids[DbNames[i]]).Id;
                }
            }

            for (var i = 0; i < DbNames.Length; i++)
            {
                var folder = Path.Combine(sdir.Path, StoreEngine.DatabaseFolderName(standbyIds[i]));
                Directory.Delete(folder, recursive: true);
                CopyDirectory(checkpoints[i], folder);
                RecordFollowedFrom(pdir.Path, sdir.Path, uids[DbNames[i]]);
            }

            // 3. Start the primary listening, and the standby following it.
            using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
            primary.Replication.Listen();
            var endpoint = primary.Replication.ListenerEndpoint!;

            using var standby = CrystalStore.Open(sdir.Path, Options(listen: "127.0.0.1:0"));
            standby.Replication.Follow(endpoint);

            // 4. Primary writes a stream of rows across all three databases.
            var primaryDbs = DbNames.Select(primary.OpenDatabase).ToArray();
            var primaryBoxes = primaryDbs.Select(d => d.Boxes.Find("box")!.Value).ToArray();
            for (var i = 0; i < 100; i++)
            {
                var d = i % 3;
                Write(primaryDbs[d], primaryBoxes[d], $"k{i:D4}", $"v{i:D4}");
            }

            primary.SyncAll();

            // 5. Wait for the standby to catch up on all three, then assert byte-identical by key.
            var standbyDbs = DbNames.Select(standby.OpenDatabase).ToArray();
            WaitUntilCaughtUp(primaryDbs, standbyDbs, TimeSpan.FromSeconds(30));

            for (var d = 0; d < DbNames.Length; d++)
            {
                AssertByteIdentical(primaryDbs[d], standbyDbs[d]);
            }
        }
        finally
        {
            foreach (var c in checkpoints)
            {
                try { Directory.Delete(c, recursive: true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void AStandby_FarBehind_CatchesUpOverTheWire_WhenItStartsFollowing()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();

        // One aligned database, as above.
        string uid;
        var checkpoint = Path.Combine(Path.GetTempPath(), "crystal-ckpt-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var seed = CrystalStore.Open(pdir.Path, Options(listen: null)))
            {
                var db = seed.CreateDatabase("db", replicated: true);
                var box = db.Boxes.Create("box");
                Write(db, box, "seed", "0");
                uid = db.Uid;
                new WalAccess(db.Slot.Instance).Checkpoint(checkpoint);
            }

            uint id;
            using (var seedStandby = CrystalStore.Open(sdir.Path, Options(listen: null)))
            {
                id = seedStandby.Engine.CreateDatabaseWithUid(seedStandby, "db", replicated: true, uid).Id;
            }

            var folder = Path.Combine(sdir.Path, StoreEngine.DatabaseFolderName(id));
            Directory.Delete(folder, recursive: true);
            CopyDirectory(checkpoint, folder);
            RecordFollowedFrom(pdir.Path, sdir.Path, uid);

            using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
            primary.Replication.Listen();
            var endpoint = primary.Replication.ListenerEndpoint!;

            using var standby = CrystalStore.Open(sdir.Path, Options(listen: null));

            // Write a backlog on the primary BEFORE the standby follows, so the first pull catches up far behind the
            // tail (served from the WAL through the held reader), then goes byte-identical.
            var pdb = primary.OpenDatabase("db");
            var pbox = pdb.Boxes.Find("box")!.Value;
            for (var i = 0; i < 30; i++)
            {
                Write(pdb, pbox, $"k{i:D4}", $"v{i:D4}");
            }

            primary.SyncAll();
            standby.Replication.Follow(endpoint);

            var sdb = standby.OpenDatabase("db");
            WaitUntilCaughtUp([pdb], [sdb], TimeSpan.FromSeconds(30));
            AssertByteIdentical(pdb, sdb);
        }
        finally
        {
            try { Directory.Delete(checkpoint, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Writes the follow record a real bootstrap writes (spec 003 R7.7): the hand-copied checkpoint came from the
    /// primary's incarnation at the time, which the primary's incarnation record names. Without it the standby holds an
    /// unknown history above the primary's base and is rightly re-bootstrapped (003 addendum B3).
    /// </summary>
    private static void RecordFollowedFrom(string primaryDirectory, string standbyDirectory, string uid)
    {
        var primaryHistory = IncarnationRecord.Read(primaryDirectory, uid)
            ?? throw new InvalidOperationException($"the primary has no incarnation record for {uid}");
        FollowRecord.Write(standbyDirectory, uid, primaryHistory.Id, primaryHistory.Base);
    }

    private static void WaitUntilCaughtUp(CrystalDatabase[] primary, CrystalDatabase[] standby, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var caught = true;
            for (var d = 0; d < primary.Length; d++)
            {
                if (standby[d].Slot.LatestSequence < primary[d].Slot.LatestSequence)
                {
                    caught = false;
                    break;
                }
            }

            if (caught)
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("the standby did not catch up to the primary in time");
    }

    private static void AssertByteIdentical(CrystalDatabase primary, CrystalDatabase standby)
    {
        var box = "box";
        using var ps = primary.Snapshot();
        using var ss = standby.Snapshot();
        var pbox = primary.Boxes.Find(box)!.Value;
        var sbox = standby.Boxes.Find(box)!.Value;

        var pRows = ps.Scan(pbox, [], [], int.MaxValue).Select(kv => (Key: Convert.ToHexString(kv.Key), Value: Convert.ToHexString(kv.Value))).ToList();
        var sRows = ss.Scan(sbox, [], [], int.MaxValue).Select(kv => (Key: Convert.ToHexString(kv.Key), Value: Convert.ToHexString(kv.Value))).ToList();

        Assert.Equal(pRows, sRows);
    }
}
