// Ported from Highway: tests/Highway.Server.Tests/Storage/StorageFormatTests.cs and
// tests/Highway.Integration.Tests/DurableByDefaultTests.cs (the format tests) (spec 004 design § 2.4, task T12).
// Highway's --wipe-data is Crystal's CrystalStore.Reset; its highway.format stamp is crystal.format. The two replica
// tests (a snapshot or a primary that does not state its format) belong to spec 003.

using System;
using System.IO;
using RocksDbSharp;
using Xunit;

namespace SystemD.Crystal.Tests.Ported;

public sealed class StorageFormatTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A RocksDB database written straight into the directory, as a foreign or older layout would be.</summary>
    private void PlantForeignDatabase()
    {
        using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true), _dir.Path);
        db.Put("kreg:node:old\0\0"u8.ToArray(), "x"u8.ToArray());
    }

    // Ported from Highway: StorageFormatTests.A2xDataDirectory_IsRefused_WithTheUpgradePath. Data that is not a Crystal
    // store is refused, never served as an empty, healthy-looking store, and the refusal names the way forward.
    [Fact]
    public void AForeignDataDirectory_IsRefused_WithTheWayForward()
    {
        PlantForeignDatabase();
        var before = Directory.GetFileSystemEntries(_dir.Path).Length;

        var refused = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(_dir.Path));

        Assert.Null(refused.FoundFormat);
        Assert.False(string.IsNullOrWhiteSpace(refused.WayForward));
        Assert.Contains("crystal.format", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir.Path).Length);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "crystal.format")), "A refused directory is left unwritten.");
    }

    // Ported from Highway: StorageFormatTests.AFormat3Stamp_IsRefused_WithTheSameGuidance
    [Fact]
    public void AnotherFormatStamp_IsRefused_WithTheWayForward()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "crystal.format"), "crystal 0\n");

        var refused = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(_dir.Path));

        Assert.Equal("crystal 0", refused.FoundFormat);
        Assert.False(string.IsNullOrWhiteSpace(refused.WayForward));
    }

    // Ported from Highway: DurableByDefaultTests.AMismatchedStorageFormat_IsRefusedNamingWhatItFound
    [Fact]
    public void AMismatchedStorageFormat_IsRefusedNamingWhatItFound()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "crystal.format"), "1");

        var refused = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(_dir.Path));

        Assert.Contains("'1'", refused.Message, StringComparison.Ordinal);
    }

    // Ported from Highway: DurableByDefaultTests.ADataDirectoryFromAnOlderBuild_IsRefusedBeforeRecovery. A directory
    // that looks used but carries no stamp is refused before RocksDB opens anything in it.
    [Fact]
    public void AUsedDirectoryWithoutAStamp_IsRefusedBeforeRecovery()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "checkpoints"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "_crystal"));

        var refused = Assert.Throws<CrystalFormatException>(() => CrystalStore.Open(_dir.Path));

        Assert.Contains("format", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(refused.WayForward));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir.Path, "_crystal")));
    }

    // Ported from Highway: DurableByDefaultTests.AFreshDataDirectory_IsStampedSoTheNextBuildCanCheckIt
    [Fact]
    public void AFreshDataDirectory_IsStampedSoTheNextOpenCanCheckIt()
    {
        using (CrystalStore.Open(_dir.Path))
        {
            Assert.True(File.Exists(Path.Combine(_dir.Path, "crystal.format")));
        }

        using (CrystalStore.Open(_dir.Path))
        {
        }
    }

    // Ported from Highway: StorageFormatTests.WipeData_EmptiesTheDirectory_KeepingTheReplicationIdentity
    [Fact]
    public void Reset_EmptiesTheStore_KeepingTheReplicationIdentity()
    {
        string nodeId;
        using (var store = CrystalStore.Open(_dir.Path))
        {
            nodeId = store.NodeId;
            var db = store.CreateDatabase("shop");
            var box = db.Boxes.Create("orders");
            using var batch = db.NewBatch();
            batch.Put(box, "k"u8, "v"u8);
            batch.Commit();
        }

        var epochPath = Path.Combine(_dir.Path, "node.epoch");
        File.WriteAllText(epochPath, "7");

        CrystalStore.Reset(_dir.Path);

        Assert.Equal("7", File.ReadAllText(epochPath));
        using var reopened = CrystalStore.Open(_dir.Path);
        Assert.Equal(nodeId, reopened.NodeId);
        Assert.Empty(reopened.ListDatabases());
    }

    // Ported from Highway: StorageFormatTests.WipeData_RefusesWhileABrokerHoldsTheDirectory
    [Fact]
    public void Reset_RefusesWhileTheStoreIsOpen()
    {
        using var running = CrystalStore.Open(_dir.Path);

        var refused = Assert.Throws<StoreLockedException>(() => CrystalStore.Reset(_dir.Path));

        Assert.Contains("lock", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Ported from Highway: DurableByDefaultTests.AnUnusableDataDirectory_ThrowsAtBuild_NamingBothWaysOut (adapted:
    // Crystal has no ephemeral mode to fall back to, and never falls back silently). A directory that cannot be
    // created fails the open and names the path.
    [Fact]
    public void AnUnusableDataDirectory_FailsTheOpen_NamingThePath()
    {
        Directory.CreateDirectory(_dir.Path);
        var file = Path.Combine(_dir.Path, "not-a-directory");
        File.WriteAllText(file, string.Empty);
        var path = Path.Combine(file, "data");

        var refused = Assert.ThrowsAny<IOException>(() => CrystalStore.Open(path));

        Assert.Contains("not-a-directory", refused.Message, StringComparison.Ordinal);
    }
}
