// Spec 010 R1, R3, R4; design § 4, § 5 — one backup: every live database, one at a time, each into its own
// BackupEngine under the root, then the set's manifest, written last. The store keeps serving: the store gate is held
// only to read the list of databases; a drop or a close stops a copy rather than waiting for it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using SystemD.Crystal.Observability;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Backup;

/// <summary>A running backup's progress (spec 010 R7.3), read by <c>Status()</c> while it runs.</summary>
internal sealed class BackupProgress(string destination)
{
    private int _done;
    private int _total;
    private long _bytes;
    private long _files;

    /// <summary>The backup root.</summary>
    public string Destination { get; } = destination;

    public int DatabasesDone => Volatile.Read(ref _done);
    public int DatabasesTotal => Volatile.Read(ref _total);
    public long BytesCopied => Interlocked.Read(ref _bytes);
    public long FilesCopied => Interlocked.Read(ref _files);

    public void SetTotal(int total) => Volatile.Write(ref _total, total);
    public void DatabaseDone() => Interlocked.Increment(ref _done);
    public void AddCopied(long bytes, long files)
    {
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _files, files);
    }
}

/// <summary>Runs one backup (spec 010 design § 4). Synchronous: <see cref="CrystalStore.BackupAsync"/> runs it on the pool.</summary>
internal static class BackupRunner
{
    /// <summary>The reason recorded for a database dropped during the backup (R3.2).</summary>
    public const string DroppedReason = "dropped";

    /// <summary>
    /// The reason recorded for a database closed under its copy on every attempt (a standby re-installing it from
    /// snapshots again and again): the store and the database are live, but no copy of it completed.
    /// </summary>
    public const string ReplacedReason = "replaced";

    /// <summary>How many times a database replaced under its copy is tried (review S1).</summary>
    private const int Attempts = 3;

    /// <summary>
    /// Backs up every database live at the start into the root at <paramref name="root"/> (a full, checked path).
    /// <paramref name="storeClosing"/> is cancelled by the store's dispose: the backup then fails with
    /// <see cref="ObjectDisposedException"/> (R3.4); <paramref name="cancellationToken"/> fails it with
    /// <see cref="OperationCanceledException"/> (R4.4). Either way no set is written.
    /// </summary>
    public static BackupSetInfo Run(
        CrystalStore store,
        string root,
        BackupOptions options,
        BackupProgress progress,
        CancellationToken storeClosing,
        CancellationToken cancellationToken)
    {
        var engine = store.Engine;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(storeClosing, cancellationToken);
        var token = linked.Token;
        try
        {
            using var backupRoot = BackupRoot.OpenOrCreate(root, engine.Clock.GetUtcNow());
            var startTimestamp = engine.Clock.GetTimestamp();
            var started = engine.Clock.GetUtcNow();
            var setId = NewSetId(started);

            // Step 4: the members, under the store gate for a moment (R3.1).
            var members = engine.LiveDatabasesForBackup();
            progress.SetTotal(members.Count);
            if (engine.Logger is { } logger)
            {
                CrystalLog.BackupStarted(logger, root, setId, members.Count);
            }

            var databases = new List<BackupManifest.ManifestDatabase>(members.Count);
            var omitted = new List<BackupManifest.ManifestOmitted>();
            foreach (var member in members)
            {
                ThrowIfStopped(storeClosing, cancellationToken);
                var (outcome, copied) = (Outcome.Replaced, (BackupManifest.ManifestDatabase?)null);
                for (var attempt = 0; attempt < Attempts && outcome == Outcome.Replaced; attempt++)
                {
                    (outcome, copied) = BackupOne(store, backupRoot, member, setId, options, progress, storeClosing, token);
                }

                if (copied is not null)
                {
                    databases.Add(copied);
                }
                else
                {
                    var reason = outcome == Outcome.Dropped ? DroppedReason : ReplacedReason;
                    omitted.Add(new BackupManifest.ManifestOmitted { Name = member.Name, Uid = member.Uid, Reason = reason });
                    if (engine.Logger is { } omittedLogger)
                    {
                        CrystalLog.BackupDatabaseOmitted(omittedLogger, root, setId, member.Name, member.Uid, reason);
                    }
                }

                ThrowIfStopped(storeClosing, cancellationToken);
                progress.DatabaseDone();
            }

            // Step 6: the manifest, last (R4.1). From its rename on, the set exists.
            var manifest = new BackupManifest
            {
                Set = setId,
                Label = options.Label,
                Node = store.NodeId,
                Started = BackupManifest.FormatTime(started),
                Completed = BackupManifest.FormatTime(engine.Clock.GetUtcNow()),
                Databases = databases,
                Omitted = omitted,
            };
            ThrowIfStopped(storeClosing, cancellationToken);
            manifest.WriteTo(backupRoot.SetsDirectory, engine.RunHook);
            var info = manifest.ToInfo();
            engine.Metrics?.BackupCompleted(startTimestamp, progress.BytesCopied, progress.FilesCopied, info.Completed);
            if (engine.Logger is { } completedLogger)
            {
                CrystalLog.BackupCompleted(
                    completedLogger, root, setId, databases.Count, omitted.Count, progress.BytesCopied,
                    engine.Clock.GetElapsedTime(startTimestamp).TotalMilliseconds);
            }

            return info;
        }
        catch (OperationCanceledException) when (storeClosing.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(CrystalStore), "The store was closed during the backup; no set was written (spec 010 R3.4).");
        }
    }

    /// <summary>How one database's backup ended.</summary>
    private enum Outcome
    {
        /// <summary>Copied: the manifest entry is set.</summary>
        Copied,

        /// <summary>Dropped before or during its copy (R3.2): omitted.</summary>
        Dropped,

        /// <summary>
        /// Closed under the copy, the store still open and the database still live: a standby's snapshot install replaced
        /// it (review S1). The caller tries again on the database as it is now.
        /// </summary>
        Replaced,
    }

    /// <summary>
    /// Backs up one database (design § 4 step 5): its manifest entry, or how it ended without one. A store close throws
    /// <see cref="ObjectDisposedException"/>, the token <see cref="OperationCanceledException"/>.
    /// </summary>
    private static (Outcome Outcome, BackupManifest.ManifestDatabase? Database) BackupOne(
        CrystalStore store,
        BackupRoot root,
        StoreEngine.BackupMember member,
        string setId,
        BackupOptions options,
        BackupProgress progress,
        CancellationToken storeClosing,
        CancellationToken token)
    {
        var engine = store.Engine;

        // The engine is opened before the database is entered: opening it reads its meta files and removes a partial
        // backup, which a drop must not wait for (review M5).
        var engineDirectory = root.EngineDirectory(member.Uid);
        var before = DirectoryStats(engineDirectory);
        using var backupEngine = BackupEngineHandle.Open(
            engineDirectory,
            options.BackgroundOperations,
            (ulong)(options.RateLimitBytesPerSecond ?? 0));

        // a. Enter it, opening it if closed (R3.3). Dropped meanwhile: omitted.
        if (engine.OpenForBackup(store, member.Id) is not { } opened)
        {
            return (Outcome.Dropped, null);
        }

        var (slot, openedHere) = opened;
        try
        {
            using var entered = slot.TryEnter();
            if (entered is null)
            {
                return (Closing(slot, storeClosing), null);
            }

            engine.RunHook("backup.entered");
            uint backupId;
            long sequence;

            // b. Registered on the slot, so a drop, a close or an install stops it (design § 5).
            if (!slot.BeginBackup(backupEngine))
            {
                return (Closing(slot, storeClosing), null);
            }

            try
            {
                using var stopOnCancel = token.Register(backupEngine.Stop);

                // c. The sequence, read before the copy's flush: every commit at or below it is in the backup (R1.5).
                sequence = (long)slot.LatestSequence;
                engine.RunHook("backup.copying");

                // d. The copy, flushing first (R1.3), tagged with the set and the UID (R4.3).
                backupId = backupEngine.Create(slot.Instance, Metadata(setId, member.Uid));
            }
            catch (RocksException) when (slot.IsClosing || token.IsCancellationRequested)
            {
                // f. Stopped: by the token (thrown), a store close (thrown), a drop (omitted) or an install (retried).
                token.ThrowIfCancellationRequested();
                return (Closing(slot, storeClosing), null);
            }
            finally
            {
                slot.EndBackup();
            }

            // g. The engine's facts about the new backup.
            var entry = backupEngine.List().Single(e => e.Id == backupId);
            var after = DirectoryStats(engineDirectory);
            progress.AddCopied(Math.Max(0, after.Bytes - before.Bytes), Math.Max(0, after.Files - before.Files));
            return (Outcome.Copied, new BackupManifest.ManifestDatabase
            {
                Name = member.Name,
                Uid = member.Uid,
                Replicated = member.Replicated,
                Created = BackupManifest.FormatTime(new DateTimeOffset(member.CreatedTicks, TimeSpan.Zero)),
                Backup = backupId,
                Sequence = sequence,
                Bytes = (long)entry.Size,
                Files = (int)entry.Files,
            });
        }
        finally
        {
            // e. Closed again if the backup opened it and nobody took it meanwhile (R3.3).
            if (openedHere)
            {
                engine.CloseOpenedForBackup(slot);
            }
        }
    }

    /// <summary>
    /// Why a slot was found closing: the store closing throws; a drop means omitted; anything else (a standby's snapshot
    /// install) means the database was replaced under the copy, and is tried again.
    /// </summary>
    private static Outcome Closing(DatabaseSlot slot, CancellationToken storeClosing)
    {
        if (storeClosing.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(CrystalStore), "The store was closed during the backup; no set was written (spec 010 R3.4).");
        }

        return slot.WasDropped ? Outcome.Dropped : Outcome.Replaced;
    }

    private static void ThrowIfStopped(CancellationToken storeClosing, CancellationToken cancellationToken)
    {
        if (storeClosing.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(CrystalStore), "The store was closed during the backup; no set was written (spec 010 R3.4).");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>A set id (design § 1): the start time in UTC, then 8 random hex digits; ids sort by time.</summary>
    internal static string NewSetId(DateTimeOffset started) =>
        started.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture)
        + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static byte[] Metadata(string setId, string uid) =>
        System.Text.Encoding.UTF8.GetBytes($"{{\"set\":\"{setId}\",\"uid\":\"{uid}\"}}");

    /// <summary>The bytes and files the engine folder holds: their growth over a backup is what it copied (design § 9).</summary>
    private static (long Bytes, long Files) DirectoryStats(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        long bytes = 0, files = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            bytes += file.Length;
            files++;
        }

        return (bytes, files);
    }
}
