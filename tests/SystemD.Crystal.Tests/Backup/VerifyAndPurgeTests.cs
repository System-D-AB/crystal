// Spec 010 T6 — verify and purge on a root opened without a store (R5; design § 6): a deleted engine file caught by
// quick verify; a flipped byte inside a table file caught by deep verify and not by quick; an edited manifest caught by
// both and never listed as a set; purge keeping the newest sets, each still restorable, and deleting the engines of
// databases no kept set includes; the root's lock held while it is open.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class VerifyAndPurgeTests
{
    private static async Task<(BackupSetInfo Set, string Uid)> OneSet(string dir, string root, int rows = 2_000)
    {
        using var store = CrystalStore.Open(dir);
        var db = store.CreateDatabase("db");
        BackupTests.Fill(db, db.Boxes.Create("box"), rows, 256);
        return (await store.BackupAsync(root), db.Uid);
    }

    private static string LargestTableFile(string root, string uid) =>
        Directory.EnumerateFiles(Path.Combine(root, "engines", uid, "shared_checksum"), "*.sst")
            .OrderByDescending(f => new FileInfo(f).Length).First();

    [Fact]
    public async Task AGoodSet_VerifiesQuickAndDeep_AndDeepLeavesNoScratch()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var (set, _) = await OneSet(dir.Path, root.Path);

        using var open = CrystalBackupRoot.Open(root.Path);
        Assert.True((await open.VerifyAsync(set.SetId, deep: false)).Ok);
        var deep = await open.VerifyAsync(set.SetId, deep: true);
        Assert.True(deep.Ok, string.Join("; ", deep.Databases.Select(d => d.Problem)));
        Assert.True(deep.Deep);
        Assert.Empty(Directory.Exists(Path.Combine(root.Path, "scratch")) ? Directory.EnumerateFileSystemEntries(Path.Combine(root.Path, "scratch")) : []);
    }

    [Fact]
    public async Task ADeletedEngineFile_IsCaughtByQuickVerify_AsAResultNotAThrow()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var (set, uid) = await OneSet(dir.Path, root.Path);
        File.Delete(LargestTableFile(root.Path, uid));

        using var open = CrystalBackupRoot.Open(root.Path);
        var result = await open.VerifyAsync(set.SetId, deep: false);
        var database = Assert.Single(result.Databases);
        Assert.False(result.Ok);
        Assert.False(database.Ok);
        Assert.NotNull(database.Problem);
    }

    [Theory]
    [InlineData("truncate a table file")]
    [InlineData("delete a private file")]
    public async Task ATruncatedTableFile_OrAMissingPrivateFile_IsCaughtByQuickVerify(string damage)
    {
        // The engine's own verify misses a truncation: it takes the sizes on disk when it opens as the recorded ones.
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var (set, uid) = await OneSet(dir.Path, root.Path);
        if (damage == "truncate a table file")
        {
            var file = LargestTableFile(root.Path, uid);
            File.WriteAllBytes(file, File.ReadAllBytes(file)[..^1]);
        }
        else
        {
            File.Delete(Directory.EnumerateFiles(Path.Combine(root.Path, "engines", uid, "private"), "MANIFEST-*", SearchOption.AllDirectories).Single());
        }

        using var open = CrystalBackupRoot.Open(root.Path);
        var quick = await open.VerifyAsync(set.SetId, deep: false);
        Assert.False(quick.Ok);
        Assert.Contains("its file", Assert.Single(quick.Databases).Problem);
    }

    [Fact]
    public async Task ATableFileDamagedBeforeItWasBackedUp_IsCaughtByDeepVerifysChecksums_AndNotByQuick()
    {
        // The backup copies the damaged file faithfully, with a checksum of the damaged bytes, so its restore succeeds:
        // only RocksDB's block checksums, which deep verify reads, find it.
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        uint id;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var db = store.CreateDatabase("db");
            BackupTests.Fill(db, db.Boxes.Create("box"), 2_000, 256);
            id = db.Id;
            using var flushing = new TempStoreDirectory();
            await store.BackupAsync(flushing.Path); // a backup flushes: the data is in table files from here
        }

        var table = Directory.EnumerateFiles(dir.DatabaseFolder(id), "*.sst").OrderByDescending(f => new FileInfo(f).Length).First();
        var bytes = File.ReadAllBytes(table);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(table, bytes);

        BackupSetInfo set;
        using (var store = CrystalStore.Open(dir.Path))
        {
            set = await store.BackupAsync(root.Path);
        }

        using var open = CrystalBackupRoot.Open(root.Path);
        Assert.True((await open.VerifyAsync(set.SetId, deep: false)).Ok);
        var deep = await open.VerifyAsync(set.SetId, deep: true);
        Assert.False(deep.Ok);
        Assert.NotNull(Assert.Single(deep.Databases).Problem);
    }

    [Fact]
    public async Task AFlippedByteInsideATableFile_IsCaughtByDeepVerify_AndNotByQuick()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var (set, uid) = await OneSet(dir.Path, root.Path);
        var file = LargestTableFile(root.Path, uid);
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        using var open = CrystalBackupRoot.Open(root.Path);
        Assert.True((await open.VerifyAsync(set.SetId, deep: false)).Ok);
        var deep = await open.VerifyAsync(set.SetId, deep: true);
        Assert.False(deep.Ok);
        Assert.NotNull(Assert.Single(deep.Databases).Problem);
    }

    [Fact]
    public async Task AnEditedManifest_IsCaughtByBothVerifies_AndListedAsDamaged_NeverAsASet()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var (set, _) = await OneSet(dir.Path, root.Path);
        var path = Path.Combine(root.Path, "sets", set.SetId + ".json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"db\"", "\"dc\"", StringComparison.Ordinal));

        using var open = CrystalBackupRoot.Open(root.Path);
        await Assert.ThrowsAsync<BackupDamagedException>(() => open.VerifyAsync(set.SetId, deep: false));
        await Assert.ThrowsAsync<BackupDamagedException>(() => open.VerifyAsync(set.SetId, deep: true));
        Assert.Empty(open.ListSets());
        Assert.Equal([path], open.DamagedManifests());
    }

    [Fact]
    public async Task AnOpenRoot_HoldsItsLock_AgainstABackupAndASecondOpener()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        await OneSet(dir.Path, root.Path);
        using var store = CrystalStore.Open(dir.Path);

        using (CrystalBackupRoot.Open(root.Path))
        {
            Assert.Throws<BackupRootLockedException>(() => CrystalBackupRoot.Open(root.Path));
            await Assert.ThrowsAsync<BackupRootLockedException>(() => store.BackupAsync(root.Path));
        }

        await store.BackupAsync(root.Path);
    }

    [Fact]
    public async Task Purge_KeepsTheNewestSets_EachRestorable_AndDeletesTheRest()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        string goneUid, keptUid;
        var sets = new System.Collections.Generic.List<BackupSetInfo>();
        using (var store = CrystalStore.Open(dir.Path))
        {
            var gone = store.CreateDatabase("gone"); // only in the first two sets
            var kept = store.CreateDatabase("kept");
            goneUid = gone.Uid;
            keptUid = kept.Uid;
            var box = kept.Boxes.Create("box");
            for (var i = 0; i < 4; i++)
            {
                BackupTests.Fill(kept, box, 200, 64, prefix: $"round{i}-");
                if (i == 2)
                {
                    store.DropDatabase(gone);
                }

                sets.Add(await store.BackupAsync(root.Path));
            }
        }

        using (var open = CrystalBackupRoot.Open(root.Path))
        {
            Assert.Equal(sets.Select(s => s.SetId), open.ListSets().Select(s => s.SetId));
            var result = open.Purge(keepLast: 2);

            Assert.Equal(new BackupPurgeResult(2, 2, 1), result);
            Assert.Equal(sets.Skip(2).Select(s => s.SetId), open.ListSets().Select(s => s.SetId));
            Assert.False(Directory.Exists(Path.Combine(root.Path, "engines", goneUid)));
            using (var engine = BackupEngineHandle.Open(Path.Combine(root.Path, "engines", keptUid)))
            {
                Assert.Equal(2, engine.List().Length); // the two older backups of "kept" are gone
            }

            foreach (var set in sets.Skip(2))
            {
                Assert.True((await open.VerifyAsync(set.SetId, deep: true)).Ok);
            }
        }

        foreach (var set in sets.Skip(2))
        {
            using var target = new TempStoreDirectory();
            using var store = CrystalStore.Open(target.Path);
            await store.RestoreAsync(root.Path, set.SetId);
            Assert.Equal(["kept"], store.ListDatabases().Select(d => d.Name));
        }

        using (var open = CrystalBackupRoot.Open(root.Path))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => open.Purge(0));
        }
    }
}
