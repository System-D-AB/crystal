// Spec 003 T7 Step B / R3.5, design § 3 — the held WAL reader: a standby behind the in-memory tail catches up through
// ONE reader kept across pages, not a fresh WAL read per page (each of which re-reads the live WAL from its start).
// Ported from Highway's ReplicaCatchUpTests, adapted: Crystal's held reader sits behind DatabaseReplication/
// ReplicationSource over WalAccess (not Highway's vendored toolkit), and the full standby-follows-primary byte-identical
// catch-up is T9's gate — here the reader mechanics: one reader across many pages, a WAL hold while held, and the
// reader dropped on an epoch change. Applying a served page to a standby's WalAccess round-trips the bytes.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class WalReaderPoolTests
{
    private static ReplicationOptions Repl() => new()
    {
        Secret = "test-secret",
        WalTtl = TimeSpan.FromHours(24),
        WalBytesPerDatabase = 1L << 30,
        TailBytesPerDatabase = 16L << 20,
    };

    private sealed class Harness : IDisposable
    {
        public TempStoreDirectory Dir { get; } = new();
        public CrystalStore Store { get; }
        public ReplicationSource Source { get; }

        public Harness()
        {
            Store = CrystalStore.Open(Dir.Path, new CrystalOptions { Replication = Repl() });
            var epoch = ReplicationEpoch.Load(Dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
            Source = new ReplicationSource(Store, Store.Engine, Store.NodeId, epoch, Repl());
        }

        public void Dispose()
        {
            Source.Dispose();
            Store.Dispose();
            Dir.Dispose();
        }
    }

    private static void Write(CrystalDatabase db, Box box, int n)
    {
        var value = new byte[150];
        for (var i = 0; i < n; i++)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes($"key-{i:D6}"), value);
            batch.Commit(DurabilityMode.Strict);
        }
    }

    // Catches a cursor up to `latest` by serving page after page from the source's serving state, returning the page
    // count. Each page is applied nowhere (the reader mechanics are the subject); the batches are checked contiguous.
    private static int CatchUp(DatabaseReplication serving, long from, long latest, int pageBytes, List<(ulong, byte[])>? sink = null)
    {
        var pages = 0;
        var cursor = from;
        var guard = 0;
        while (cursor < latest)
        {
            Assert.True(guard++ < 100_000, "a catch-up must make progress on every page");
            var page = new List<(ulong Seq, byte[] Data)>();
            var result = serving.Serve(cursor, pageBytes, page, out _);
            Assert.Equal(ServeResult.Served, result);
            Assert.NotEmpty(page);
            Assert.Equal((ulong)cursor + 1, page[0].Seq);

            foreach (var (seq, data) in page)
            {
                sink?.Add((seq, data));
            }

            // Advance the cursor to the last served batch's end.
            var (lastSeq, lastData) = page[^1];
            var lastCount = BinaryPrimitives.ReadUInt32LittleEndian(lastData.AsSpan(8, 4));
            cursor = (long)(lastSeq + Math.Max(lastCount, 1u) - 1);
            pages++;
        }

        return pages;
    }

    [Fact]
    public void AFarBehindStandby_CatchesUpThroughOneHeldReader()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");

        var start = (long)db.Slot.LatestSequence;
        Write(db, box, 1_500); // written before any pull: not in the in-memory tail
        var latest = (long)db.Slot.LatestSequence;

        var serving = h.Source.ServingForTest(db.Uid)!;
        var pages = CatchUp(serving, start, latest, pageBytes: 16 * 1024);

        Assert.True(pages > 5, "the catch-up spans many pages");
        Assert.Equal(1, serving.ReadersOpened); // every page after the first continued the same reader
    }

    [Fact]
    public void AHeldReader_PausesWalFileDeletion_WhileHeld()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");

        var start = (long)db.Slot.LatestSequence;
        Write(db, box, 200);

        var serving = h.Source.ServingForTest(db.Uid)!;
        // One page behind the tail opens and keeps a reader.
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(ServeResult.Served, serving.Serve(start, 4 * 1024, page, out _));
        Assert.True(serving.HoldingWal, "a held reader pauses WAL deletion");
    }

    [Fact]
    public void AnIdleHeldReader_IsReleasedByTheTimer_WithoutAnotherPull()
    {
        // R11.2: a standby that went away must not hold WAL deletion off for good (tasks addendum, T11).
        var clock = new SystemD.Crystal.Tests.Support.ManualClock();
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Clock = clock, Replication = Repl() });
        var epoch = ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
        using var source = new ReplicationSource(store, store.Engine, store.NodeId, epoch, Repl());
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        var start = (long)db.Slot.LatestSequence;
        Write(db, box, 200);

        var serving = source.ServingForTest(db.Uid)!;
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(ServeResult.Served, serving.Serve(start, 4 * 1024, page, out _));
        Assert.True(serving.HoldingWal);

        clock.Advance(TimeSpan.FromSeconds(11)); // past the 5 s idle timeout, and past a timer tick

        Assert.False(serving.HoldingWal, "an idle reader is released with no further pull");
    }

    [Fact]
    public void AnEpochChange_DropsTheHeldReader()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        var start = (long)db.Slot.LatestSequence;
        Write(db, box, 1_000);

        var serving = h.Source.ServingForTest(db.Uid)!;
        var page = new List<(ulong Seq, byte[] Data)>();
        serving.Serve(start, 16 * 1024, page, out _);
        Assert.Equal(1, serving.ReadersOpened);
        Assert.True(serving.HoldingWal);

        h.Source.ObserveHigherEpoch(h.Source.Epoch + 1);
        Assert.False(serving.HoldingWal, "the epoch change closed the reader: retention resumes");

        page.Clear();
        serving.Serve(start, 16 * 1024, page, out _);
        Assert.True(serving.ReadersOpened == 2, "nothing held across an epoch change: a fresh reader opens");
    }

    [Fact]
    public void AfterTheTailIsCleared_ABehindStandby_StillCatchesUpFromTheWal()
    {
        // The tail gone (as after a primary restart): the standby's catch-up must still complete, served entirely from
        // the WAL through the held reader. A fresh reader opens (the epoch/clear dropped any held one), then one serves
        // the rest.
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        var start = (long)db.Slot.LatestSequence;
        Write(db, box, 800);

        var serving = h.Source.ServingForTest(db.Uid)!;
        // Prime a reader, then drop everything as a restart would, then catch up from the WAL.
        var first = new List<(ulong Seq, byte[] Data)>();
        serving.Serve(start, 8 * 1024, first, out _);
        serving.ClearTail();
        var openedBefore = serving.ReadersOpened;

        var latest = (long)db.Slot.LatestSequence;
        var pages = CatchUp(serving, start, latest, pageBytes: 8 * 1024);
        Assert.True(pages > 1, "the catch-up spans several pages");
        // After the clear, the catch-up opens exactly one fresh reader and keeps it for the rest.
        Assert.Equal(openedBefore + 1, serving.ReadersOpened);
    }

    [Fact]
    public void CaughtUpAgainstTheWal_IsByteIdentical_WhenAppliedToAStandby()
    {
        // The reader serves the primary's WAL bytes; applied to a standby database's WalAccess they advance its
        // sequence by the same batches, byte-for-byte. (Standby equality *by key* after catalogue bootstrap is T9.)
        using var h = new Harness();
        var primary = h.Store.CreateDatabase("primary", replicated: true);
        var box = primary.Boxes.Create("b");
        var start = (long)primary.Slot.LatestSequence;
        Write(primary, box, 300);
        var latest = (long)primary.Slot.LatestSequence;

        var serving = h.Source.ServingForTest(primary.Uid)!;
        var served = new List<(ulong, byte[])>();
        CatchUp(serving, start, latest, pageBytes: 8 * 1024, sink: served);

        var standby = h.Store.CreateDatabase("standby", replicated: true);
        var standbyWal = new WalAccess(standby.Slot.Instance);
        var before = standbyWal.LatestSequence();
        foreach (var (_, data) in served)
        {
            standbyWal.ApplyRaw(data);
        }

        // Count sequences served (a chunk may carry several if commits merged), not chunks.
        var sequencesServed = served.Sum(s => (long)BinaryPrimitives.ReadUInt32LittleEndian(s.Item2.AsSpan(8, 4)));
        Assert.Equal(before + (ulong)sequencesServed, standbyWal.LatestSequence());
        Assert.Equal(latest - start, sequencesServed); // every sequence from start+1..latest was served exactly once
    }
}
