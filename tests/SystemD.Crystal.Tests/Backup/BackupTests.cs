// Spec 010 T4 — a backup (R1, R3, R4; design § 4, § 5): every live database, open or closed, each captured into its
// own engine and listed in a manifest written last; incremental; cancelled promptly; a drop during a copy stops it and
// is recorded as omitted; a store close stops it; one backup at a time; closed databases closed again after.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Backup;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Puts <paramref name="count"/> keys, each with its own random value: incompressible, so a rate-limited copy of N bytes takes N / rate.</summary>
    internal static void Fill(CrystalDatabase db, Box box, int count, int valueBytes, string prefix = "k")
    {
        for (var i = 0; i < count; i += 100)
        {
            using var batch = db.NewBatch();
            for (var j = i; j < Math.Min(count, i + 100); j++)
            {
                var value = new byte[valueBytes];
                Random.Shared.NextBytes(value);
                batch.Put(box, Text.UTF8.GetBytes($"{prefix}{j:D6}"), value);
            }

            batch.Commit(DurabilityMode.Buffered);
        }
    }

    /// <summary>Every row of every family of an instance, as hex key = hex value.</summary>
    internal static List<string> Rows(RocksInstance instance)
    {
        var rows = new List<string>();
        foreach (var family in RocksInstance.FamilyOrder)
        {
            using var iterator = instance.Db.NewIterator(instance.Family(family));
            for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next())
            {
                rows.Add(family + ":" + Convert.ToHexString(iterator.Key()) + "=" + Convert.ToHexString(iterator.Value()));
            }
        }

        return rows;
    }

    /// <summary>The rows of one database's backup in the set, restored into a scratch folder and opened read-only.</summary>
    internal static List<string> BackedUpRows(CrystalStore store, string root, BackupDatabaseInfo database, string setId)
    {
        var manifest = BackupManifest.Read(Path.Combine(root, "sets", setId + ".json"));
        var entry = manifest.Databases.Single(d => d.Uid == database.Uid);
        var scratch = Path.Combine(root, "test-scratch-" + Guid.NewGuid().ToString("N"));
        using (var engine = BackupEngineHandle.Open(Path.Combine(root, "engines", database.Uid)))
        {
            engine.Restore(entry.Backup, scratch);
        }

        try
        {
            using var copy = RocksInstance.OpenReadOnly(scratch, store.Engine.Resources, new Dictionary<string, BoxProfile>());
            return Rows(copy);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task EveryLiveDatabase_OpenOrClosed_IsBackedUp_AndClosedOnesAreClosedAgain()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        Dictionary<string, List<string>> expected = [];
        using (var store = CrystalStore.Open(dir.Path))
        {
            foreach (var name in new[] { "alpha", "beta", "gamma" })
            {
                var db = store.CreateDatabase(name, replicated: name != "beta");
                Fill(db, db.Boxes.Create("general"), 300, 64);
                Fill(db, db.Boxes.Create("queue", BoxProfile.DeleteHeavy), 200, 32);
            }

            store.RenameDatabase(store.OpenDatabase("gamma"), "delta");
            store.SwapDatabases(store.OpenDatabase("alpha"), store.OpenDatabase("beta"));
        }

        using (var store = CrystalStore.Open(dir.Path))
        {
            var held = store.OpenDatabase("alpha"); // one open, two closed
            var set = await store.BackupAsync(root.Path, new BackupOptions { Label = "nightly" });

            Assert.Equal("nightly", set.Label);
            Assert.Equal(store.NodeId, set.SourceNodeId);
            Assert.Empty(set.Omitted);
            Assert.Equal(["alpha", "beta", "delta"], set.Databases.Select(d => d.Name).Order());
            Assert.True(File.Exists(Path.Combine(root.Path, "sets", set.SetId + ".json")));
            Assert.Equal(
                store.ListDatabases().Select(d => (d.Name, d.Uid, d.Replicated, d.Created)).Order(),
                set.Databases.Select(d => (d.Name, d.Uid, d.Replicated, d.Created)).Order());
            Assert.All(set.Databases, d => Assert.True(d.Bytes > 0 && d.Files > 0 && d.Sequence > 0));

            // The backup opened the closed databases and closed them again; the product's stays open.
            Assert.Equal(["alpha"], store.Status().Databases.Select(d => d.Name));

            foreach (var database in set.Databases)
            {
                var db = store.OpenDatabase(database.Name);
                Assert.Equal(Rows(db.Slot.Instance), BackedUpRows(store, root.Path, database, set.SetId));
                Assert.Equal(database.Uid, db.Uid);
            }

            GC.KeepAlive(held);
        }
    }

    [Fact]
    public async Task ASecondBackup_CopiesOnlyNewFiles_AndBothSetsHoldTheirData()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        Fill(db, box, 5_000, 512);

        var first = await store.BackupAsync(root.Path);
        var firstRows = Rows(db.Slot.Instance);
        var afterFirst = EngineBytes(root.Path, db.Uid);

        Fill(db, box, 10, 512, prefix: "new");
        var second = await store.BackupAsync(root.Path);
        var copied = EngineBytes(root.Path, db.Uid) - afterFirst;

        Assert.True(copied < afterFirst / 4, $"the second backup copied {copied} bytes of {afterFirst}");
        Assert.True(second.Databases.Single().Sequence > first.Databases.Single().Sequence);
        Assert.Equal(firstRows, BackedUpRows(store, root.Path, first.Databases.Single(), first.SetId));
        Assert.Equal(Rows(db.Slot.Instance), BackedUpRows(store, root.Path, second.Databases.Single(), second.SetId));
    }

    [Fact]
    public async Task EveryCommitAcknowledgedBeforeTheCopy_IsInTheBackup_AtOrBelowItsSequence()
    {
        // Buffered commits, never synced: the flush before the copy takes them (R1.3, R1.5).
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Durability = DurabilityMode.Buffered });
        var db = store.CreateDatabase("db");
        Fill(db, db.Boxes.Create("box"), 100, 16);
        var sequence = (long)db.Slot.LatestSequence;

        var set = await store.BackupAsync(root.Path);

        Assert.Equal(sequence, set.Databases.Single().Sequence);
        Assert.Equal(Rows(db.Slot.Instance), BackedUpRows(store, root.Path, set.Databases.Single(), set.SetId));
    }

    [Fact]
    public async Task ASecondConcurrentBackup_IsRefused_NamingTheRunningOnesDestination()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var other = new TempStoreDirectory();
        using var holding = new Hold("backup.copying");
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Hook = holding.Hook });
        store.CreateDatabase("db");

        var running = store.BackupAsync(root.Path);
        holding.WaitReached();
        var refusal = Assert.Throws<BackupInProgressException>(() => { _ = store.BackupAsync(other.Path); }); // refused synchronously
        Assert.Equal(root.Path, refusal.RunningDestination);

        holding.Release();
        await running;
        await store.BackupAsync(other.Path); // the flag is cleared after
    }

    [Fact]
    public async Task ACancelledBackup_StopsItsCopyPromptly_AndWritesNoSet_LeavingEarlierSetsIntact()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        Fill(db, db.Boxes.Create("box"), 20_000, 1024); // ~20 MB, copied at 1 MB/s: about 20 s uncancelled
        var earlier = await store.BackupAsync(root.Path, new BackupOptions { Label = "earlier" });
        Fill(db, db.Boxes.Create("more"), 20_000, 1024);

        using var cancel = new CancellationTokenSource();
        var running = store.BackupAsync(root.Path, new BackupOptions { RateLimitBytesPerSecond = 1 << 20 }, cancel.Token);
        await Task.Delay(500);
        var watch = Stopwatch.StartNew();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the copy took {watch.Elapsed} to stop");
        Assert.Equal([earlier.SetId + ".json"], Directory.EnumerateFiles(Path.Combine(root.Path, "sets")).Select(Path.GetFileName));
        Assert.NotEmpty(BackedUpRows(store, root.Path, earlier.Databases.Single(), earlier.SetId));

        var next = await store.BackupAsync(root.Path);
        Assert.Equal(Rows(db.Slot.Instance), BackedUpRows(store, root.Path, next.Databases.Single(), next.SetId));
    }

    [Fact]
    public async Task ADropDuringACopy_GoesAheadWithoutWaitingForIt_AndTheDatabaseIsOmitted()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var keep = store.CreateDatabase("keep");
        Fill(keep, keep.Boxes.Create("box"), 100, 16);
        var big = store.CreateDatabase("big"); // id after "keep": copied second
        Fill(big, big.Boxes.Create("box"), 20_000, 1024);
        var bigUid = big.Uid;

        var running = store.BackupAsync(root.Path, new BackupOptions { RateLimitBytesPerSecond = 1 << 20 });
        await WaitUntil(() => Directory.Exists(Path.Combine(root.Path, "engines", bigUid)));
        await Task.Delay(500); // into the copy

        var watch = Stopwatch.StartNew();
        store.DropDatabase(big);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the drop waited {watch.Elapsed}");

        var set = await running.WaitAsync(Bound);
        Assert.Equal(["keep"], set.Databases.Select(d => d.Name));
        var omitted = Assert.Single(set.Omitted);
        Assert.Equal(("big", bigUid, "dropped"), (omitted.Name, omitted.Uid, omitted.Reason));
    }

    [Fact]
    public async Task ADropWhileTheCopyIsHeldAtTheHook_StopsItBeforeItStarts()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var holding = new Hold("backup.copying");
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Hook = holding.Hook });
        var db = store.CreateDatabase("db");
        Fill(db, db.Boxes.Create("box"), 100, 16);

        var running = store.BackupAsync(root.Path);
        holding.WaitReached();
        var dropping = Task.Run(() => store.DropDatabase(db));
        await WaitUntil(() => db.Slot.IsClosing);
        holding.Release();

        await dropping.WaitAsync(Bound);
        var set = await running.WaitAsync(Bound);
        Assert.Empty(set.Databases);
        Assert.Equal("dropped", Assert.Single(set.Omitted).Reason);
    }

    [Fact]
    public async Task ADatabaseDroppedBeforeItsTurn_IsOmitted()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var holding = new Hold("backup.copying");
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Hook = holding.Hook });
        store.CreateDatabase("first");
        var second = store.CreateDatabase("second");

        var running = store.BackupAsync(root.Path);
        holding.WaitReached(); // copying "first"
        store.DropDatabase(second);
        holding.Release();

        var set = await running.WaitAsync(Bound);
        Assert.Equal(["first"], set.Databases.Select(d => d.Name));
        Assert.Equal("second", Assert.Single(set.Omitted).Name);
    }

    [Fact]
    public async Task ClosingTheStoreDuringABackup_StopsIt_AndNoSetIsWritten()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        Fill(db, db.Boxes.Create("box"), 20_000, 1024);

        var running = store.BackupAsync(root.Path, new BackupOptions { RateLimitBytesPerSecond = 1 << 20 });
        await Task.Delay(500);
        var watch = Stopwatch.StartNew();
        store.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the close waited {watch.Elapsed}");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => running);
        var sets = Path.Combine(root.Path, "sets");
        Assert.False(Directory.Exists(sets) && Directory.EnumerateFiles(sets, "*.json").Any());
    }

    [Fact]
    public async Task ABackupOnAStoreWithNoRoomToOpenAClosedDatabase_FailsWithTheLimit()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("a");
            store.CreateDatabase("b");
        }

        using (var store = CrystalStore.Open(dir.Path, new CrystalOptions { MaxOpenDatabases = 1 }))
        {
            store.OpenDatabase("a");
            await Assert.ThrowsAsync<DatabaseLimitException>(() => store.BackupAsync(root.Path));
        }
    }

    [Fact]
    public async Task ADestinationInsideTheDataDirectory_IsRefusedBeforeAnythingRuns()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        Assert.Throws<BackupDestinationException>(() => { _ = store.BackupAsync(Path.Combine(dir.Path, "backups")); });
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "backups")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EachEngineBackup_CarriesItsSetAndUid()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var set = await store.BackupAsync(root.Path);

        using var engine = BackupEngineHandle.Open(Path.Combine(root.Path, "engines", db.Uid));
        Assert.Equal(
            $"{{\"set\":\"{set.SetId}\",\"uid\":\"{db.Uid}\"}}",
            Text.UTF8.GetString(Assert.Single(engine.List()).Metadata));
    }

    private static long EngineBytes(string root, string uid) =>
        new DirectoryInfo(Path.Combine(root, "engines", uid)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Bound, "timed out waiting");
            await Task.Delay(10);
        }
    }

    /// <summary>Holds the first caller to reach a hook step until released.</summary>
    internal sealed class Hold(string step) : IDisposable
    {
        private readonly ManualResetEventSlim _reached = new();
        private readonly ManualResetEventSlim _release = new();
        private int _taken;

        public void Hook(string at)
        {
            if (at == step && Interlocked.Exchange(ref _taken, 1) == 0)
            {
                _reached.Set();
                _release.Wait(Bound);
            }
        }

        public void WaitReached() => Assert.True(_reached.Wait(Bound), $"'{step}' was not reached");

        public void Release() => _release.Set();

        public void Dispose() => _release.Set();
    }
}
