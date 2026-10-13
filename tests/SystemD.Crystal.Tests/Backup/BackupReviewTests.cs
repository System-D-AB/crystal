// Spec 010 T12 — the independent review's findings, each proved: a slot replication holds is never closed by the
// backup that opened it (B1); a standby's snapshot install stops a copy instead of waiting for it, and the backup takes
// the installed database (S1); a drop never removes the replication records of a UID a restore brought back while the
// drop drained (S3); a manifest UID that is not a UID is damage (S4); a set id in another case is unknown (M3).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Backup;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupReviewTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static CrystalOptions Replicating(Action<string>? hook = null) => new()
    {
        Hook = hook,
        Replication = new ReplicationOptions { Secret = "test-secret" },
    };

    [Fact]
    public void ASlotReplicationTakes_IsNotClosedByTheBackupThatOpenedIt()
    {
        using var dir = new TempStoreDirectory();
        string uid;
        uint id;
        using (var store = CrystalStore.Open(dir.Path, Replicating()))
        {
            var db = store.CreateDatabase("x", replicated: true);
            (uid, id) = (db.Uid, db.Id);
        }

        using (var store = CrystalStore.Open(dir.Path, Replicating()))
        {
            var (slot, openedHere) = store.Engine.OpenForBackup(store, id)!.Value;
            Assert.True(openedHere);
            var followed = store.Engine.OpenReplicatedByUid(store, uid); // a standby's follower takes it meanwhile
            Assert.Same(slot, followed);

            store.Engine.CloseOpenedForBackup(slot);

            Assert.False(slot.IsClosing);
            Assert.Equal(["x"], store.Status().Databases.Select(d => d.Name));
        }
    }

    [Fact]
    public async Task ASnapshotInstallDuringACopy_StopsItWithoutWaiting_AndTheBackupTakesTheInstalledDatabase()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Replicating());
        var db = store.CreateDatabase("x", replicated: true);
        BackupTests.Fill(db, db.Boxes.Create("box"), 5_000, 1024); // ~5 MB, incompressible: ~5 s at 1 MB/s
        var snapshot = Path.Combine(dir.Path, "bootstrap-review-s1");
        new WalAccess(db.Slot.Instance).Checkpoint(snapshot);
        var uid = db.Uid;

        var running = store.BackupAsync(root.Path, new BackupOptions { RateLimitBytesPerSecond = 1 << 20 });
        var watch = Stopwatch.StartNew();
        while (!Directory.Exists(Path.Combine(root.Path, "engines", uid)))
        {
            Assert.True(watch.Elapsed < Bound);
            await Task.Delay(10);
        }

        await Task.Delay(1000); // into the copy
        watch.Restart();
        await Task.Run(() => store.Engine.InstallBootstrap(store, uid, "x", snapshot)).WaitAsync(Bound);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the install waited {watch.Elapsed} for the copy");

        var set = await running.WaitAsync(Bound);
        Assert.Empty(set.Omitted);
        Assert.Equal(uid, Assert.Single(set.Databases).Uid);
        Assert.Equal(
            BackupTests.Rows(store.OpenDatabase("x").Slot.Instance),
            BackupTests.BackedUpRows(store, root.Path, set.Databases[0], set.SetId));
    }

    [Fact]
    public async Task ADropThatEndsAfterARestoreBroughtItsUidBack_LeavesTheRestoredDatabasesRecords()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo set;
        using (var source = new TempStoreDirectory())
        using (var store = CrystalStore.Open(source.Path, Replicating()))
        {
            store.CreateDatabase("x", replicated: true);
            set = await store.BackupAsync(root.Path);
        }

        CrystalStore? target = null;
        var uid = set.Databases[0].Uid;
        var restoredDuringDrop = false;
        target = CrystalStore.Open(dir.Path, Replicating(step =>
        {
            if (step == "drop.closed" && !restoredDuringDrop)
            {
                // The drop has committed and closed; before it removes the records, a restore brings the UID back and
                // the primary writes its history for it.
                restoredDuringDrop = true;
                target!.RestoreAsync(root.Path, set.SetId).GetAwaiter().GetResult();
                IncarnationRecord.Write(dir.Path, uid, LogIncarnation.Fresh(7));
            }
        }));
        using (target)
        {
            await target.RestoreAsync(root.Path, set.SetId);
            target.DropDatabase(target.OpenDatabase("x"));

            Assert.True(restoredDuringDrop);
            Assert.Equal(uid, Assert.Single(target.ListDatabases()).Uid);
            Assert.True(IncarnationRecord.Exists(dir.Path, uid), "the drop removed the restored database's record");
        }
    }

    [Fact]
    public async Task AManifestUidThatIsAPath_IsDamage_EvenResealed()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        store.CreateDatabase("x");
        var set = await store.BackupAsync(root.Path);
        var path = Path.Combine(root.Path, "sets", set.SetId + ".json");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(path))!;
        var crafted = manifest with { Databases = [manifest.Databases[0] with { Uid = @"..\..\outside" }] };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(crafted.Sealed()));

        Assert.Throws<BackupDamagedException>(() => BackupManifest.Read(path));
        using var target = new TempStoreDirectory();
        using var other = CrystalStore.Open(target.Path);
        await Assert.ThrowsAsync<BackupDamagedException>(() => other.RestoreAsync(root.Path, set.SetId));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "..", "outside")));
    }

    [Fact]
    public async Task ASetIdInAnotherCase_IsAnUnknownSet()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        store.CreateDatabase("x");
        var set = await store.BackupAsync(root.Path);

        using var target = new TempStoreDirectory();
        using var other = CrystalStore.Open(target.Path);
        await Assert.ThrowsAsync<BackupSetNotFoundException>(() => other.RestoreAsync(root.Path, set.SetId.ToUpperInvariant().Replace('T', 't')));
        using var open = CrystalBackupRoot.Open(root.Path);
        await Assert.ThrowsAsync<BackupSetNotFoundException>(() => open.VerifyAsync(set.SetId.Replace('T', 't'), deep: false));
    }
}
