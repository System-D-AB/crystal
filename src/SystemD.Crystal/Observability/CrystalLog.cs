using System;
using Microsoft.Extensions.Logging;

namespace SystemD.Crystal.Observability;

/// <summary>
/// Every log message Crystal writes, source-generated so a disabled level costs nothing (spec 007 R3.2). Each uses its
/// id from <see cref="CrystalLogEvents"/>; the numbers here must match it (a test checks). No method takes a key or a
/// value (R3.4, an architecture test checks).
/// </summary>
internal static partial class CrystalLog
{
    [LoggerMessage(EventId = 1001, EventName = nameof(CrystalLogEvents.StoreOpened), Level = LogLevel.Information,
        Message = "Opened the Crystal store at '{DataDirectory}': node {NodeId}, durability {Durability}.")]
    public static partial void StoreOpened(ILogger logger, string dataDirectory, string nodeId, DurabilityMode durability);

    [LoggerMessage(EventId = 1002, EventName = nameof(CrystalLogEvents.StoreClosed), Level = LogLevel.Information,
        Message = "Closed the Crystal store at '{DataDirectory}': node {NodeId}, durability {Durability}.")]
    public static partial void StoreClosed(ILogger logger, string dataDirectory, string nodeId, DurabilityMode durability);

    [LoggerMessage(EventId = 1003, EventName = nameof(CrystalLogEvents.FormatRefused), Level = LogLevel.Error,
        Message = "Refused {What}: {Reason} Way forward: {WayForward}")]
    public static partial void FormatRefused(ILogger logger, string what, string reason, string wayForward);

    [LoggerMessage(EventId = 1004, EventName = nameof(CrystalLogEvents.ResetDeletedFolder), Level = LogLevel.Information,
        Message = "Reset deleted '{Path}'.")]
    public static partial void ResetDeletedFolder(ILogger logger, string path);

    [LoggerMessage(EventId = 1101, EventName = nameof(CrystalLogEvents.RegistryFailed), Level = LogLevel.Critical,
        Message = "{Registry} failed: a registry write failed. It refuses changes until the store is opened again.")]
    public static partial void RegistryFailed(ILogger logger, Exception cause, string registry);

    [LoggerMessage(EventId = 1201, EventName = nameof(CrystalLogEvents.RecoveryDeletedDroppedFolder), Level = LogLevel.Information,
        Message = "Deleted the folder of dropped database {Id} ('{Path}') (R8.3).")]
    public static partial void RecoveryDeletedDroppedFolder(ILogger logger, uint id, string path);

    [LoggerMessage(EventId = 1202, EventName = nameof(CrystalLogEvents.RecoveryFolderMissing), Level = LogLevel.Error,
        Message = "Database '{Name}' (id {Id}) is registered but its folder '{Path}' is missing; opening it will be refused.")]
    public static partial void RecoveryFolderMissing(ILogger logger, string name, uint id, string path);

    [LoggerMessage(EventId = 1203, EventName = nameof(CrystalLogEvents.RecoveryRemovedUnregisteredFolder), Level = LogLevel.Information,
        Message = "Removed unregistered database folder '{Path}', left by an interrupted create (R8.3).")]
    public static partial void RecoveryRemovedUnregisteredFolder(ILogger logger, string path);

    [LoggerMessage(EventId = 1204, EventName = nameof(CrystalLogEvents.RecoveryLeftFutureFolder), Level = LogLevel.Warning,
        Message = "Unregistered folder '{Path}' has id {Id}, which the allocator ({Next}) has not issued. Leaving it untouched (R8.3).")]
    public static partial void RecoveryLeftFutureFolder(ILogger logger, string path, uint id, ulong next);

    [LoggerMessage(EventId = 1205, EventName = nameof(CrystalLogEvents.RecoveryLeftUnrecognisedEntry), Level = LogLevel.Warning,
        Message = "Unrecognised entry '{Path}' in the Crystal store directory. Leaving it untouched (R8.3).")]
    public static partial void RecoveryLeftUnrecognisedEntry(ILogger logger, string path);

    [LoggerMessage(EventId = 1301, EventName = nameof(CrystalLogEvents.DatabaseCreated), Level = LogLevel.Information,
        Message = "Created database '{Name}' (id {Id}).")]
    public static partial void DatabaseCreated(ILogger logger, string name, uint id);

    [LoggerMessage(EventId = 1302, EventName = nameof(CrystalLogEvents.DatabaseRenamed), Level = LogLevel.Information,
        Message = "Renamed database {Id} from '{OldName}' to '{NewName}'.")]
    public static partial void DatabaseRenamed(ILogger logger, uint id, string oldName, string newName);

    [LoggerMessage(EventId = 1303, EventName = nameof(CrystalLogEvents.DatabasesSwapped), Level = LogLevel.Information,
        Message = "Swapped the names of databases {FirstId} and {SecondId}: now '{FirstName}' and '{SecondName}'.")]
    public static partial void DatabasesSwapped(ILogger logger, uint firstId, uint secondId, string firstName, string secondName);

    [LoggerMessage(EventId = 1304, EventName = nameof(CrystalLogEvents.DatabaseDropped), Level = LogLevel.Information,
        Message = "Dropped database '{Name}' (id {Id}).")]
    public static partial void DatabaseDropped(ILogger logger, string name, uint id);

    [LoggerMessage(EventId = 1305, EventName = nameof(CrystalLogEvents.NameFileNotWritten), Level = LogLevel.Warning,
        Message = "Could not rewrite the name file of database {Id}; the next open repairs it (R8.4).")]
    public static partial void NameFileNotWritten(ILogger logger, Exception cause, uint id);

    [LoggerMessage(EventId = 1306, EventName = nameof(CrystalLogEvents.DroppedFolderNotDeleted), Level = LogLevel.Warning,
        Message = "Could not delete the folder '{Path}' of a dropped database; the next open deletes it (R8.3).")]
    public static partial void DroppedFolderNotDeleted(ILogger logger, Exception cause, string path);

    [LoggerMessage(EventId = 1307, EventName = nameof(CrystalLogEvents.FailedCreateFolderNotRemoved), Level = LogLevel.Warning,
        Message = "Could not remove '{Path}' after a failed create; the next open removes it (R8.3).")]
    public static partial void FailedCreateFolderNotRemoved(ILogger logger, Exception cause, string path);

    [LoggerMessage(EventId = 1308, EventName = nameof(CrystalLogEvents.CloseWaitingForCalls), Level = LogLevel.Warning,
        Message = "Closing database {Id} is waiting for {Count} call(s) in progress; an unfinished scan enumeration holds it open.")]
    public static partial void CloseWaitingForCalls(ILogger logger, uint id, int count);

    [LoggerMessage(EventId = 1309, EventName = nameof(CrystalLogEvents.NameFileRepaired), Level = LogLevel.Warning,
        Message = "Repaired the name file of database {Id}: it now says '{Name}', as the registry does (R8.4).")]
    public static partial void NameFileRepaired(ILogger logger, uint id, string name);

    [LoggerMessage(EventId = 1310, EventName = nameof(CrystalLogEvents.DatabaseCreatedTimeCorrected), Level = LogLevel.Debug,
        Message = "Set the registry's creation time of database '{Name}' (id {Id}) to the database's own (spec 009 R2.3).")]
    public static partial void DatabaseCreatedTimeCorrected(ILogger logger, string name, uint id);

    [LoggerMessage(EventId = 1401, EventName = nameof(CrystalLogEvents.BoxCreated), Level = LogLevel.Debug,
        Message = "Created box '{Name}' (id {Id}) in {Database}.")]
    public static partial void BoxCreated(ILogger logger, string name, uint id, string database);

    [LoggerMessage(EventId = 1402, EventName = nameof(CrystalLogEvents.BoxRenamed), Level = LogLevel.Debug,
        Message = "Renamed box {Id} from '{OldName}' to '{NewName}' in {Database}.")]
    public static partial void BoxRenamed(ILogger logger, uint id, string oldName, string newName, string database);

    [LoggerMessage(EventId = 1403, EventName = nameof(CrystalLogEvents.BoxesSwapped), Level = LogLevel.Debug,
        Message = "Swapped the names of boxes {FirstId} and {SecondId} in {Database}.")]
    public static partial void BoxesSwapped(ILogger logger, uint firstId, uint secondId, string database);

    [LoggerMessage(EventId = 1404, EventName = nameof(CrystalLogEvents.BoxDropped), Level = LogLevel.Debug,
        Message = "Dropped box '{Name}' (id {Id}) in {Database}.")]
    public static partial void BoxDropped(ILogger logger, string name, uint id, string database);

    [LoggerMessage(EventId = 1501, EventName = nameof(CrystalLogEvents.DatabaseFailed), Level = LogLevel.Critical,
        Message = "{Database} failed: {What} failed. It refuses writes until the store is opened again.")]
    public static partial void DatabaseFailed(ILogger logger, Exception cause, string database, string what);

    [LoggerMessage(EventId = 1502, EventName = nameof(CrystalLogEvents.SlowCommit), Level = LogLevel.Warning,
        Message = "A {Durability} commit of {Operations} operation(s) in {Database} took {Milliseconds} ms.")]
    public static partial void SlowCommit(ILogger logger, DurabilityMode durability, int operations, string database, double milliseconds);

    [LoggerMessage(EventId = 1601, EventName = nameof(CrystalLogEvents.WriteStallStarted), Level = LogLevel.Warning,
        Message = "RocksDB {State} writes in {Database}: flushes or compaction are behind.")]
    public static partial void WriteStallStarted(ILogger logger, string state, string database);

    [LoggerMessage(EventId = 1602, EventName = nameof(CrystalLogEvents.WriteStallEnded), Level = LogLevel.Information,
        Message = "RocksDB resumed writes in {Database}.")]
    public static partial void WriteStallEnded(ILogger logger, string database);

    // ---- 2000s: replication (spec 003 R13.3) ----------------------------------

    [LoggerMessage(EventId = 2001, EventName = nameof(CrystalLogEvents.ReplicationPromoted), Level = LogLevel.Information,
        Message = "Replication: promoted to primary at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationPromoted(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2002, EventName = nameof(CrystalLogEvents.ReplicationDemoted), Level = LogLevel.Information,
        Message = "Replication: demoted at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationDemoted(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2003, EventName = nameof(CrystalLogEvents.ReplicationFenced), Level = LogLevel.Warning,
        Message = "Replication: fenced read-only at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationFenced(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2004, EventName = nameof(CrystalLogEvents.ReplicationEpochChanged), Level = LogLevel.Information,
        Message = "Replication: adopted epoch {Epoch}. {Detail}")]
    public static partial void ReplicationEpochChanged(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2005, EventName = nameof(CrystalLogEvents.ReplicationSlotLagging), Level = LogLevel.Warning,
        Message = "Replication: a standby slot is lagging at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationSlotLagging(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2006, EventName = nameof(CrystalLogEvents.ReplicationSlotDropped), Level = LogLevel.Warning,
        Message = "Replication: a standby slot was dropped at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationSlotDropped(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2007, EventName = nameof(CrystalLogEvents.ReplicationGap), Level = LogLevel.Warning,
        Message = "Replication: a database reported a gap at epoch {Epoch}; re-bootstrapping it. {Detail}")]
    public static partial void ReplicationGap(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2008, EventName = nameof(CrystalLogEvents.ReplicationDivergence), Level = LogLevel.Warning,
        Message = "Replication: a database diverged at epoch {Epoch}; re-bootstrapping it. {Detail}")]
    public static partial void ReplicationDivergence(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2009, EventName = nameof(CrystalLogEvents.ReplicationFormatRefused), Level = LogLevel.Error,
        Message = "Replication: a peer's format differs at epoch {Epoch}; not replicating. {Detail}")]
    public static partial void ReplicationFormatRefused(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2010, EventName = nameof(CrystalLogEvents.ReplicationConflict), Level = LogLevel.Error,
        Message = "Replication: two primaries at epoch {Epoch}; the product must resolve it. {Detail}")]
    public static partial void ReplicationConflict(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2013, EventName = nameof(CrystalLogEvents.ReplicationPromotedWithPendingResync), Level = LogLevel.Warning,
        Message = "Replication: promoted at epoch {Epoch} with databases marked for re-bootstrap. {Detail}")]
    public static partial void ReplicationPromotedWithPendingResync(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2014, EventName = nameof(CrystalLogEvents.ReplicationEventHandlerFailed), Level = LogLevel.Error,
        Message = "Replication: a handler of the {Kind} event threw; later events are still delivered.")]
    public static partial void ReplicationEventHandlerFailed(ILogger logger, string kind, Exception exception);

    [LoggerMessage(EventId = 2011, EventName = nameof(CrystalLogEvents.ReplicationConnected), Level = LogLevel.Information,
        Message = "Replication: connected at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationConnected(ILogger logger, long epoch, string detail);

    [LoggerMessage(EventId = 2012, EventName = nameof(CrystalLogEvents.ReplicationDisconnected), Level = LogLevel.Information,
        Message = "Replication: connection lost at epoch {Epoch}. {Detail}")]
    public static partial void ReplicationDisconnected(ILogger logger, long epoch, string detail);
}
