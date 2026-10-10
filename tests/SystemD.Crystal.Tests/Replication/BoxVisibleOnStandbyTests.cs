// Spec 003 T8 "Done when" — a box created on the primary is visible by name on the standby, through the real apply +
// catalogue-reload path (R4.5). The two databases are aligned by a checkpoint of the primary's folder into the
// standby's (bootstrap proper is T10, snapshot over the wire; here a local checkpoint stands in so T8's apply and
// reload are proven end-to-end). After alignment, a box created on the primary ships as a box-0 page, and applying it
// on the standby reloads the catalogue so the new box is found by name.

using System;
using System.Collections.Generic;
using System.IO;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class BoxVisibleOnStandbyTests
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

    [Fact]
    public void ABoxCreatedOnThePrimary_IsVisibleByNameOnTheStandby_AfterApply()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();

        string uid;
        long alignedSeq;
        string checkpointDir = Path.Combine(Path.GetTempPath(), "crystal-ckpt-" + Guid.NewGuid().ToString("N"));

        // Build the primary, seed a box, and checkpoint its database folder for the standby to start from.
        using (var primary = Open(pdir.Path))
        {
            var pdb = primary.CreateDatabase("shop", replicated: true);
            var a = pdb.Boxes.Create("a");
            using (var batch = pdb.NewBatch())
            {
                batch.Put(a, "k"u8.ToArray(), "v"u8.ToArray());
                batch.Commit(DurabilityMode.Strict);
            }

            uid = pdb.Uid;
            alignedSeq = (long)pdb.Slot.LatestSequence;
            new WalAccess(pdb.Slot.Instance).Checkpoint(checkpointDir);
        }

        try
        {
            // The standby store: create "shop" with the primary's UID, as reconciliation does, then replace its database
            // folder with the primary's checkpoint so the two are physically aligned at `alignedSeq` (what T10's
            // snapshot bootstrap does over the wire). The UID must match: opening checks the identity (spec 009 R2.3).
            uint standbyDbId;
            using (var standbyStore = Open(sdir.Path))
            {
                var sdb = standbyStore.Engine.CreateDatabaseWithUid(standbyStore, "shop", replicated: true, uid);
                standbyDbId = sdb.Id;
            }

            var standbyDbFolder = Path.Combine(sdir.Path, StoreEngine.DatabaseFolderName(standbyDbId));
            Directory.Delete(standbyDbFolder, recursive: true);
            CopyDirectory(checkpointDir, standbyDbFolder);

            // Primary again: create a NEW box "b" (a box-0 write) past the aligned point, and read the page.
            List<(ulong Seq, byte[] Data)> page;
            using (var primary = Open(pdir.Path))
            {
                var pdb = primary.OpenDatabase("shop");
                pdb.Boxes.Create("b");
                page = ReadPage(pdb, alignedSeq);
                Assert.NotEmpty(page);
            }

            // The standby applies the box-create page; the catalogue reload makes "b" visible by name.
            using (var standbyStore = Open(sdir.Path))
            {
                var sdb = standbyStore.OpenDatabase("shop");
                Assert.NotNull(sdb.Boxes.Find("a"));   // the aligned box
                Assert.Null(sdb.Boxes.Find("b"));      // not yet applied

                var standby = new StandbyDatabase(sdb.Slot, uid, sdir.Path);
                var result = standby.Apply(epoch: 1, page, uid, servedBase: alignedSeq);
                Assert.True(result.Applied > 0);
                Assert.True(result.TouchedCatalogue, "a box create writes box 0");

                // After the apply + catalogue reload, the box created on the primary is found by name here.
                Assert.NotNull(sdb.Boxes.Find("b"));
            }
        }
        finally
        {
            try { Directory.Delete(checkpointDir, recursive: true); } catch (IOException) { }
        }
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
}
