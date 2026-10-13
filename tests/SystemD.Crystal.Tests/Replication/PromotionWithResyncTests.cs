// Spec 011 R2.3, R2.4 — a promotion while databases are marked for re-bootstrap. A database marked for a divergence may
// hold batches the log it followed does not, so its new history claims nothing of that log: its other standbys
// re-bootstrap from this node rather than continue on data it never had. A database marked for a gap is only behind, so
// it continues the log it followed. Either way the promotion goes ahead (the product decides whether to serve them),
// and one event names them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class PromotionWithResyncTests
{
    [Fact]
    public void APromotion_GivesADivergedDatabaseAFreshHistory_ContinuesAGappedOne_AndNamesBoth()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "test-secret" },
        });
        var diverged = Written(store, "diverged");
        var gapped = Written(store, "gapped");
        var clean = Written(store, "clean");

        // Each followed incarnation X as a standby (no record of its own writes), then two were marked.
        var x = Guid.NewGuid();
        foreach (var db in new[] { diverged, gapped, clean })
        {
            FollowRecord.Write(dir.Path, db.Uid, x, @base: 0);
            IncarnationRecord.Delete(dir.Path, db.Uid);
        }

        store.Replication.Demote();
        ResyncMarker.Write(dir.Path, diverged.Uid, "divergence");
        ResyncMarker.Write(dir.Path, gapped.Uid, "gap");

        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => events.Add(e);
        store.Replication.Promote("test");

        // The diverged database claims nothing of X; the gapped and the clean ones continue it.
        Assert.DoesNotContain(IncarnationRecord.Read(dir.Path, diverged.Uid)!.History, h => h.Id == x);
        Assert.Contains(IncarnationRecord.Read(dir.Path, gapped.Uid)!.History, h => h.Id == x);
        Assert.Contains(IncarnationRecord.Read(dir.Path, clean.Uid)!.History, h => h.Id == x);

        // One warning names the two marked databases and why, after Promoted.
        EventDelivery.Drain(store);
        var warning = Assert.Single(events, e => e.Kind == ReplicationEventKind.PromotedWithPendingResync);
        Assert.Contains("'diverged' (divergence)", warning.Detail, StringComparison.Ordinal);
        Assert.Contains("'gapped' (gap)", warning.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("'clean'", warning.Detail, StringComparison.Ordinal);
        Assert.True(events.FindIndex(e => e.Kind == ReplicationEventKind.Promoted) < events.IndexOf(warning));
    }

    [Fact]
    public void ADatabaseMarkedForADivergence_OpenedAfterThePromotion_GetsAFreshHistoryToo()
    {
        // The same rule when the database opens on the primary later rather than at the promotion (NewHistory serves both).
        using var dir = new TempStoreDirectory();
        string uid;
        var x = Guid.NewGuid();
        using (var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = "test-secret" } }))
        {
            uid = Written(store, "diverged").Uid;
            store.Replication.Demote();
        }

        FollowRecord.Write(dir.Path, uid, x, @base: 0);
        IncarnationRecord.Delete(dir.Path, uid);
        ResyncMarker.Write(dir.Path, uid, "diverged (primary reported from above its latest)");

        using (var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = "test-secret" } }))
        {
            store.Replication.Promote("test");
            _ = store.OpenDatabase("diverged");
            Assert.DoesNotContain(IncarnationRecord.Read(dir.Path, uid)!.History, h => h.Id == x);
        }
    }

    [Fact]
    public void APromotion_NamesMarkedDatabasesThatAreNotOpen_Too()
    {
        // Review 2026-10-13, S5: after a restart a marked database is re-bootstrapped without being opened; the warning
        // must still name it.
        using var dir = new TempStoreDirectory();
        string uid;
        using (var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = "test-secret" } }))
        {
            uid = Written(store, "closed").Uid;
            store.Replication.Demote();
        }

        ResyncMarker.Write(dir.Path, uid, "divergence");
        using var reopened = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = "test-secret" } });
        var events = new List<ReplicationEvent>();
        reopened.Replication.Events += (_, e) => events.Add(e);

        reopened.Replication.Promote("test");
        EventDelivery.Drain(reopened);

        var warning = Assert.Single(events, e => e.Kind == ReplicationEventKind.PromotedWithPendingResync);
        Assert.Contains("'closed' (divergence)", warning.Detail, StringComparison.Ordinal);
    }

    private static CrystalDatabase Written(CrystalStore store, string name)
    {
        var db = store.CreateDatabase(name, replicated: true);
        var box = db.Boxes.Create("box");
        using var batch = db.NewBatch();
        batch.Put(box, "k"u8, "v"u8);
        batch.Commit();
        return db;
    }
}
