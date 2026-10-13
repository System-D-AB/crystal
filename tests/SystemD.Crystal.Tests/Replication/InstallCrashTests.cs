// Spec 011 R9 — crash ordering. An install of a new UID advances the allocator before it moves the snapshot in, as a
// create does, so a process killed at any step leaves a store that opens and creates (R9.2). And every rename that
// publishes replication state is followed by a sync of its directory (R9.1): counted through DurableFile's event, since
// a power loss cannot be simulated in a test (R9.3).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class InstallCrashTests
{
    /// <summary>
    /// A checkpoint of a replicated database from another store, placed in <paramref name="holder"/> (not in the target's
    /// data directory, whose startup recovery removes <c>bootstrap-*</c> leftovers; the same volume, so the install's
    /// move is a rename).
    /// </summary>
    private static (string Uid, string Snapshot) SnapshotInto(string holder)
    {
        using var source = new TempStoreDirectory();
        using var store = CrystalStore.Open(source.Path);
        var db = store.CreateDatabase("remote", replicated: true);
        var box = db.Boxes.Create("box");
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "k"u8, "v"u8);
            batch.Commit();
        }

        Directory.CreateDirectory(holder);
        var snapshot = Path.Combine(holder, "snapshot");
        new WalAccess(db.Slot.Instance).Checkpoint(snapshot);
        return (db.Uid, snapshot);
    }

    [Theory]
    [InlineData("install.allocated")]
    [InlineData("install.aside")]
    [InlineData("install.moved")]
    [InlineData("none")]
    public void AnInstallOfANewUid_KilledAtAnyStep_LeavesAStoreThatOpensAndCreates(string step)
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("existing");
        }

        using var holder = new TempStoreDirectory();
        var (uid, snapshot) = SnapshotInto(holder.Path);
        var reached = CrashHost.RunAndKillAtStep("install", dir.Path, "remote", uid, snapshot, step);
        Assert.Equal(step == "none" ? "DONE" : step, reached);

        using var reopened = CrystalStore.Open(dir.Path);
        var live = reopened.ListDatabases();
        var installed = step == "none";
        Assert.Equal(installed, live.Any(d => d.Name == "remote"));

        // Every db-* folder belongs to a live database: no folder of an interrupted install is left behind.
        var folders = Directory.EnumerateDirectories(dir.Path)
            .Select(path => Path.GetFileName(path)!)
            .Where(name => name.StartsWith("db-", StringComparison.Ordinal) && !name.Contains('.', StringComparison.Ordinal))
            .ToHashSet();
        Assert.Equal(reopened.ListDatabases().Select(d => StoreEngine.DatabaseFolderName(d.Id)).ToHashSet(), folders);

        // And a create works: no folder sits at the next id (before the fix, a kill after the move left one there,
        // and every create was refused until it was removed by hand).
        var shop = reopened.CreateDatabase("shop");
        Assert.Contains(reopened.ListDatabases(), d => d.Name == "shop" && d.Id == shop.Id);
    }

    [Fact]
    public void EveryRenameThatPublishesReplicationState_IsFollowedByADirectorySync()
    {
        using var dir = new TempStoreDirectory();
        var synced = new ConcurrentBag<string>();
        void Record(string d)
        {
            if (Path.GetFullPath(d).StartsWith(Path.GetFullPath(dir.Path), StringComparison.OrdinalIgnoreCase))
            {
                synced.Add(Path.GetFullPath(d));
            }
        }

        DurableFile.DirectorySynced += Record;
        try
        {
            using var store = CrystalStore.Open(dir.Path, new CrystalOptions
            {
                Replication = new ReplicationOptions { Secret = "test-secret" },
            });

            // node.epoch (a promotion persists it).
            synced.Clear();
            store.Replication.Promote("test");
            Assert.Contains(Path.GetFullPath(dir.Path), synced);

            // A follow record and an incarnation record.
            synced.Clear();
            var uid = Guid.NewGuid().ToString("D");
            FollowRecord.Write(dir.Path, uid, Guid.NewGuid(), @base: 5);
            Assert.Contains(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(FollowRecord.Directory(dir.Path), uid + ".json"))), synced);

            synced.Clear();
            IncarnationRecord.Write(dir.Path, uid, LogIncarnation.Fresh(5));
            Assert.Single(synced);

            // An install of a new UID: the folder move into place.
            synced.Clear();
            using var holder = new TempStoreDirectory();
            var (remoteUid, snapshot) = SnapshotInto(holder.Path);
            store.Engine.InstallBootstrap(store, remoteUid, "remote", snapshot);
            Assert.Contains(Path.GetFullPath(dir.Path), synced);
        }
        finally
        {
            DurableFile.DirectorySynced -= Record;
        }
    }
}
