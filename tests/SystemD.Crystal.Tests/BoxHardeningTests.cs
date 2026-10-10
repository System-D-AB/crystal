// Spec 002, tasks T15–T18: the defects the independent review of 2026-10-09 found, each pinned by a test that fails
// with the guard removed (the mutations are recorded in tasks.md).

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using Text = System.Text.Encoding;
using RocksDbSharp;
using SystemD.Crystal.Encoding;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class BoxHardeningTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    // ------------------------------------------------------------------ handles belong to one database (finding 1)

    [Fact]
    public void AHandleFromAnotherDatabase_IsRefused_AndWritesNothing()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var shop = store.CreateDatabase("shop");
        var users = store.CreateDatabase("users");
        var orders = shop.Boxes.Create("orders");
        var accounts = users.Boxes.Create("accounts");
        Assert.Equal(orders.Id, accounts.Id);   // ids repeat across databases: the handle must carry more than the id

        using (var batch = users.NewBatch())
        {
            Assert.Throws<BoxNotInDatabaseException>(() => batch.Put(orders, "k"u8, "meant for shop"u8));
            Assert.Throws<BoxNotInDatabaseException>(() => batch.Delete(orders, "k"u8));
            Assert.Throws<BoxNotInDatabaseException>(() => batch.DeleteRange(orders, [], []));
            Assert.Throws<BoxNotInDatabaseException>(() => batch.Get(orders, "k"u8));
            Assert.Throws<BoxNotInDatabaseException>(() => batch.Scan(orders, [], [], 10));
            batch.Commit();
        }

        using (var snapshot = users.Snapshot())
        {
            Assert.Throws<BoxNotInDatabaseException>(() => snapshot.Get(orders, "k"u8));
            Assert.Throws<BoxNotInDatabaseException>(() => snapshot.Scan(orders, [], [], 10));
            Assert.Null(snapshot.Get(accounts, "k"u8));
        }

        Assert.Throws<BoxNotInDatabaseException>(() => users.Boxes.Drop(orders));
        Assert.Throws<BoxNotInDatabaseException>(() => users.Boxes.Rename(orders, "x"));
        Assert.Throws<BoxNotInDatabaseException>(() => users.Boxes.Swap(orders, accounts));
        Assert.Equal(["accounts"], users.Boxes.List().Select(b => b.Name));
    }

    [Fact]
    public void AHandBuiltHandle_IsRefused()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var orders = db.Boxes.Create("orders");

        using var batch = db.NewBatch();
        var refused = Assert.Throws<BoxNotInDatabaseException>(() => batch.Put(new Box(orders.Id, "orders"), "k"u8, "v"u8));
        Assert.Equal(orders.Id, refused.BoxId);
    }

    [Fact]
    public void AHandleFromBeforeTheStoreWasReopened_IsRefused()
    {
        Box orders;
        using (var store = CrystalStore.Open(_dir.Path))
        {
            orders = store.CreateDatabase("shop").Boxes.Create("orders");
        }

        using var reopened = CrystalStore.Open(_dir.Path);
        var db = reopened.OpenDatabase("shop");
        using var batch = db.NewBatch();
        Assert.Throws<BoxNotInDatabaseException>(() => batch.Put(orders, "k"u8, "v"u8));
        batch.Put(db.Boxes.Find("orders")!.Value, "k"u8, "v"u8);
        batch.Commit();
    }

    // ------------------------------------------------------------------ a drop deletes the range (finding 2)

    [Fact]
    public void Drop_DeletesTheBoxsKeysOnDisk_ForBothProfiles()
    {
        uint databaseId, generalId, heavyId;
        using (var store = CrystalStore.Open(_dir.Path))
        {
            var db = store.CreateDatabase("shop");
            databaseId = db.Id;
            var general = db.Boxes.Create("general");
            var heavy = db.Boxes.Create("heavy", BoxProfile.DeleteHeavy);
            var neighbour = db.Boxes.Create("neighbour");
            (generalId, heavyId) = (general.Id, heavy.Id);

            using (var batch = db.NewBatch())
            {
                for (var i = 0; i < 100; i++)
                {
                    batch.Put(general, [(byte)i], "g"u8);
                    batch.Put(heavy, [(byte)i], "h"u8);
                    batch.Put(neighbour, [(byte)i], "n"u8);
                }

                batch.Put(general, [0xFF, 0xFF, 0xFF], "last"u8);
                batch.Commit();
            }

            db.Boxes.Drop(general);
            db.Boxes.Drop(heavy);
        }

        Assert.Equal(0, RawCount(databaseId, "general", generalId));
        Assert.Equal(0, RawCount(databaseId, "delete_heavy", heavyId));
        Assert.Equal(100, RawCount(databaseId, "general", heavyId + 1));
    }

    private int RawCount(uint databaseId, string family, uint boxId)
    {
        BoxKeys.GetBoxBounds(boxId, out var lower, out var upper);
        var families = new ColumnFamilies { { "general", new ColumnFamilyOptions() }, { "delete_heavy", new ColumnFamilyOptions() } };
        using var rocks = RocksDb.Open(new DbOptions(), _dir.DatabaseFolder(databaseId), families);
        using var iterator = rocks.NewIterator(rocks.GetColumnFamily(family));
        var count = 0;
        for (iterator.Seek(lower); iterator.Valid() && iterator.Key().AsSpan().SequenceCompareTo(upper) < 0; iterator.Next())
        {
            count++;
        }

        return count;
    }

    // ------------------------------------------------------------------ interleavings forced with the gate hook (finding 12)

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// R10.2: two renames to one name. The first is held inside the exclusive gate, after its "name is free" check;
    /// the second must wait for the gate and then find the name taken. A rename that checked outside the gate would
    /// pass its check while the first is held, and both would claim the name.
    /// </summary>
    [Fact]
    public void TwoRenamesForOneName_TheSecondWaitsForTheGate_AndIsRefused_R102()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var a = db.Boxes.Create("a");
        var b = db.Boxes.Create("b");

        using var firstInside = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondInside = new ManualResetEventSlim();
        var calls = 0;
        db.Slot.Context.OnCatalogueChangeInsideGate = op =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstInside.Set();
                releaseFirst.Wait(Patience);
            }
            else
            {
                secondInside.Set();
            }
        };

        var first = DedicatedThread.Run(() => db.Boxes.Rename(a, "target"));
        Assert.True(firstInside.Wait(Patience));

        Exception? secondError = null;
        var second = DedicatedThread.Run(() =>
        {
            try
            {
                db.Boxes.Rename(b, "target");
            }
            catch (BoxNameTakenException ex)
            {
                secondError = ex;
            }
        });
        Assert.False(secondInside.Wait(Blocked), "The second rename cannot pass its check while the first holds the gate.");

        releaseFirst.Set();
        first.Complete(Patience);
        second.Complete(Patience);
        db.Slot.Context.OnCatalogueChangeInsideGate = null;

        Assert.IsType<BoxNameTakenException>(secondError);
        Assert.False(secondInside.IsSet, "The refused rename never reached its write.");
        Assert.Equal(a.Id, db.Boxes.Find("target")!.Value.Id);
        Assert.Equal(b.Id, db.Boxes.Find("b")!.Value.Id);
    }

    /// <summary>
    /// R10.2: a create racing a drop of the same name. The drop is held inside the gate; the create waits for it, then
    /// finds the name free and gets a new id. The old handle is refused, never redirected to the new box.
    /// </summary>
    [Fact]
    public void ACreateRacingADropOfTheSameName_WaitsForIt_AndGetsANewBox_R102()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var old = db.Boxes.Create("orders");

        using var dropInside = new ManualResetEventSlim();
        using var releaseDrop = new ManualResetEventSlim();
        using var createInside = new ManualResetEventSlim();
        db.Slot.Context.OnCatalogueChangeInsideGate = op =>
        {
            if (op == "drop")
            {
                dropInside.Set();
                releaseDrop.Wait(Patience);
            }
            else if (op == "create")
            {
                createInside.Set();
            }
        };

        var drop = DedicatedThread.Run(() => db.Boxes.Drop(old));
        Assert.True(dropInside.Wait(Patience));

        Box created = default;
        var create = DedicatedThread.Run(() => created = db.Boxes.Create("orders"));
        Assert.False(createInside.Wait(Blocked), "The create waits for the drop to leave the gate.");

        releaseDrop.Set();
        drop.Complete(Patience);
        create.Complete(Patience);
        db.Slot.Context.OnCatalogueChangeInsideGate = null;

        Assert.NotEqual(old.Id, created.Id);
        Assert.Equal(created, db.Boxes.Find("orders"));
        using var batch = db.NewBatch();
        batch.Put(old, "k"u8, "stale"u8);
        Assert.Throws<BoxDroppedException>(() => batch.Commit());
    }

    // ------------------------------------------------------------------ an empty batch writes nothing (finding 3)

    [Fact]
    public void AnEmptyBatch_WritesNothing_R63()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        db.Boxes.Create("orders");
        var folder = _dir.DatabaseFolder(db.Id);
        long WalBytes() => System.IO.Directory.GetFiles(folder, "*.log").Sum(f => new System.IO.FileInfo(f).Length);
        var sequence = db.Slot.LatestSequence;
        var wal = WalBytes();

        using (var batch = db.NewBatch())
        {
            batch.Commit(DurabilityMode.Strict);
        }

        // RocksDB does not advance the sequence for an empty write batch, but it does append it to the WAL.
        Assert.Equal(sequence, db.Slot.LatestSequence);
        Assert.Equal(wal, WalBytes());
    }

    // ------------------------------------------------------------------ the catalogue loads strictly (findings 4, 8, 10)

    private (uint DatabaseId, Box Box) CreateShopWithBoxes(int boxes)
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        Box last = default;
        for (var i = 0; i < boxes; i++)
        {
            last = db.Boxes.Create($"box{i}");
        }

        return (db.Id, last);
    }

    private void Tamper(uint databaseId, Action<RocksDb> change)
    {
        var families = new ColumnFamilies { { "general", new ColumnFamilyOptions() }, { "delete_heavy", new ColumnFamilyOptions() } };
        using var rocks = RocksDb.Open(new DbOptions(), _dir.DatabaseFolder(databaseId), families);
        change(rocks);
    }

    private CrystalFormatException OpenShopRefused()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var refused = Assert.Throws<CrystalFormatException>(() => store.OpenDatabase("shop"));
        Assert.False(string.IsNullOrWhiteSpace(refused.WayForward));
        return refused;
    }

    private static byte[] UInt64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    [Fact]
    public void Open_MissingAllocator_IsRefused_NotDefaulted()
    {
        var (id, _) = CreateShopWithBoxes(3);
        Tamper(id, rocks => rocks.Remove(BoxKeys.AllocatorKey));

        Assert.Contains("allocator", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_AllocatorBehindTheRecords_IsRefused_SoNoIdIsReused()
    {
        var (id, last) = CreateShopWithBoxes(3);
        Tamper(id, rocks => rocks.Put(BoxKeys.AllocatorKey, UInt64(last.Id)));

        Assert.Contains($"box id {last.Id}", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_MissingGenerationOrEncoderVersion_IsRefused()
    {
        var (id, _) = CreateShopWithBoxes(1);
        Tamper(id, rocks => rocks.Remove(BoxKeys.GenerationKey));
        Assert.Contains("generation", OpenShopRefused().Message, StringComparison.Ordinal);

        Tamper(id, rocks =>
        {
            rocks.Put(BoxKeys.GenerationKey, UInt64(2));
            rocks.Remove(BoxKeys.EncoderVersionKey);
        });
        Assert.Contains("encoder", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":1,\"name\":\"box0\",\"profile\":\"General\",\"state\":\"zombie\",\"created\":0,\"dropped\":null}")]
    [InlineData("{\"v\":1,\"name\":\"box0\",\"profile\":\"Fast\",\"state\":\"live\",\"created\":0,\"dropped\":null}")]
    public void Open_UnreadableRecord_IsRefusedTyped(string record)
    {
        var (id, last) = CreateShopWithBoxes(1);
        Tamper(id, rocks => rocks.Put(BoxKeys.MakeRecordKey(last.Id), Text.UTF8.GetBytes(record)));

        OpenShopRefused();
    }

    [Fact]
    public void Open_AliasTableDisagreeingWithTheRecords_IsRefused()
    {
        var (id, last) = CreateShopWithBoxes(2);
        Tamper(id, rocks =>
        {
            var other = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(other, last.Id - 1);
            rocks.Put(BoxKeys.MakeAliasKey(last.Name), other);
        });

        Assert.Contains("alias", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_AnAliasNamingALiveBoxUnderAnotherName_IsRefused()
    {
        var (id, last) = CreateShopWithBoxes(1);
        Tamper(id, rocks =>
        {
            var target = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(target, last.Id);
            rocks.Put(BoxKeys.MakeAliasKey("ghost"), target);
        });

        Assert.Contains("'ghost'", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_MissingAlias_IsRefused_NotRebuiltFromTheRecords()
    {
        var (id, last) = CreateShopWithBoxes(2);
        Tamper(id, rocks => rocks.Remove(BoxKeys.MakeAliasKey(last.Name)));

        Assert.Contains("alias", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_AnUnknownCatalogueSpace_IsRefused()
    {
        var (id, _) = CreateShopWithBoxes(1);
        Tamper(id, rocks => rocks.Put(new byte[] { 0, 0, 0, 0, 0x08 }, "future"u8.ToArray()));

        Assert.Contains("0x08", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_UntamperedCatalogue_LoadsExactly()
    {
        var (id, last) = CreateShopWithBoxes(5);

        using var store = CrystalStore.Open(_dir.Path);
        var db = store.OpenDatabase("shop");
        Assert.Equal(5, db.Boxes.List().Count);
        Assert.Equal(last.Id, db.Boxes.Find(last.Name)!.Value.Id);
        Assert.Equal(last.Id + 1, db.Boxes.Create("next").Id);
        Assert.Equal(id, db.Id);
    }

    // ------------------------------------------------------------------ the allocator ends (finding 5)

    [Fact]
    public void Create_AfterTheLastId_IsRefusedTyped_AndNeverWrapsToTheCatalogue()
    {
        var (id, _) = CreateShopWithBoxes(0);
        Tamper(id, rocks => rocks.Put(BoxKeys.AllocatorKey, UInt64(DatabaseContext.MaxBoxId)));

        using var store = CrystalStore.Open(_dir.Path);
        var db = store.OpenDatabase("shop");
        Assert.Equal(DatabaseContext.MaxBoxId, db.Boxes.Create("last").Id);
        Assert.Throws<InvalidOperationException>(() => db.Boxes.Create("one-too-many"));
        Assert.Equal(["last"], db.Boxes.List().Select(b => b.Name));
    }

    // ------------------------------------------------------------------ names are valid UTF-16 (finding 7)

    [Fact]
    public void Names_WithAnUnpairedSurrogate_AreRefused_SoTwoNamesNeverShareAnAlias()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");

        Assert.Throws<ArgumentException>(() => db.Boxes.Create("q\uD800"));
        db.Boxes.Create("q�");
        Assert.Throws<ArgumentException>(() => KeyEncoding.EncodeString("q\uDC00"));
        Assert.Throws<FormatException>(() => KeyEncoding.DecodeString([(byte)'q', 0xC3, 0x00, 0x01], out _));
    }

    [Fact]
    public void Names_OfOnlyWhitespace_AreAllowed_R36()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");

        Assert.Equal(" ", db.Boxes.Create(" ").Name);
        Assert.Throws<ArgumentException>(() => db.Boxes.Create(""));
    }

    // ------------------------------------------------------------------ range deletes (finding 9)

    [Fact]
    public void DeleteRange_StartAfterEnd_IsRefusedWhenCalled_AndTheBatchStaysUsable()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        using var batch = db.NewBatch();
        Assert.Throws<ArgumentException>(() => batch.DeleteRange(box, "z"u8, "b"u8));
        batch.Put(box, "k"u8, "v"u8);
        batch.Commit();
    }

    [Fact]
    public void DeleteRange_EmptyEnd_DeletesToTheEndOfTheBox_LikeAScan()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");
        var neighbour = db.Boxes.Create("next");

        using (var batch = db.NewBatch())
        {
            batch.Put(box, "a"u8, "keep"u8);
            batch.Put(box, "m"u8, "x"u8);
            batch.Put(box, [0xFF, 0xFF], "x"u8);
            batch.Put(neighbour, "a"u8, "keep"u8);
            batch.Commit();
        }

        using (var batch = db.NewBatch())
        {
            batch.DeleteRange(box, "b"u8, []);
            Assert.Null(batch.Get(box, [0xFF, 0xFF]));     // the overlay agrees with the commit
            Assert.Single(batch.Scan(box, [], [], 10));
            batch.Commit();
        }

        using var snapshot = db.Snapshot();
        Assert.Equal(["a"], snapshot.Scan(box, [], [], 10).Select(kv => Text.ASCII.GetString(kv.Key)));
        Assert.Single(snapshot.Scan(neighbour, [], [], 10));
    }

    [Fact]
    public void Scan_WithLimitZero_ReturnsNothing_ForSnapshotAndBatch()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        var box = db.Boxes.Create("orders");

        using var snapshot = db.Snapshot();
        using var batch = db.NewBatch();
        Assert.Empty(snapshot.Scan(box, [], [], 0));
        Assert.Empty(batch.Scan(box, [], [], 0));
    }
}
