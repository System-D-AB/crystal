using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Xunit;

namespace SystemD.Crystal.Tests;

/// <summary>
/// Spec 005 R13.7, shown structurally (tasks T23): every registry change is a fixed number of entries written to
/// the registry, whatever the number of databases, and a rename writes nothing to the database it renames.
/// Entries are counted by the registry's RocksDB sequence number, which advances by one per entry written.
/// Timings are the benchmark harness's to report, not a unit test's.
/// </summary>
public sealed class DatabaseCostTests
{
    private const int Chunk = 100;

    [Fact]
    public void LifecycleOperations_At10And100Databases_WriteTheSameRegistryEntries() => AssertFlatUpTo(100);

    /// <summary>
    /// The same at 1 000 databases: a long cost probe (spec 004 R4.1; spec 015 T2), about three minutes of database
    /// creation, so it is left out of CI's everyday run and run by the release gate.
    /// </summary>
    [Fact]
    [Trait("Category", "LongProbe")]
    public void LifecycleOperations_At10And1000Databases_WriteTheSameRegistryEntries() => AssertFlatUpTo(1000);

    private static void AssertFlatUpTo(int databases)
    {
        using var dir = new TempStoreDirectory();

        GrowTo(dir.Path, 10);
        var at10 = MeasureLifecycle(dir.Path, "probe-10");

        GrowTo(dir.Path, databases);
        var atMany = MeasureLifecycle(dir.Path, $"probe-{databases}");

        Assert.Equal(at10, atMany);
        Assert.Equal(new Dictionary<string, ulong>
        {
            ["create"] = 4, // allocator; then alias, record, generation
            ["list"] = 0,
            ["rename"] = 4, // old alias, new alias, record, generation
            ["swap"] = 5,   // two aliases, two records, generation
            ["drop"] = 3,   // alias, record, generation
        }, at10);
    }

    [Fact]
    public void RenameDatabase_HoldingAMillionKeys_WritesNothingToTheDatabase()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var empty = store.CreateDatabase("empty");
        var full = store.CreateDatabase("full");
        var box = full.Boxes.Create("keys");

        var key = new byte[8];
        for (var written = 0; written < 1_000_000;)
        {
            using var batch = full.NewBatch();
            for (var i = 0; i < 50_000; i++, written++)
            {
                BinaryPrimitives.WriteInt64BigEndian(key, written);
                batch.Put(box, key, key);
            }

            batch.Commit(DurabilityMode.Buffered);
        }

        var emptyRename = RegistryEntries(store, () => store.RenameDatabase(empty, "empty-renamed"));

        var fullSequence = full.Slot.LatestSequence;
        var fullRename = RegistryEntries(store, () => store.RenameDatabase(full, "full-renamed"));

        Assert.Equal(emptyRename, fullRename);
        Assert.Equal(fullSequence, full.Slot.LatestSequence);
    }

    private static Dictionary<string, ulong> MeasureLifecycle(string dataDirectory, string name)
    {
        using var store = CrystalStore.Open(dataDirectory, new CrystalOptions { MaxOpenDatabases = Chunk + 10 });
        var costs = new Dictionary<string, ulong>();
        CrystalDatabase? probe = null;
        CrystalDatabase? partner = null;

        costs["create"] = RegistryEntries(store, () => probe = store.CreateDatabase(name));
        partner = store.CreateDatabase(name + "-partner");
        costs["list"] = RegistryEntries(store, () => store.ListDatabases());
        costs["rename"] = RegistryEntries(store, () => store.RenameDatabase(probe!, name + "-renamed"));
        costs["swap"] = RegistryEntries(store, () => store.SwapDatabases(probe!, partner));
        costs["drop"] = RegistryEntries(store, () => store.DropDatabase(probe!));
        store.DropDatabase(partner);
        return costs;
    }

    private static ulong RegistryEntries(CrystalStore store, Action operation)
    {
        var before = store.Engine.RegistrySequence;
        operation();
        return store.Engine.RegistrySequence - before;
    }

    /// <summary>Creates databases until the store holds <paramref name="count"/>, reopening it every chunk.</summary>
    private static void GrowTo(string dataDirectory, int count)
    {
        while (true)
        {
            using var store = CrystalStore.Open(dataDirectory, new CrystalOptions { MaxOpenDatabases = Chunk });
            var existing = store.ListDatabases().Count;
            if (existing >= count)
            {
                Assert.Equal(count, existing);
                return;
            }

            for (var i = existing; i < Math.Min(count, existing + Chunk); i++)
            {
                store.CreateDatabase($"db-{i}");
            }
        }
    }
}
