// Spec 010 design § 8 — the public types of backup and restore: options, a set's description, verify and purge results.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemD.Crystal;

/// <summary>Options of <see cref="CrystalStore.BackupAsync"/> (spec 010 R1.7).</summary>
public sealed class BackupOptions
{
    /// <summary>A label recorded in the set, for the product's own use (for example "nightly").</summary>
    public string? Label { get; init; }

    /// <summary>The copy rate limit in bytes per second; null for none. It bounds the copy, not the flush before it.</summary>
    public long? RateLimitBytesPerSecond { get; init; }

    /// <summary>The number of background copy threads per database (default 2, at least 1).</summary>
    public int BackgroundOperations { get; init; } = 2;
}

/// <summary>Options of <see cref="CrystalStore.RestoreAsync"/> (spec 010 R6).</summary>
public sealed class RestoreOptions
{
    /// <summary>The set's databases to restore, by their names in the set; null for all of them (R6.7).</summary>
    public IReadOnlyCollection<string>? Databases { get; init; }

    /// <summary>A name in the set mapped to the name the database gets in this store (R6.3); unmapped names are kept.</summary>
    public IReadOnlyDictionary<string, string>? Rename { get; init; }

    /// <summary>
    /// Restore as a copy (R6.5): each restored database gets a new UID, so it can sit beside the database it was backed up
    /// from. This is the only time a database's identity record changes.
    /// </summary>
    public bool NewIdentity { get; init; }

    /// <summary>
    /// The replicated flag of every restored database; null keeps each one's flag as the set records it (R6.3). Restoring a
    /// replicated database needs this node to be the primary (spec 010 R11.3); <c>false</c> restores on any node.
    /// </summary>
    public bool? Replicated { get; init; }
}

/// <summary>
/// A complete backup set (spec 010 R4.2): one backup of each database live when it began, each captured at its own moment
/// (R1.4), not one moment across databases.
/// </summary>
/// <param name="SetId">The set's id: its start time in UTC, then random hex; ids sort by time.</param>
/// <param name="Label">The label the backup was given, if any.</param>
/// <param name="SourceNodeId">The <c>node.id</c> of the store the set was taken from (R2.3).</param>
/// <param name="Started">When the backup began.</param>
/// <param name="Completed">When its manifest was written: from then on the set exists.</param>
/// <param name="Databases">The databases in the set, in id order at the source.</param>
/// <param name="Omitted">The databases live at the start but not in the set (dropped during the backup).</param>
public sealed record BackupSetInfo(
    string SetId,
    string? Label,
    string SourceNodeId,
    DateTimeOffset Started,
    DateTimeOffset Completed,
    IReadOnlyList<BackupDatabaseInfo> Databases,
    IReadOnlyList<OmittedDatabase> Omitted);

/// <summary>One database in a backup set (spec 010 R4.2).</summary>
/// <param name="Name">Its name at the source when the backup began.</param>
/// <param name="Uid">Its UID.</param>
/// <param name="Replicated">Whether it was replicated at the source.</param>
/// <param name="Created">When it was created.</param>
/// <param name="Sequence">Every commit at or below this sequence is in the backup; later ones may be too (R1.5).</param>
/// <param name="Bytes">The size of its backup, shared files included.</param>
/// <param name="Files">The number of files of its backup, shared files included.</param>
public sealed record BackupDatabaseInfo(string Name, string Uid, bool Replicated, DateTimeOffset Created, long Sequence, long Bytes, int Files);

/// <summary>A database read at a backup's start but not in its set (spec 010 R3.2).</summary>
/// <param name="Name">Its name when the backup began.</param>
/// <param name="Uid">Its UID.</param>
/// <param name="Reason">
/// Why it is not in the set: <c>dropped</c> (dropped during the backup), or <c>replaced</c> (on a standby, re-installed
/// from snapshots under every attempt to copy it; the next backup takes it).
/// </param>
public sealed record OmittedDatabase(string Name, string Uid, string Reason);

/// <summary>What a verify found (spec 010 R5.4). Damage is reported here, not thrown.</summary>
/// <param name="SetId">The set verified.</param>
/// <param name="Deep">Whether every byte was read (deep verify) or only presence and sizes (quick verify).</param>
/// <param name="Databases">Each database's result.</param>
public sealed record BackupVerifyResult(string SetId, bool Deep, IReadOnlyList<DatabaseVerifyResult> Databases)
{
    /// <summary>Whether every database verified.</summary>
    public bool Ok => Databases.All(d => d.Ok);
}

/// <summary>One database's verify result (spec 010 R5.4).</summary>
/// <param name="Name">Its name in the set.</param>
/// <param name="Uid">Its UID.</param>
/// <param name="Ok">Whether it verified.</param>
/// <param name="Problem">What was wrong, when not <paramref name="Ok"/>.</param>
public sealed record DatabaseVerifyResult(string Name, string Uid, bool Ok, string? Problem);

/// <summary>What a purge did (spec 010 R5.5).</summary>
/// <param name="SetsKept">The sets that remain.</param>
/// <param name="SetsDeleted">The manifests deleted.</param>
/// <param name="EnginesDeleted">The engine folders deleted, of databases no kept set includes.</param>
public sealed record BackupPurgeResult(int SetsKept, int SetsDeleted, int EnginesDeleted);
