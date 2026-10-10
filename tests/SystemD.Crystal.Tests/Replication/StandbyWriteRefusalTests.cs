// Spec 003 T8 / R1.6 — a database following a primary refuses every local write with a typed error
// (NotPrimaryException), while reads keep working. The applier's own replicated writes go through WalAccess.ApplyRaw,
// which does not pass the handle's commit path, so this refusal does not block replication (covered elsewhere).

using System;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class StandbyWriteRefusalTests
{
    private static CrystalStore Open(TempStoreDirectory dir) =>
        CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "s" },
        });

    [Fact]
    public void AFollowingDatabase_RefusesEveryWriteApi_ButKeepsReading()
    {
        using var dir = new TempStoreDirectory();
        using var store = Open(dir);
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");

        // Seed one value as the primary (before following), so reads have something to return.
        using (var seed = db.NewBatch())
        {
            seed.Put(box, "k"u8.ToArray(), "v"u8.ToArray());
            seed.Commit(DurabilityMode.Strict);
        }

        // Become a standby following a primary.
        db.Slot.Context.FollowingPrimary = true;

        // Every write API refuses with the typed error.
        Assert.Throws<NotPrimaryException>(() =>
        {
            using var batch = db.NewBatch();
            batch.Put(box, "k2"u8.ToArray(), "v2"u8.ToArray());
            batch.Commit();
        });
        Assert.Throws<NotPrimaryException>(() => db.Boxes.Create("c"));
        Assert.Throws<NotPrimaryException>(() => db.Boxes.Rename(box, "b2"));
        Assert.Throws<NotPrimaryException>(() => db.Boxes.Drop(box));
        Assert.Throws<NotPrimaryException>(() => db.Boxes.Swap(box, box));

        // Reads still work: the value seeded as primary is visible, and the box is still listed.
        using var snap = db.Snapshot();
        Assert.Equal("v"u8.ToArray(), snap.Get(box, "k"u8.ToArray()));
        Assert.NotNull(db.Boxes.Find("b"));
        Assert.NotEmpty(db.Boxes.List());
    }

    [Fact]
    public void StoppingFollowing_AcceptsWritesAgain()
    {
        using var dir = new TempStoreDirectory();
        using var store = Open(dir);
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");

        db.Slot.Context.FollowingPrimary = true;
        Assert.Throws<NotPrimaryException>(() =>
        {
            using var batch = db.NewBatch();
            batch.Put(box, "k"u8.ToArray(), "v"u8.ToArray());
            batch.Commit();
        });

        db.Slot.Context.FollowingPrimary = false; // promotion (T12)
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "k"u8.ToArray(), "v"u8.ToArray());
            batch.Commit(DurabilityMode.Strict);
        }

        using var snap = db.Snapshot();
        Assert.Equal("v"u8.ToArray(), snap.Get(box, "k"u8.ToArray()));
    }

    [Fact]
    public void AnEmptyBatchCommit_OnAStandby_DoesNotThrow()
    {
        // An empty batch writes nothing (R6.3) and never reaches the follow guard — committing one on a standby is a
        // no-op, not a refusal, matching the primary's behaviour.
        using var dir = new TempStoreDirectory();
        using var store = Open(dir);
        var db = store.CreateDatabase("db", replicated: true);
        db.Slot.Context.FollowingPrimary = true;

        using var batch = db.NewBatch();
        batch.Commit(); // no operations buffered
    }
}
