// Spec 010 T2 — the backup root and the manifest (R2.1–R2.4, R4.1–R4.3): each destination refusal by name, the root
// lock refused to a second opener, a manifest written last and refused once edited, stale temporaries cleaned up.

using System;
using System.IO;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Backup;
using Xunit;

namespace SystemD.Crystal.Tests.Backup;

public sealed class BackupRootTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARelativeDestination_IsRefused()
    {
        var refusal = Assert.Throws<BackupDestinationException>(() => BackupRoot.CheckDestination("backups", dataDirectory: null));
        Assert.Equal("backups", refusal.Destination);
    }

    [Fact]
    public void ADestinationInsideTheDataDirectory_OrContainingIt_OrTheSame_IsRefused()
    {
        using var dir = new TempStoreDirectory();
        Assert.Throws<BackupDestinationException>(() => BackupRoot.CheckDestination(Path.Combine(dir.Path, "backups"), dir.Path));
        Assert.Throws<BackupDestinationException>(() => BackupRoot.CheckDestination(Path.GetDirectoryName(dir.Path)!, dir.Path));
        Assert.Throws<BackupDestinationException>(() => BackupRoot.CheckDestination(dir.Path + Path.DirectorySeparatorChar, dir.Path));

        // A sibling whose name only starts with the data directory's is not inside it.
        Assert.Equal(Path.GetFullPath(dir.Path + "-backups"), BackupRoot.CheckDestination(dir.Path + "-backups", dir.Path));
    }

    [Fact]
    public void ANonEmptyForeignFolder_IsRefused_AndNothingIsWrittenIntoIt()
    {
        using var root = new TempStoreDirectory();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, "notes.txt"), "mine");

        Assert.Throws<BackupDestinationException>(() => BackupRoot.OpenOrCreate(root.Path, Now));
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(root.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void AnEmptyOrMissingFolder_BecomesARoot_AndReopens()
    {
        using var root = new TempStoreDirectory();
        using (BackupRoot.OpenOrCreate(root.Path, Now))
        {
        }

        Assert.True(File.Exists(Path.Combine(root.Path, BackupRoot.RootFileName)));
        using (BackupRoot.OpenOrCreate(root.Path, Now))
        {
        }

        using (BackupRoot.OpenExisting(root.Path))
        {
        }
    }

    [Fact]
    public void AFolderWithoutARootFile_IsNotOpenedAsARoot()
    {
        using var root = new TempStoreDirectory();
        Directory.CreateDirectory(root.Path);
        Assert.Throws<BackupDestinationException>(() => BackupRoot.OpenExisting(root.Path));
    }

    [Fact]
    public void ARootOfAnotherFormat_IsRefused()
    {
        using var root = new TempStoreDirectory();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, BackupRoot.RootFileName), "{\"format\":\"crystal-backup 9\",\"created\":\"\"}");

        var refusal = Assert.Throws<CrystalFormatException>(() => BackupRoot.OpenExisting(root.Path));
        Assert.Equal("crystal-backup 9", refusal.FoundFormat);
    }

    [Fact]
    public void ASecondOpener_IsRefusedAtOnce_UntilTheFirstReleases()
    {
        using var root = new TempStoreDirectory();
        using (BackupRoot.OpenOrCreate(root.Path, Now))
        {
            var refusal = Assert.Throws<BackupRootLockedException>(() => BackupRoot.OpenExisting(root.Path));
            Assert.Equal(root.Path, refusal.RootPath);
            Assert.Throws<BackupRootLockedException>(() => BackupRoot.OpenOrCreate(root.Path, Now));
        }

        using (BackupRoot.OpenExisting(root.Path))
        {
        }
    }

    [Fact]
    public void AManifest_RoundTrips_AndIsRefusedOnceAnyByteOfItsContentChanges()
    {
        using var root = new TempStoreDirectory();
        using var open = BackupRoot.OpenOrCreate(root.Path, Now);
        var manifest = Sample();
        manifest.WriteTo(open.SetsDirectory);

        var path = open.ManifestPath(manifest.Set);
        var read = BackupManifest.Read(path);
        Assert.Equal(manifest.Set, read.Set);
        Assert.Equal(918273, read.Databases.Single().Sequence);
        Assert.Equal(read.ComputeSha256(), read.Sha256);

        var info = read.ToInfo();
        Assert.Equal(Now, info.Started);
        Assert.Equal("dropped", info.Omitted.Single().Reason);

        // One byte of content: a digit of the sequence.
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("918273", "918274", StringComparison.Ordinal));
        Assert.Throws<BackupDamagedException>(() => BackupManifest.Read(path));

        // Not JSON at all.
        File.WriteAllText(path, text[..^2]);
        Assert.Throws<BackupDamagedException>(() => BackupManifest.Read(path));
    }

    [Fact]
    public void AManifestCopiedUnderAnotherSetsName_IsRefused()
    {
        using var root = new TempStoreDirectory();
        using var open = BackupRoot.OpenOrCreate(root.Path, Now);
        var manifest = Sample();
        manifest.WriteTo(open.SetsDirectory);
        var other = open.ManifestPath("20261013T120000000Z-00000000");
        File.Copy(open.ManifestPath(manifest.Set), other);

        Assert.Throws<BackupDamagedException>(() => BackupManifest.Read(other));
    }

    [Fact]
    public void AManifestIsWrittenToATemporaryFileFirst_AndAStaleOneIsDeletedByTheNextOpen()
    {
        using var root = new TempStoreDirectory();
        string temp;
        using (var open = BackupRoot.OpenOrCreate(root.Path, Now))
        {
            var manifest = Sample();
            string? seen = null;
            manifest.WriteTo(open.SetsDirectory, step =>
            {
                if (step == "backup.manifest.written")
                {
                    seen = string.Join(",", Directory.EnumerateFiles(open.SetsDirectory).Select(Path.GetFileName));
                }
            });

            temp = open.ManifestPath(manifest.Set) + BackupManifest.TempSuffix;
            Assert.Equal(Path.GetFileName(temp), seen); // only the temporary exists before the rename
            File.Copy(open.ManifestPath(manifest.Set), temp); // a killed writer's leftover
            Assert.Single(open.ManifestFiles());
        }

        using (BackupRoot.OpenExisting(root.Path))
        {
            Assert.False(File.Exists(temp));
        }
    }

    [Fact]
    public void SetIds_SortByTime()
    {
        var earlier = BackupRunner.NewSetId(Now);
        var later = BackupRunner.NewSetId(Now.AddMilliseconds(1));
        Assert.Matches("^20261013T120000000Z-[0-9a-f]{8}$", earlier);
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    private static BackupManifest Sample() => new()
    {
        Set = BackupRunner.NewSetId(Now),
        Label = "nightly",
        Node = Guid.NewGuid().ToString("D"),
        Started = BackupManifest.FormatTime(Now),
        Completed = BackupManifest.FormatTime(Now.AddSeconds(3)),
        Databases =
        [
            new BackupManifest.ManifestDatabase
            {
                Name = "shop", Uid = Guid.NewGuid().ToString("D"), Replicated = true, Created = BackupManifest.FormatTime(Now),
                Backup = 7, Sequence = 918273, Bytes = 100, Files = 4,
            },
        ],
        Omitted = [new BackupManifest.ManifestOmitted { Name = "tmp", Uid = Guid.NewGuid().ToString("D"), Reason = "dropped" }],
    };
}
