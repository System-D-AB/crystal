// Spec 003 T2 / R11.1 — a replicated database retains its WAL for standbys; a not-replicated one keeps RocksDB's
// default (no retention). With WAL retention on, RocksDB moves a flushed WAL into an `archive/` subfolder instead of
// deleting it; with retention off, a flushed WAL is deleted. The test writes enough to force flushes, then checks
// which database kept archived WAL.

using System;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests;

public sealed class WalRetentionTests
{
    private static ReplicationOptions Repl() => new()
    {
        Secret = "test-secret",
        // Keep the WAL for a day and up to a generous size, so a flush archives rather than deletes it.
        WalTtl = TimeSpan.FromHours(24),
        WalBytesPerDatabase = 1L << 30,
    };

    private static void WriteAndFlush(CrystalDatabase db)
    {
        var box = db.Boxes.Create("b");
        var value = new byte[4096];
        // Write a batch, then force a memtable flush, which rolls the active WAL. With retention on, the rolled WAL
        // is archived; with retention off, it is deleted. Repeating a few times guarantees a WAL is rolled.
        for (var round = 0; round < 4; round++)
        {
            using (var batch = db.NewBatch())
            {
                for (var i = 0; i < 500; i++)
                {
                    var key = Text.UTF8.GetBytes($"k_{round:D3}_{i:D4}");
                    batch.Put(box, key, value);
                }

                batch.Commit(DurabilityMode.Strict);
            }

            // Force the flush deterministically through the internal instance (test-only; spec 003 T2).
            db.Slot.Instance.Flush();
        }
    }

    // RocksDB creates an `archive/` subdirectory under a database only when WAL retention is on
    // (WAL_ttl_seconds / WAL_size_limit_MB set): rolled WAL files are archived there instead of deleted. With
    // retention off, no `archive/` directory is created. Its presence is the deterministic signal that a database
    // retains its WAL for a standby (spec 003 R11.1).
    private static bool RetainsWal(string dbFolder) => Directory.Exists(Path.Combine(dbFolder, "archive"));

    [Fact]
    public void ReplicatedDatabase_RetainsWal_NotReplicatedDoesNot()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });

        var replicated = store.CreateDatabase("replicated", replicated: true);
        var local = store.CreateDatabase("local", replicated: false);

        WriteAndFlush(replicated);
        WriteAndFlush(local);

        // Give RocksDB a moment to settle its WAL housekeeping after the flushes.
        store.SyncAll();

        var replicatedFolder = dir.DatabaseFolder(replicated.Id);
        var localFolder = dir.DatabaseFolder(local.Id);

        Assert.True(RetainsWal(replicatedFolder), "a replicated database must retain (archive) its WAL (R11.1)");
        Assert.False(RetainsWal(localFolder), "a not-replicated database must not retain its WAL");
    }

    [Fact]
    public void WithoutReplicationOptions_NoDatabaseRetainsWal()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path); // no Replication options

        var db = store.CreateDatabase("db", replicated: true);
        WriteAndFlush(db);
        store.SyncAll();

        // A store with no replication configured never retains a WAL, even for a database marked replicated.
        Assert.False(RetainsWal(dir.DatabaseFolder(db.Id)));
    }
}
