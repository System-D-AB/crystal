// Spec 003 T5 / R1.4 — the database-set reconciliation diff: a standby creates (by the primary's UID), drops and
// renames to match the primary's replicated set, and flags a name clash with one of its own databases. Also the
// engine's read side (ReplicatedDatabaseSet / LocalDatabasesForReconcile): the set excludes not-replicated databases.

using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class DatabaseSetReconcileTests
{
    private static ReplicatedDatabase P(string uid, string name, long latest = 0) => new(uid, name, latest);
    private static LocalDatabase L(string uid, string name, bool replicated = true) => new(uid, name, replicated);

    [Fact]
    public void NewUidOnPrimary_PlansCreateAndBootstrap()
    {
        var plan = DatabaseSetReconcile.Plan([P("uid-A", "shop")], []);
        var step = Assert.Single(plan);
        Assert.Equal(ReconcileAction.CreateAndBootstrap, step.Action);
        Assert.Equal("uid-A", step.Uid);
        Assert.Equal("shop", step.ToName);
    }

    [Fact]
    public void UidPresentUnderDifferentName_PlansRename()
    {
        var plan = DatabaseSetReconcile.Plan([P("uid-A", "shop")], [L("uid-A", "oldshop")]);
        var step = Assert.Single(plan);
        Assert.Equal(ReconcileAction.Rename, step.Action);
        Assert.Equal("oldshop", step.FromName);
        Assert.Equal("shop", step.ToName);
    }

    [Fact]
    public void SameUidSameName_PlansNothing()
    {
        var plan = DatabaseSetReconcile.Plan([P("uid-A", "shop")], [L("uid-A", "shop")]);
        Assert.Empty(plan);
    }

    [Fact]
    public void ReplicatedUidNoLongerListed_PlansDrop()
    {
        var plan = DatabaseSetReconcile.Plan([], [L("uid-A", "gone")]);
        var step = Assert.Single(plan);
        Assert.Equal(ReconcileAction.Drop, step.Action);
        Assert.Equal("uid-A", step.Uid);
        Assert.Equal("gone", step.FromName);
    }

    [Fact]
    public void LocalNotReplicatedDatabase_IsUntouched()
    {
        // A standby's own (not replicated) database is never dropped, renamed or counted.
        var plan = DatabaseSetReconcile.Plan([P("uid-A", "shop")], [L("uid-local", "mine", replicated: false)]);
        // Only the create of the primary's database; the local one is untouched.
        var step = Assert.Single(plan);
        Assert.Equal(ReconcileAction.CreateAndBootstrap, step.Action);
        Assert.Equal("uid-A", step.Uid);
    }

    [Fact]
    public void NewDatabaseNameClashesWithOwnDatabase_PlansNameClash_NotCreate()
    {
        // The primary's new database "mine" collides with the standby's own non-replicated "mine": flag, do not follow.
        var plan = DatabaseSetReconcile.Plan([P("uid-A", "mine")], [L("uid-local", "mine", replicated: false)]);
        var step = Assert.Single(plan);
        Assert.Equal(ReconcileAction.NameClash, step.Action);
        Assert.Equal("uid-A", step.Uid);
        Assert.Equal("mine", step.ToName);
    }

    [Fact]
    public void RenameTargetHeldByOwnDatabase_PlansNameClash_NotRename()
    {
        // uid-A must become "mine", but "mine" is the standby's own non-replicated database: a clash, not a rename.
        var plan = DatabaseSetReconcile.Plan(
            [P("uid-A", "mine")],
            [L("uid-A", "shopA"), L("uid-local", "mine", replicated: false)]);
        var clash = Assert.Single(plan);
        Assert.Equal(ReconcileAction.NameClash, clash.Action);
    }

    [Fact]
    public void MixedSet_PlansEachActionOnce()
    {
        var primary = new[] { P("uid-keep", "keep"), P("uid-ren", "newname"), P("uid-new", "fresh") };
        var local = new[]
        {
            L("uid-keep", "keep"),            // unchanged
            L("uid-ren", "oldname"),          // rename
            L("uid-gone", "gone"),            // drop (replicated, not listed)
            L("uid-own", "owndb", replicated: false), // untouched
        };

        var plan = DatabaseSetReconcile.Plan(primary, local);

        Assert.Equal(ReconcileAction.Rename, plan.Single(s => s.Uid == "uid-ren").Action);
        Assert.Equal(ReconcileAction.CreateAndBootstrap, plan.Single(s => s.Uid == "uid-new").Action);
        Assert.Equal(ReconcileAction.Drop, plan.Single(s => s.Uid == "uid-gone").Action);
        Assert.DoesNotContain(plan, s => s.Uid == "uid-keep");   // unchanged
        Assert.DoesNotContain(plan, s => s.Uid == "uid-own");    // own database untouched
    }

    // --- The engine's read side ---

    [Fact]
    public void ReplicatedDatabaseSet_ExcludesNotReplicated_AndCarriesUid()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "s" },
        });

        var shop = store.CreateDatabase("shop", replicated: true);
        _ = store.CreateDatabase("cache", replicated: false);

        var set = store.Engine.ReplicatedDatabaseSet();

        var one = Assert.Single(set);
        Assert.Equal(shop.Uid, one.Uid);
        Assert.Equal("shop", one.Name);

        // The local view includes both, flagged by replicated-ness, for reconciliation.
        var local = store.Engine.LocalDatabasesForReconcile();
        Assert.Equal(2, local.Count);
        Assert.True(local.Single(d => d.Name == "shop").Replicated);
        Assert.False(local.Single(d => d.Name == "cache").Replicated);
    }

    [Fact]
    public void SetVersion_ChangesWhenTheSetChanges()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "s" },
        });

        var before = store.Engine.ReplicatedSetVersion;
        store.CreateDatabase("shop", replicated: true);
        var after = store.Engine.ReplicatedSetVersion;

        Assert.True(after > before, "creating a database advances the set version");
    }
}
