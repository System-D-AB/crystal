// Spec 009 — self-describing databases: every database carries its identity record (UID, kind, creation time) in its
// own box 0, the registry keeps copies, and opening a database checks the two agree.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseIdentityTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    // ------------------------------------------------------------------ the record (R1.1, R1.2)

    [Fact]
    public void Identity_RoundTrips()
    {
        var identity = new DatabaseIdentity(Guid.NewGuid().ToString("D"), DatabaseKind.Registry, 638_000_000_000_000_000);

        Assert.Equal(identity, DatabaseIdentity.Deserialize(DatabaseIdentity.Serialize(identity)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"database\",\"uid\":\"6f1c2a52-6f0e-4d36-9b3c-1a2b3c4d5e6f\",\"created\":1}")]
    [InlineData("{\"v\":1,\"kind\":\"index\",\"uid\":\"6f1c2a52-6f0e-4d36-9b3c-1a2b3c4d5e6f\",\"created\":1}")]
    [InlineData("{\"v\":1,\"kind\":\"database\",\"uid\":\"not-a-guid\",\"created\":1}")]
    [InlineData("{\"v\":1,\"kind\":\"database\",\"uid\":\"6f1c2a52-6f0e-4d36-9b3c-1a2b3c4d5e6f\"}")]
    public void Identity_Damaged_IsRefused(string json)
    {
        Assert.Throws<FormatException>(() => DatabaseIdentity.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Identity_UidNotInCanonicalForm_IsDamage()
    {
        var json = "{\"v\":1,\"kind\":\"database\",\"uid\":\"6F1C2A52-6F0E-4D36-9B3C-1A2B3C4D5E6F\",\"created\":1}";

        Assert.Throws<FormatException>(() => DatabaseIdentity.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Identity_UnknownVersion_IsRefusedAsUnsupported()
    {
        var json = "{\"v\":2,\"kind\":\"database\",\"uid\":\"6f1c2a52-6f0e-4d36-9b3c-1a2b3c4d5e6f\",\"created\":1}";

        Assert.Throws<NotSupportedException>(() => DatabaseIdentity.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    // ------------------------------------------------------------------ written at create, never changed (R1.1, R2.1)

    [Fact]
    public void NewDatabase_HasAnIdentity_MatchingItsRegistryRecord()
    {
        DatabaseInfo info;
        using (var store = CrystalStore.Open(_dir.Path))
        {
            var db = store.CreateDatabase("shop");
            info = store.ListDatabases().Single(d => d.Id == db.Id);
        }

        var identity = ReadIdentity(_dir.DatabaseFolder(info.Id));
        Assert.Equal(info.Uid, identity.Uid);
        Assert.Equal(DatabaseKind.Database, identity.Kind);
        Assert.Equal(info.Created.UtcTicks, identity.CreatedTicks);
    }

    [Fact]
    public void Identity_SurvivesReopenRenameAndSwap_Unchanged()
    {
        uint shopId, usersId;
        DatabaseIdentity shopBefore, usersBefore;
        using (var store = CrystalStore.Open(_dir.Path))
        {
            shopId = store.CreateDatabase("shop").Id;
            usersId = store.CreateDatabase("users").Id;
        }

        shopBefore = ReadIdentity(_dir.DatabaseFolder(shopId));
        usersBefore = ReadIdentity(_dir.DatabaseFolder(usersId));

        using (var store = CrystalStore.Open(_dir.Path))
        {
            var shop = store.OpenDatabase("shop");
            var users = store.OpenDatabase("users");
            store.RenameDatabase(shop, "shop2");
            store.SwapDatabases(shop, users);
            shop.Boxes.Create("orders");
        }

        Assert.Equal(shopBefore, ReadIdentity(_dir.DatabaseFolder(shopId)));
        Assert.Equal(usersBefore, ReadIdentity(_dir.DatabaseFolder(usersId)));

        using (var store = CrystalStore.Open(_dir.Path))
        {
            Assert.Equal(shopBefore.Uid, store.OpenDatabase("users").Uid);
            Assert.Equal(usersBefore.Uid, store.OpenDatabase("shop2").Uid);
        }
    }

    [Fact]
    public void Registry_HasAnIdentityOfKindRegistry()
    {
        using (CrystalStore.Open(_dir.Path))
        {
        }

        var identity = ReadIdentity(RegistryFolder);
        Assert.Equal(DatabaseKind.Registry, identity.Kind);
        Assert.True(Guid.TryParseExact(identity.Uid, "D", out _));
    }

    // ------------------------------------------------------------------ refusals on open (R1.2, R1.3, R2.3, R3.1)

    [Fact]
    public void Open_MissingIdentity_IsRefused()
    {
        var id = CreateShop();
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Remove(BoxKeys.IdentityKey));

        Assert.Contains("identity", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_DamagedIdentity_IsRefused()
    {
        var id = CreateShop();
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Put(BoxKeys.IdentityKey, "not json"u8.ToArray()));

        Assert.Contains("identity", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_IdentityOfAnUnknownVersion_IsRefused_NamingTheVersion()
    {
        var id = CreateShop();
        var uid = ReadIdentity(_dir.DatabaseFolder(id)).Uid;
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Put(
            BoxKeys.IdentityKey,
            System.Text.Encoding.UTF8.GetBytes($"{{\"v\":2,\"kind\":\"database\",\"uid\":\"{uid}\",\"created\":1}}")));

        Assert.Equal("identity 2", OpenShopRefused().FoundFormat);
    }

    [Fact]
    public void Open_AnotherDatabasesFolder_IsRefused_NamingBothUids()
    {
        var id = CreateShop();
        var expected = ReadIdentity(_dir.DatabaseFolder(id));
        var stranger = expected with { Uid = Guid.NewGuid().ToString("D") };
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(stranger)));

        var refused = OpenShopRefused();
        Assert.Contains(expected.Uid, refused.Message, StringComparison.Ordinal);
        Assert.Contains(stranger.Uid, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ARegistryWhereADatabaseBelongs_IsRefused()
    {
        var id = CreateShop();
        var registry = ReadIdentity(_dir.DatabaseFolder(id)) with { Kind = DatabaseKind.Registry };
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(registry)));

        Assert.Contains("registry", OpenShopRefused().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ADatabaseWhereTheRegistryBelongs_IsRefused()
    {
        CreateShop();
        var database = ReadIdentity(RegistryFolder) with { Kind = DatabaseKind.Database };
        Tamper(RegistryFolder, rocks => rocks.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(database)));

        var refused = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(_dir.Path).Dispose());
        Assert.Contains("registry", refused.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(refused.WayForward));
    }

    [Fact]
    public void Open_ARegistryWithoutAnIdentity_IsRefused_NamingReset()
    {
        CreateShop();
        Tamper(RegistryFolder, rocks => rocks.Remove(BoxKeys.IdentityKey));

        var refused = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(_dir.Path).Dispose());
        Assert.Contains("identity", refused.Message, StringComparison.Ordinal);
        Assert.Contains("CrystalStore.Reset", refused.WayForward, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the database is the source (R2.3)

    [Fact]
    public void Open_CreationTimeDiffering_CorrectsTheRegistry_WithoutMovingTheGeneration_Once()
    {
        var id = CreateShop();
        var identity = ReadIdentity(_dir.DatabaseFolder(id)) with { CreatedTicks = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks };
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(identity)));
        var generationBefore = ReadRegistryGeneration();

        using (var store = CrystalStore.Open(_dir.Path))
        {
            store.OpenDatabase("shop");
            Assert.Equal(identity.CreatedTicks, store.ListDatabases().Single().Created.UtcTicks);
        }

        Assert.Equal(generationBefore, ReadRegistryGeneration());
        var recordAfterFirst = ReadRegistryRecord(id);

        using (var store = CrystalStore.Open(_dir.Path))
        {
            Assert.Equal(identity.CreatedTicks, store.ListDatabases().Single().Created.UtcTicks);
            store.OpenDatabase("shop");
        }

        Assert.Equal(recordAfterFirst, ReadRegistryRecord(id));
        Assert.Equal(generationBefore, ReadRegistryGeneration());
    }

    // ------------------------------------------------------------------ a snapshot of the wrong database (R5.1)

    [Fact]
    public void InstallBootstrap_ASnapshotOfAnotherDatabase_IsRefused_AndTheDatabaseIsLeftAsItWas()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var alpha = store.CreateDatabase("alpha");
        var alphaBox = alpha.Boxes.Create("box");
        using (var batch = alpha.NewBatch())
        {
            batch.Put(alphaBox, "k"u8, "alpha's"u8);
            batch.Commit();
        }

        var beta = store.CreateDatabase("beta");
        var snapshotOfBeta = Path.Combine(_dir.Path, "db-beta.bootstrap-test");
        new WalAccess(beta.Slot.Instance).Checkpoint(snapshotOfBeta);

        var refused = Assert.Throws<CrystalFormatException>(
            () => store.Engine.InstallBootstrap(store, alpha.Uid, "alpha", snapshotOfBeta));
        Assert.Contains(beta.Uid, refused.Message, StringComparison.Ordinal);

        // Nothing was closed or moved: the same handle still reads alpha's data.
        using var snapshot = alpha.Snapshot();
        Assert.Equal("alpha's"u8.ToArray(), snapshot.Get(alphaBox, "k"u8));
        Assert.Equal(alpha.Uid, ReadIdentityWhileOpen(alpha));
    }

    private static string ReadIdentityWhileOpen(CrystalDatabase db) =>
        DatabaseIdentity.Deserialize(db.Slot.Instance.Get("default", BoxKeys.IdentityKey)!).Uid;

    [Fact]
    public void Open_CreationTimeDiffering_WithTheRegistryFailed_StillOpens_AndCorrectsNothing()
    {
        var id = CreateShop();
        var identity = ReadIdentity(_dir.DatabaseFolder(id)) with { CreatedTicks = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks };
        Tamper(_dir.DatabaseFolder(id), rocks => rocks.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(identity)));
        var recordBefore = ReadRegistryRecord(id);

        var store = CrystalStore.Open(_dir.Path);
        var failed = typeof(StoreEngine).GetField("_registryFailed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        failed.SetValue(store.Engine, new DatabaseFailedException("registry", "failed by the test"));

        var shop = store.OpenDatabase("shop");
        Assert.Equal("shop", shop.Name);

        // Closing a store whose registry failed reports it (006 R5); the instances are still closed.
        var closing = Assert.Throws<AggregateException>(store.Dispose);
        Assert.Contains(closing.InnerExceptions, e => e.Message == "failed by the test");

        Assert.Equal(recordBefore, ReadRegistryRecord(id));
    }

    [Fact]
    public void InstallBootstrap_ANewDatabase_IsRegisteredWithTheSnapshotsUidAndCreationTime()
    {
        using var primaryDir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(primaryDir.Path);
        var alpha = primary.CreateDatabase("alpha");
        var created = primary.ListDatabases().Single().Created;
        var snapshotDir = Path.Combine(_dir.Path, "db-alpha.bootstrap-test");

        using var standby = CrystalStore.Open(_dir.Path);
        new WalAccess(alpha.Slot.Instance).Checkpoint(snapshotDir);
        var installed = standby.Engine.InstallBootstrap(standby, alpha.Uid, "alpha", snapshotDir);

        Assert.Equal(alpha.Uid, installed.Uid);
        var info = standby.ListDatabases().Single();
        Assert.Equal(created, info.Created);
        Assert.Equal(alpha.Uid, info.Uid);
    }

    // ------------------------------------------------------------------ a database moves between stores whole (R1.4, R6.2)

    [Fact]
    public void ADatabase_PlacedUnderAnotherIdInAnotherStore_OpensWithItsUidAndData()
    {
        using var source = new TempStoreDirectory();
        uint sourceId;
        string uid;
        long created;
        using (var store = CrystalStore.Open(source.Path))
        {
            var shop = store.CreateDatabase("shop", replicated: false);
            var orders = shop.Boxes.Create("orders");
            var stock = shop.Boxes.Create("stock", BoxProfile.DeleteHeavy);
            using (var batch = shop.NewBatch())
            {
                batch.Put(orders, "o-1"u8, "first"u8);
                batch.Put(orders, "o-2"u8, "second"u8);
                batch.Put(stock, "sku-9"u8, "4"u8);
                batch.Commit();
            }

            sourceId = shop.Id;
            uid = shop.Uid;
            created = store.ListDatabases().Single().Created.UtcTicks;
        }

        // The target already holds two databases, so the moved one takes an id the source never gave it.
        using (var store = CrystalStore.Open(_dir.Path))
        {
            store.CreateDatabase("a");
            store.CreateDatabase("b");
        }

        var targetId = sourceId + 2;
        CopyDirectory(source.DatabaseFolder(sourceId), _dir.DatabaseFolder(targetId));
        Tamper(RegistryFolder, rocks =>
        {
            var family = rocks.GetColumnFamily("general");
            var record = new DatabaseRecord
            {
                Version = DatabaseRecord.CurrentVersion,
                Name = "moved",
                Uid = uid,
                Replicated = false,
                State = "live",
                CreatedTicks = created,
            };
            var idBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(idBytes, targetId);
            rocks.Put(StoreEngine.AliasKey("moved"), idBytes, family);
            rocks.Put(StoreEngine.RecordKey(targetId), DatabaseRecord.Serialize(record), family);
            rocks.Put(RegistryKey(0x03), UInt64(targetId + 1UL), family);
            rocks.Put(RegistryKey(0x04), UInt64(BinaryPrimitives.ReadUInt64BigEndian(rocks.Get(RegistryKey(0x04), family)) + 1), family);
        });

        using (var store = CrystalStore.Open(_dir.Path))
        {
            var moved = store.OpenDatabase("moved");
            Assert.Equal(targetId, moved.Id);
            Assert.NotEqual(sourceId, moved.Id);
            Assert.Equal(uid, moved.Uid);

            var orders = moved.Boxes.Find("orders")!.Value;
            var stock = moved.Boxes.Find("stock")!.Value;
            using var snapshot = moved.Snapshot();
            Assert.Equal("first"u8.ToArray(), snapshot.Get(orders, "o-1"u8));
            Assert.Equal("second"u8.ToArray(), snapshot.Get(orders, "o-2"u8));
            Assert.Equal("4"u8.ToArray(), snapshot.Get(stock, "sku-9"u8));
        }
    }

    // ------------------------------------------------------------------ helpers

    private string RegistryFolder => Path.Combine(_dir.Path, StoreEngine.RegistryFolderName);

    private uint CreateShop()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var db = store.CreateDatabase("shop");
        db.Boxes.Create("orders");
        return db.Id;
    }

    private CrystalFormatException OpenShopRefused()
    {
        using var store = CrystalStore.Open(_dir.Path);
        var refused = Assert.Throws<CrystalFormatException>(() => store.OpenDatabase("shop"));
        Assert.False(string.IsNullOrWhiteSpace(refused.WayForward));
        return refused;
    }

    private static ColumnFamilies Families() =>
        new() { { "general", new ColumnFamilyOptions() }, { "delete_heavy", new ColumnFamilyOptions() } };

    private static void Tamper(string folder, Action<RocksDb> change)
    {
        using var rocks = RocksDb.Open(new DbOptions(), folder, Families());
        change(rocks);
    }

    private static DatabaseIdentity ReadIdentity(string folder)
    {
        using var rocks = RocksDb.OpenReadOnly(new DbOptions(), folder, Families(), errIfLogFileExists: false);
        return DatabaseIdentity.Deserialize(rocks.Get(BoxKeys.IdentityKey) ?? throw new InvalidOperationException("no identity record"));
    }

    private ulong ReadRegistryGeneration()
    {
        using var rocks = RocksDb.OpenReadOnly(new DbOptions(), RegistryFolder, Families(), errIfLogFileExists: false);
        return BinaryPrimitives.ReadUInt64BigEndian(rocks.Get(RegistryKey(0x04), rocks.GetColumnFamily("general")));
    }

    private byte[] ReadRegistryRecord(uint id)
    {
        using var rocks = RocksDb.OpenReadOnly(new DbOptions(), RegistryFolder, Families(), errIfLogFileExists: false);
        return rocks.Get(StoreEngine.RecordKey(id), rocks.GetColumnFamily("general"));
    }

    private static byte[] RegistryKey(byte tag) => BoxKeys.BuildKey(1, [tag]);

    private static byte[] UInt64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            if (!string.Equals(Path.GetFileName(file), "LOCK", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
        }
    }
}
