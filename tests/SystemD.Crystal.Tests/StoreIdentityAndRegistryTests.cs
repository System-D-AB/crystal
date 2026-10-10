using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

/// <summary>
/// Spec 005 design addendum A1 (opening order, node identity), A2 (create's allocator), A4 (registry integrity),
/// A6 (ways forward) and A7 (reset) — tasks T14, T15, T17, T19, T20.
/// </summary>
public sealed class StoreIdentityAndRegistryTests
{
    // ---- A1: node identity and opening order

    [Fact]
    public void Open_StampedStoreWithoutNodeId_IsRefusedAndNoIdentityIsCreated()
    {
        using var dir = new TempStoreDirectory();
        CrystalStore.Open(dir.Path).Dispose();
        var nodeIdPath = Path.Combine(dir.Path, "node.id");
        File.Delete(nodeIdPath);

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("node.id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("backup", ex.WayForward, StringComparison.Ordinal);
        Assert.False(File.Exists(nodeIdPath));
    }

    [Fact]
    public void Open_StampedStoreWithUnreadableNodeId_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        CrystalStore.Open(dir.Path).Dispose();
        File.WriteAllText(Path.Combine(dir.Path, "node.id"), "not-a-guid\n");

        Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
    }

    [Fact]
    public void Open_InterruptedCreation_StampsAndKeepsTheNodeId()
    {
        using var dir = new TempStoreDirectory();
        Directory.CreateDirectory(dir.Path);
        var nodeId = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(dir.Path, "node.id"), nodeId + "\n");
        File.WriteAllText(Path.Combine(dir.Path, "crystal.lock"), string.Empty);

        using var store = CrystalStore.Open(dir.Path);
        Assert.Equal(nodeId, store.NodeId);
        Assert.Equal("crystal 1\n", File.ReadAllText(Path.Combine(dir.Path, "crystal.format")));
    }

    [Fact]
    public void Open_RefusedDirectory_IsLeftUnwritten()
    {
        using var dir = new TempStoreDirectory();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "foreign.txt"), "hello");

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.DoesNotContain("Reset", ex.WayForward, StringComparison.Ordinal);
        Assert.Equal(["foreign.txt"], Directory.EnumerateFileSystemEntries(dir.Path).Select(p => Path.GetFileName(p)));
    }

    [Fact]
    public void Open_FutureStamp_NamesABuildThatReadsIt()
    {
        using var dir = new TempStoreDirectory();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "crystal.format"), "crystal 2\n");

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("crystal 2", ex.WayForward, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir.Path, "crystal.lock")));
    }

    [Fact]
    public void Open_ConcurrentOpensOfANewDirectory_AgreeOnOneNodeId()
    {
        for (var round = 0; round < 20; round++)
        {
            using var dir = new TempStoreDirectory();
            using var start = new Barrier(2);
            var stores = new CrystalStore?[2];
            var threads = Enumerable.Range(0, 2).Select(i => DedicatedThread.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    stores[i] = CrystalStore.Open(dir.Path);
                }
                catch (StoreLockedException)
                {
                }
            })).ToList();
            threads.ForEach(t => t.Complete(TimeSpan.FromSeconds(30)));

            var opened = Assert.Single(stores, s => s is not null)!;
            Assert.Equal(opened.NodeId, File.ReadAllText(Path.Combine(dir.Path, "node.id")).Trim());
            opened.Dispose();
        }
    }

    // ---- A2: create advances the allocator first; the catalogue is initialised by the catalogue

    [Fact]
    public void CreateDatabase_NewDatabase_HasAFullyInitialisedCatalogue()
    {
        using var dir = new TempStoreDirectory();
        uint id;
        using (var store = CrystalStore.Open(dir.Path))
        {
            id = store.CreateDatabase("shop").Id;
        }

        using var rocks = OpenRaw(dir.DatabaseFolder(id));
        Assert.NotNull(rocks.Get(BoxKeys.FormatKey));
        Assert.Equal(BoxKeys.FirstProductBoxId, BinaryPrimitives.ReadUInt64BigEndian(rocks.Get(BoxKeys.AllocatorKey)));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64BigEndian(rocks.Get(BoxKeys.GenerationKey)));
    }

    [Fact]
    public void CreateDatabase_FolderAlreadyAtTheNewId_IsRefusedAndTheFolderSurvivesReopen()
    {
        // Second review, finding 1: the refusal must not advance the allocator past the folder, or the next open
        // takes it for an interrupted create and deletes it.
        using var dir = new TempStoreDirectory();
        CrystalStore.Open(dir.Path).Dispose();
        var stranger = dir.DatabaseFolder(16);
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "keep.txt"), "not Crystal's");

        using (var store = CrystalStore.Open(dir.Path))
        {
            var ex = Assert.Throws<IOException>(() => store.CreateDatabase("shop"));
            Assert.Contains("move it out", ex.Message, StringComparison.Ordinal);
            Assert.Throws<IOException>(() => store.CreateDatabase("shop"));
        }

        using (CrystalStore.Open(dir.Path))
        {
        }

        Assert.True(File.Exists(Path.Combine(stranger, "keep.txt")));

        Directory.Move(stranger, Path.Combine(Path.GetTempPath(), "crystal-moved-" + Guid.NewGuid().ToString("N")));
        using var after = CrystalStore.Open(dir.Path);
        Assert.Equal(16u, after.CreateDatabase("shop").Id);
    }

    // ---- A4: registry integrity

    [Fact]
    public void Open_DatabaseFoldersWithoutRegistry_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        Directory.Delete(Path.Combine(dir.Path, StoreEngine.RegistryFolderName), recursive: true);

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("Reset", ex.WayForward, StringComparison.Ordinal);
        Assert.True(Directory.Exists(dir.DatabaseFolder(16)));
    }

    [Fact]
    public void Open_EmptiedRegistryWithDatabaseFolders_IsRefused()
    {
        // Second review, finding 2: an empty _crystal is never given a fresh registry over existing databases.
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        var registry = Path.Combine(dir.Path, StoreEngine.RegistryFolderName);
        Directory.Delete(registry, recursive: true);
        Directory.CreateDirectory(registry);

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("Reset", ex.WayForward, StringComparison.Ordinal);
        Assert.True(Directory.Exists(dir.DatabaseFolder(16)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":1,\"name\":null,\"replicated\":true,\"state\":\"live\",\"created\":0}")]
    [InlineData("{\"name\":\"shop\",\"replicated\":true,\"state\":\"live\",\"created\":0}")]
    [InlineData("{\"v\":1,\"name\":\"_reserved\",\"replicated\":true,\"state\":\"live\",\"created\":0}")]
    public void Open_MalformedRegistryRecord_IsRefusedWithTheWayForward(string record)
    {
        // Second review, finding 3: a damaged record is a typed refusal, never a raw parser exception.
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        using (var rocks = OpenRaw(Path.Combine(dir.Path, StoreEngine.RegistryFolderName)))
        {
            rocks.Put(BoxKeys.BuildKey(1, [0x02, 0x00, 0x00, 0x00, 0x10]), System.Text.Encoding.UTF8.GetBytes(record), rocks.GetColumnFamily("general"));
        }

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("backup", ex.WayForward, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RegistryAliasMissingForLiveDatabase_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        using (var rocks = OpenRaw(Path.Combine(dir.Path, StoreEngine.RegistryFolderName)))
        {
            var general = rocks.GetColumnFamily("general");
            var aliasPrefix = BoxKeys.BuildKey(1, [0x01]);
            using var it = rocks.NewIterator(general);
            for (it.Seek(aliasPrefix); it.Valid() && it.Key().AsSpan().StartsWith(aliasPrefix); it.Next())
            {
                rocks.Remove(it.Key(), general);
            }
        }

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("alias", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RegistryAliasNamingTheWrongDatabase_IsRefused()
    {
        // The alias count still matches; only the alias's id is wrong.
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
            store.CreateDatabase("billing");
        }

        using (var rocks = OpenRaw(Path.Combine(dir.Path, StoreEngine.RegistryFolderName)))
        {
            var wrongId = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(wrongId, 17); // billing's id
            rocks.Put(AliasKey("shop"), wrongId, rocks.GetColumnFamily("general"));
        }

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("shop", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RegistryAliasNamingNoLiveDatabase_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        using (var rocks = OpenRaw(Path.Combine(dir.Path, StoreEngine.RegistryFolderName)))
        {
            var shopId = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(shopId, 16);
            rocks.Put(AliasKey("ghost"), shopId, rocks.GetColumnFamily("general"));
        }

        var ex = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
        Assert.Contains("names no live database", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RegistryRecordAtAnIdNotIssued_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        using (var rocks = OpenRaw(Path.Combine(dir.Path, StoreEngine.RegistryFolderName)))
        {
            var allocator = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(allocator, 16);
            rocks.Put(BoxKeys.BuildKey(1, [0x03]), allocator, rocks.GetColumnFamily("general"));
        }

        Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(dir.Path));
    }

    // ---- A6: the open-database limit

    [Fact]
    public void OpenDatabase_AtTheLimit_IsRefusedByName()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("one");
            store.CreateDatabase("two");
        }

        using var limited = CrystalStore.Open(dir.Path, new CrystalOptions { MaxOpenDatabases = 1 });
        limited.OpenDatabase("one");
        var ex = Assert.Throws<DatabaseLimitException>(() => limited.OpenDatabase("two"));
        Assert.Equal("two", ex.DatabaseName);
        Assert.Contains("MaxOpenDatabases", ex.Message, StringComparison.Ordinal);
    }

    // ---- A7: reset

    [Fact]
    public void Reset_MissingDirectory_IsReportedAsMissing()
    {
        using var dir = new TempStoreDirectory();
        Assert.Throws<DirectoryNotFoundException>(() => CrystalStore.Reset(dir.Path));
    }

    [Fact]
    public void Reset_DirectoryThatIsNotAStore_IsRefusedAndLeftUnwritten()
    {
        using var dir = new TempStoreDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "db-00000010"));

        Assert.Throws<CrystalFormatException>(() => CrystalStore.Reset(dir.Path));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "db-00000010")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "crystal.lock")));
    }

    [Fact]
    public void Reset_FoldersCrystalDidNotName_AreLeftAlone()
    {
        using var dir = new TempStoreDirectory();
        using (var store = CrystalStore.Open(dir.Path))
        {
            store.CreateDatabase("shop");
        }

        var operatorFolder = Path.Combine(dir.Path, "db-backup-mine");
        var upperCase = Path.Combine(dir.Path, "db-0000001A");
        Directory.CreateDirectory(operatorFolder);
        Directory.CreateDirectory(upperCase);

        CrystalStore.Reset(dir.Path);

        Assert.False(Directory.Exists(dir.DatabaseFolder(16)));
        Assert.True(Directory.Exists(operatorFolder));
        Assert.True(Directory.Exists(upperCase));
    }

    private static byte[] AliasKey(string name)
    {
        var escaped = EscapedString.Encode(name);
        var key = new byte[escaped.Length + 1];
        key[0] = 0x01;
        escaped.CopyTo(key.AsSpan(1));
        return BoxKeys.BuildKey(1, key);
    }

    private static RocksDb OpenRaw(string path)
    {
        var families = new ColumnFamilies
        {
            { "general", new ColumnFamilyOptions() },
            { "delete_heavy", new ColumnFamilyOptions() },
        };
        return RocksDb.Open(new DbOptions(), path, families);
    }
}
