// Spec 010 R5.3; design § 6 — deep verify of one database's backup: restored into a scratch folder, opened read-only,
// every table file's checksums verified by RocksDB, its catalogue loaded with the identity the manifest expects. The
// only check that reads every byte. Here, in Storage, because it touches RocksDB types.

using System;
using System.Collections.Generic;
using System.IO;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>Quick and deep verify of one engine backup (spec 010 R5.2, R5.3).</summary>
internal static class BackupVerification
{
    /// <summary>
    /// The file check of quick verify (R5.2): every file backup <paramref name="backupId"/>'s meta file lists is present,
    /// and each shared table file is the size its name records (<c>…_{size}.sst</c>). Returns null when they are, or what
    /// is wrong. Needed beside the engine's own verify, which compares sizes with those it read from disk when it opened:
    /// a file truncated before the open passes it. A private file (<c>CURRENT</c>, <c>MANIFEST</c>, <c>OPTIONS</c>) has
    /// no size recorded anywhere, so only its presence is checked here; deep verify reads its bytes.
    /// </summary>
    public static string? CheckFiles(string engineDirectory, uint backupId)
    {
        var meta = Path.Combine(engineDirectory, "meta", backupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string[] lines;
        try
        {
            lines = File.ReadAllLines(meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"its engine's meta file for backup {backupId} cannot be read ({ex.Message})";
        }

        // A file line is "{relative path} crc32 {value}"; the header lines (time, sequence, metadata, count) are not.
        var listed = 0;
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            if (fields.Length < 3 || fields[1] != "crc32" || !fields[0].Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            listed++;
            var file = Path.Combine(engineDirectory, fields[0].Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return $"its file '{fields[0]}' is missing";
            }

            if (RecordedSize(fields[0]) is { } size && info.Length != size)
            {
                return $"its file '{fields[0]}' is {info.Length} bytes, not the {size} its name records";
            }
        }

        return listed == 0 ? $"its engine's meta file for backup {backupId} lists no file" : null;
    }

    /// <summary>The size a shared file's name records: <c>{number}_s{session}_{size}.{ext}</c>; null for any other name.</summary>
    private static long? RecordedSize(string relativePath)
    {
        if (!relativePath.StartsWith("shared_checksum/", StringComparison.Ordinal))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(relativePath);
        var underscore = name.LastIndexOf('_');
        return underscore > 0 && long.TryParse(name.AsSpan(underscore + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size)
            ? size
            : null;
    }

    /// <summary>
    /// Restores backup <paramref name="backupId"/> of the engine in <paramref name="engineDirectory"/> into a new folder
    /// under <paramref name="scratchRoot"/>, verifies it and deletes the folder, also on failure. Returns null when it
    /// verified, or what was wrong.
    /// </summary>
    public static string? DeepVerify(string engineDirectory, uint backupId, string uid, string name, string scratchRoot, TimeProvider clock)
    {
        var scratch = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratchRoot); // the engine creates the restore folder, not its parent
            using (var engine = BackupEngineHandle.Open(engineDirectory))
            {
                engine.Restore(backupId, scratch);
            }

            // Small resources of its own: a verify reads each block once.
            using var resources = StoreResources.Create(blockCacheBytes: 8UL << 20, writeBufferBytes: 8UL << 20, backgroundThreads: 1);
            using var instance = RocksInstance.OpenReadOnly(scratch, resources, new Dictionary<string, BoxProfile>());
            NativeRocks.VerifyChecksums(instance.Db);
            using var context = new DatabaseContext(instance, clock, $"the backup of database '{name}'");
            context.LoadCatalogue(DatabaseKind.Database, uid); // spec 009's identity check, read-only (R11.6)
            return null;
        }
        catch (Exception ex) when (ex is RocksException or RocksDbException or CrystalFormatException or IOException)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    StoreEngine.DeleteDirectoryWithRetries(scratch);
                }
            }
            catch (IOException)
            {
                // Best effort: the next deep verify uses a new folder.
            }
        }
    }
}
