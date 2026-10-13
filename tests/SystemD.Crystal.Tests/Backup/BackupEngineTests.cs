// Spec 010 T1 — the native BackupEngine surface, at storage level: a database is backed up with its metadata, the
// backup listed, verified and restored into another folder, and the restored data is the same, family by family.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupEngineTests
{
    [Fact]
    public void ABackup_IsListedWithItsMetadata_Verified_AndRestoresTheSameData()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        Directory.CreateDirectory(root.Path);
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        for (var i = 0; i < 100; i++)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes($"k{i:D3}"), Text.UTF8.GetBytes($"v{i}"));
            batch.Commit(DurabilityMode.Buffered); // not synced: the backup's flush must still include it
        }

        var engineDir = Path.Combine(root.Path, "engine");
        using var engine = BackupEngineHandle.Open(engineDir);
        var id = engine.Create(db.Slot.Instance, Text.UTF8.GetBytes("{\"set\":\"s1\"}"));

        var listed = Assert.Single(engine.List());
        Assert.Equal(id, listed.Id);
        Assert.Equal("{\"set\":\"s1\"}", Text.UTF8.GetString(listed.Metadata));
        Assert.True(listed.Files > 0 && listed.Size > 0);
        engine.Verify(id);

        var restored = Path.Combine(root.Path, "restored");
        engine.Restore(id, restored);
        using var copy = RocksInstance.OpenReadOnly(restored, store.Engine.Resources, new Dictionary<string, BoxProfile>());
        NativeRocks.VerifyChecksums(copy.Db);
        foreach (var family in RocksInstance.FamilyOrder)
        {
            Assert.Equal(All(db.Slot.Instance, family), All(copy, family));
        }

        Assert.Contains(All(copy, "default").Concat(All(copy, "general")), kv => kv.EndsWith("=v99", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFile_FailsQuickVerify()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        Directory.CreateDirectory(root.Path);
        using var store = CrystalStore.Open(dir.Path);
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "k"u8, "v"u8);
            batch.Commit();
        }

        using var engine = BackupEngineHandle.Open(Path.Combine(root.Path, "engine"));
        var id = engine.Create(db.Slot.Instance, []);
        var file = Directory.EnumerateFiles(Path.Combine(root.Path, "engine", "shared_checksum")).First(f => f.EndsWith(".sst", StringComparison.Ordinal));
        File.Delete(file);

        Assert.Throws<RocksException>(() => engine.Verify(id));
    }

    private static List<string> All(RocksInstance instance, string family)
    {
        var rows = new List<string>();
        using var iterator = instance.Db.NewIterator(instance.Family(family));
        for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next())
        {
            rows.Add(Convert.ToHexString(iterator.Key()) + "=" + Text.UTF8.GetString(iterator.Value()));
        }

        return rows;
    }
}
