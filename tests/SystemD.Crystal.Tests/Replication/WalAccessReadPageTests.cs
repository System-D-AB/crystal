// Spec 003 T6 / R3.5, R3.6, R4.1 — the primary's WAL read path (WalAccess.ReadPage) and raw apply (ApplyRaw) through
// a live database, and the identity between the in-memory tail's bytes and the WAL's bytes (RocksInstance.OnCommitted
// captures exactly what the WAL holds). Crystal's own, since Crystal's WAL path sits behind WalAccess, not Highway's
// RocksDbStore. The full standby-equality catch-up is T8/T9 (needs catalogue bootstrap); here the bytes themselves.

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

public sealed class WalAccessReadPageTests
{
    private static ReplicationOptions Repl() => new()
    {
        Secret = "test-secret",
        WalTtl = TimeSpan.FromHours(24),
        WalBytesPerDatabase = 1L << 30,
    };

    // Writes `n` single-key batches, each a committed WAL batch, and returns the latest sequence after each.
    private static void WriteBatches(CrystalDatabase db, Box box, int n)
    {
        for (var i = 0; i < n; i++)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes($"k{i:D5}"), Text.UTF8.GetBytes($"v{i:D5}"));
            batch.Commit(DurabilityMode.Strict);
        }
    }

    [Fact]
    public void ReadPage_ReadsTheContiguousRun_FromACursor_WithTheSameBytesTheTailCaptured()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });
        var db = store.CreateDatabase("primary", replicated: true);
        var box = db.Boxes.Create("b");

        // Capture every committed batch's bytes as the tail would (R3.5): the WAL record Crystal shipped.
        var captured = new List<byte[]>();
        db.Slot.Instance.OnCommitted = bytes => captured.Add(bytes);

        var start = db.Slot.Instance.Db.GetLatestSequenceNumber();
        WriteBatches(db, box, 20);
        var latest = db.Slot.Instance.Db.GetLatestSequenceNumber();

        var wal = new WalAccess(db.Slot.Instance);

        // Read everything after `start`: a contiguous run up to `latest`.
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, wal.ReadPage(start, 1 << 20, page));
        Assert.NotEmpty(page);
        Assert.Equal(start + 1, page[0].Seq);
        Assert.Equal(latest, SeqOfLast(page));

        // The WAL's bytes for each batch are byte-identical to what the tail captured at commit.
        var byTailSeq = captured.ToDictionary(b => BinaryPrimitives.ReadUInt64LittleEndian(b), b => b);
        foreach (var (seq, data) in page)
        {
            Assert.True(byTailSeq.TryGetValue(seq, out var tailBytes), $"the tail captured seq {seq}");
            Assert.Equal(tailBytes, data);
        }
    }

    [Fact]
    public void ReadPage_FromLatest_IsCaughtUp_WithNoBatches()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });
        var db = store.CreateDatabase("primary", replicated: true);
        var box = db.Boxes.Create("b");
        WriteBatches(db, box, 5);

        var wal = new WalAccess(db.Slot.Instance);
        var latest = db.Slot.Instance.Db.GetLatestSequenceNumber();

        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, wal.ReadPage(latest, 1 << 20, page));
        Assert.Empty(page); // caught up: nothing after the latest sequence
    }

    [Fact]
    public void ReadPage_ByteCap_StopsEarly_FirstBatchAlwaysFits()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });
        var db = store.CreateDatabase("primary", replicated: true);
        var box = db.Boxes.Create("b");

        var start = db.Slot.Instance.Db.GetLatestSequenceNumber();
        WriteBatches(db, box, 30);
        var wal = new WalAccess(db.Slot.Instance);

        // A tiny cap: the first batch always fits, and the page is shorter than the whole range.
        var capped = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, wal.ReadPage(start, 1, capped));
        Assert.Single(capped);

        var whole = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, wal.ReadPage(start, 1 << 20, whole));
        Assert.True(whole.Count > capped.Count);
    }

    [Fact]
    public void ApplyRaw_WritesTheBytes_AdvancingTheWatermark()
    {
        // A standby database (its own catalogue) applies raw batch bytes the primary shipped: the watermark advances
        // by the applied batches, byte-for-byte. (Standby-equality by *key* is T8/T9, after catalogue bootstrap.)
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });
        var primary = store.CreateDatabase("primary", replicated: true);
        var box = primary.Boxes.Create("b");

        var start = primary.Slot.Instance.Db.GetLatestSequenceNumber();
        WriteBatches(primary, box, 10);
        var primaryWal = new WalAccess(primary.Slot.Instance);
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, primaryWal.ReadPage(start, 1 << 20, page));
        Assert.NotEmpty(page);

        var standby = store.CreateDatabase("standby", replicated: true);
        var standbyWal = new WalAccess(standby.Slot.Instance);
        var before = standbyWal.LatestSequence();

        foreach (var (_, data) in page)
        {
            standbyWal.ApplyRaw(data);
        }

        var after = standbyWal.LatestSequence();
        Assert.Equal(before + (ulong)page.Count, after); // each applied batch advanced the sequence by one
    }

    [Fact]
    public void ApplyRaw_ConcatenatedPage_WritesOnce_AdvancingToTheLastSequence()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });
        var primary = store.CreateDatabase("primary", replicated: true);
        var box = primary.Boxes.Create("b");

        var start = primary.Slot.Instance.Db.GetLatestSequenceNumber();
        WriteBatches(primary, box, 8);
        var primaryWal = new WalAccess(primary.Slot.Instance);
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, primaryWal.ReadPage(start, 1 << 20, page));

        // Concatenate the contiguous run into one write batch, as the applier does for a page.
        Assert.True(BatchApplier.TryConcat(page.Select(b => b.Data).ToList(), out var merged));

        var standby = store.CreateDatabase("standby", replicated: true);
        var standbyWal = new WalAccess(standby.Slot.Instance);
        var before = standbyWal.LatestSequence();

        standbyWal.ApplyRaw(merged);

        // One write of a merged batch advances the sequence by the total op count of the whole page.
        var totalOps = page.Sum(b => (long)BinaryPrimitives.ReadUInt32LittleEndian(b.Data.AsSpan(8, 4)));
        Assert.Equal(before + (ulong)totalOps, standbyWal.LatestSequence());
    }

    private static ulong SeqOfLast(List<(ulong Seq, byte[] Data)> page)
    {
        var (seq, data) = page[^1];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        return seq + Math.Max(count, 1u) - 1;
    }
}
