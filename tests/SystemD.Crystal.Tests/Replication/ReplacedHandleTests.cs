// Spec 013 R7 — when a standby's database is re-created from its primary's snapshot, the old handle says so: a
// DatabaseNotFoundException that says to open the database again, never "the store has been closed" while it is open.

using System;
using System.IO;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class ReplacedHandleTests
{
    [Fact]
    public void AReplacedDatabasesOldHandles_SayItWasRecreated_AndTheNewHandleWorks()
    {
        using var dir = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = new ReplicationOptions { Secret = "test-secret" } });
        var old = store.CreateDatabase("x", replicated: true);
        var oldBox = old.Boxes.Create("box");
        var snapshot = Path.Combine(dir.Path, "bootstrap-replaced-handle");
        new WalAccess(old.Slot.Instance).Checkpoint(snapshot);

        var fresh = store.Engine.InstallBootstrap(store, old.Uid, "x", snapshot);

        var refusal = Assert.Throws<DatabaseNotFoundException>(() => old.NewBatch());
        Assert.Contains("re-created", refusal.Message, StringComparison.Ordinal);
        Assert.Throws<DatabaseNotFoundException>(() => old.Snapshot().Get(oldBox, "k"u8));
        Assert.NotNull(store.OpenDatabase("x").Boxes.Find("box"));
        Assert.Same(fresh, store.OpenDatabase("x"));

        // A closed store is still reported as closed.
        var current = store.OpenDatabase("x");
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => current.NewBatch());
    }
}
