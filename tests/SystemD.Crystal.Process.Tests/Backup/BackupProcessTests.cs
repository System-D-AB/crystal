// Spec 010 T8 — backups in other processes (R9.3; design § 10): a backup taken while the crash harness writes its stream
// restores to a whole prefix of the stream that holds every batch acknowledged before the backup began; a node process
// backs up on command, and the set it wrote is verified and restored here.

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Backup;

public sealed class BackupProcessTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    [Theory]
    [InlineData(DurabilityMode.Strict)]
    [InlineData(DurabilityMode.Buffered)] // never synced: the backup's flush takes them
    public async Task ABackupUnderTheCrashHarnessStream_RestoresToAWholePrefix_HoldingEveryBatchAcknowledgedBeforeIt(DurabilityMode mode)
    {
        using var dir = new TempDirectory("crystal-backup-load");
        using var root = new TempDirectory("crystal-backup-root");
        using var target = new TempDirectory("crystal-backup-restored");

        long startedAfter = -1;
        string? setId = null;
        using (var harness = HostProcess.Start(
            "SystemD.Crystal.CrashHarness.dll", "write", "--dir", dir.Path, "--mode", mode.ToString(),
            "--backup", root.Path, "--backup-after", "2000"))
        {
            while (setId is null)
            {
                var line = harness.ReadLine(Patience);
                if (line.StartsWith("BACKUPSTART ", StringComparison.Ordinal))
                {
                    startedAfter = long.Parse(line["BACKUPSTART ".Length..], CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("BACKUP ", StringComparison.Ordinal))
                {
                    setId = line["BACKUP ".Length..];
                }
                else if (line.StartsWith("BACKUPFAILED", StringComparison.Ordinal))
                {
                    Assert.Fail(line);
                }
            }

            harness.Kill(Patience); // still writing: the backup ran while it did
        }

        Assert.True(startedAfter >= 2000);
        using (var store = CrystalStore.Open(target.Path))
        {
            await store.RestoreAsync(root.Path, setId);
        }

        var audit = CrashHarnessRunner.AuditStore(target.Path);
        Assert.True(audit.Sound, string.Join(Environment.NewLine, audit.Problems));
        Assert.True(audit.Batches >= startedAfter + 1, $"Acknowledged {startedAfter + 1} batches before the backup, restored {audit.Batches}.");
    }

    [Fact]
    public async Task ANodeProcess_BacksUpOnCommand_AndItsSetVerifiesAndRestoresHere()
    {
        using var root = new TempDirectory("crystal-backup-root");
        string setId;
        using (var node = TestNode.Start())
        {
            node.CreateDatabase("shop");
            node.Box("shop", "orders");
            for (var i = 0; i < 50; i++)
            {
                node.Put("shop", "orders", $"k{i:D3}", $"v{i}");
            }

            setId = node.Backup(root.Path);
            node.Put("shop", "orders", "after", "not in the set");
            node.Stop();
        }

        using (var open = CrystalBackupRoot.Open(root.Path))
        {
            Assert.Equal([setId], open.ListSets().Select(s => s.SetId));
            Assert.True((await open.VerifyAsync(setId, deep: true)).Ok);
        }

        using var target = new TempDirectory("crystal-backup-restored");
        using var store = CrystalStore.Open(target.Path);
        await store.RestoreAsync(root.Path, setId);
        var db = store.OpenDatabase("shop");
        using var snapshot = db.Snapshot();
        Assert.Equal(50, snapshot.Scan(db.Boxes.Find("orders")!.Value, [], [], int.MaxValue).Count());
    }
}
