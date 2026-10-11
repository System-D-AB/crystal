// Spec 003 T11 / R5; design § 6 — gaps and divergence drive a per-database re-bootstrap by the one bootstrap path.
// The divergence rule (design § 6 table) is unit-tested on StandbyDatabase.HasDiverged; the end-to-end gap (R14.3)
// and divergence (R14.4) scenarios drive a blank standby that re-bootstraps the affected database alone while the
// others keep following.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class DivergenceAndGapTests
{
    private static CrystalOptions Options(string? listen, TimeSpan? walTtl = null, long? walBytes = null, long? tailBytes = null) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
            WalTtl = walTtl ?? TimeSpan.FromHours(24),
            WalBytesPerDatabase = walBytes ?? (1L << 30),
            TailBytesPerDatabase = tailBytes ?? (16L << 20),
        },
    };

    private static void Write(CrystalDatabase db, Box box, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    // ---- the divergence rule (design § 6 table) --------------------------------

    [Fact]
    public void HasDiverged_FollowsTheDesignTable()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: null));
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        for (var i = 0; i < 10; i++)
        {
            Write(db, box, $"k{i}", $"v{i}");
        }

        var standby = new StandbyDatabase(db.Slot, db.Uid, dir.Path);
        var position = (long)standby.Watermark;
        var inc = Guid.NewGuid().ToString("D");

        // The design table as amended (tasks addendum 2026-10-11, exact history).
        // Row 1 — same incarnation kept and served: continue, whatever the position.
        FollowRecord.Write(dir.Path, db.Uid, Guid.Parse(inc), @base: position - 1);
        Assert.False(standby.HasDiverged(inc, servedBase: position - 1));

        // Row 2 — the kept incarnation is in the served history: continue up to its end, diverged past it.
        var other = Guid.NewGuid().ToString("D");
        Assert.False(standby.HasDiverged(other, servedBase: 0, [new IncarnationEnd(Guid.Parse(inc), position)]));
        Assert.True(standby.HasDiverged(other, servedBase: 0, [new IncarnationEnd(Guid.Parse(inc), position - 1)]));

        // Row 3 — an incarnation the served log does not name: diverged, even at or below the served base (a standby
        // that wrote as a primary holds its own batches there). The base is no longer a reason to continue.
        Assert.True(standby.HasDiverged(other, servedBase: position));
        Assert.True(standby.HasDiverged(other, servedBase: position + 5));

        // A missing kept record compares as unknown (R5.5): diverged, so a crash before the record is written costs at
        // most a needless re-bootstrap. The record is read when following starts (then kept in memory), so a new follow.
        FollowRecord.Delete(dir.Path, db.Uid);
        var restarted = new StandbyDatabase(db.Slot, db.Uid, dir.Path);
        Assert.True(restarted.HasDiverged(inc, servedBase: position + 5));
    }

    [Fact]
    public void TheFollowRecord_IsKeptAsApplyWroteIt_NotReadAgainOnEveryPull()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(listen: null));
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        Write(db, box, "k", "v");

        // A page from a new incarnation (nothing new in it) records what this standby follows.
        var standby = new StandbyDatabase(db.Slot, db.Uid, dir.Path);
        var inc = Guid.NewGuid().ToString("D");
        var result = standby.Apply(epoch: 1, Array.Empty<(ulong, byte[])>(), inc, servedBase: 0);
        Assert.Equal(SystemD.Crystal.Replication.ApplyStatus.Applied, result.Status);
        Assert.NotNull(FollowRecord.Read(dir.Path, db.Uid));
        Assert.False(standby.HasDiverged(inc, servedBase: 0));

        // The disk is not read again while the database is followed: nothing else writes the record meanwhile.
        FollowRecord.Delete(dir.Path, db.Uid);
        Assert.False(standby.HasDiverged(inc, servedBase: 0));
    }

    [Fact]
    public void ResyncMarker_PersistsUntilCleared()
    {
        using var dir = new TempStoreDirectory();
        var uid = Guid.NewGuid().ToString("D");

        Assert.False(ResyncMarker.Exists(dir.Path, uid));
        ResyncMarker.Write(dir.Path, uid, "gap");
        Assert.True(ResyncMarker.Exists(dir.Path, uid));
        ResyncMarker.Clear(dir.Path, uid);
        Assert.False(ResyncMarker.Exists(dir.Path, uid));
    }

    // ---- end to end: a gap re-bootstraps that database (R14.3) -----------------

    /// <summary>
    /// R14.3, made certain (rewritten 2026-10-11: the earlier version could pass without a gap ever happening). The
    /// standby stops following; the primary writes on and its WAL retention runs out past the standby's position, which
    /// the test checks before going on; the standby follows again, meets the gap, re-bootstraps that database, and ends
    /// byte-identical.
    /// </summary>
    [Fact]
    public void AGap_ReBootstrapsTheAffectedDatabase_OverTheWire()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();

        // A one-second TTL and no size limit (RocksDB then purges the archive every half TTL), and a 1 MiB tail, so
        // neither the WAL nor memory still holds the standby's position once the primary has written on.
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0", walTtl: TimeSpan.FromSeconds(1), walBytes: 0, tailBytes: 1 << 20));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        Write(db, box, "k0000", "v");
        primary.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options(listen: null));
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Any(d => d.Name == "db")
            && standby.OpenDatabase("db").Slot.LatestSequence >= db.Slot.LatestSequence, TimeSpan.FromSeconds(30));
        standby.Replication.StopFollowing();
        var position = standby.OpenDatabase("db").Slot.LatestSequence;

        // Write well past the tail, flush and wait until the WAL no longer reaches the standby's position: the gap is
        // certain.
        var wal = new WalAccess(db.Slot.Instance);
        var value = new string('v', 4096);
        var i = 1;
        WaitUntil(() =>
        {
            for (var n = 0; n < 400; n++, i++)
            {
                Write(db, box, $"k{i:D5}", value);
            }

            db.Slot.Instance.Flush();
            Thread.Sleep(600);
            return !wal.WalHolds(position);
        }, TimeSpan.FromSeconds(60));

        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.OpenDatabase("db").Slot.LatestSequence >= db.Slot.LatestSequence, TimeSpan.FromSeconds(60));

        lock (events)
        {
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.Gap && e.Uid == db.Uid);
        }

        AssertByteIdentical(db, standby.OpenDatabase("db"));
    }

    /// <summary>
    /// R14.4, made real (rewritten 2026-10-11: the earlier version faked the divergence by editing the follow record
    /// while the data stayed identical). A standby is promoted by mistake and writes to 'alpha' only, then steps down;
    /// the real primary goes on, is promoted past it, and the stepped-down node follows it again. 'alpha', which holds
    /// writes the primary never had, re-bootstraps; 'beta', which it never wrote to, keeps following without one.
    /// </summary>
    [Fact]
    public void Divergence_ReBootstrapsTheAffectedDatabase_LeavingOthersFollowing()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        var a = primary.CreateDatabase("alpha", replicated: true);
        var b = primary.CreateDatabase("beta", replicated: true);
        var aBox = a.Boxes.Create("box");
        var bBox = b.Boxes.Create("box");
        Write(a, aBox, "k", "v");
        Write(b, bBox, "k", "v");
        primary.Replication.Listen();

        using var standby = CrystalStore.Open(sdir.Path, Options(listen: null));
        var events = new List<ReplicationEvent>();
        standby.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => standby.ListDatabases().Count == 2
            && standby.OpenDatabase("alpha").Slot.LatestSequence >= a.Slot.LatestSequence
            && standby.OpenDatabase("beta").Slot.LatestSequence >= b.Slot.LatestSequence, TimeSpan.FromSeconds(30));

        standby.Replication.StopFollowing();
        standby.Replication.Promote("by mistake");
        var strayAlpha = standby.OpenDatabase("alpha");
        for (var i = 0; i < 5; i++)
        {
            Write(strayAlpha, strayAlpha.Boxes.Find("box")!.Value, $"own-{i}", "only on the stepped-down node");
        }

        standby.Replication.Demote();

        for (var i = 0; i < 20; i++)
        {
            Write(a, aBox, $"k{i:D3}", $"v{i:D3}");
            Write(b, bBox, $"k{i:D3}", $"v{i:D3}");
        }

        primary.Replication.Promote("past the split");
        primary.Replication.Promote("past the split");
        lock (events)
        {
            events.Clear();
        }

        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        WaitUntil(() => Keys(standby.OpenDatabase("alpha")).SequenceEqual(Keys(a))
            && Keys(standby.OpenDatabase("beta")).SequenceEqual(Keys(b)), TimeSpan.FromSeconds(60));

        AssertByteIdentical(a, standby.OpenDatabase("alpha"));
        AssertByteIdentical(b, standby.OpenDatabase("beta"));
        lock (events)
        {
            Assert.Contains(events, e => e.Kind == ReplicationEventKind.Divergence && e.Uid == a.Uid);
            Assert.DoesNotContain(events, e => e.Kind is ReplicationEventKind.Divergence or ReplicationEventKind.Gap && e.Uid == b.Uid);
        }
    }

    private static List<string> Keys(CrystalDatabase db)
    {
        using var snapshot = db.Snapshot();
        return snapshot.Scan(db.Boxes.Find("box")!.Value, [], [], int.MaxValue).Select(kv => Text.UTF8.GetString(kv.Key)).ToList();
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
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException or NullReferenceException)
            {
                // still bootstrapping, or being reinstalled
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
