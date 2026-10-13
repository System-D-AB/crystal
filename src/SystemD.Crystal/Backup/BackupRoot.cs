// Spec 010 R2.1–R2.4; design § 1 — a backup root: the folder a caller names, holding the root file, the lock, one
// BackupEngine per database UID and one manifest per set. One operation at a time holds the lock, across processes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Backup;

/// <summary>An open, locked backup root (spec 010 design § 1). Disposing it releases the lock.</summary>
internal sealed class BackupRoot : IDisposable
{
    public const string RootFileName = "crystal-backup.json";
    public const string LockFileName = "backup.lock";
    public const string RootFormat = "crystal-backup 1";

    private FileStream? _lock;

    private BackupRoot(string path, FileStream lockStream)
    {
        Path = path;
        _lock = lockStream;
    }

    /// <summary>The root's full path.</summary>
    public string Path { get; }

    /// <summary><c>engines/</c>: one BackupEngine folder per database UID.</summary>
    public string EnginesDirectory => System.IO.Path.Combine(Path, "engines");

    /// <summary><c>sets/</c>: one manifest per complete set.</summary>
    public string SetsDirectory => System.IO.Path.Combine(Path, "sets");

    /// <summary><c>scratch/</c>: deep verify's default scratch folder.</summary>
    public string ScratchDirectory => System.IO.Path.Combine(Path, "scratch");

    /// <summary>The engine folder of the database <paramref name="uid"/>.</summary>
    public string EngineDirectory(string uid) => System.IO.Path.Combine(EnginesDirectory, uid);

    /// <summary>
    /// The full path of <paramref name="destination"/>, checked (R2.1, R2.2): absolute, and neither inside the data
    /// directory nor containing it. Throws <see cref="BackupDestinationException"/>.
    /// </summary>
    public static string CheckDestination(string destination, string? dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        if (!System.IO.Path.IsPathFullyQualified(destination))
        {
            throw new BackupDestinationException(
                destination,
                $"The backup destination '{destination}' is not an absolute path. Name the root by its full path.");
        }

        var full = System.IO.Path.GetFullPath(destination);
        if (dataDirectory is not null)
        {
            var data = System.IO.Path.GetFullPath(dataDirectory);
            if (SameOrInside(full, data) || SameOrInside(data, full))
            {
                throw new BackupDestinationException(
                    destination,
                    $"The backup destination '{destination}' is inside the store's data directory '{dataDirectory}', or " +
                    "contains it. A backup that shares a folder with what it protects protects nothing: choose a folder " +
                    "outside it, ideally on another disk.");
            }
        }

        return full;
    }

    /// <summary>
    /// Opens the root at <paramref name="path"/> for a backup (R2.1): created if missing or empty, with its root file
    /// stamped <paramref name="created"/>; a directory that holds anything else and no root file is refused with
    /// <see cref="BackupDestinationException"/>, checked before the lock is taken so a foreign folder is never written to.
    /// </summary>
    public static BackupRoot OpenOrCreate(string path, DateTimeOffset created)
    {
        Directory.CreateDirectory(path);
        var rootFile = System.IO.Path.Combine(path, RootFileName);
        if (!File.Exists(rootFile) && Directory.EnumerateFileSystemEntries(path).Any(e => !IsOwnLeftover(e)))
        {
            throw new BackupDestinationException(
                path,
                $"The backup destination '{path}' is not empty and is not a backup root (it has no '{RootFileName}'). " +
                "Name an empty folder, or an existing backup root.");
        }

        var root = new BackupRoot(path, TakeLock(path));
        try
        {
            if (File.Exists(rootFile))
            {
                root.CheckRootFile();
            }
            else
            {
                root.WriteRootFile(created);
            }

            root.DeleteStaleTemporaries();
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing root (R5, R6): <paramref name="path"/> must hold the root file. Throws
    /// <see cref="BackupDestinationException"/> if it does not, <see cref="BackupRootLockedException"/> if another
    /// operation holds it.
    /// </summary>
    public static BackupRoot OpenExisting(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new BackupDestinationException(path, $"The backup root '{path}' is not an absolute path. Name it by its full path.");
        }

        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(System.IO.Path.Combine(path, RootFileName)))
        {
            throw new BackupDestinationException(
                path,
                $"'{path}' is not a backup root (it has no '{RootFileName}'). Name the folder a backup was written to.");
        }

        var root = new BackupRoot(path, TakeLock(path));
        try
        {
            root.CheckRootFile();
            root.DeleteStaleTemporaries();
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    /// <summary>The manifests in <c>sets/</c>, by file path, in set-id order (which is start-time order).</summary>
    public IReadOnlyList<string> ManifestFiles() =>
        Directory.Exists(SetsDirectory)
            ? [.. Directory.EnumerateFiles(SetsDirectory, "*.json").Order(StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// Whether the root holds set <paramref name="setId"/>, by exact name (review M3: on a case-insensitive file system a
    /// set id in another case would otherwise find the manifest and be refused as damaged, not as unknown).
    /// </summary>
    public bool HasSet(string setId) =>
        setId.IndexOfAny(['/', '\\', ':']) < 0
        && !setId.Contains("..", StringComparison.Ordinal)
        && Directory.Exists(SetsDirectory)
        && Directory.EnumerateFiles(SetsDirectory, "*.json").Any(f => string.Equals(System.IO.Path.GetFileName(f), setId + ".json", StringComparison.Ordinal));

    /// <summary>The path of set <paramref name="setId"/>'s manifest.</summary>
    public string ManifestPath(string setId) => System.IO.Path.Combine(SetsDirectory, setId + ".json");

    public void Dispose()
    {
        var stream = _lock;
        _lock = null;
        stream?.Dispose();
    }

    private static FileStream TakeLock(string path)
    {
        var lockPath = System.IO.Path.Combine(path, LockFileName);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
        }
        catch (IOException ex) when (ex is not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException))
        {
            // Only a sharing conflict means another operation holds the root (as StoreLock).
            throw new BackupRootLockedException(
                path,
                $"The backup root '{path}' is in use by another backup, verify, purge or restore (its '{LockFileName}' " +
                "is held). Wait for that operation to finish, then try again.",
                ex);
        }
    }

    // What a backup killed while creating the root leaves before the root file is in place: the lock, the root file's temporary.
    private static bool IsOwnLeftover(string entry) =>
        System.IO.Path.GetFileName(entry) is LockFileName or RootFileName + BackupManifest.TempSuffix;

    private static bool SameOrInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var p = System.IO.Path.TrimEndingDirectorySeparator(path);
        var f = System.IO.Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(p, f, comparison)
            || p.StartsWith(f + System.IO.Path.DirectorySeparatorChar, comparison)
            || p.StartsWith(f + System.IO.Path.AltDirectorySeparatorChar, comparison);
    }

    private void WriteRootFile(DateTimeOffset created)
    {
        var path = System.IO.Path.Combine(Path, RootFileName);
        var temp = path + BackupManifest.TempSuffix;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new RootFile { Format = RootFormat, Created = BackupManifest.FormatTime(created) });
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        DurableFile.Replace(temp, path); // spec 010 R11.5
    }

    private void CheckRootFile()
    {
        var path = System.IO.Path.Combine(Path, RootFileName);
        RootFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RootFile>(File.ReadAllBytes(path));
        }
        catch (JsonException ex)
        {
            throw new BackupDamagedException(path, $"The backup root file '{path}' is not valid JSON. Check the folder is a backup root.", ex);
        }

        if (!string.Equals(file?.Format, RootFormat, StringComparison.Ordinal))
        {
            throw new CrystalFormatException(
                $"The backup root '{Path}' is stamped '{file?.Format}'; this build reads '{RootFormat}'.",
                foundFormat: file?.Format,
                supportedFormat: RootFormat,
                wayForward: "Use a Crystal build that reads this backup root, or back up into a new one.");
        }
    }

    /// <summary>Deletes manifests left half-written by a killed backup (design § 1): they were never sets.</summary>
    private void DeleteStaleTemporaries()
    {
        if (!Directory.Exists(SetsDirectory))
        {
            return;
        }

        foreach (var temp in Directory.EnumerateFiles(SetsDirectory, "*" + BackupManifest.TempSuffix))
        {
            File.Delete(temp);
        }

        var rootTemp = System.IO.Path.Combine(Path, RootFileName + BackupManifest.TempSuffix);
        if (File.Exists(rootTemp))
        {
            File.Delete(rootTemp);
        }
    }

    private sealed record RootFile
    {
        [JsonPropertyName("format")] public string Format { get; init; } = string.Empty;
        [JsonPropertyName("created")] public string Created { get; init; } = string.Empty;
    }
}
