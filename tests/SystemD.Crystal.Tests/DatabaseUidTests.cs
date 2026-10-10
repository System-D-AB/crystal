// Spec 003 T1 / R1.3 — every database has a stable UID, assigned at create, back-filled onto version 1 records on
// load in one synced registry batch, and exposed on the handle and DatabaseInfo.

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Text.Json;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseUidTests
{
    // The registry's record key: box 1 (the kernel registry box), tag 0x02, id big-endian, in the "general" family.
    private const uint RegistryBoxId = 1;
    private const byte TagRecord = 0x02;

    private static byte[] RecordKey(uint id)
    {
        Span<byte> suffix = stackalloc byte[5];
        suffix[0] = TagRecord;
        BinaryPrimitives.WriteUInt32BigEndian(suffix[1..], id);
        return BoxKeys.BuildKey(RegistryBoxId, suffix);
    }

    [Fact]
    public void CreatedDatabase_HasAUid_OnHandleAndInfo()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("shop");

        Assert.False(string.IsNullOrEmpty(db.Uid));
        Assert.True(Guid.TryParse(db.Uid, out _), "a UID is a canonical GUID string");

        var info = store.ListDatabases().Single(d => d.Id == db.Id);
        Assert.Equal(db.Uid, info.Uid);
    }

    [Fact]
    public void Uid_IsStable_AcrossReopen_Rename_AndSwap()
    {
        using var dir = new TempStoreDirectory();

        string shopUid, usersUid;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var shop = store.CreateDatabase("shop");
            var users = store.CreateDatabase("users");
            shopUid = shop.Uid;
            usersUid = users.Uid;

            // Rename does not change the UID.
            store.RenameDatabase(shop, "shop2");
            Assert.Equal(shopUid, store.ListDatabases().Single(d => d.Name == "shop2").Uid);

            // Swap exchanges names, not UIDs: each UID stays with its own id.
            store.SwapDatabases(shop, users); // shop2 <-> users
            var afterSwap = store.ListDatabases();
            Assert.Equal(shopUid, afterSwap.Single(d => d.Id == shop.Id).Uid);
            Assert.Equal(usersUid, afterSwap.Single(d => d.Id == users.Id).Uid);
        }

        // Reopen: the UIDs survive, bound to the same ids.
        using (var store = CrystalStore.Open(dir.Path))
        {
            var reopened = store.ListDatabases();
            Assert.Contains(reopened, d => d.Uid == shopUid);
            Assert.Contains(reopened, d => d.Uid == usersUid);
        }
    }

    [Fact]
    public void UidsAreUnique_PerDatabase()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path);
        var a = store.CreateDatabase("a");
        var b = store.CreateDatabase("b");
        var c = store.CreateDatabase("c");

        var uids = new[] { a.Uid, b.Uid, c.Uid };
        Assert.Equal(3, uids.Distinct().Count());
    }

    [Fact]
    public void Version1Record_IsUpgradedToVersion2_WithAUid_ExactlyOnce()
    {
        using var dir = new TempStoreDirectory();
        uint id;

        // Create a database, then rewrite its registry record as a version 1 record (no uid), behind the store's back.
        using (var store = CrystalStore.Open(dir.Path))
        {
            var db = store.CreateDatabase("legacy");
            id = db.Id;
        }

        var registryPath = System.IO.Path.Combine(dir.Path, "_crystal");
        WriteV1Record(registryPath, id, name: "legacy");

        // Reopen: the record is upgraded to version 2 with a fresh UID.
        string assignedUid;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var info = store.ListDatabases().Single(d => d.Id == id);
            Assert.False(string.IsNullOrEmpty(info.Uid));
            Assert.True(Guid.TryParse(info.Uid, out _));
            assignedUid = info.Uid;
        }

        // The record on disk is now version 2 with that UID, and a second reopen leaves it unchanged (upgraded once).
        var stored = ReadRecord(registryPath, id);
        Assert.Equal(2, stored.GetProperty("v").GetInt32());
        Assert.Equal(assignedUid, stored.GetProperty("uid").GetString());

        using (var store = CrystalStore.Open(dir.Path))
        {
            Assert.Equal(assignedUid, store.ListDatabases().Single(d => d.Id == id).Uid);
        }
    }

    private static void WriteV1Record(string registryPath, uint id, string name)
    {
        var options = new DbOptions();
        var cfs = new ColumnFamilies
        {
            { "general", new ColumnFamilyOptions() },
            { "delete_heavy", new ColumnFamilyOptions() },
        };
        using var rocks = RocksDb.Open(options, registryPath, cfs);

        // A version 1 record: the shape before spec 003, with no "uid" field.
        var v1 = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            name,
            replicated = true,
            state = "live",
            created = 638000000000000000L,
            dropped = (long?)null,
        });

        rocks.Put(RecordKey(id), v1, rocks.GetColumnFamily("general"));
    }

    private static JsonElement ReadRecord(string registryPath, uint id)
    {
        var options = new DbOptions();
        var cfs = new ColumnFamilies
        {
            { "general", new ColumnFamilyOptions() },
            { "delete_heavy", new ColumnFamilyOptions() },
        };
        using var rocks = RocksDb.Open(options, registryPath, cfs);
        var bytes = rocks.Get(RecordKey(id), rocks.GetColumnFamily("general"));
        Assert.NotNull(bytes);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }
}
