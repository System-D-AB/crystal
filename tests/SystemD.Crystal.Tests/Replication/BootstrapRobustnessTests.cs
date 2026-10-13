// Spec 003 R7 (task T10's done criteria, completed 2026-10-10): a corrupted chunk is detected and refetched within the
// session; a failed bootstrap is retried on the follower's own schedule; an install that fails leaves the database as
// it was, and a crash between its moves is put right at the next open; leftover downloads and checkpoints are cleared at
// open; an abandoned session's checkpoint is released after the idle limit without another call; the follower survives
// an unexpected exception; a manifest cannot name a file outside the download.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using SystemD.Crystal.Tests.Support;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class BootstrapRobustnessTests
{
    private const string Secret = "test-secret";

    private static CrystalOptions Options(string? listen = null, Action<string>? hook = null, TimeProvider? clock = null) => new()
    {
        Clock = clock ?? TimeProvider.System,
        Hook = hook,
        Replication = new ReplicationOptions
        {
            Secret = Secret,
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public void ACorruptedChunk_IsDetectedAndRefetched_WithinTheSameSession()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = Seeded(primary, rows: 200);
        primary.Replication.Listen();

        var sessions = primary.Replication.Source!.SnapshotsForTest;
        var corrupted = 0;
        sessions.ChunkHookForTest = (file, offset, bytes) =>
        {
            if (bytes.Length > 0 && Interlocked.CompareExchange(ref corrupted, 1, 0) == 0)
            {
                var copy = (byte[])bytes.Clone();
                copy[copy.Length / 2] ^= 0xFF; // one flipped byte in the first chunk served
                return copy;
            }

            return bytes;
        };

        using var standby = CrystalStore.Open(sdir.Path, Options());
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));
        Assert.Equal(1, corrupted);
        Assert.Equal(1, sessions.Begun); // refetched within the session, not a whole new download
        Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));
        _ = db;
    }

    [Fact]
    public void AFailedBootstrap_IsRetried_WithoutWaitingForTheDatabaseSetToChange()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        Seeded(primary, rows: 20);
        primary.Replication.Listen();

        var failures = 0;
        using var standby = CrystalStore.Open(sdir.Path, Options(hook: step =>
        {
            if (step == "bootstrap.downloaded" && Interlocked.Increment(ref failures) == 1)
            {
                throw new InvalidOperationException("injected: the first install fails");
            }
        }));
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));
        Assert.True(failures >= 2, "the bootstrap was tried again after the injected failure");
        Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));
        Assert.Empty(Directory.GetDirectories(sdir.Path, "bootstrap-*"));
    }

    [Fact]
    public void AnInstallThatFails_LeavesTheDatabaseAsItWas_AndTheRetryInstallsIt()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var pdb = Seeded(primary, rows: 20);
        primary.Replication.Listen();

        var failAside = 0;
        var keptDuringFailure = false;
        using var standby = CrystalStore.Open(sdir.Path, Options(hook: step =>
        {
            if (step == "install.aside" && Volatile.Read(ref failAside) == 1)
            {
                Interlocked.Exchange(ref failAside, 2);
                throw new IOException("injected: the snapshot cannot be moved in");
            }
        }));
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));
        var uid = pdb.Uid;
        var before = RowsOf(standby, "db");

        // Force a re-bootstrap of the database the standby holds; its first install fails after the old content was
        // moved aside.
        standby.Replication.StopFollowing();
        ResyncMarker.Write(sdir.Path, uid, "test");
        Interlocked.Exchange(ref failAside, 1);
        Put(pdb, "after", "1");
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        WaitUntil(() => Volatile.Read(ref failAside) == 2, TimeSpan.FromSeconds(30));
        keptDuringFailure = RowsOf(standby, "db").Count >= before.Count; // the old content is back in place
        WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));

        Assert.True(keptDuringFailure);
        Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));
        Assert.Empty(Directory.GetDirectories(sdir.Path, "*" + StoreEngine.ReplacedSuffix + "*"));
    }

    [Fact]
    public void ACrashBetweenTheInstallsMoves_IsPutRightAtTheNextOpen_AndLeftoversAreCleared()
    {
        using var dir = new TempStoreDirectory();
        uint id;
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            var db = Seeded(store, rows: 5);
            id = db.Id;
        }

        // As a crash would leave it: the database moved aside and nothing moved in; a download and a checkpoint left.
        var dbPath = dir.DatabaseFolder(id);
        Directory.Move(dbPath, dbPath + StoreEngine.ReplacedSuffix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "bootstrap-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(dir.Path, StoreEngine.DatabaseFolderName(id) + ".checkpoint-abc"));

        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            Assert.Equal(5, RowsOf(store, "db").Count);
        }

        Assert.Equal([StoreEngine.DatabaseFolderName(id)], Directory.GetDirectories(dir.Path, "db-*").Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(dir.Path, "bootstrap-*"));
    }

    [Fact]
    public async Task AnAbandonedSession_IsReleasedAfterTheIdleLimit_WithoutAnotherCall()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(dir.Path, Options(listen: "127.0.0.1:0", clock: clock));
        var db = Seeded(primary, rows: 5);
        primary.Replication.Listen();
        var sessions = primary.Replication.Source!.SnapshotsForTest;

        using (var tcp = new TcpClient())
        {
            var endpoint = primary.Replication.ListenerEndpoint!;
            var colon = endpoint.LastIndexOf(':');
            await tcp.ConnectAsync(endpoint[..colon], int.Parse(endpoint[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            var client = new ReplicationClient(tcp.GetStream(), "standby-1", Secret);
            await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
            var reply = await client.RequestAsync(
                [B(ReplicationProtocol.Snapshot), B("BEGIN"), B(db.Uid), B(ReplicationProtocol.Format), B(ReplicationProtocol.Layout)],
                CancellationToken.None);
            Assert.Equal(RespType.Array, reply.Type);
        }

        Assert.Equal(1, sessions.Count);
        Assert.Single(Directory.GetDirectories(dir.Path, "*.checkpoint-*"));

        clock.Advance(SnapshotSessions.IdleLimit + SnapshotSessions.ExpiryCheck + TimeSpan.FromSeconds(1));

        Assert.Equal(0, sessions.Count);
        Assert.Empty(Directory.GetDirectories(dir.Path, "*.checkpoint-*"));
    }

    [Fact]
    public void TheFollower_SurvivesAnUnexpectedException_AndGoesOnFollowing()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var pdb = Seeded(primary, rows: 5);
        primary.Replication.Listen();

        var thrown = 0;
        using var standby = CrystalStore.Open(sdir.Path, Options(hook: step =>
        {
            if (step == "follower.applied" && Interlocked.Increment(ref thrown) == 3)
            {
                throw new InvalidOperationException("injected: something unexpected while following");
            }
        }));
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => Volatile.Read(ref thrown) >= 3, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 10; i++)
        {
            Put(pdb, $"later-{i}", "x");
        }

        WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));
        Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("/etc/outside")]
    [InlineData("C:/outside")]
    [InlineData("")]
    public void AManifestFileOutsideTheDownload_IsRefused(string relativePath)
    {
        using var dir = new TempStoreDirectory();
        Assert.Throws<ReplicationProtocolException>(() => SnapshotBootstrap.ContainedPath(dir.Path, relativePath));
    }

    [Fact]
    public void AManifestFileInsideTheDownload_IsAccepted()
    {
        using var dir = new TempStoreDirectory();
        var path = SnapshotBootstrap.ContainedPath(dir.Path, "sub/000012.sst");
        Assert.StartsWith(Path.GetFullPath(dir.Path), path, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    // ------------------------------------------------------------------ spec 011 R3: bootstraps beside the pull loop

    [Fact]
    public void ABootstrapInProgress_DoesNotStallTheOtherDatabases()
    {
        // R3.1: while one database downloads (held mid-chunk by a hook), another followed database keeps applying.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var small = Seeded(primary, rows: 5);
        primary.Replication.Listen();

        using var holding = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false); // set in finally, so a failing run never leaves a download held
        var hold = 0;
        using var standby = CrystalStore.Open(sdir.Path, Options(hook: step =>
        {
            if (step == "bootstrap.chunk" && Volatile.Read(ref hold) == 1)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }
        }));
        try
        {
            standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
            WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));
            Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));

            // A second database appears on the primary; its download is held at its first chunk.
            Volatile.Write(ref hold, 1);
            var big = primary.CreateDatabase("big", replicated: true);
            big.Boxes.Create("box");
            Put(big, "k", "v");
            Assert.True(holding.Wait(TimeSpan.FromSeconds(30)), "the second database's download did not start");

            // Meanwhile the first database keeps following.
            Put(small, "while-downloading", "x");
            WaitUntil(() => RowsOf(standby, "db").Contains("while-downloading=x"), TimeSpan.FromSeconds(15));
            Assert.Contains("while-downloading=x", RowsOf(standby, "db"));
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            release.Set();
        }

        WaitUntil(() => RowsOf(standby, "big").Count == 1, TimeSpan.FromSeconds(30));
        Assert.Equal(["k=v"], RowsOf(standby, "big"));
    }

    [Fact]
    public void ADownloadCutMidFile_ResumesAtItsOffset_WithinTheSameSession()
    {
        // R3.3: the connection is lost after a chunk of a multi-chunk file; the download reconnects and continues from
        // that offset in the same session. No chunk is served twice.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var db = primary.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");
        var random = new Random(11);
        for (var i = 0; i < 40; i++)
        {
            var value = new char[100_000];
            for (var c = 0; c < value.Length; c++)
            {
                value[c] = (char)('a' + random.Next(26)); // incompressible enough to keep the table file over 2 MiB
            }

            Put(db, $"k{i:D3}", new string(value));
        }

        db.Slot.Instance.Flush();
        primary.Replication.Listen();

        var sessions = primary.Replication.Source!.SnapshotsForTest;
        var served = new System.Collections.Concurrent.ConcurrentQueue<(string File, long Offset)>();
        sessions.ChunkHookForTest = (file, offset, bytes) =>
        {
            served.Enqueue((file, offset));
            return bytes;
        };

        var chunks = 0;
        using var standby = CrystalStore.Open(sdir.Path, Options(hook: step =>
        {
            if (step == "bootstrap.chunk" && Interlocked.Increment(ref chunks) == 2)
            {
                throw new IOException("injected: the connection is lost after the second chunk");
            }
        }));
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => RowsOf(standby, "db").Count == 40, TimeSpan.FromSeconds(60));

        Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));
        Assert.True(chunks > 2, "the snapshot had more than two chunks");
        Assert.True(served.Any(s => s.Offset > 0), "a file was served in more than one chunk");
        Assert.Equal(1, sessions.Begun); // resumed within the session, not a new download
        var twice = served.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "chunks served twice: " + string.Join(", ", twice) + " in order: " + string.Join(", ", served));
    }

    [Fact]
    public void ADownloadOfADatabaseThePrimaryDropsMeanwhile_IsDiscarded_NotInstalled()
    {
        // Review 2026-10-13, S3: downloads run beside the reconcile, which may learn of the drop while the download
        // runs; the finished download is checked against the primary's set as last parsed.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        Seeded(primary, rows: 5);
        primary.Replication.Listen();

        using var holding = new ManualResetEventSlim(false);
        using var discarded = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false); // set in finally
        var hold = 0;
        using var standby = CrystalStore.Open(sdir.Path, Options(hook: step =>
        {
            if (step == "bootstrap.chunk" && Volatile.Read(ref hold) == 1)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }
            else if (step == "bootstrap.discarded")
            {
                discarded.Set();
            }
        }));
        try
        {
            standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
            WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));

            Volatile.Write(ref hold, 1);
            var doomed = primary.CreateDatabase("doomed", replicated: true);
            doomed.Boxes.Create("box");
            Put(doomed, "k", "v");
            Assert.True(holding.Wait(TimeSpan.FromSeconds(30)), "the download did not start");

            // The primary drops it while it downloads; the standby's loop learns of it (one more DATABASES).
            var asked = primary.Replication.Source!.DatabasesServed;
            primary.DropDatabase(doomed);
            WaitUntil(() => primary.Replication.Source!.DatabasesServed > asked, TimeSpan.FromSeconds(30));
            Assert.True(primary.Replication.Source!.DatabasesServed > asked, "the standby did not reconcile while it downloaded");
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            release.Set();
        }

        Assert.True(discarded.Wait(TimeSpan.FromSeconds(30)), "the finished download was not discarded");
        Assert.DoesNotContain(standby.ListDatabases(), d => d.Name == "doomed");
    }

    [Fact]
    public void ASnapshotWhoseBeginIsSlow_IsStillDownloaded_UnderTheReplyDeadline()
    {
        // Re-review 2026-10-13, N1: before it answers BEGIN, the primary reads the whole checkpoint for the CRCs, which a
        // large database takes minutes to do. With a 1 s reply deadline and a BEGIN held 2.5 s, the bootstrap must still
        // finish: BEGIN has a deadline of its own.
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, new CrystalOptions
        {
            Hook = step =>
            {
                if (step == "snapshot.manifest")
                {
                    Thread.Sleep(2500); // the slow checksum pass, not a wait for an outcome
                }
            },
            Replication = new ReplicationOptions { Secret = Secret, ListenEndpoint = "127.0.0.1:0", LongPollMax = TimeSpan.FromMilliseconds(300) },
        });
        Seeded(primary, rows: 5);
        primary.Replication.Listen();

        var failures = 0;
        using var standby = CrystalStore.Open(sdir.Path, new CrystalOptions
        {
            Hook = step =>
            {
                if (step == "bootstrap.failed")
                {
                    Interlocked.Increment(ref failures);
                }
            },
            Replication = new ReplicationOptions
            {
                Secret = Secret,
                LongPollMax = TimeSpan.FromMilliseconds(300),
                RequestTimeout = TimeSpan.FromSeconds(1),
            },
        });
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        WaitUntil(() => RowsOf(standby, "db").SequenceEqual(RowsOf(primary, "db")), TimeSpan.FromSeconds(30));
        Assert.Equal(RowsOf(primary, "db"), RowsOf(standby, "db"));
        Assert.Equal(0, Volatile.Read(ref failures));
    }

    [Theory]
    [InlineData(5, 5, "Accept")]
    [InlineData(6, 5, "Adopt")]
    [InlineData(4, 5, "Refuse")]
    [InlineData(0, 5, "Refuse")]
    [InlineData((1L << 62) + 1, 5, "Refuse")]
    public void ASnapshotsEpoch_IsJudgedAsAPagesIs(long snapshotEpoch, long ownEpoch, string verdict)
    {
        // R3.4: the BEGIN reply's epoch. The main connection's handshake already makes a stale primary stand down (its
        // standby's HELLO states the higher epoch), so this guards the snapshot's own connection, which may reach a node
        // whose epoch the standby has since passed; tested on the decision itself, which the follower applies.
        Assert.Equal(Enum.Parse<SnapshotBootstrap.EpochVerdict>(verdict), SnapshotBootstrap.JudgeEpoch(snapshotEpoch, ownEpoch));
    }

    private static CrystalDatabase Seeded(CrystalStore store, int rows)
    {
        var db = store.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");
        for (var i = 0; i < rows; i++)
        {
            Put(db, $"k{i:D5}", new string('v', 200));
        }

        db.Slot.Instance.Flush(); // table files, so the snapshot has more than a WAL
        return db;
    }

    private static void Put(CrystalDatabase db, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(db.Boxes.Find("box")!.Value, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    private static List<string> RowsOf(CrystalStore store, string name)
    {
        try
        {
            var db = store.OpenDatabase(name);
            using var snapshot = db.Snapshot();
            return snapshot.Scan(db.Boxes.Find("box")!.Value, [], [], int.MaxValue)
                .Select(kv => Text.UTF8.GetString(kv.Key) + "=" + Text.UTF8.GetString(kv.Value)).ToList();
        }
        catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException or CrystalFormatException or NullReferenceException)
        {
            return []; // not there yet, or being installed
        }
    }

    private static byte[] B(string text) => Text.UTF8.GetBytes(text);

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
        {
            Thread.Sleep(50);
        }
    }
}
