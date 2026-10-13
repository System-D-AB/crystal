// Spec 010 R5; design § 6 — a backup root opened without a store: its sets listed, verified (quick: presence and sizes;
// deep: every byte, through a restore into a scratch folder) and purged to a retention limit. It holds the root's lock
// while open, so no backup, restore or other operation runs on the root meanwhile.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Backup;
using SystemD.Crystal.Observability;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>
/// A backup root opened on its own (spec 010 R5): list, verify and purge its sets. Holds the root's lock until disposed
/// (R2.4): a backup, restore or second <see cref="CrystalBackupRoot"/> on the same root is refused meanwhile, in this
/// process or another.
/// </summary>
public sealed class CrystalBackupRoot : IDisposable
{
    private readonly BackupRoot _root;
    private readonly ILogger? _logger;
    private int _disposed;

    private CrystalBackupRoot(BackupRoot root, ILogger? logger)
    {
        _root = root;
        _logger = logger;
    }

    /// <summary>The root's full path.</summary>
    public string Path => _root.Path;

    /// <summary>
    /// Opens the backup root at <paramref name="path"/> (an absolute path to a folder a backup was written to) and takes
    /// its lock. Throws <see cref="BackupDestinationException"/> if it is not a backup root, and
    /// <see cref="BackupRootLockedException"/> if another operation holds it.
    /// </summary>
    public static CrystalBackupRoot Open(string path, ILoggerFactory? loggerFactory = null) =>
        new(BackupRoot.OpenExisting(path), loggerFactory?.CreateLogger<CrystalBackupRoot>());

    /// <summary>Every complete set, oldest first (R5.1). A manifest that fails its checksum is not a set: see <see cref="DamagedManifests"/>.</summary>
    public IReadOnlyList<BackupSetInfo> ListSets()
    {
        ThrowIfDisposed();
        return [.. ReadAll().Where(m => m.Manifest is not null).Select(m => m.Manifest!.ToInfo())];
    }

    /// <summary>The paths of manifests that are unreadable or fail their checksum (R5.1): never used, never listed as sets.</summary>
    public IReadOnlyList<string> DamagedManifests()
    {
        ThrowIfDisposed();
        return [.. ReadAll().Where(m => m.Manifest is null).Select(m => m.Path)];
    }

    /// <summary>
    /// Verifies set <paramref name="setId"/> (R5.2–R5.4). <b>Quick</b> (<paramref name="deep"/> false): the manifest's
    /// checksum, and each database's backup present with every file at its recorded size; no byte of data is read.
    /// <b>Deep</b>: each database is also restored into a scratch folder (<paramref name="scratchDirectory"/>, or
    /// <c>scratch/</c> under the root), opened read-only, every file's checksums verified by RocksDB, and its identity
    /// checked against the manifest; the scratch folder is deleted after each. Deep verify is the only check that reads
    /// every byte. Damage is returned in the result, not thrown; this throws only if the verify cannot run (an unknown or
    /// damaged set). <b>Yields:</b> the native work runs on a thread-pool thread (R8.2).
    /// </summary>
    public Task<BackupVerifyResult> VerifyAsync(string setId, bool deep, string? scratchDirectory = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(setId);
        if (scratchDirectory is not null && !System.IO.Path.IsPathFullyQualified(scratchDirectory))
        {
            throw new ArgumentException("The scratch directory must be an absolute path.", nameof(scratchDirectory));
        }

        return Task.Run(() => Verify(setId, deep, scratchDirectory, cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// Keeps the newest <paramref name="keepLast"/> sets (at least 1) and removes the rest (R5.5): first their manifests,
    /// then, in each engine, the backups older than the oldest a kept set references, then the engines no kept set
    /// references. A crash part-way leaves only data no set references, never a set with missing data. Damaged manifests
    /// are never used, so they keep nothing: an engine only a damaged manifest references is deleted with the rest. The
    /// damaged manifest itself is left in place for the operator. Synchronous native work.
    /// </summary>
    public BackupPurgeResult Purge(int keepLast)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(keepLast, 1);

        // 1. The sets to keep, newest by completion; the other manifests deleted first.
        var sets = ReadAll().Where(m => m.Manifest is not null).OrderBy(m => m.Manifest!.ToInfo().Completed).ThenBy(m => m.Manifest!.Set, StringComparer.Ordinal).ToList();
        var kept = sets.Skip(Math.Max(0, sets.Count - keepLast)).ToList();
        var deleted = sets.Take(sets.Count - kept.Count).ToList();
        foreach (var set in deleted)
        {
            DurableFile.Delete(set.Path);
        }

        // 2, 3. Each engine: none of its backups referenced, deleted; otherwise everything below the oldest referenced.
        var oldestReferenced = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var database in kept.SelectMany(s => s.Manifest!.Databases))
        {
            oldestReferenced[database.Uid] = oldestReferenced.TryGetValue(database.Uid, out var id) ? Math.Min(id, database.Backup) : database.Backup;
        }

        var enginesDeleted = 0;
        if (Directory.Exists(_root.EnginesDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_root.EnginesDirectory))
            {
                var uid = System.IO.Path.GetFileName(directory);
                if (!oldestReferenced.TryGetValue(uid, out var min))
                {
                    StoreEngine.DeleteDirectoryWithRetries(directory);
                    enginesDeleted++;
                    continue;
                }

                using var engine = BackupEngineHandle.Open(directory);
                var keep = engine.List().Count(e => e.Id >= min);
                engine.Purge((uint)keep);
            }
        }

        var result = new BackupPurgeResult(kept.Count, deleted.Count, enginesDeleted);
        if (_logger is not null)
        {
            CrystalLog.BackupPurged(_logger, Path, result.SetsKept, result.SetsDeleted, result.EnginesDeleted);
        }

        return result;
    }

    /// <summary>Releases the root's lock.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _root.Dispose();
        }
    }

    private BackupVerifyResult Verify(string setId, bool deep, string? scratchDirectory, CancellationToken cancellationToken)
    {
        var path = _root.ManifestPath(setId);
        if (!_root.HasSet(setId))
        {
            throw new BackupSetNotFoundException(setId, $"The backup root '{Path}' has no set '{setId}'.");
        }

        var manifest = BackupManifest.Read(path); // a damaged manifest: the verify cannot run (R5.4)
        var results = new List<DatabaseVerifyResult>(manifest.Databases.Count);
        foreach (var database in manifest.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var problem = RestoreRunner.QuickVerifyProblem(_root, database);
            if (problem is null && deep)
            {
                problem = BackupVerification.DeepVerify(
                    _root.EngineDirectory(database.Uid),
                    database.Backup,
                    database.Uid,
                    database.Name,
                    scratchDirectory ?? _root.ScratchDirectory,
                    new CrystalOptions().Clock); // the default clock; a verify records no time
            }

            results.Add(new DatabaseVerifyResult(database.Name, database.Uid, problem is null, problem));
            if (problem is not null && _logger is not null)
            {
                CrystalLog.BackupVerifyFailed(_logger, Path, setId, database.Name, problem);
            }
        }

        return new BackupVerifyResult(setId, deep, results);
    }

    private List<(string Path, BackupManifest? Manifest)> ReadAll()
    {
        var all = new List<(string, BackupManifest?)>();
        foreach (var path in _root.ManifestFiles())
        {
            try
            {
                all.Add((path, BackupManifest.Read(path)));
            }
            catch (BackupDamagedException)
            {
                all.Add((path, null));
            }
        }

        return all;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
