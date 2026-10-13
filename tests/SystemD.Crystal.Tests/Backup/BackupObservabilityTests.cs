// Spec 010 T7 — observability of backup and restore (R7): each 1700s event, each crystal.backup.* instrument, and the
// running backup in Status(). No event carries a key or a value.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupObservabilityTests
{
    [Fact]
    public async Task ABackup_LogsStartedOmittedAndCompleted_AndRecordsItsInstruments()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var logs = new CapturingLoggerFactory();
        using var metrics = new MetricsCapture();
        using var holding = new BackupTests.Hold("backup.copying");
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { LoggerFactory = logs, MeterFactory = metrics, Hook = holding.Hook });
        var first = store.CreateDatabase("first");
        BackupTests.Fill(first, first.Boxes.Create("box"), 500, 64, prefix: "secret-key-");
        var second = store.CreateDatabase("second");
        Assert.Empty(metrics.Collect("crystal.backup.last_success")); // absent before the first set

        var running = store.BackupAsync(root.Path);
        holding.WaitReached();
        var status = Assert.IsType<BackupStatus>(store.Status().Backup);
        Assert.Equal((root.Path, 0, 2), (status.Destination, status.DatabasesDone, status.DatabasesTotal));
        store.DropDatabase(second);
        holding.Release();
        var set = await running;

        Assert.Null(store.Status().Backup);
        Assert.Single(logs.Of(CrystalLogEvents.BackupStarted));
        Assert.Contains(set.SetId, Assert.Single(logs.Of(CrystalLogEvents.BackupCompleted)).Message);
        Assert.Contains("second", Assert.Single(logs.Of(CrystalLogEvents.BackupDatabaseOmitted)).Message);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("secret-key-", StringComparison.Ordinal));

        Assert.Single(metrics.Of("crystal.backup.duration"));
        Assert.True(Assert.Single(metrics.Of("crystal.backup.bytes")).Value > 0);
        Assert.True(Assert.Single(metrics.Of("crystal.backup.files")).Value > 0);
        Assert.Equal(set.Completed.ToUnixTimeSeconds(), Assert.Single(metrics.Collect("crystal.backup.last_success")).Value);
        Assert.Empty(metrics.Of("crystal.backup.failures"));
    }

    [Fact]
    public async Task AFailedBackup_IsLoggedAndCounted()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        var logs = new CapturingLoggerFactory();
        using var metrics = new MetricsCapture();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            LoggerFactory = logs,
            MeterFactory = metrics,
            Hook = step =>
            {
                if (step == "backup.copying")
                {
                    throw new IOException("injected");
                }
            },
        });
        store.CreateDatabase("db");

        await Assert.ThrowsAsync<IOException>(() => store.BackupAsync(root.Path));

        Assert.Single(logs.Of(CrystalLogEvents.BackupFailed));
        Assert.Equal(1, Assert.Single(metrics.Of("crystal.backup.failures")).Value);
        Assert.Empty(metrics.Of("crystal.backup.duration"));
    }

    [Fact]
    public async Task ARestore_LogsStartedAndCompleted_OrFailed()
    {
        using var source = new TempStoreDirectory();
        using var target = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        BackupSetInfo set;
        using (var store = CrystalStore.Open(source.Path))
        {
            store.CreateDatabase("db");
            set = await store.BackupAsync(root.Path);
        }

        var logs = new CapturingLoggerFactory();
        using (var store = CrystalStore.Open(target.Path, new CrystalOptions { LoggerFactory = logs }))
        {
            await store.RestoreAsync(root.Path, set.SetId);
            await Assert.ThrowsAsync<DatabaseNameTakenException>(() => store.RestoreAsync(root.Path, set.SetId, new RestoreOptions { NewIdentity = true }));
        }

        Assert.Equal(2, logs.Of(CrystalLogEvents.RestoreStarted).Count);
        Assert.Contains("1 database", Assert.Single(logs.Of(CrystalLogEvents.RestoreCompleted)).Message);
        Assert.IsType<DatabaseNameTakenException>(Assert.Single(logs.Of(CrystalLogEvents.RestoreFailed)).Exception);
    }

    [Fact]
    public async Task VerifyDamage_AndAPurge_AreLoggedThroughTheRootsLoggerFactory()
    {
        using var dir = new TempStoreDirectory();
        using var root = new TempStoreDirectory();
        string uid;
        BackupSetInfo set;
        using (var store = CrystalStore.Open(dir.Path))
        {
            var db = store.CreateDatabase("db");
            uid = db.Uid;
            set = await store.BackupAsync(root.Path);
            await store.BackupAsync(root.Path);
        }

        File.Delete(Directory.EnumerateFiles(Path.Combine(root.Path, "engines", uid, "shared_checksum")).First());
        var logs = new CapturingLoggerFactory();
        using var open = CrystalBackupRoot.Open(root.Path, logs);
        Assert.False((await open.VerifyAsync(set.SetId, deep: false)).Ok);
        open.Purge(keepLast: 1);

        Assert.Single(logs.Of(CrystalLogEvents.BackupVerifyFailed));
        Assert.Contains("1 set(s) kept, 1 deleted", Assert.Single(logs.Of(CrystalLogEvents.BackupPurged)).Message);
    }
}
