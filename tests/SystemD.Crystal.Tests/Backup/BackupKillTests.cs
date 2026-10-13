// Spec 010 T8 — kills in another process (R9.4, R9.9, R2.4): a backup killed during its native copy leaves every
// earlier set listed, verifiable and restorable, and the next backup into the root succeeds; a restore killed before
// its registry batch leaves no visible database and no folder after the next open; a root held by one process is
// refused to another.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupKillTests
{
    [Fact]
    public async Task ABackupKilledDuringItsCopy_LeavesEarlierSetsWhole_AndTheNextBackupSucceeds()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo earlier;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var small = store.CreateDatabase("small");
            BackupTests.Fill(small, small.Boxes.Create("box"), 100, 16);
            earlier = await store.BackupAsync(root.Path);
            var big = store.CreateDatabase("big");
            BackupTests.Fill(big, big.Boxes.Create("box"), 20_000, 1024); // ~20 MB at 1 MB/s: the kill lands mid-copy
        }

        // "small" is copied first (it is unchanged: little to copy), then "big", killed a second into its copy.
        var at = CrashHost.RunAndKillAfter(TimeSpan.FromSeconds(1.5), "backup", dir.Path, root.Path, "backup.copying+run");
        Assert.Equal("backup.copying", at);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(root.Path, "engines")));

        using (var open = CrystalBackupRoot.Open(root.Path))
        {
            Assert.Equal([earlier.SetId], open.ListSets().Select(s => s.SetId));
            Assert.Empty(open.DamagedManifests());
            var verify = await open.VerifyAsync(earlier.SetId, deep: true);
            Assert.True(verify.Ok, string.Join("; ", verify.Databases.Select(d => d.Problem)));
        }

        using (var target = new TempStoreDirectory())
        using (var store = CrystalStore.Open(target.Path))
        {
            await store.RestoreAsync(root.Path, earlier.SetId);
            Assert.Equal(["small"], store.ListDatabases().Select(d => d.Name));
        }

        using (var store = CrystalStore.Open(dir.Path))
        {
            var next = await store.BackupAsync(root.Path);
            Assert.Equal(["big", "small"], next.Databases.Select(d => d.Name).Order());
            using var open = CrystalBackupRoot.Open(root.Path);
            Assert.True((await open.VerifyAsync(next.SetId, deep: true)).Ok);
        }
    }

    [Theory]
    [InlineData("restore.moved")]       // the first database moved into db-{id}
    [InlineData("restore.registering")] // every database in place, the registry batch not yet written
    public async Task ARestoreKilledBeforeItsRegistryBatch_LeavesNoVisibleDatabase_AndNoFolderAfterTheNextOpen(string step)
    {
        using var source = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo set;
        using (var store = CrystalStore.Open(source.Path))
        {
            store.CreateDatabase("a");
            store.CreateDatabase("b");
            set = await store.BackupAsync(root.Path);
        }

        using var dir = new TempStoreDirectory();
        uint kept;
        using (var store = CrystalStore.Open(dir.Path))
        {
            kept = store.CreateDatabase("kept").Id;
        }

        Assert.Equal(step, CrashHost.RunAndKillAtStep("restore", dir.Path, root.Path, set.SetId, step));
        Assert.Contains(Directory.EnumerateDirectories(dir.Path), d => Path.GetFileName(d) == StoreEngine.DatabaseFolderName(kept + 1)); // a restored folder was in place

        using (var store = CrystalStore.Open(dir.Path))
        {
            Assert.Equal(["kept"], store.ListDatabases().Select(d => d.Name));
            Assert.Equal(
                [StoreEngine.DatabaseFolderName(kept)],
                Directory.EnumerateDirectories(dir.Path).Select(Path.GetFileName).Where(n => n!.StartsWith("db-", StringComparison.Ordinal) || n.StartsWith(StoreEngine.RestoreStagingPrefix, StringComparison.Ordinal)));

            // The restore can simply be run again.
            await store.RestoreAsync(root.Path, set.SetId);
            Assert.Equal(["a", "b", "kept"], store.ListDatabases().Select(d => d.Name).Order());
        }
    }

    [Fact]
    public async Task ARootHeldByAnotherProcess_IsRefused_UntilItIsReleased()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        store.CreateDatabase("db");
        await store.BackupAsync(root.Path);

        using (var holder = CrashHost.StartAndWaitFor("HELD", "holdroot", root.Path))
        {
            Assert.Throws<BackupRootLockedException>(() => CrystalBackupRoot.Open(root.Path));
            await Assert.ThrowsAsync<BackupRootLockedException>(() => store.BackupAsync(root.Path));
            holder.StandardInput.Close();
            Assert.True(holder.WaitForExit(60_000));
        }

        using (CrystalBackupRoot.Open(root.Path))
        {
        }
    }
}
