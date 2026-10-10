// Spec 003 T8 / R4, R4.5, R5.2; design § 5 — applying a primary WAL page onto a standby database through
// StandbyDatabase: skip-by-watermark idempotency on re-pull, one synced write per page, epoch refusal, the derived
// watermark (= latest sequence, no separate write), and the follow record. A standby is first ALIGNED with the
// primary by a checkpoint of its folder (what T10's snapshot bootstrap does over the wire): the skip-by-watermark
// guarantee holds only when the two share a sequence origin, because `db.Write` assigns fresh sequences on the
// standby — applying the primary's batches 1:1 keeps the two in lockstep after alignment, not before.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class StandbyApplyTests
{
    private static ReplicationOptions Repl() => new()
    {
        Secret = "s",
        WalTtl = TimeSpan.FromHours(24),
        WalBytesPerDatabase = 1L << 30,
    };

    private static CrystalStore Open(string path) =>
        CrystalStore.Open(path, new CrystalOptions { Replication = Repl() });

    private static List<(ulong Seq, byte[] Data)> ReadPage(CrystalDatabase db, long from)
    {
        var wal = new WalAccess(db.Slot.Instance);
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(WalPageStatus.Ok, wal.ReadPage((ulong)from, 1 << 20, page));
        return page;
    }

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

    /// <summary>
    /// A primary with one database and one box, and a standby ALIGNED to it by a checkpoint of the primary's folder.
    /// Returns both stores, the two database handles, the shared box, the aligned sequence, and the primary's UID.
    /// Disposing the returned <see cref="Fixture"/> closes both stores and cleans the checkpoint.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public TempStoreDirectory PDir { get; } = new();
        public TempStoreDirectory SDir { get; } = new();
        public CrystalStore Primary { get; }
        public CrystalStore StandbyStore { get; }
        public CrystalDatabase PrimaryDb { get; }
        public CrystalDatabase StandbyDb { get; }
        public Box PrimaryBox { get; }
        public long AlignedSeq { get; }
        public string Uid { get; }
        private readonly string _checkpoint;

        public Fixture()
        {
            _checkpoint = Path.Combine(Path.GetTempPath(), "crystal-ckpt-" + Guid.NewGuid().ToString("N"));
            Primary = Open(PDir.Path);
            PrimaryDb = Primary.CreateDatabase("db", replicated: true);
            PrimaryBox = PrimaryDb.Boxes.Create("b");
            using (var batch = PrimaryDb.NewBatch())
            {
                batch.Put(PrimaryBox, "seed"u8.ToArray(), "0"u8.ToArray());
                batch.Commit(DurabilityMode.Strict);
            }

            Uid = PrimaryDb.Uid;
            AlignedSeq = (long)PrimaryDb.Slot.LatestSequence;
            new WalAccess(PrimaryDb.Slot.Instance).Checkpoint(_checkpoint);

            // The standby store: create "db" with the primary's UID, as reconciliation does (spec 003 design § 2), then
            // replace its folder with the primary's checkpoint to align them. The UID must match: opening checks the
            // database's identity against the registry (spec 009 R2.3).
            uint id;
            using (var tmp = Open(SDir.Path))
            {
                id = tmp.Engine.CreateDatabaseWithUid(tmp, "db", replicated: true, Uid).Id;
            }

            var folder = Path.Combine(SDir.Path, StoreEngine.DatabaseFolderName(id));
            Directory.Delete(folder, recursive: true);
            CopyDirectory(_checkpoint, folder);

            StandbyStore = Open(SDir.Path);
            StandbyDb = StandbyStore.OpenDatabase("db");
        }

        public StandbyDatabase NewStandby(long initialEpoch = 0) =>
            new(StandbyDb.Slot, Uid, SDir.Path, initialEpoch);

        public List<(ulong Seq, byte[] Data)> PageFromAligned()
        {
            // Re-open the primary to write past the aligned point and read the page (the checkpoint froze the folder).
            return ReadPage(PrimaryDb, AlignedSeq);
        }

        public void Dispose()
        {
            StandbyStore.Dispose();
            Primary.Dispose();
            try { Directory.Delete(_checkpoint, recursive: true); } catch (IOException) { }
            SDir.Dispose();
            PDir.Dispose();
        }
    }

    [Fact]
    public void AlignedStandby_StartsAtThePrimarysSequence()
    {
        using var f = new Fixture();
        var standby = f.NewStandby();
        Assert.Equal((ulong)f.AlignedSeq, standby.Watermark); // the checkpoint aligned the two
    }

    [Fact]
    public void Apply_RePull_IsIdempotent_ByDerivedWatermark()
    {
        using var f = new Fixture();

        // Write past the aligned point on the primary, read the page.
        for (var i = 0; i < 5; i++)
        {
            using var batch = f.PrimaryDb.NewBatch();
            batch.Put(f.PrimaryBox, Text.UTF8.GetBytes($"k{i}"), Text.UTF8.GetBytes($"v{i}"));
            batch.Commit(DurabilityMode.Strict);
        }

        var page = ReadPage(f.PrimaryDb, f.AlignedSeq);
        Assert.NotEmpty(page);

        var standby = f.NewStandby();
        var first = standby.Apply(epoch: 1, page, f.Uid, servedBase: f.AlignedSeq);
        Assert.Equal(ApplyStatus.Applied, first.Status);
        Assert.Equal(page.Count, first.Applied);
        Assert.Equal(1, standby.SyncedWrites);
        var mark = standby.Watermark;

        // Re-pull of the same page: every batch is at or below the watermark now, so all are skipped.
        var second = standby.Apply(epoch: 1, page, f.Uid, servedBase: f.AlignedSeq);
        Assert.Equal(ApplyStatus.Applied, second.Status);
        Assert.Equal(0, second.Applied);
        Assert.Equal(page.Count, second.Skipped);
        Assert.Equal(mark, standby.Watermark);
        Assert.Equal(1, standby.SyncedWrites);
    }

    [Fact]
    public void Apply_ContiguousPage_IsOneSyncedWrite_AndStaysSequenceAligned()
    {
        using var f = new Fixture();
        for (var i = 0; i < 20; i++)
        {
            using var batch = f.PrimaryDb.NewBatch();
            batch.Put(f.PrimaryBox, Text.UTF8.GetBytes($"k{i}"), Text.UTF8.GetBytes($"v{i}"));
            batch.Commit(DurabilityMode.Strict);
        }

        var page = ReadPage(f.PrimaryDb, f.AlignedSeq);
        Assert.True(page.Count > 1);

        var standby = f.NewStandby();
        var result = standby.Apply(epoch: 1, page, f.Uid, f.AlignedSeq);
        Assert.Equal(page.Count, result.Applied);
        Assert.Equal(1, standby.SyncedWrites);

        // The standby ends at the primary's latest: an aligned apply keeps the two sequences in lockstep.
        Assert.Equal((ulong)f.PrimaryDb.Slot.LatestSequence, standby.Watermark);
        Assert.Equal(standby.Watermark, result.Watermark);
    }

    [Fact]
    public void Apply_LowerEpoch_RefusesAndWritesNothing()
    {
        using var f = new Fixture();
        using (var batch = f.PrimaryDb.NewBatch())
        {
            batch.Put(f.PrimaryBox, "k"u8.ToArray(), "v"u8.ToArray());
            batch.Commit(DurabilityMode.Strict);
        }

        var page = ReadPage(f.PrimaryDb, f.AlignedSeq);
        var standby = f.NewStandby();

        Assert.Equal(ApplyStatus.Applied, standby.Apply(epoch: 3, [], f.Uid, f.AlignedSeq).Status);
        Assert.Equal(3UL, standby.SeenEpoch);

        var before = standby.Watermark;
        var refused = standby.Apply(epoch: 2, page, f.Uid, f.AlignedSeq);
        Assert.Equal(ApplyStatus.EpochRefused, refused.Status);
        Assert.Equal(0, refused.Applied);
        Assert.Equal(before, standby.Watermark);
        Assert.Equal(3UL, standby.SeenEpoch);
    }

    [Fact]
    public void Apply_WritesTheFollowRecord_WithTheServedIncarnationAndBase()
    {
        using var f = new Fixture();
        using (var batch = f.PrimaryDb.NewBatch())
        {
            batch.Put(f.PrimaryBox, "k"u8.ToArray(), "v"u8.ToArray());
            batch.Commit(DurabilityMode.Strict);
        }

        var page = ReadPage(f.PrimaryDb, f.AlignedSeq);
        var standby = f.NewStandby();

        var incarnation = Guid.NewGuid();
        standby.Apply(epoch: 1, page, incarnation.ToString("D"), servedBase: f.AlignedSeq);

        var record = FollowRecord.Read(f.SDir.Path, f.Uid);
        Assert.NotNull(record);
        Assert.Equal(incarnation.ToString("D"), record!.Incarnation);
        Assert.Equal(f.AlignedSeq, record.Base);
    }

    [Fact]
    public void Apply_SameIncarnation_DoesNotRewriteTheFollowRecord_ButAStaleEpochNeverRecords()
    {
        using var f = new Fixture();
        using (var batch = f.PrimaryDb.NewBatch())
        {
            batch.Put(f.PrimaryBox, "k"u8.ToArray(), "v"u8.ToArray());
            batch.Commit(DurabilityMode.Strict);
        }

        var page = ReadPage(f.PrimaryDb, f.AlignedSeq);
        var standby = f.NewStandby(initialEpoch: 5);
        var before = FollowRecord.Read(f.SDir.Path, f.Uid);

        // A page below the seen epoch writes nothing and records nothing: the follow record is as it was.
        var refused = standby.Apply(epoch: 4, page, Guid.NewGuid().ToString("D"), f.AlignedSeq);
        Assert.Equal(ApplyStatus.EpochRefused, refused.Status);
        Assert.Equal(before?.Incarnation, FollowRecord.Read(f.SDir.Path, f.Uid)?.Incarnation);
        Assert.Equal(before?.Base, FollowRecord.Read(f.SDir.Path, f.Uid)?.Base);
    }

    [Fact]
    public void TotalOpsAcrossThePage_MatchTheSequenceAdvance()
    {
        using var f = new Fixture();
        for (var i = 0; i < 8; i++)
        {
            using var batch = f.PrimaryDb.NewBatch();
            batch.Put(f.PrimaryBox, Text.UTF8.GetBytes($"k{i}"), Text.UTF8.GetBytes($"v{i}"));
            batch.Commit(DurabilityMode.Strict);
        }

        var page = ReadPage(f.PrimaryDb, f.AlignedSeq);
        var standby = f.NewStandby();
        var before = standby.Watermark;
        standby.Apply(epoch: 1, page, f.Uid, f.AlignedSeq);

        var totalOps = page.Sum(b => (long)BinaryPrimitives.ReadUInt32LittleEndian(b.Data.AsSpan(8, 4)));
        Assert.Equal(before + (ulong)totalOps, standby.Watermark);
    }
}
