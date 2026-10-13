using Microsoft.Extensions.Logging;

namespace SystemD.Crystal;

/// <summary>
/// Every event Crystal logs, with a stable id an operator can filter and alert on across versions (spec 007 R3.1).
/// Ids are grouped by area and never reused: 1000s the store, 1100s the registry, 1200s startup recovery, 1300s
/// databases, 1400s boxes, 1500s durability, 1600s storage, 1700s backup and restore (spec 010), 2000s replication (spec 003). Messages never contain a key,
/// a value or a credential (R3.4).
/// </summary>
public static class CrystalLogEvents
{
    /// <summary>Information: the store opened (data directory, node id, durability).</summary>
    public static readonly EventId StoreOpened = new(1001, nameof(StoreOpened));

    /// <summary>Information: the store closed.</summary>
    public static readonly EventId StoreClosed = new(1002, nameof(StoreClosed));

    /// <summary>Error: a store, registry or database was refused on open: its format, or a damaged catalogue.</summary>
    public static readonly EventId FormatRefused = new(1003, nameof(FormatRefused));

    /// <summary>Information: <c>CrystalStore.Reset</c> deleted a folder.</summary>
    public static readonly EventId ResetDeletedFolder = new(1004, nameof(ResetDeletedFolder));

    /// <summary>Critical: a registry write failed; database changes are refused until the store is opened again.</summary>
    public static readonly EventId RegistryFailed = new(1101, nameof(RegistryFailed));

    /// <summary>Information: startup recovery deleted the folder of a dropped database.</summary>
    public static readonly EventId RecoveryDeletedDroppedFolder = new(1201, nameof(RecoveryDeletedDroppedFolder));

    /// <summary>Error: a registered database's folder is missing; opening it will be refused.</summary>
    public static readonly EventId RecoveryFolderMissing = new(1202, nameof(RecoveryFolderMissing));

    /// <summary>Information: startup recovery removed a folder left by an interrupted create.</summary>
    public static readonly EventId RecoveryRemovedUnregisteredFolder = new(1203, nameof(RecoveryRemovedUnregisteredFolder));

    /// <summary>Warning: a folder with an id the allocator has not issued was left untouched.</summary>
    public static readonly EventId RecoveryLeftFutureFolder = new(1204, nameof(RecoveryLeftFutureFolder));

    /// <summary>Warning: an entry Crystal does not recognise was left untouched in the data directory.</summary>
    public static readonly EventId RecoveryLeftUnrecognisedEntry = new(1205, nameof(RecoveryLeftUnrecognisedEntry));

    /// <summary>Information: a database was created.</summary>
    public static readonly EventId DatabaseCreated = new(1301, nameof(DatabaseCreated));

    /// <summary>Information: a database was renamed.</summary>
    public static readonly EventId DatabaseRenamed = new(1302, nameof(DatabaseRenamed));

    /// <summary>Information: two databases swapped names.</summary>
    public static readonly EventId DatabasesSwapped = new(1303, nameof(DatabasesSwapped));

    /// <summary>Information: a database was dropped.</summary>
    public static readonly EventId DatabaseDropped = new(1304, nameof(DatabaseDropped));

    /// <summary>Warning: a database's <c>name</c> file could not be rewritten; the next open repairs it.</summary>
    public static readonly EventId NameFileNotWritten = new(1305, nameof(NameFileNotWritten));

    /// <summary>Warning: a dropped database's folder could not be deleted; the next open deletes it.</summary>
    public static readonly EventId DroppedFolderNotDeleted = new(1306, nameof(DroppedFolderNotDeleted));

    /// <summary>Warning: the folder of a failed create could not be removed; the next open removes it.</summary>
    public static readonly EventId FailedCreateFolderNotRemoved = new(1307, nameof(FailedCreateFolderNotRemoved));

    /// <summary>Warning: closing a database is waiting for calls in progress (an unfinished scan, usually).</summary>
    public static readonly EventId CloseWaitingForCalls = new(1308, nameof(CloseWaitingForCalls));

    /// <summary>Warning: a database's <c>name</c> file disagreed with the registry and was rewritten.</summary>
    public static readonly EventId NameFileRepaired = new(1309, nameof(NameFileRepaired));

    /// <summary>
    /// Debug: the registry's copy of a database's creation time was set to the database's own (spec 009 R2.3), as on a
    /// standby after a bootstrap.
    /// </summary>
    public static readonly EventId DatabaseCreatedTimeCorrected = new(1310, nameof(DatabaseCreatedTimeCorrected));

    /// <summary>Debug: a box was created.</summary>
    public static readonly EventId BoxCreated = new(1401, nameof(BoxCreated));

    /// <summary>Debug: a box was renamed.</summary>
    public static readonly EventId BoxRenamed = new(1402, nameof(BoxRenamed));

    /// <summary>Debug: two boxes swapped names.</summary>
    public static readonly EventId BoxesSwapped = new(1403, nameof(BoxesSwapped));

    /// <summary>Debug: a box was dropped.</summary>
    public static readonly EventId BoxDropped = new(1404, nameof(BoxDropped));

    /// <summary>Critical: a write or WAL sync failed; the database refuses writes until the store is opened again.</summary>
    public static readonly EventId DatabaseFailed = new(1501, nameof(DatabaseFailed));

    /// <summary>Warning: a commit took longer than <c>CrystalOptions.SlowCommitThreshold</c>.</summary>
    public static readonly EventId SlowCommit = new(1502, nameof(SlowCommit));

    /// <summary>Warning: RocksDB stopped or slowed writes in a database (flushes or compaction are behind).</summary>
    public static readonly EventId WriteStallStarted = new(1601, nameof(WriteStallStarted));

    /// <summary>Information: a write stall or slowdown ended.</summary>
    public static readonly EventId WriteStallEnded = new(1602, nameof(WriteStallEnded));

    // ---- 1700s: backup and restore (spec 010 R7.1) ----------------------------

    /// <summary>Information: a backup began.</summary>
    public static readonly EventId BackupStarted = new(1701, nameof(BackupStarted));

    /// <summary>Information: a backup completed; its set exists.</summary>
    public static readonly EventId BackupCompleted = new(1702, nameof(BackupCompleted));

    /// <summary>Error: a backup failed or was cancelled; no set was written.</summary>
    public static readonly EventId BackupFailed = new(1703, nameof(BackupFailed));

    /// <summary>Warning: a database live when the backup began is not in its set (dropped during it).</summary>
    public static readonly EventId BackupDatabaseOmitted = new(1704, nameof(BackupDatabaseOmitted));

    /// <summary>Information: a restore began.</summary>
    public static readonly EventId RestoreStarted = new(1711, nameof(RestoreStarted));

    /// <summary>Information: a restore completed; its databases are registered.</summary>
    public static readonly EventId RestoreCompleted = new(1712, nameof(RestoreCompleted));

    /// <summary>Error: a restore failed; nothing was registered.</summary>
    public static readonly EventId RestoreFailed = new(1713, nameof(RestoreFailed));

    /// <summary>Error: a verify found a damaged database backup.</summary>
    public static readonly EventId BackupVerifyFailed = new(1721, nameof(BackupVerifyFailed));

    /// <summary>Information: a purge completed.</summary>
    public static readonly EventId BackupPurged = new(1731, nameof(BackupPurged));

    // ---- 2000s: replication (spec 003 R13.3) ----------------------------------

    /// <summary>Information: this node promoted to primary at a new epoch.</summary>
    public static readonly EventId ReplicationPromoted = new(2001, nameof(ReplicationPromoted));

    /// <summary>Information: this node demoted from primary to standby.</summary>
    public static readonly EventId ReplicationDemoted = new(2002, nameof(ReplicationDemoted));

    /// <summary>Warning: this node fenced itself read-only.</summary>
    public static readonly EventId ReplicationFenced = new(2003, nameof(ReplicationFenced));

    /// <summary>Information: this node adopted a higher epoch.</summary>
    public static readonly EventId ReplicationEpochChanged = new(2004, nameof(ReplicationEpochChanged));

    /// <summary>Warning: a standby slot is lagging past a fraction of the cap.</summary>
    public static readonly EventId ReplicationSlotLagging = new(2005, nameof(ReplicationSlotLagging));

    /// <summary>Warning: a standby slot was dropped (lag cap or staleness).</summary>
    public static readonly EventId ReplicationSlotDropped = new(2006, nameof(ReplicationSlotDropped));

    /// <summary>Warning: a database reported a gap and must re-bootstrap.</summary>
    public static readonly EventId ReplicationGap = new(2007, nameof(ReplicationGap));

    /// <summary>Warning: a database diverged and must re-bootstrap.</summary>
    public static readonly EventId ReplicationDivergence = new(2008, nameof(ReplicationDivergence));

    /// <summary>Error: a peer's storage format or layout did not match; nothing was served or applied.</summary>
    public static readonly EventId ReplicationFormatRefused = new(2009, nameof(ReplicationFormatRefused));

    /// <summary>Error: two primaries at the same epoch were detected (a conflict for the product to resolve).</summary>
    public static readonly EventId ReplicationConflict = new(2010, nameof(ReplicationConflict));

    /// <summary>Information: a replication connection to or from a peer was established.</summary>
    public static readonly EventId ReplicationConnected = new(2011, nameof(ReplicationConnected));

    /// <summary>Information: a replication connection was lost.</summary>
    public static readonly EventId ReplicationDisconnected = new(2012, nameof(ReplicationDisconnected));

    /// <summary>Warning: this node was promoted while some of its databases were marked for re-bootstrap (spec 011 R2.4).</summary>
    public static readonly EventId ReplicationPromotedWithPendingResync = new(2013, nameof(ReplicationPromotedWithPendingResync));

    /// <summary>Error: a handler of <c>CrystalReplication.Events</c> threw; the events after it are still delivered (spec 011 R5.3).</summary>
    public static readonly EventId ReplicationEventHandlerFailed = new(2014, nameof(ReplicationEventHandlerFailed));
}
