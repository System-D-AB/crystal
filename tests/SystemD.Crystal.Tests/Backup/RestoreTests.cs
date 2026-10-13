// Spec 010 T5 — a restore (R6, R11; design § 7): a set's databases attached to a store with new ids, all or none; the
// round trip of R9.1; a subset and renames; a copy beside its original; each refusal before anything is written; an
// injected failure that leaves the store as it was; a restore into a store that keeps serving; the replication rules of
// the addendum (fresh history, primary only for replicated databases).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Backup;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Backup;

public sealed class RestoreTests
{
    private static CrystalOptions Replicating(Action<string>? hook = null) => new()
    {
        Hook = hook,
        Replication = new ReplicationOptions { Secret = "test-secret" },
    };

    /// <summary>A store of three databases with boxes of both profiles, renamed and swapped, backed up into the root.</summary>
    private static async Task<BackupSetInfo> BackedUpStore(string dir, string root, CrystalOptions? options = null)
    {
        using var store = CrystalStore.Open(dir, options);
        foreach (var name in new[] { "orders", "users", "scratch" })
        {
            var db = store.CreateDatabase(name, replicated: name != "scratch");
            BackupTests.Fill(db, db.Boxes.Create("general"), 300, 48, prefix: name);
            BackupTests.Fill(db, db.Boxes.Create("queue", BoxProfile.DeleteHeavy), 100, 16, prefix: name);
        }

        store.RenameDatabase(store.OpenDatabase("scratch"), "notes");
        store.SwapDatabases(store.OpenDatabase("orders"), store.OpenDatabase("users"));
        return await store.BackupAsync(root);
    }

    /// <summary>Every database of a store by name: its boxes (name, profile) and every row.</summary>
    private static Dictionary<string, (List<string> Boxes, List<string> Rows)> Contents(CrystalStore store) =>
        store.ListDatabases().ToDictionary(
            d => d.Name,
            d =>
            {
                var db = store.OpenDatabase(d.Name);
                return (db.Boxes.List().Select(b => $"{b.Name}:{b.Profile}").Order().ToList(), BackupTests.Rows(db.Slot.Instance));
            });

    private static List<string> Folders(string dir) =>
        [.. Directory.EnumerateDirectories(dir).Select(d => Path.GetFileName(d)).Where(n => n.StartsWith("db-", StringComparison.Ordinal) || n.StartsWith(StoreEngine.RestoreStagingPrefix, StringComparison.Ordinal)).Order()];

    [Fact]
    public async Task ARoundTrip_IntoANewStore_GivesTheSameDatabases_KeyByKeyAndBoxByBox_NamesIncluded()
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);

        Dictionary<string, (List<string> Boxes, List<string> Rows)> expected;
        IReadOnlyList<DatabaseInfo> before;
        using (var store = CrystalStore.Open(source.Path))
        {
            expected = Contents(store);
            before = store.ListDatabases();
        }

        using (var store = CrystalStore.Open(target.Path))
        {
            var restored = await store.RestoreAsync(root.Path, set.SetId);

            Assert.Equal(["notes", "orders", "users"], restored.Select(d => d.Name).Order());
            Assert.Equal(
                before.Select(d => (d.Name, d.Uid, d.Replicated, d.Created)).Order(),
                store.ListDatabases().Select(d => (d.Name, d.Uid, d.Replicated, d.Created)).Order());
            var actual = Contents(store);
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var name in expected.Keys)
            {
                Assert.Equal(expected[name].Boxes, actual[name].Boxes);
                Assert.Equal(expected[name].Rows, actual[name].Rows);
            }

            Assert.NotEqual(store.NodeId, set.SourceNodeId); // a restored store is its own node (R6.1)
        }

        // And again after a reopen: the registry and each database's identity agree.
        using (var store = CrystalStore.Open(target.Path))
        {
            Assert.Equal(3, Contents(store).Count);
        }
    }

    [Fact]
    public async Task ASubset_UnderNewNames_AsNotReplicated()
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);
        using var store = CrystalStore.Open(target.Path);

        var restored = await store.RestoreAsync(root.Path, set.SetId, new RestoreOptions
        {
            Databases = ["orders", "notes"],
            Rename = new Dictionary<string, string> { ["orders"] = "orders-2026" },
            Replicated = false,
        });

        Assert.Equal(["notes", "orders-2026"], store.ListDatabases().Select(d => d.Name).Order());
        Assert.All(store.ListDatabases(), d => Assert.False(d.Replicated));
        Assert.Equal(set.Databases.Single(d => d.Name == "orders").Uid, restored.Single(d => d.Name == "orders-2026").Uid);
    }

    [Fact]
    public async Task ACopy_IsRestoredBesideItsOriginal_WithANewUid_AndKeepsItAcrossAReopen()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(dir.Path, root.Path);
        string copyUid;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var original = store.OpenDatabase("orders");
            var restored = await store.RestoreAsync(root.Path, set.SetId, new RestoreOptions
            {
                Databases = ["orders"],
                Rename = new Dictionary<string, string> { ["orders"] = "orders-copy" },
                NewIdentity = true,
            });

            var copy = Assert.Single(restored);
            copyUid = copy.Uid;
            Assert.NotEqual(original.Uid, copy.Uid);
            Assert.Equal(set.Databases.Single(d => d.Name == "orders").Created, copy.Created);
            var opened = store.OpenDatabase("orders-copy");
            Assert.Equal(copy.Uid, opened.Uid);
            Assert.Equal(
                BackupTests.Rows(original.Slot.Instance).Where(r => !r.StartsWith("default:", StringComparison.Ordinal)),
                BackupTests.Rows(opened.Slot.Instance).Where(r => !r.StartsWith("default:", StringComparison.Ordinal)));
        }

        using (var store = CrystalStore.Open(dir.Path))
        {
            Assert.Equal(copyUid, store.OpenDatabase("orders-copy").Uid); // its identity record says the new UID too
        }
    }

    [Fact]
    public async Task ATakenNameOrUid_IsRefused_BeforeAnythingIsWritten()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(dir.Path, root.Path);
        using var store = CrystalStore.Open(dir.Path);
        var folders = Folders(dir.Path);
        var generation = store.RegistryGeneration;

        // Every name and UID is live here already.
        var uidTaken = await Assert.ThrowsAsync<DatabaseUidTakenException>(() => store.RestoreAsync(root.Path, set.SetId, new RestoreOptions
        {
            Databases = ["orders"],
            Rename = new Dictionary<string, string> { ["orders"] = "elsewhere" },
        }));
        Assert.Equal("orders", uidTaken.ExistingName);

        var nameTaken = await Assert.ThrowsAsync<DatabaseNameTakenException>(() => store.RestoreAsync(root.Path, set.SetId, new RestoreOptions
        {
            Databases = ["orders"],
            NewIdentity = true,
        }));
        Assert.Equal("orders", nameTaken.DatabaseName);

        Assert.Equal(folders, Folders(dir.Path));
        Assert.Equal(generation, store.RegistryGeneration);
    }

    [Fact]
    public async Task AnUnknownSet_ADamagedManifest_AndAForeignFormat_AreRefusedByName()
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);
        using var store = CrystalStore.Open(target.Path);

        await Assert.ThrowsAsync<BackupSetNotFoundException>(() => store.RestoreAsync(root.Path, "20200101T000000000Z-00000000"));
        await Assert.ThrowsAsync<BackupSetNotFoundException>(() => store.RestoreAsync(root.Path, "../" + set.SetId));
        await Assert.ThrowsAsync<BackupDestinationException>(() => store.RestoreAsync(source.Path, set.SetId));

        var path = Path.Combine(root.Path, "sets", set.SetId + ".json");
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("\"orders\"", "\"ordets\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<BackupDamagedException>(() => store.RestoreAsync(root.Path, set.SetId));

        // A set sealed by a build of another encoder version: well formed, checksum right, refused by its format.
        var foreign = JsonSerializer.Deserialize<BackupManifest>(text)! with { Versions = new BackupManifest.ManifestVersions { Encoder = 9 } };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(foreign.Sealed()));
        var refusal = await Assert.ThrowsAsync<CrystalFormatException>(() => store.RestoreAsync(root.Path, set.SetId));
        Assert.Equal("encoder 9", refusal.FoundFormat);

        Assert.Empty(store.ListDatabases());
        Assert.Empty(Folders(target.Path));
    }

    [Fact]
    public async Task ADatabaseWhoseBackupFailsQuickVerify_StopsTheRestore_BeforeAnythingIsWritten()
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);
        var users = set.Databases.Single(d => d.Name == "users");
        var file = Directory.EnumerateFiles(Path.Combine(root.Path, "engines", users.Uid, "shared_checksum")).First();
        File.Delete(file);
        using var store = CrystalStore.Open(target.Path);

        await Assert.ThrowsAsync<BackupDamagedException>(() => store.RestoreAsync(root.Path, set.SetId));
        Assert.Empty(store.ListDatabases());

        // The other databases still restore on their own.
        await store.RestoreAsync(root.Path, set.SetId, new RestoreOptions { Databases = ["orders", "notes"] });
        Assert.Equal(2, store.ListDatabases().Count);
    }

    [Theory]
    [InlineData("restore.moved", 2)]       // after the second of three is in place
    [InlineData("restore.registering", 1)] // after all are in place, before the registry batch
    public async Task AFailureBeforeTheRegistryBatch_LeavesTheStoreAsItWas(string step, int nth)
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);
        var seen = 0;
        using (var store = CrystalStore.Open(target.Path, new CrystalOptions
        {
            Hook = at =>
            {
                if (at == step && Interlocked.Increment(ref seen) == nth)
                {
                    throw new IOException("injected");
                }
            },
        }))
        {
            var kept = store.CreateDatabase("kept");
            var generation = store.RegistryGeneration;

            await Assert.ThrowsAsync<IOException>(() => store.RestoreAsync(root.Path, set.SetId));

            Assert.Equal(["kept"], store.ListDatabases().Select(d => d.Name));
            Assert.Equal(generation, store.RegistryGeneration);
            Assert.Equal([Path.GetFileName(target.DatabaseFolder(kept.Id))], Folders(target.Path)); // the restore's are gone; their ids stay skipped
            GC.KeepAlive(kept);

            // Nothing was left half-done: the same restore succeeds now, with later ids.
            var restored = await store.RestoreAsync(root.Path, set.SetId);
            Assert.All(restored, d => Assert.True(d.Id > 4));
        }
    }

    [Fact]
    public async Task AStagingFolderLeftByACrash_IsRemovedByTheNextOpen()
    {
        using var dir = new TempStoreDirectory();
        using (CrystalStore.Open(dir.Path))
        {
        }

        var staging = Path.Combine(dir.Path, StoreEngine.RestoreStagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "CURRENT"), "x");

        using (CrystalStore.Open(dir.Path))
        {
            Assert.False(Directory.Exists(staging));
        }
    }

    [Fact]
    public async Task ARestore_IntoAStoreThatKeepsServingItsOtherDatabases()
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);
        using var store = CrystalStore.Open(target.Path);
        var live = store.CreateDatabase("live");
        var box = live.Boxes.Create("box");

        using var stop = new CancellationTokenSource();
        var commits = 0;
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var batch = live.NewBatch();
                batch.Put(box, Text.UTF8.GetBytes($"k{commits:D8}"), "v"u8);
                batch.Commit();
                commits++;
            }
        });

        var startCommits = Volatile.Read(ref commits);
        await store.RestoreAsync(root.Path, set.SetId);
        var during = Volatile.Read(ref commits) - startCommits;
        stop.Cancel();
        await writer;

        Assert.True(during > 0, "the live database took no commit during the restore");
        Assert.Equal(4, store.ListDatabases().Count);
    }

    [Fact]
    public async Task ARestoredReplicatedDatabase_StartsAFreshHistory_LeavingNoRecordOfAnEarlierLife()
    {
        // R11.2: records of the UID left behind (a crash before R11.1, or a hand-made folder) are cleared before it is
        // registered, so a promotion can never claim an earlier life's history for the restored data.
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Replicating());
        var db = store.CreateDatabase("db", replicated: true);
        BackupTests.Fill(db, db.Boxes.Create("box"), 10, 8);
        var uid = db.Uid;
        var set = await store.BackupAsync(root.Path);
        store.DropDatabase(db);
        FollowRecord.Write(dir.Path, uid, Guid.NewGuid(), @base: 0);
        var earlierLife = LogIncarnation.Fresh(3);
        IncarnationRecord.Write(dir.Path, uid, earlierLife);
        ResyncMarker.Write(dir.Path, uid, "divergence");

        await store.RestoreAsync(root.Path, set.SetId);

        Assert.Null(FollowRecord.Read(dir.Path, uid));
        Assert.False(ResyncMarker.Exists(dir.Path, uid));
        var reopened = store.OpenDatabase("db");
        Assert.Equal(uid, reopened.Uid);
        Assert.NotEqual(earlierLife.Id, reopened.Slot.Incarnation.Id); // not the left-behind incarnation
        Assert.DoesNotContain(reopened.Slot.Incarnation.History, h => h.Id == earlierLife.Id);
    }

    [Fact]
    public async Task AReplicatedRestore_IsRefusedOnAStandby_AFencedNode_AndDuringAHandover_ButNotReplicatedIsAllowed()
    {
        using var source = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var set = await BackedUpStore(source.Path, root.Path);

        using (var dir = new TempStoreDirectory())
        using (var store = CrystalStore.Open(dir.Path, Replicating()))
        {
            store.Replication.Demote();
            await Assert.ThrowsAsync<NotPrimaryException>(() => store.RestoreAsync(root.Path, set.SetId));
            Assert.Empty(store.ListDatabases());

            await store.RestoreAsync(root.Path, set.SetId, new RestoreOptions { Replicated = false });
            Assert.Equal(3, store.ListDatabases().Count);
        }

        using (var dir = new TempStoreDirectory())
        using (var store = CrystalStore.Open(dir.Path, Replicating()))
        {
            store.Replication.Fence("test");
            await Assert.ThrowsAsync<NotPrimaryException>(() => store.RestoreAsync(root.Path, set.SetId));

            // "notes" was not replicated at the source: alone, it needs no primary.
            await store.RestoreAsync(root.Path, set.SetId, new RestoreOptions { Databases = ["notes"] });
        }

        using (var dir = new TempStoreDirectory())
        using (var store = CrystalStore.Open(dir.Path, Replicating()))
        {
            Assert.True(store.Engine.WriteGate.BeginStepDown());
            await Assert.ThrowsAsync<NotPrimaryException>(() => store.RestoreAsync(root.Path, set.SetId));
            store.Engine.WriteGate.EndStepDown();
            await store.RestoreAsync(root.Path, set.SetId);
        }
    }

    [Fact]
    public async Task ABackup_IsAllowedOnAStandby()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Replicating());
        store.CreateDatabase("db", replicated: true);
        store.Replication.Demote();

        var set = await store.BackupAsync(root.Path);
        Assert.Single(set.Databases);
    }
}
