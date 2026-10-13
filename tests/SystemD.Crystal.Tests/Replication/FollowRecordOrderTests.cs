// Spec 011 R2.1, R2.2 — the follow record never names an older history than the data it describes. A promotion trusts
// the record: it names the followed incarnation as shared up to what the database holds. If a crash left the record
// older than the data, a promotion would claim the old incarnation past its real end, and a standby holding batches the
// old primary lost would continue on wrong data (the 2026-10-12 review, S-1). Each test stops the work at the exact
// step with a hook, which leaves the disk as a process kill there would, and checks what a promotion then claims.

using System;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Storage;
using Xunit;
using BinaryPrimitives = System.Buffers.Binary.BinaryPrimitives;
using WriteBatch = RocksDbSharp.WriteBatch;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class FollowRecordOrderTests
{
    private const string Secret = "test-secret";

    // A raw batch of `count` puts into box 999 (not the catalogue), numbered from `seq`, as a primary would ship it.
    private static (ulong, byte[]) Entry(ulong seq, int count)
    {
        using var wb = new WriteBatch();
        for (var i = 0; i < count; i++)
        {
            var key = new byte[6];
            BinaryPrimitives.WriteUInt32BigEndian(key, 999);
            key[4] = (byte)seq;
            key[5] = (byte)i;
            wb.Put(key, new byte[] { 7 });
        }

        var data = wb.ToBytes();
        BinaryPrimitives.WriteUInt64LittleEndian(data, seq);
        return (seq, data);
    }

    [Fact]
    public void ACrashRightAfterAPagesWrite_LeavesTheRecordOfItsIncarnation_SoAPromotionClaimsNoMore()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = Secret },
        });
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        using (var batch = db.NewBatch())
        {
            batch.Put(box, "k"u8, "v"u8);
            batch.Commit();
        }

        // This node holds incarnation X up to p0, and follows it as a standby.
        var p0 = (long)db.Slot.LatestSequence;
        var x = Guid.NewGuid();
        FollowRecord.Write(dir.Path, db.Uid, x, @base: 0);
        store.Replication.Demote();

        // The primary was promoted at p0: its new incarnation Y continues X up to p0, and ships batches past it.
        var y = Guid.NewGuid().ToString("D");
        var history = new[] { new IncarnationEnd(x, p0) };
        var standby = new StandbyDatabase(db.Slot, db.Uid, dir.Path, store.Replication.Epoch, hook: step =>
        {
            if (step == "standby.written")
            {
                throw new IOException("injected: the process dies right after the page's synced write");
            }
        });
        Assert.False(standby.HasDiverged(y, p0, history));

        Assert.Throws<IOException>(() =>
            standby.Apply(store.Replication.Epoch, [Entry((ulong)p0 + 1, 2), Entry((ulong)p0 + 3, 1)], y, p0, history));

        // The page reached the disk, and so did the record of Y, written before it.
        Assert.Equal((ulong)p0 + 3, db.Slot.LatestSequence);
        Assert.Equal(y, FollowRecord.Read(dir.Path, db.Uid)!.Incarnation);

        // A promotion now claims X only up to p0, where it really ended: never up to what the database holds past it.
        store.Replication.Promote("after the crash");
        var promoted = IncarnationRecord.Read(dir.Path, db.Uid)!;
        Assert.DoesNotContain(promoted.History, h => h.Id == x && h.End > p0);
        Assert.Contains(promoted.History, h => h.Id == Guid.Parse(y));
    }

    [Fact]
    public void AnInstall_RemovesTheOldFollowRecord_BeforeMovingTheSnapshotIn()
    {
        var uid = Guid.NewGuid().ToString("D");

        // The snapshot: the same database (same UID) on another node, checkpointed into a folder on the same volume.
        using var holder = new TempStoreDirectory();
        Directory.CreateDirectory(holder.Path);
        var snapshot = Path.Combine(holder.Path, "snapshot");
        using (var sourceDir = new TempStoreDirectory())
        using (var source = CrystalStore.Open(sourceDir.Path))
        {
            var remote = source.Engine.CreateDatabaseWithUid(source, "remote", replicated: true, uid);
            new WalAccess(remote.Slot.Instance).Checkpoint(snapshot);
        }

        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Hook = step =>
            {
                if (step == "install.moved")
                {
                    throw new IOException("injected: the process dies right after the snapshot is moved in");
                }
            },
        });
        store.Engine.CreateDatabaseWithUid(store, "remote", replicated: true, uid);

        // This node followed another log for the database before (incarnation X): its record names it.
        FollowRecord.Write(dir.Path, uid, Guid.NewGuid(), @base: 0);

        Assert.Throws<IOException>(() => store.Engine.InstallBootstrap(store, uid, "remote", snapshot));

        // The snapshot's data is in place and no record survived to describe it as the old log.
        Assert.Null(FollowRecord.Read(dir.Path, uid));
        Assert.Contains(store.ListDatabases(), d => d.Name == "remote");
    }
}
