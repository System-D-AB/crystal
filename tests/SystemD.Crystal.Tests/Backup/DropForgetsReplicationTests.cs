// Spec 010 R11.1 — a drop removes the database's replication records (follow record, incarnation record, resync
// marker). Left behind, they would describe a history to a database restored later under the same UID, and a promotion
// would claim it. Startup recovery removes them too, when a crash came between the drop's registry batch and the
// removal, but never those of a UID that is live again.

using System;
using System.IO;
using SystemD.Crystal;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class DropForgetsReplicationTests
{
    private static CrystalOptions Options(Action<string>? hook = null) => new()
    {
        Hook = hook,
        Replication = new ReplicationOptions { Secret = "test-secret" },
    };

    private static void Remember(string dir, string uid)
    {
        FollowRecord.Write(dir, uid, Guid.NewGuid(), @base: 0);
        IncarnationRecord.Write(dir, uid, LogIncarnation.Fresh(3));
        ResyncMarker.Write(dir, uid, "divergence");
    }

    private static bool AnyRecord(string dir, string uid) =>
        FollowRecord.Read(dir, uid) is not null
        || IncarnationRecord.Exists(dir, uid)
        || ResyncMarker.Exists(dir, uid);

    [Fact]
    public void ADrop_RemovesTheDatabasesReplicationRecords()
    {
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options());
        var db = store.CreateDatabase("db", replicated: true);
        Remember(dir.Path, db.Uid);

        store.DropDatabase(db);

        Assert.False(AnyRecord(dir.Path, db.Uid));
    }

    [Fact]
    public void ACrashAfterTheDropsBatch_IsFinishedByTheNextOpen()
    {
        using var dir = new TempStoreDirectory();
        string uid;
        using (var store = CrystalStore.Open(dir.Path, Options(hook: step =>
        {
            if (step == "drop.closed")
            {
                throw new IOException("injected: the process dies after the drop's registry batch");
            }
        })))
        {
            var db = store.CreateDatabase("db", replicated: true);
            uid = db.Uid;
            Remember(dir.Path, uid);
            Assert.Throws<IOException>(() => store.DropDatabase(db));
            Assert.True(AnyRecord(dir.Path, uid)); // the drop did not get as far as the removal
        }

        using (CrystalStore.Open(dir.Path, Options()))
        {
            Assert.False(AnyRecord(dir.Path, uid));
        }
    }

    [Fact]
    public void Recovery_LeavesTheRecordsOfAUidThatIsLiveAgain()
    {
        // A restore may bring a dropped UID back; its records are its own then. Simulated: a live database created with
        // the UID of one dropped earlier.
        using var dir = new TempStoreDirectory();
        string uid;
        using (var store = CrystalStore.Open(dir.Path, Options()))
        {
            var dropped = store.CreateDatabase("old", replicated: true);
            uid = dropped.Uid;
            store.DropDatabase(dropped);
            store.Engine.CreateDatabaseWithUid(store, "back", replicated: true, uid);
            Remember(dir.Path, uid);
        }

        using (CrystalStore.Open(dir.Path, Options()))
        {
            Assert.True(FollowRecord.Read(dir.Path, uid) is not null);
            Assert.True(IncarnationRecord.Exists(dir.Path, uid));
        }
    }
}
