// Spec 010 R6, R11; design § 7 — a restore: a backup set's databases attached to this store with new ids, all or
// none. Each is restored into a staging folder in the data directory, its identity checked (and, for a copy, replaced)
// before it moves into db-{id}; only then are all of them registered, in one synced registry batch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SystemD.Crystal.Encoding;
using SystemD.Crystal.Observability;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Backup;

/// <summary>Runs one restore (spec 010 design § 7). Synchronous: <see cref="CrystalStore.RestoreAsync"/> runs it on the pool.</summary>
internal static class RestoreRunner
{
    private readonly record struct Chosen(BackupManifest.ManifestDatabase Database, StoreEngine.RestoreTarget Target);

    /// <summary>
    /// Restores set <paramref name="setId"/> of the root <paramref name="source"/> into <paramref name="store"/>. Throws
    /// before anything is written for a missing or damaged set, a format this build cannot read, a database that fails
    /// quick verify, a taken name or UID, or (for replicated databases) a node that is not the primary (R11.3).
    /// </summary>
    public static IReadOnlyList<DatabaseInfo> Run(
        CrystalStore store,
        string source,
        string setId,
        RestoreOptions options,
        CancellationToken storeClosing,
        CancellationToken cancellationToken)
    {
        var engine = store.Engine;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(storeClosing, cancellationToken);
        var token = linked.Token;
        try
        {
            // Step 1: the root, locked; the manifest, checked (R6.2).
            using var root = BackupRoot.OpenExisting(source);
            if (engine.Logger is { } logger)
            {
                CrystalLog.RestoreStarted(logger, root.Path, setId);
            }

            var manifest = ReadSet(root, setId);

            // Step 2: every format it records, one this build reads (R6.2).
            CheckFormats(manifest, root.ManifestPath(setId));

            // Step 3: the databases chosen and named; each one quick-verified (R6.2, R5.2).
            var chosen = Choose(manifest, options);
            foreach (var c in chosen)
            {
                token.ThrowIfCancellationRequested();
                QuickVerify(root, c.Database);
            }

            // A restore of replicated databases changes the primary's set (R11.3): admitted through the write gate, so
            // refused on a standby, a fenced node or during a handover, and a demotion waits for it.
            using var admitted = chosen.Any(c => c.Target.Replicated)
                ? CrystalStore.AdmitIfPrimary(engine.WriteGate, $"restore replicated databases from set '{setId}'")
                : default;

            // Step 4: names and UIDs checked, the allocator advanced (R6.4, R11.5).
            token.ThrowIfCancellationRequested();
            var ids = engine.AllocateForRestore([.. chosen.Select(c => c.Target)]);

            // Step 5: each restored, checked and moved into place. Step 6: all registered at once (R6.6).
            var made = new List<string>();
            var registering = false;
            try
            {
                var restored = new List<StoreEngine.RestoredDatabase>(chosen.Count);
                for (var i = 0; i < chosen.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var (database, target) = chosen[i];
                    var staging = engine.RestoreStagingPath();
                    made.Add(staging);
                    using (var backupEngine = BackupEngineHandle.Open(root.EngineDirectory(database.Uid)))
                    {
                        backupEngine.Restore(database.Backup, staging);
                    }

                    var identity = engine.CheckRestoredFolder(
                        staging,
                        database.Uid,
                        options.NewIdentity ? target.Uid : null,
                        $"database '{database.Name}' of backup set '{setId}'");
                    made.Add(engine.DatabasePath(ids[i])); // before the move: a failure during it may leave either
                    engine.MoveRestoredIntoPlace(staging, ids[i], target.Name);
                    restored.Add(new StoreEngine.RestoredDatabase(ids[i], target.Name, target.Uid, target.Replicated, identity.CreatedTicks));
                }

                token.ThrowIfCancellationRequested();
                engine.RegisterRestored(restored, ref registering);
                if (engine.Logger is { } completedLogger)
                {
                    CrystalLog.RestoreCompleted(completedLogger, root.Path, setId, restored.Count);
                }

                return [.. restored.Select(r => new DatabaseInfo(r.Id, r.Name, r.Uid, r.Replicated, new DateTimeOffset(r.CreatedTicks, TimeSpan.Zero)))];
            }
            catch when (!registering)
            {
                // Nothing names them: removed now, their ids skipped (ids are never reused). A crash here leaves them
                // to startup recovery, below the allocator and unregistered (005 addendum A2).
                foreach (var folder in made)
                {
                    engine.RemoveRestoredFolder(folder);
                }

                throw;
            }
        }
        catch (OperationCanceledException) when (storeClosing.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(CrystalStore), "The store was closed during the restore; nothing was registered.");
        }
    }

    private static BackupManifest ReadSet(BackupRoot root, string setId)
    {
        ArgumentException.ThrowIfNullOrEmpty(setId);
        var path = root.ManifestPath(setId);
        if (!root.HasSet(setId))
        {
            throw new BackupSetNotFoundException(
                setId,
                $"The backup root '{root.Path}' has no set '{setId}'. List its sets (CrystalBackupRoot.ListSets) and name one of them.");
        }

        return BackupManifest.Read(path);
    }

    private static void CheckFormats(BackupManifest manifest, string path)
    {
        var versions = manifest.Versions;
        const string WayForward = "Restore it with a Crystal build that reads it.";
        if (!string.Equals(versions.Store, StoreFormat.SupportedFormat, StringComparison.Ordinal))
        {
            throw new CrystalFormatException(
                $"The backup set '{path}' was taken from a store of format '{versions.Store}'; this build reads '{StoreFormat.SupportedFormat}'.",
                foundFormat: versions.Store,
                supportedFormat: StoreFormat.SupportedFormat,
                wayForward: WayForward);
        }

        if (versions.Catalogue != DatabaseContext.CatalogueFormat)
        {
            throw new CrystalFormatException(
                $"The backup set '{path}' holds catalogues of format {versions.Catalogue}; this build reads {DatabaseContext.CatalogueFormat}.",
                foundFormat: $"catalogue {versions.Catalogue}",
                supportedFormat: $"catalogue {DatabaseContext.CatalogueFormat}",
                wayForward: WayForward);
        }

        if (versions.Encoder != KeyEncoding.Version)
        {
            throw new CrystalFormatException(
                $"The backup set '{path}' holds keys of encoder version {versions.Encoder}; this build reads {KeyEncoding.Version}.",
                foundFormat: $"encoder {versions.Encoder}",
                supportedFormat: $"encoder {KeyEncoding.Version}",
                wayForward: WayForward);
        }
    }

    /// <summary>The set's databases to restore (R6.7), each with its name here (R6.3), UID (new for a copy) and flag.</summary>
    private static List<Chosen> Choose(BackupManifest manifest, RestoreOptions options)
    {
        var byName = manifest.Databases.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var names = options.Databases ?? [.. manifest.Databases.Select(d => d.Name)];
        if (names.Count == 0)
        {
            throw new ArgumentException("RestoreOptions.Databases names no database; leave it null to restore all of them.", nameof(options));
        }

        foreach (var from in options.Rename?.Keys ?? [])
        {
            if (!names.Contains(from, StringComparer.Ordinal))
            {
                throw new ArgumentException($"RestoreOptions.Rename maps '{from}', which this restore does not include.", nameof(options));
            }
        }

        var chosen = new List<Chosen>(names.Count);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (!byName.TryGetValue(name, out var database))
            {
                throw new ArgumentException(
                    $"The backup set '{manifest.Set}' has no database '{name}'" +
                    (manifest.Omitted.Any(o => o.Name == name) ? " (it was dropped during that backup)." : "."),
                    nameof(options));
            }

            var target = options.Rename is not null && options.Rename.TryGetValue(name, out var renamed) ? renamed : name;
            chosen.Add(new Chosen(
                database,
                new StoreEngine.RestoreTarget(
                    target,
                    options.NewIdentity ? DatabaseRecord.NewUid() : database.Uid,
                    options.Replicated ?? database.Replicated)));
        }

        return chosen;
    }

    /// <summary>Quick verify of one database's engine backup (R5.2): every file present at its recorded size.</summary>
    internal static string? QuickVerifyProblem(BackupRoot root, BackupManifest.ManifestDatabase database)
    {
        var directory = root.EngineDirectory(database.Uid);
        if (!Directory.Exists(directory))
        {
            return $"its engine folder '{directory}' is missing";
        }

        // Checked before the engine opens: the engine takes the sizes on disk at its open as the recorded ones.
        if (BackupVerification.CheckFiles(directory, database.Backup) is { } fileProblem)
        {
            return fileProblem;
        }

        try
        {
            using var engine = BackupEngineHandle.Open(directory);
            if (!engine.List().Any(e => e.Id == database.Backup))
            {
                return $"its engine has no backup {database.Backup}";
            }

            engine.Verify(database.Backup);
            return null;
        }
        catch (RocksException ex)
        {
            return ex.Message;
        }
    }

    private static void QuickVerify(BackupRoot root, BackupManifest.ManifestDatabase database)
    {
        if (QuickVerifyProblem(root, database) is { } problem)
        {
            var directory = root.EngineDirectory(database.Uid);
            throw new BackupDamagedException(
                directory,
                $"The backup of database '{database.Name}' ({database.Uid}) in '{root.Path}' is damaged: {problem}. Nothing " +
                "was restored. Restore from another set, or restore the other databases (RestoreOptions.Databases).");
        }
    }
}
