// Spec 010 R11.4 — a restore is a change of the replicated set: a standby following the primary bootstraps the
// restored database promptly, with its data, like any new database.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

[Collection("replication-e2e")]
public sealed class RestoreReplicationTests
{
    private static CrystalOptions Options(string? listen = null) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromSeconds(10), // a pull waits long: only the registry change can wake it promptly
        },
    };

    [Fact]
    public async Task AStandbyFollowingThePrimary_BootstrapsARestoredDatabasePromptly()
    {
        using var origin = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo set;
        string expected;
        using (var store = CrystalStore.Open(origin.Path))
        {
            var db = store.CreateDatabase("restored", replicated: true);
            BackupTests.Fill(db, db.Boxes.Create("box"), 200, 32);
            expected = string.Join("|", BackupTests.Rows(db.Slot.Instance).Where(r => !r.StartsWith("default:", StringComparison.Ordinal)));
            set = await store.BackupAsync(root.Path);
        }

        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options());
        primary.Replication.Listen();
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        await Task.Delay(1000); // the standby is now waiting in a long pull

        var restored = Assert.Single(await primary.RestoreAsync(root.Path, set.SetId));

        var watch = Stopwatch.StartNew();
        while (!standby.ListDatabases().Any(d => d.Uid == restored.Uid))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "the standby did not bootstrap the restored database before its pull timed out");
            Thread.Sleep(20);
        }

        var copy = standby.OpenDatabase("restored");
        var deadline = Stopwatch.StartNew();
        while (string.Join("|", BackupTests.Rows(copy.Slot.Instance).Where(r => !r.StartsWith("default:", StringComparison.Ordinal))) != expected)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "the standby's copy never matched");
            Thread.Sleep(50);
        }
    }
}
