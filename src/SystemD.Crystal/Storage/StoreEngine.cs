// Ported from StowDB: src/Stow.Storage.Crystal/CrystalEngine.cs
// and StowDB: src/Stow.Storage.Crystal/InternalBoxes.cs
// Adapted for Crystal: store, registry database (_crystal), and database lifecycle (spec 005 R4, R5);
// corrected per design addendum A2–A5, A7.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using RocksDbSharp;

using SystemD.Crystal.Observability;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Internal storage engine managing the registry database (<c>_crystal</c>), shared resources,
/// and open database instances for one store (spec 005 R4, R5, R9).
/// </summary>
internal sealed class StoreEngine : IDisposable
{
    internal const string RegistryFolderName = "_crystal";
    internal const string NameFileName = "name";

    /// <summary>The registry's kernel box in <c>_crystal</c> (ids 1–15 are the kernel's; design addendum A4).</summary>
    private const uint RegistryBoxId = 1;
    private const string RegistryFamily = "general";
    private const string RegistryLabel = "the store's registry '_crystal'";

    private static string DatabaseLabel(string name, uint id) => $"database '{name}' (id {id})";
    private const byte TagAlias = 0x01;
    private const byte TagRecord = 0x02;
    private const byte TagAllocator = 0x03;
    private const byte TagGeneration = 0x04;
    private const uint FirstDatabaseId = 16;
    private const string LiveState = "live";
    private const string DroppedState = "dropped";

    private static readonly Dictionary<string, BoxProfile> EmptyFamilyProfiles = [];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] KnownRootFiles =
        [StoreFormat.FormatFileName, StoreFormat.NodeIdFileName, StoreLock.LockFileName, "node.epoch"];

    private readonly string _dataDirectory;
    private readonly CrystalOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly StoreResources _resources;

    /// <summary>The store's shared native resources (cache, environment, write budget): backup and restore open with them.</summary>
    internal StoreResources Resources => _resources;
    private readonly RocksInstance _registryDb;
    private readonly DatabaseContext _registryCatalogue;

    /// <summary>The store gate (R7.1): create, open, list, rename, swap, drop and dispose. Data operations never take it.</summary>
    private readonly object _gate = new();

    private readonly Dictionary<string, uint> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, DatabaseRecord> _records = [];
    private readonly Dictionary<uint, DatabaseSlot> _open = [];

    private ulong _nextDatabaseId = FirstDatabaseId;
    private long _generation = 1;
    private int _dropsInProgress;
    private bool _disposed;

    // The WAL sync timer (spec 006 R4): created on the first Interval commit; it has nothing to do before one.
    private readonly object _timerSync = new();
    private ITimer? _syncTimer;
    private bool _timerStopped;

    // The open slots as an immutable array, replaced under the store gate whenever _open changes and read without it,
    // so the timer and SyncAll are never held up by a create, open or drop holding the gate (006 review, finding 3).
    private DatabaseSlot[] _openSlots = [];

    // A registry write that failed (006 R5 for the registry, review finding 5): every later registry write is refused.
    private DatabaseFailedException? _registryFailed;

    // Spec 007: the store's instruments and the write-stall check, started once the store is open (StartObservability).
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);
    private CrystalMetrics? _metrics;
    private ITimer? _stallTimer;
    private readonly Dictionary<uint, string> _stalled = [];   // touched only by the stall tick, which never overlaps itself

    /// <summary>
    /// Test seam (spec 007 R5.2): a database's write state, <c>"stopped"</c>, <c>"slowed"</c> or null. Null reads
    /// RocksDB's <c>rocksdb.is-write-stopped</c> and <c>rocksdb.actual-delayed-write-rate</c>; a stall cannot be forced
    /// reliably in a test.
    /// </summary>
    internal Func<DatabaseSlot, string?>? StallProbe { get; set; }

    private static string? WriteState(RocksInstance instance) =>
        StorageGauges.Db(instance, "rocksdb.is-write-stopped") != 0 ? "stopped"
        : StorageGauges.Db(instance, "rocksdb.actual-delayed-write-rate") != 0 ? "slowed"
        : null;


    private StoreEngine(
        string dataDirectory,
        CrystalOptions options,
        ILogger? logger,
        StoreResources resources,
        RocksInstance registryDb,
        DatabaseContext registryCatalogue)
    {
        _dataDirectory = dataDirectory;
        _options = options;
        _clock = options.Clock;
        _logger = logger;
        _resources = resources;
        _registryDb = registryDb;
        _registryCatalogue = registryCatalogue;
    }

    /// <summary>The store's default durability, for the store's closing event.</summary>
    internal DurabilityMode Durability => _options.Durability;

    /// <summary>The most databases this store opens at once (sizes the replication request cap, spec 011 R4.2).</summary>
    internal int MaxOpenDatabases => _options.MaxOpenDatabases;

    /// <summary>The registry generation (R4.2). Read without the store gate.</summary>
    public long RegistryGeneration => Interlocked.Read(ref _generation);

    /// <summary>The registry's latest sequence number: the cost tests' count of registry writes (R13.7).</summary>
    internal ulong RegistrySequence => _registryDb.Db.GetLatestSequenceNumber();

    /// <summary>The registry's instance, for tests that inject a registry write failure (006 R5).</summary>
    internal RocksInstance RegistryInstance => _registryDb;

    /// <summary>
    /// Opens the store engine: shared resources, the registry (R4), startup recovery (R8.3, R8.4). Product databases
    /// stay closed until asked for. The caller holds the store lock.
    /// </summary>
    public static StoreEngine Open(string dataDirectory, CrystalOptions options, string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var logger = options.LoggerFactory?.CreateLogger<CrystalStore>();
        var registryPath = Path.Combine(dataDirectory, RegistryFolderName);

        if (!Directory.Exists(registryPath) && EnumerateDatabaseFolders(dataDirectory).Any())
        {
            throw new CrystalFormatException(
                $"The store at '{dataDirectory}' has database folders but no registry '{RegistryFolderName}'. " +
                "Crystal never serves a folder the registry does not name, and never starts an empty registry over " +
                "existing databases (R4.5).",
                foundFormat: StoreFormat.SupportedFormat,
                supportedFormat: StoreFormat.SupportedFormat,
                wayForward: "Restore the store from a backup, or empty it with CrystalStore.Reset if its data is not needed.");
        }

        var resources = StoreResources.Create(
            (ulong)options.BlockCacheBytes,
            (ulong)options.WriteBufferBudgetBytes,
            backgroundThreads: options.BackgroundThreads);

        RocksInstance? registryDb = null;
        DatabaseContext? registryCatalogue = null;
        try
        {
            registryDb = RocksInstance.Open(registryPath, resources, EmptyFamilyProfiles, 0, 0);
            registryCatalogue = new DatabaseContext(registryDb, options.Clock, RegistryLabel);
            var engine = new StoreEngine(dataDirectory, options, logger, resources, registryDb, registryCatalogue);
            engine.LoadOrInitializeRegistry(registryPath);
            engine.RunStartupRecovery();
            engine.StartObservability(nodeId);
            return engine;
        }
        catch
        {
            registryCatalogue?.Dispose();
            registryDb?.Dispose();
            resources.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Empties a store offline (spec 005 R10; design addendum A7): deletes the registry and every database folder,
    /// recreates an empty registry, and keeps <c>crystal.format</c>, <c>node.id</c> and <c>node.epoch</c>.
    /// Refuses a directory it cannot identify as a Crystal store, and a store that is open anywhere (R10.2).
    /// </summary>
    internal static void Reset(string dataDirectory, CrystalOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var fullPath = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"There is no store to reset: directory '{fullPath}' does not exist.");
        }

        // Identify the store before writing anything, so a foreign directory never gets a lock file.
        StoreFormat.VerifyExisting(fullPath);
        using var storeLock = StoreLock.Acquire(fullPath);
        StoreFormat.VerifyExisting(fullPath);

        var logger = options.LoggerFactory?.CreateLogger<CrystalStore>();
        var registryPath = Path.Combine(fullPath, RegistryFolderName);

        // The registry first: a crash part-way then leaves database folders and no registry, which Open refuses with
        // Reset as the way forward, never a registry naming databases whose folders are gone.
        foreach (var folder in EnumerateDatabaseFolders(fullPath).Select(f => f.Path).Prepend(registryPath))
        {
            if (Directory.Exists(folder))
            {
                DeleteDirectoryWithRetries(folder);
                if (logger is not null)
                {
                    CrystalLog.ResetDeletedFolder(logger, folder);
                }
            }

            if (folder == registryPath)
            {
                options.Hook?.Invoke("reset.registry-deleted");
            }
        }

        using var resources = StoreResources.Create(
            (ulong)options.BlockCacheBytes,
            (ulong)options.WriteBufferBudgetBytes,
            options.BackgroundThreads);
        using var registryDb = RocksInstance.Open(registryPath, resources, EmptyFamilyProfiles, 0, 0);
        using var registryCatalogue = new DatabaseContext(registryDb, options.Clock, RegistryLabel);
        InitializeEmptyRegistry(registryDb, registryCatalogue, options.Clock);
    }

    private static void InitializeEmptyRegistry(RocksInstance registryDb, DatabaseContext registryCatalogue, TimeProvider clock)
    {
        // Spec 009 R3.1: the registry describes itself too, as kind "registry", so it is never taken for a database.
        registryCatalogue.InitializeNewCatalogue(
            new DatabaseIdentity(DatabaseRecord.NewUid(), DatabaseKind.Registry, clock.GetUtcNow().UtcTicks));

        using var batch = new WriteBatch();
        var family = registryDb.Family(RegistryFamily);
        batch.Put(RegistryKey(TagAllocator), UInt64Bytes(FirstDatabaseId), family);
        batch.Put(RegistryKey(TagGeneration), UInt64Bytes(1), family);
        registryDb.Write(batch, sync: true);
    }

    private void LoadOrInitializeRegistry(string registryPath)
    {
        var formatBytes = _registryDb.Get("default", BoxKeys.FormatKey);
        var entries = ReadRegistryEntries();

        if (formatBytes is null)
        {
            if (entries.Count != 0)
            {
                throw RegistryRefusal(registryPath, "has registry entries but no format key");
            }

            // A new store, or a crash before the registry's first write.
            InitializeEmptyRegistryIfNoDatabases(registryPath);
            return;
        }

        if (formatBytes.Length != 4 || BinaryPrimitives.ReadUInt32BigEndian(formatBytes) != 1)
        {
            var found = formatBytes.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(formatBytes).ToString() : "unreadable";
            throw new CrystalFormatException(
                $"The registry at '{registryPath}' has format {found}; this build reads format 1.",
                foundFormat: found,
                supportedFormat: "1",
                wayForward: "Open the store with a Crystal build that reads it, or empty it with CrystalStore.Reset " +
                    "if its data is not needed.");
        }

        // Spec 009 R3.1: the registry's own UID is recorded nowhere else, so only its kind is checked. Its refusals
        // name the registry's way forward: it cannot be dropped or put back, only reset (005 addendum A6).
        try
        {
            _registryCatalogue.LoadCatalogue(DatabaseKind.Registry, expectedUid: null);
        }
        catch (CrystalFormatException ex)
        {
            throw new CrystalFormatException(
                ex.Message,
                foundFormat: ex.FoundFormat,
                supportedFormat: ex.SupportedFormat,
                wayForward: "Open the store with a Crystal build that reads it, or empty it with CrystalStore.Reset if " +
                    "its data is not needed.",
                innerException: ex);
        }

        byte[]? allocator = null;
        byte[]? generation = null;
        var aliasesOnDisk = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (key.Length <= BoxKeys.PrefixLength)
            {
                throw RegistryRefusal(registryPath, "holds an entry with no tag");
            }

            var tag = key[BoxKeys.PrefixLength];
            var payload = key.AsSpan(BoxKeys.PrefixLength + 1);
            switch (tag)
            {
                case TagAlias when value.Length == 4:
                    aliasesOnDisk[DecodeAlias(registryPath, payload)] = BinaryPrimitives.ReadUInt32BigEndian(value);
                    break;
                case TagRecord when payload.Length == 4:
                    var recordId = BinaryPrimitives.ReadUInt32BigEndian(payload);
                    _records[recordId] = DecodeRecord(registryPath, recordId, value);
                    break;
                case TagAllocator when payload.IsEmpty && value.Length == 8:
                    allocator = value;
                    break;
                case TagGeneration when payload.IsEmpty && value.Length == 8:
                    generation = value;
                    break;
                default:
                    throw RegistryRefusal(registryPath, $"holds an entry this build does not recognise (tag 0x{tag:x2})");
            }
        }

        if (allocator is null || generation is null)
        {
            if (aliasesOnDisk.Count != 0 || _records.Count != 0)
            {
                throw RegistryRefusal(registryPath, "has databases but no id allocator or generation");
            }

            // A crash between the catalogue's first write and the registry's.
            InitializeEmptyRegistryIfNoDatabases(registryPath);
            return;
        }

        _nextDatabaseId = BinaryPrimitives.ReadUInt64BigEndian(allocator);
        _generation = (long)BinaryPrimitives.ReadUInt64BigEndian(generation);

        foreach (var (id, record) in _records)
        {
            if (record.Version is < 1 or > DatabaseRecord.CurrentVersion || record.State is not (LiveState or DroppedState))
            {
                throw RegistryRefusal(registryPath, $"has a record for id {id} with version {record.Version} and state '{record.State}'");
            }

            if (id < FirstDatabaseId || id >= _nextDatabaseId)
            {
                throw RegistryRefusal(registryPath, $"has a record for id {id}, outside the ids the allocator has issued");
            }

            if (record.State == LiveState)
            {
                if (!aliasesOnDisk.TryGetValue(record.Name, out var aliasId) || aliasId != id)
                {
                    throw RegistryRefusal(registryPath, $"has live database '{record.Name}' (id {id}) without a matching alias");
                }

                _aliases[record.Name] = id;
            }
        }

        if (aliasesOnDisk.Count != _aliases.Count)
        {
            throw RegistryRefusal(registryPath, "has an alias that names no live database");
        }

        UpgradeRecordsToCurrentVersion();
    }

    /// <summary>
    /// Gives every version 1 record (no UID) a stable UID and version 2, in one synced registry batch (spec 003 T1,
    /// R1.3, design § 2). A version 1 registry upgrades exactly once: a later load sees only version 2 records and
    /// writes nothing. The numeric id, name, state and times are unchanged.
    /// </summary>
    private void UpgradeRecordsToCurrentVersion()
    {
        var toUpgrade = new List<(uint Id, DatabaseRecord Upgraded)>();
        foreach (var (id, record) in _records)
        {
            if (record.Version < DatabaseRecord.CurrentVersion || string.IsNullOrEmpty(record.Uid))
            {
                toUpgrade.Add((id, record with
                {
                    Version = DatabaseRecord.CurrentVersion,
                    Uid = string.IsNullOrEmpty(record.Uid) ? DatabaseRecord.NewUid() : record.Uid,
                }));
            }
        }

        if (toUpgrade.Count == 0)
        {
            return;
        }

        using var batch = new WriteBatch();
        var family = _registryDb.Family(RegistryFamily);
        foreach (var (id, upgraded) in toUpgrade)
        {
            batch.Put(RecordKey(id), DatabaseRecord.Serialize(upgraded), family);
        }

        _registryDb.Write(batch, sync: true);

        foreach (var (id, upgraded) in toUpgrade)
        {
            _records[id] = upgraded;
        }
    }

    /// <summary>
    /// Starts an empty registry, unless database folders exist: then the registry was lost, and starting an empty one
    /// would put those folders at ids the allocator has not issued, the next creates' ids (design addendum A4).
    /// </summary>
    private void InitializeEmptyRegistryIfNoDatabases(string registryPath)
    {
        if (EnumerateDatabaseFolders(_dataDirectory).Any())
        {
            throw RegistryRefusal(registryPath, "is empty, but the store has database folders");
        }

        InitializeEmptyRegistry(_registryDb, _registryCatalogue, _clock);
    }

    private List<(byte[] Key, byte[] Value)> ReadRegistryEntries()
    {
        BoxKeys.GetBoxBounds(RegistryBoxId, out var lowerBound, out var upperBound);
        using var snapshot = _registryDb.CreateSnapshot();
        using var iterator = _registryDb.Scan(RegistryFamily, snapshot, lowerBound, upperBound, int.MaxValue);

        var entries = new List<(byte[], byte[])>();
        foreach (var (key, value) in iterator.Scan(lowerBound))
        {
            entries.Add((key, value));
        }

        return entries;
    }

    private static string DecodeAlias(string registryPath, ReadOnlySpan<byte> payload)
    {
        try
        {
            var name = EscapedString.Decode(payload, out var consumed);
            if (consumed != payload.Length)
            {
                throw RegistryRefusal(registryPath, "holds an alias key with bytes after the name");
            }

            return name;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            throw RegistryRefusal(registryPath, "holds an alias key that does not decode", ex);
        }
    }

    private static DatabaseRecord DecodeRecord(string registryPath, uint id, byte[] value)
    {
        DatabaseRecord record;
        try
        {
            record = DatabaseRecord.Deserialize(value);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw RegistryRefusal(registryPath, $"holds a record for id {id} that does not parse", ex);
        }

        try
        {
            DatabaseNameRules.Validate(record.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or ReservedNameException)
        {
            throw RegistryRefusal(registryPath, $"holds a record for id {id} whose name breaks the name rules", ex);
        }

        return record;
    }

    private static CrystalFormatException RegistryRefusal(string registryPath, string problem, Exception? inner = null) =>
        new(
            $"The registry at '{registryPath}' {problem}. Crystal will not guess which databases exist (R4.5).",
            foundFormat: "1",
            supportedFormat: "1",
            wayForward: "Restore the store from a backup, or empty it with CrystalStore.Reset if its data is not needed.",
            innerException: inner);

    /// <summary>Startup recovery (R8.3, R8.4).</summary>
    private void RunStartupRecovery()
    {
        foreach (var (id, record) in _records)
        {
            var dbPath = DatabasePath(id);
            if (record.State == DroppedState)
            {
                // A crash may have come between the drop's batch and the removal; not when the UID is live again (a
                // restore may bring a dropped UID back, and its records are its own then).
                if (!_records.Values.Any(r => r.State == LiveState && string.Equals(r.Uid, record.Uid, StringComparison.Ordinal)))
                {
                    ForgetReplicationRecords(record.Uid);
                }
                if (Directory.Exists(dbPath))
                {
                    DeleteDirectoryWithRetries(dbPath);
                    if (_logger is not null)
                    {
                        CrystalLog.RecoveryDeletedDroppedFolder(_logger, id, dbPath);
                    }
                }
            }
            else if (Directory.Exists(dbPath))
            {
                RepairNameFile(id, record.Name);
            }
            else
            {
                if (_logger is not null)
                {
                    CrystalLog.RecoveryFolderMissing(_logger, record.Name, id, dbPath);
                }
            }
        }

        var databaseFolders = EnumerateDatabaseFolders(_dataDirectory).ToDictionary(f => f.Path, f => f.Id);
        foreach (var (path, id) in databaseFolders)
        {
            if (_records.ContainsKey(id))
            {
                continue;
            }

            if (id < _nextDatabaseId)
            {
                // The allocator has passed it, so a create crashed before registering it (R8.1, design addendum A2).
                DeleteDirectoryWithRetries(path);
                if (_logger is not null)
                {
                    CrystalLog.RecoveryRemovedUnregisteredFolder(_logger, path);
                }
            }
            else
            {
                if (_logger is not null)
                {
                    CrystalLog.RecoveryLeftFutureFolder(_logger, path, id, _nextDatabaseId);
                }
            }
        }

        RecoverReplicationLeftovers();

        foreach (var entry in Directory.EnumerateFileSystemEntries(_dataDirectory))
        {
            var name = Path.GetFileName(entry);
            var known = databaseFolders.ContainsKey(entry)
                || (Directory.Exists(entry) && name == ReplicationFolderName)
                || (Directory.Exists(entry) && name == RegistryFolderName)
                || (File.Exists(entry) && KnownRootFiles.Contains(name, StringComparer.Ordinal));
            if (!known)
            {
                if (_logger is not null)
                {
                    CrystalLog.RecoveryLeftUnrecognisedEntry(_logger, entry);
                }
            }
        }
    }

    internal const string ReplacedSuffix = ".replaced-";
    private const string ReplicationFolderName = "replication";

    /// <summary>
    /// Replication's own temporary folders left by a crash (spec 003 R7.3, R7.5), recognised by name and handled
    /// before anything opens: a snapshot download (<c>bootstrap-*</c>) and a primary's snapshot checkpoint
    /// (<c>db-*.checkpoint-*</c>) are deleted; a database's old content moved aside by an install
    /// (<c>db-*.replaced-*</c>) is put back if the install never moved the new content in, and deleted otherwise.
    /// </summary>
    private void RecoverReplicationLeftovers()
    {
        foreach (var entry in Directory.EnumerateDirectories(_dataDirectory))
        {
            var name = Path.GetFileName(entry);
            var replaced = name.IndexOf(ReplacedSuffix, StringComparison.Ordinal);
            if (replaced > 0)
            {
                var dbPath = Path.Combine(_dataDirectory, name[..replaced]);
                if (!Directory.Exists(dbPath))
                {
                    Directory.Move(entry, dbPath); // the install crashed between its moves: the old content comes back
                    DurableFile.SyncDirectory(_dataDirectory); // spec 011 R9.1
                }
                else
                {
                    DeleteDirectoryWithRetries(entry);
                }

                LogRecovered(entry);
            }
            else if (name.StartsWith("bootstrap-", StringComparison.Ordinal)
                || name.StartsWith(RestoreStagingPrefix, StringComparison.Ordinal) // a restore's, never moved in (spec 010)
                || (name.StartsWith("db-", StringComparison.Ordinal) && name.Contains(".checkpoint-", StringComparison.Ordinal)))
            {
                DeleteDirectoryWithRetries(entry);
                LogRecovered(entry);
            }
        }
    }

    private void LogRecovered(string entry)
    {
        if (_logger is not null)
        {
            CrystalLog.RecoveryRemovedUnregisteredFolder(_logger, entry);
        }
    }

    public CrystalDatabase CreateDatabase(CrystalStore store, string name, bool replicated) =>
        CreateDatabase(store, name, replicated, uid: null);

    /// <summary>
    /// Creates a database with a chosen UID (spec 003 design § 2), with its own catalogue and an identity of that UID
    /// (spec 009 R1.1). Reconciliation does not use it: a database new to a standby arrives whole through
    /// <see cref="InstallBootstrap"/>. Tests use it to stand up a standby database aligned with a primary's.
    /// </summary>
    internal CrystalDatabase CreateDatabaseWithUid(CrystalStore store, string name, bool replicated, string uid)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        return CreateDatabase(store, name, replicated, uid);
    }

    /// <summary>
    /// Installs a downloaded snapshot as the database with <paramref name="uid"/> (spec 003 T10, R7.7, design § 7):
    /// moves <paramref name="bootstrapDirectory"/> into place as the database's folder, registering the record (with
    /// the primary's UID) if the standby does not have it yet, and opens it. If a database with the UID is open, it is
    /// closed first; its old folder is removed only after the new one is in place, so a failure before the move leaves
    /// the database as it was. Returns the installed database's handle.
    /// </summary>
    internal CrystalDatabase InstallBootstrap(CrystalStore store, string uid, string name, string bootstrapDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(uid);
        DatabaseNameRules.Validate(name);
        if (!Directory.Exists(bootstrapDirectory))
        {
            throw new DirectoryNotFoundException($"The bootstrap directory '{bootstrapDirectory}' does not exist.");
        }

        // Spec 009 R5.1: the snapshot must hold the database asked for. Checked before anything is closed or moved, so
        // a wrong snapshot leaves the database as it was.
        var snapshotIdentity = VerifySnapshotIdentity(bootstrapDirectory, uid, name);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A new database takes its name only if no other holds it (003 should-fix 2): never overwrite another's alias.
            // Checked before anything is closed or moved.
            if (_aliases.TryGetValue(name, out var holder)
                && !string.Equals(_records[holder].Uid, uid, StringComparison.Ordinal))
            {
                throw new DatabaseNameTakenException(
                    name,
                    $"Database '{name}' is held by another database here (id {holder}); the snapshot of {uid} is installed once " +
                    "reconcile frees the name (R1.4).");
            }

            // The snapshot replaces this node's log with what it received: none of its own history may be continued.
            IncarnationRecord.Delete(_dataDirectory, uid);

            // Find an existing record for this UID, or allocate a new id for a fresh one.
            uint? existingId = null;
            foreach (var kv in _records)
            {
                if (string.Equals(kv.Value.Uid, uid, StringComparison.Ordinal) && kv.Value.State == LiveState)
                {
                    existingId = kv.Key;
                    break;
                }
            }

            var id = existingId ?? (uint)_nextDatabaseId;
            var dbPath = DatabasePath(id);

            // Close the database if it is open, so its folder can be replaced.
            DatabaseSlot? open = null;
            if (existingId is { } eid && _open.TryGetValue(eid, out var slot))
            {
                open = slot;
                _open.Remove(eid);
                PublishOpenSlots();
                slot.BeginReplace(); // its handles say "re-created, open again", never "store closed" (spec 013 R7)
                slot.StopRunningBackup(); // a backup copying it is stopped, not waited for; it tries again (spec 010 review S1)
            }

            if (open is not null)
            {
                open.WaitForCallsInProgress(_logger);
                open.Close();
            }

            try
            {
                return InstallSwapLocked(store, uid, name, id, dbPath, bootstrapDirectory, existingId, snapshotIdentity.CreatedTicks);
            }
            catch when (existingId is not null)
            {
                // A mid-install failure for a database the standby already had: the open slot was closed above and
                // its content may be gone. Do not leave the running store without the database for the process
                // lifetime (review finding) — reopen whatever is at its folder (the fresh snapshot if the move
                // succeeded, or the old content if it did not). A failure to reopen rethrows the original.
                try
                {
                    if (Directory.Exists(dbPath) && !_open.ContainsKey(existingId.Value))
                    {
                        OpenSlotLocked(store, existingId.Value, name);
                    }
                }
                catch
                {
                    // The reopen failed too; surface the original install failure below.
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Opens a downloaded snapshot on its own and loads its catalogue expecting the database <paramref name="uid"/>
    /// (spec 009 R2.3, R5.1); a snapshot of another database, or of the registry, is refused by name.
    /// </summary>
    private DatabaseIdentity VerifySnapshotIdentity(string directory, string uid, string name)
    {
        var (walTtl, walSize) = WalRetentionFor(replicated: true);
        using var instance = RocksInstance.Open(directory, _resources, EmptyFamilyProfiles, walTtl, walSize);
        using var context = new DatabaseContext(instance, _clock, $"the snapshot of database '{name}'");
        return context.LoadCatalogue(DatabaseKind.Database, uid);
    }

    private CrystalDatabase InstallSwapLocked(
        CrystalStore store, string uid, string name, uint id, string dbPath, string bootstrapDirectory, uint? existingId,
        long createdTicks)
    {
        {
            // Swap the folder (R7.3): move the old content aside, move the verified snapshot in, and only then delete
            // what was moved aside. If the snapshot cannot be moved in, the old content goes back. A crash between the
            // two moves leaves the old content beside an empty place, which startup recovery puts back. Same
            // filesystem (the download is a sibling), so each move is a rename.
            // A new UID advances the allocator before its folder is moved in, as a create does (spec 011 R9.2): a crash
            // after the move then leaves a folder below the allocator with no record, which startup recovery removes,
            // never a folder at the next id that blocks every later create.
            if (existingId is null)
            {
                using (var allocation = new WriteBatch())
                {
                    allocation.Put(RegistryKey(TagAllocator), UInt64Bytes((ulong)id + 1), _registryDb.Family(RegistryFamily));
                    WriteRegistry(allocation);
                }

                _nextDatabaseId = (ulong)id + 1;
                Hook("install.allocated");
            }

            // Whatever this node followed for the database before no longer describes what it will hold (spec 011
            // R2.2): the record goes before the folder moves, and the caller writes the snapshot's after. A crash in
            // between leaves no record, which knows nothing shared (003 R5.5): never a promotion that claims the old
            // log's history for the snapshot's data.
            FollowRecord.DeleteDurably(_dataDirectory, uid);

            string? aside = null;
            if (Directory.Exists(dbPath))
            {
                aside = dbPath + ReplacedSuffix + Guid.NewGuid().ToString("N");
                Directory.Move(dbPath, aside);
                DurableFile.SyncDirectory(_dataDirectory); // spec 011 R9.1
            }

            try
            {
                Hook("install.aside");
                Directory.Move(bootstrapDirectory, dbPath);
                DurableFile.SyncDirectory(_dataDirectory); // spec 011 R9.1
                Hook("install.moved");
            }
            catch
            {
                if (aside is not null && !Directory.Exists(dbPath))
                {
                    Directory.Move(aside, dbPath);
                    DurableFile.SyncDirectory(_dataDirectory);
                }

                throw;
            }

            if (aside is not null)
            {
                TryDeleteDirectory(aside);
            }

            WriteNameFile(dbPath, name);

            // Register the record for a new UID (its allocator was advanced before the move, above).
            if (existingId is null)
            {
                var record = new DatabaseRecord
                {
                    Version = DatabaseRecord.CurrentVersion,
                    Name = name,
                    Uid = uid,
                    Replicated = true,
                    State = LiveState,
                    CreatedTicks = createdTicks, // the database's own (spec 009 R2.3), read from the snapshot
                    DroppedTicks = null,
                };
                var generation = _generation + 1;
                using var batch = new WriteBatch();
                var family = _registryDb.Family(RegistryFamily);
                batch.Put(AliasKey(name), UInt32Bytes(id), family);
                batch.Put(RecordKey(id), DatabaseRecord.Serialize(record), family);
                batch.Put(RegistryKey(TagGeneration), UInt64Bytes((ulong)generation), family);
                WriteRegistry(batch);

                PublishGeneration(generation);
                _aliases[name] = id;
                _records[id] = record;
            }

            return OpenSlotLocked(store, id, name).Handle;
        }
    }

    private CrystalDatabase CreateDatabase(CrystalStore store, string name, bool replicated, string? uid)
    {
        DatabaseNameRules.Validate(name);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_aliases.ContainsKey(name))
            {
                throw new DatabaseNameTakenException(name, $"Database '{name}' already exists (R5.1).");
            }

            ThrowIfAtOpenLimit(name);

            if (_nextDatabaseId > uint.MaxValue)
            {
                throw new InvalidOperationException("The store has issued every database id; ids are never reused (R4.3).");
            }

            // A folder already at the next id is not Crystal's: refuse before the allocator passes it, or startup
            // recovery would take it for an interrupted create and delete it (R8.3).
            var id = (uint)_nextDatabaseId;
            var dbPath = DatabasePath(id);
            if (Directory.Exists(dbPath))
            {
                throw new IOException(
                    $"Database '{name}' cannot be created: folder '{dbPath}' already exists at the next database id, " +
                    $"{id}, and Crystal did not create it. Crystal owns its data directory (R2.2); move it out of the " +
                    "store directory, then create again.");
            }

            // Step 1 (design addendum A2): advance the allocator first, so a crash after the folder leaves a folder
            // the allocator has passed, which startup recovery removes.
            using (var allocation = new WriteBatch())
            {
                allocation.Put(RegistryKey(TagAllocator), UInt64Bytes((ulong)id + 1), _registryDb.Family(RegistryFamily));
                WriteRegistry(allocation);
            }

            _nextDatabaseId = (ulong)id + 1;
            Hook("create.allocated");

            // Step 2: the folder, its name file, the instance and its catalogue, before the registry names it (R8.1).
            Directory.CreateDirectory(dbPath);
            RocksInstance? instance = null;
            DatabaseContext? context = null;
            LogIncarnation? incarnation = null;
            var registering = false;
            try
            {
                WriteNameFile(dbPath, name);
                Hook("create.folder");

                // A fresh UID (spec 003 R1.3) names the database on the wire; it is stable across rename, swap,
                // reopen and promotion. The database records it itself (spec 009 R1.1); the registry keeps a copy.
                var record = new DatabaseRecord
                {
                    Version = DatabaseRecord.CurrentVersion,
                    Name = name,
                    Uid = string.IsNullOrEmpty(uid) ? DatabaseRecord.NewUid() : uid,
                    Replicated = replicated,
                    State = LiveState,
                    CreatedTicks = _clock.GetUtcNow().Ticks,
                    DroppedTicks = null,
                };

                var (walTtl, walSize) = WalRetentionFor(replicated, counting: true);
                instance = RocksInstance.Open(dbPath, _resources, EmptyFamilyProfiles, walTtl, walSize);
                context = NewDatabaseContext(instance, name, id);
                context.InitializeNewCatalogue(new DatabaseIdentity(record.Uid, DatabaseKind.Database, record.CreatedTicks));
                incarnation = IncarnationOnOpen(replicated, record.Uid, (long)instance.Db.GetLatestSequenceNumber());
                Hook("create.opened");

                // Step 3: the database exists once this batch is durable.

                var generation = _generation + 1;
                using var batch = new WriteBatch();
                var family = _registryDb.Family(RegistryFamily);
                batch.Put(AliasKey(name), UInt32Bytes(id), family);
                batch.Put(RecordKey(id), DatabaseRecord.Serialize(record), family);
                batch.Put(RegistryKey(TagGeneration), UInt64Bytes((ulong)generation), family);
                registering = true;
                WriteRegistry(batch);

                PublishGeneration(generation);
                _aliases[name] = id;
                _records[id] = record;
            }
            catch
            {
                context?.Dispose();
                instance?.Dispose();

                // A failed registry write may still have become durable, so the folder is left for the next open to
                // keep (registered) or remove (not registered). Before that write, nothing names it.
                if (!registering)
                {
                    TryDeleteDirectory(dbPath);
                }

                throw;
            }

            var handle = Publish(store, id, name, _records[id].Uid, instance, context, incarnation);
            handle.Slot.HandedOut = true;
            if (_logger is not null)
            {
                CrystalLog.DatabaseCreated(_logger, name, id);
            }
            Hook("create.registered");
            return handle;
        }
    }

    public CrystalDatabase OpenDatabase(CrystalStore store, string name)
    {
        DatabaseNameRules.Validate(name);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_aliases.TryGetValue(name, out var id))
            {
                throw new DatabaseNotFoundException(name, $"Database '{name}' was not found or has been dropped (R5.2).");
            }

            // Marked under the gate: a backup that opened this database closes it only if nobody took its handle (spec 010 R3.3).
            if (!_open.TryGetValue(id, out var slot))
            {
                slot = OpenSlotLocked(store, id, name);
            }

            slot.HandedOut = true;
            return slot.Handle;
        }
    }

    /// <summary>
    /// Opens the database <paramref name="id"/> (named <paramref name="name"/>) and publishes its slot. Called under
    /// <c>_gate</c> only when it is not already open; the caller has checked <c>_open</c>. Shared by the by-name open
    /// path and the replication source's by-UID open (spec 003 T7).
    /// </summary>
    private DatabaseSlot OpenSlotLocked(CrystalStore store, uint id, string name)
    {
        ThrowIfAtOpenLimit(name);

        var dbPath = DatabasePath(id);
        if (!Directory.Exists(dbPath))
        {
            throw new DatabaseNotFoundException(
                name,
                $"Database '{name}' (id {id}) is registered but its folder '{dbPath}' is missing. Restore the store from a backup.");
        }

        var (walTtl, walSize) = WalRetentionFor(_records[id].Replicated);
        var instance = RocksInstance.Open(dbPath, _resources, EmptyFamilyProfiles, walTtl, walSize);
        DatabaseContext? context = null;
        LogIncarnation? incarnation;
        try
        {
            var formatBytes = instance.Get("default", BoxKeys.FormatKey);
            var found = formatBytes is { Length: 4 } ? BinaryPrimitives.ReadUInt32BigEndian(formatBytes) : (uint?)null;
            if (found != 1)
            {
                throw new CrystalFormatException(
                    $"Database '{name}' at '{dbPath}' has a missing or unsupported format key; this build reads format 1.",
                    foundFormat: found?.ToString(),
                    supportedFormat: "1",
                    wayForward: "Open it with a Crystal build that reads it, restore it from a backup, or drop it " +
                        "if its data is not needed.");
            }

            context = NewDatabaseContext(instance, name, id);
            var identity = context.LoadCatalogue(DatabaseKind.Database, _records[id].Uid);
            CorrectCreatedTime(id, identity);
            incarnation = IncarnationOnOpen(_records[id].Replicated, _records[id].Uid, (long)instance.Db.GetLatestSequenceNumber());
        }
        catch
        {
            context?.Dispose();
            instance.Dispose();
            throw;
        }

        RepairNameFile(id, name);
        Publish(store, id, name, _records[id].Uid, instance, context, incarnation);
        return _open[id];
    }

    /// <summary>
    /// Spec 009 R2.3: the database's own creation time is the source; the registry's copy follows it. They differ on a
    /// standby whose record was written at reconciliation, before a bootstrap brought the primary's identity. One synced
    /// registry write, under the gate, that moves no generation: it is not a change to the set of databases.
    /// </summary>
    private void CorrectCreatedTime(uint id, DatabaseIdentity identity)
    {
        var record = _records[id];
        if (record.CreatedTicks == identity.CreatedTicks)
        {
            return;
        }

        // A correction is never a reason to refuse an open: with the registry failed (006 R5), it waits for the next
        // open after recovery; a write that fails puts the registry in that state (WriteRegistry logs it) and the open
        // goes on.
        if (Volatile.Read(ref _registryFailed) is not null)
        {
            return;
        }

        var corrected = record with { CreatedTicks = identity.CreatedTicks };
        try
        {
            using var batch = new WriteBatch();
            batch.Put(RecordKey(id), DatabaseRecord.Serialize(corrected), _registryDb.Family(RegistryFamily));
            WriteRegistry(batch);
        }
        catch (DatabaseFailedException)
        {
            return;
        }

        _records[id] = corrected;
        if (_logger is not null)
        {
            CrystalLog.DatabaseCreatedTimeCorrected(_logger, record.Name, id);
        }
    }

    public IReadOnlyList<DatabaseInfo> ListDatabases()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var list = new List<DatabaseInfo>(_aliases.Count);
            foreach (var id in _aliases.Values)
            {
                var record = _records[id];
                list.Add(new DatabaseInfo(id, record.Name, record.Uid, record.Replicated, new DateTimeOffset(record.CreatedTicks, TimeSpan.Zero)));
            }

            return list;
        }
    }

    /// <summary>
    /// The replicated database set the primary states in <c>DATABASES</c> (spec 003 R1.4, design § 2): each live,
    /// replicated database's UID, current name and latest log position (0 if it is not open — a standby's pull opens
    /// it). Paired with <see cref="ReplicatedSetVersion"/>, this is what a standby reconciles against.
    /// </summary>
    internal IReadOnlyList<Replication.ReplicatedDatabase> ReplicatedDatabaseSet()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var set = new List<Replication.ReplicatedDatabase>();
            foreach (var id in _aliases.Values)
            {
                var record = _records[id];
                if (!record.Replicated)
                {
                    continue;
                }

                var latest = _open.TryGetValue(id, out var slot) ? (long)slot.LatestSequence : 0L;
                set.Add(new Replication.ReplicatedDatabase(record.Uid, record.Name, latest));
            }

            return set;
        }
    }

    /// <summary>
    /// The set version a standby compares to decide whether to reconcile (spec 003 § 2): the registry generation
    /// (R4.2). It changes on any create, rename, swap or drop; a standby that sees a new value reads the set and makes
    /// its registry match. (A finer "replicated-only" filter is a T7 refinement; the generation is a safe superset —
    /// it may trigger a reconcile that finds nothing to do, never miss a change to the replicated set.)
    /// </summary>
    internal long ReplicatedSetVersion => RegistryGeneration;

    /// <summary>
    /// Raised after every change of the registry's generation (a database created, dropped, renamed, swapped or
    /// installed): the replicated set may have changed, so a primary wakes its waiting pulls (spec 011 R11.1, 003 R3.4).
    /// Raised under the store gate: a handler must be quick and must not call back into the engine.
    /// </summary>
    internal event Action? RegistryChanged;

    private void PublishGeneration(long generation)
    {
        Interlocked.Exchange(ref _generation, generation);
        RegistryChanged?.Invoke();
    }

    /// <summary>
    /// The slot of the live, replicated database with <paramref name="uid"/>, opening it if needed (spec 003 T7). A
    /// pull names databases by UID, and a replicated database that no standby has touched may still be closed; the
    /// primary opens it to read its WAL and latest position. Returns null when no live replicated database has that
    /// UID — the pull answers <c>gone</c> (design § 4). The <paramref name="store"/> owns handles created on open.
    /// </summary>
    internal DatabaseSlot? OpenReplicatedByUid(CrystalStore store, string uid)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(uid);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var id in _aliases.Values)
            {
                var record = _records[id];
                if (!record.Replicated || !string.Equals(record.Uid, uid, StringComparison.Ordinal))
                {
                    continue;
                }

                var slot = _open.TryGetValue(id, out var open) ? open : OpenSlotLocked(store, id, record.Name);

                // Replication holds it now (a standby's follower writes into it without entering it): a backup that opened
                // it must not close it under that holder (spec 010 review B1).
                slot.HandedOut = true;
                return slot;
            }

            return null;
        }
    }

    /// <summary>The standby's local view of its databases, for reconciliation (spec 003 § 2).</summary>
    internal IReadOnlyList<Replication.LocalDatabase> LocalDatabasesForReconcile()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var list = new List<Replication.LocalDatabase>();
            foreach (var id in _aliases.Values)
            {
                var record = _records[id];
                list.Add(new Replication.LocalDatabase(record.Uid, record.Name, record.Replicated));
            }

            return list;
        }
    }

    public void RenameDatabase(CrystalDatabase database, string newName)
    {
        ArgumentNullException.ThrowIfNull(database);
        DatabaseNameRules.Validate(newName);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = ResolveLive(database, "renamed");

            if (string.Equals(record.Name, newName, StringComparison.Ordinal))
            {
                return;
            }

            if (_aliases.ContainsKey(newName))
            {
                throw new DatabaseNameTakenException(newName, $"Database name '{newName}' is already taken (R5.4).");
            }

            var updated = record with { Name = newName };
            var generation = _generation + 1;
            using var batch = new WriteBatch();
            var family = _registryDb.Family(RegistryFamily);
            batch.Delete(AliasKey(record.Name), family);
            batch.Put(AliasKey(newName), UInt32Bytes(database.Id), family);
            batch.Put(RecordKey(database.Id), DatabaseRecord.Serialize(updated), family);
            batch.Put(RegistryKey(TagGeneration), UInt64Bytes((ulong)generation), family);
            WriteRegistry(batch);

            PublishGeneration(generation);
            _aliases.Remove(record.Name);
            _aliases[newName] = database.Id;
            _records[database.Id] = updated;
            database.Name = newName;
            if (_logger is not null)
            {
                CrystalLog.DatabaseRenamed(_logger, database.Id, record.Name, newName);
            }
            Hook("rename.registered");

            // After the registry and memory (design addendum A5): a failure here is repaired at the next open.
            WriteNameFileBestEffort(database.Id, newName);
        }
    }

    public void SwapDatabases(CrystalDatabase first, CrystalDatabase second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record1 = ResolveLive(first, "swapped");
            var record2 = ResolveLive(second, "swapped");

            if (first.Id == second.Id)
            {
                return;
            }

            var name1 = record1.Name;
            var name2 = record2.Name;
            var updated1 = record1 with { Name = name2 };
            var updated2 = record2 with { Name = name1 };

            var generation = _generation + 1;
            using var batch = new WriteBatch();
            var family = _registryDb.Family(RegistryFamily);
            batch.Put(AliasKey(name1), UInt32Bytes(second.Id), family);
            batch.Put(AliasKey(name2), UInt32Bytes(first.Id), family);
            batch.Put(RecordKey(first.Id), DatabaseRecord.Serialize(updated1), family);
            batch.Put(RecordKey(second.Id), DatabaseRecord.Serialize(updated2), family);
            batch.Put(RegistryKey(TagGeneration), UInt64Bytes((ulong)generation), family);
            WriteRegistry(batch);

            PublishGeneration(generation);
            _aliases[name1] = second.Id;
            _aliases[name2] = first.Id;
            _records[first.Id] = updated1;
            _records[second.Id] = updated2;
            first.Name = name2;
            second.Name = name1;
            if (_logger is not null)
            {
                CrystalLog.DatabasesSwapped(_logger, first.Id, second.Id, name2, name1);
            }
            Hook("swap.registered");

            WriteNameFileBestEffort(first.Id, name2);
            WriteNameFileBestEffort(second.Id, name1);
        }
    }

    public void DropDatabase(CrystalDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        DatabaseSlot slot;
        DatabaseRecord record;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            record = ResolveLive(database, "dropped");
            slot = _open[database.Id];

            // Step 1 (R5.6, R8.2): the registry batch, before anything is closed or deleted.
            var dropped = record with { State = DroppedState, DroppedTicks = _clock.GetUtcNow().Ticks };
            var generation = _generation + 1;
            using var batch = new WriteBatch();
            var family = _registryDb.Family(RegistryFamily);
            batch.Delete(AliasKey(record.Name), family);
            batch.Put(RecordKey(database.Id), DatabaseRecord.Serialize(dropped), family);
            batch.Put(RegistryKey(TagGeneration), UInt64Bytes((ulong)generation), family);
            WriteRegistry(batch);

            PublishGeneration(generation);
            _aliases.Remove(record.Name);
            _records[database.Id] = dropped;
            _open.Remove(database.Id);
            PublishOpenSlots();
            _dropsInProgress++;

            // From here every new call through the handle is refused (design § 5).
            slot.BeginClose(dropped: true);
        }

        try
        {
            Hook("drop.registered");

            // A backup copying this database is stopped, not waited for (spec 010 R3.2); it records it as omitted.
            slot.StopRunningBackup();

            // Steps 2–4: wait for calls in progress, close, delete the folder.
            slot.WaitForCallsInProgress(_logger);
            Hook("drop.drained");
            slot.Close();
            Hook("drop.closed");

            // Under the gate, and only if no live database has the UID again (review S3): a restore may have registered
            // it while this drop drained, and its records are its own.
            lock (_gate)
            {
                if (!_records.Values.Any(r => r.State == LiveState && string.Equals(r.Uid, record.Uid, StringComparison.Ordinal)))
                {
                    ForgetReplicationRecords(record.Uid);
                }
            }

            TryDeleteDroppedFolder(DatabasePath(database.Id));
            if (_logger is not null)
            {
                CrystalLog.DatabaseDropped(_logger, database.Name, database.Id);
            }
        }
        finally
        {
            lock (_gate)
            {
                _dropsInProgress--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        // Spec 006 R4.2: stop the timer first; every database still gets its final sync when its slot closes. A tick in
        // progress has entered its slots like any call, so closing waits for it. Spec 007 R2.7: the gauges stop reading
        // before anything they read is closed.
        StopSyncTimer();
        _metrics?.Close();

        List<DatabaseSlot> slots;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            slots = [.. _open.Values];
            _open.Clear();
            PublishOpenSlots();
            foreach (var slot in slots)
            {
                slot.BeginClose(dropped: false);
                slot.StopRunningBackup(); // spec 010 design § 5: a close stops a copy rather than waiting for it
            }

            // A drop in progress owns its slot; let it finish before the shared resources go.
            while (_dropsInProgress > 0)
            {
                Monitor.Wait(_gate);
            }
        }

        Hook("dispose.closing");
        var failures = new List<Exception>();
        foreach (var slot in slots)
        {
            slot.WaitForCallsInProgress(_logger);
            Attempt(slot.Close, failures);

            // 006 R5.1: a database that failed, now or earlier (its final sync included), is reported, not just logged.
            if (slot.Context.Failure is { } failure)
            {
                failures.Add(failure);
            }
        }

        // Review finding 1: the meter outlives the databases' final syncs, so they are measured.
        _metrics?.Dispose();

        if (Volatile.Read(ref _registryFailed) is { } registryFailure)
        {
            failures.Add(registryFailure);
        }

        Attempt(_registryCatalogue.Dispose, failures);
        Attempt(_registryDb.Dispose, failures);
        Attempt(_resources.Dispose, failures);
        if (failures.Count != 0)
        {
            throw new AggregateException("Closing the store failed; every instance was still closed that could be.", failures);
        }
    }

    private static void Attempt(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    /// <summary>
    /// The WAL retention a database opens with (spec 003 T2, R11.1): a replicated database retains its WAL by the
    /// configured TTL and size so a standby can tail it; a not-replicated database (and the registry) keeps RocksDB's
    /// default of no retention. Returns seconds and megabytes for <see cref="RocksInstance.Open"/>; (0, 0) is off.
    /// </summary>
    /// <summary>
    /// The WAL retention of a database as it opens (spec 003 R11.1): none for a database the store does not replicate;
    /// for a replicated one, the TTL, and a size limit that is its equal share of the store-wide budget
    /// (<c>WalBudgetBytes</c> over the live replicated databases), capped at <c>WalBytesPerDatabase</c> and never below
    /// 1 MiB. RocksDB cannot change this limit on an open database (it refuses the option at runtime), so a share
    /// lowered by a later create takes effect when each database next opens; until then the budget can be exceeded by
    /// the difference. Called under the store gate (the count reads the registry).
    /// </summary>
    private (ulong WalTtlSeconds, ulong WalSizeLimitMb) WalRetentionFor(bool replicated, bool counting = false)
    {
        if (!replicated || _options.Replication is not { } replication)
        {
            return (0, 0);
        }

        const long MiB = 1024 * 1024;
        var replicatedDatabases = 0;
        foreach (var record in _records.Values)
        {
            if (record.Replicated && record.State == LiveState)
            {
                replicatedDatabases++;
            }
        }

        var ttlSeconds = (ulong)Math.Max(0, (long)replication.WalTtl.TotalSeconds);
        if (replication.WalBytesPerDatabase <= 0)
        {
            // No size limit: the TTL alone bounds the WAL, and the budget is not applied. (RocksDB then checks the
            // archive every TTL/2; with a size limit as well, every 10 minutes.)
            return (ttlSeconds, 0);
        }

        // A create counts itself before its record exists.
        replicatedDatabases = Math.Max(1, replicatedDatabases + (counting ? 1 : 0));
        var share = Math.Max(MiB, Math.Min(replication.WalBytesPerDatabase, replication.WalBudgetBytes / replicatedDatabases));
        return (ttlSeconds, (ulong)(share / MiB));
    }

    /// <summary>The store's time provider (spec 004 R2.6), for replication's held-reader timing (spec 003 T7).</summary>
    internal TimeProvider Clock => _clock;

    /// <summary>The native iterators open now over the store's databases (spec 012 R8), for tests.</summary>
    internal long OpenIterators => _resources.OpenIterators;

    /// <summary>The store's instruments (spec 007), null until the engine is open.</summary>
    internal CrystalMetrics? Metrics => _metrics;

    /// <summary>The store's logger, null when it logs nothing.</summary>
    internal ILogger? Logger => _logger;

    private DatabaseContext NewDatabaseContext(RocksInstance instance, string name, uint id) =>
        new(instance, _clock, DatabaseLabel(name, id), _options.Durability, _logger, StartSyncTimer, _metrics, _options.SlowCommitThreshold, _options.KeyLockStripes, _options.MaxKeyBytes);

    // ------------------------------------------------------------------ durability (spec 006)

    /// <summary>Starts the WAL sync timer if it is not running (006 R4.1). Called after every Interval commit: cheap.</summary>
    internal void StartSyncTimer()
    {
        if (Volatile.Read(ref _syncTimer) is not null)
        {
            return;
        }

        lock (_timerSync)
        {
            if (_syncTimer is null && !_timerStopped)
            {
                // One-shot, re-armed after each tick, so a slow sync delays the next tick and never overlaps it.
                Volatile.Write(ref _syncTimer, _clock.CreateTimer(_ => SyncTick(), null, _options.SyncInterval, Timeout.InfiniteTimeSpan));
            }
        }
    }

    private void StopSyncTimer()
    {
        lock (_timerSync)
        {
            _timerStopped = true;
            _syncTimer?.Dispose();
            _stallTimer?.Dispose();
        }
    }

    /// <summary>Starts the store's instruments and the write-stall check (spec 007), once the store has opened.</summary>
    private void StartObservability(string nodeId)
    {
        try
        {
            _metrics = new CrystalMetrics(_options.MeterFactory, nodeId, _clock, OpenSlots, _registryDb, _options.BlockCacheBytes, _options.TagMetricsWithDatabase, () => _resources.OpenIterators);

            // The stall check only logs (its gauge reads RocksDB when collected), so without a logger it does not run.
            if (_logger is not null)
            {
                lock (_timerSync)
                {
                    _stallTimer = _clock.CreateTimer(_ => StallTick(), null, StallCheckInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch
        {
            // Review finding 3: a failed open disposes the registry; nothing may be left reading it.
            StopSyncTimer();
            _metrics?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One write-stall check (spec 007 R5): logs when RocksDB starts and stops holding writes back in a database. Ported
    /// from StowDB's <c>EngineMetricsPoller</c> transition logging. Never throws; always re-arms.
    /// </summary>
    private void StallTick()
    {
        try
        {
            var seen = new HashSet<uint>();
            foreach (var slot in OpenSlots())
            {
                IDisposable scope;
                try
                {
                    scope = slot.Enter(slot.Handle.Name);
                }
                catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException)
                {
                    continue;
                }

                string? state;
                using (scope)
                {
                    state = StallProbe is { } probe ? probe(slot) : WriteState(slot.Instance);
                }

                seen.Add(slot.Id);
                _stalled.TryGetValue(slot.Id, out var was);
                if (state != was && _logger is not null)
                {
                    var name = DatabaseLabel(slot.Handle.Name, slot.Id);
                    if (state is not null)
                    {
                        CrystalLog.WriteStallStarted(_logger, state, name);
                    }
                    else
                    {
                        CrystalLog.WriteStallEnded(_logger, name);
                    }
                }

                if (state is null)
                {
                    _stalled.Remove(slot.Id);
                }
                else
                {
                    _stalled[slot.Id] = state;
                }
            }

            foreach (var id in _stalled.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _stalled.Remove(id);
            }
        }
        catch (Exception)
        {
            // A logging provider throwing must not stop the check or crash a pool thread.
        }
        finally
        {
            lock (_timerSync)
            {
                if (!_timerStopped)
                {
                    _stallTimer?.Change(StallCheckInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>A snapshot of the store (spec 007 R4): memory and RocksDB properties only, no gate held.</summary>
    public StoreStatus Status(string nodeId)
    {
        var databases = new List<DatabaseStatus>();
        foreach (var slot in OpenSlots())
        {
            IDisposable scope;
            try
            {
                scope = slot.Enter(slot.Handle.Name);
            }
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException)
            {
                continue;
            }

            using (scope)
            {
                var instance = slot.Instance;
                var context = slot.Context;
                databases.Add(new DatabaseStatus(
                    slot.Id,
                    slot.Handle.Name,
                    context.LiveBoxCount,
                    context.Failed,
                    context.Failure?.InnerException?.Message,
                    context.LastWalSync,
                    new StorageStatus(
                        StorageGauges.Sum(instance, "rocksdb.num-deletes-active-mem-table") + StorageGauges.Sum(instance, "rocksdb.num-deletes-imm-mem-tables"),
                        StorageGauges.Sum(instance, "rocksdb.estimate-num-keys"),
                        StorageGauges.Sum(instance, "rocksdb.estimate-live-data-size"),
                        StorageGauges.Sum(instance, "rocksdb.estimate-pending-compaction-bytes"),
                        StorageGauges.SstFiles(instance),
                        StorageGauges.Sum(instance, "rocksdb.total-sst-files-size"),
                        StorageGauges.Db(instance, "rocksdb.is-write-stopped") != 0)
                    {
                        FilesPerLevel = StorageGauges.FilesPerLevel(instance),
                        ActiveMemtableBytes = StorageGauges.Sum(instance, "rocksdb.cur-size-active-mem-table"),
                        DelayedWriteRate = StorageGauges.Db(instance, "rocksdb.actual-delayed-write-rate"),
                    }));
            }
        }

        return new StoreStatus(
            nodeId,
            StoreFormat.SupportedFormat,
            _options.Durability,
            _options.SyncInterval,
            _metrics?.BlockCacheUsage() ?? 0,
            _options.BlockCacheBytes,
            databases);
    }

    /// <summary>
    /// One tick (006 R4.1, design § 3): syncs every open database with an Interval commit not yet synced. A database
    /// that is closing refuses the entry and is skipped; its close syncs it. A failed sync fails that database only.
    /// </summary>
    private void SyncTick()
    {
        try
        {
            foreach (var slot in OpenSlots())
            {
                IDisposable scope;
                try
                {
                    scope = slot.Enter(slot.Handle.Name);
                }
                catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException)
                {
                    continue;
                }

                using (scope)
                {
                    slot.Context.SyncForInterval();
                }
            }
        }
        catch (Exception)
        {
            // SyncForInterval records its own failures; what can still escape is a logging provider throwing. It must
            // neither stop the timer nor crash the process from a pool thread (006 review, finding 10).
        }
        finally
        {
            lock (_timerSync)
            {
                if (!_timerStopped)
                {
                    _syncTimer?.Change(_options.SyncInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>
    /// Syncs the WAL of every open database that has a commit not yet synced (006 R2.4). Throws
    /// <see cref="DatabaseFailedException"/> for the first database that has failed or whose sync fails, after trying
    /// every database.
    /// </summary>
    public void SyncAll()
    {
        DatabaseFailedException? first = null;
        foreach (var slot in OpenSlots())
        {
            IDisposable scope;
            try
            {
                scope = slot.Enter(slot.Handle.Name);
            }
            catch (Exception ex) when (ex is DatabaseNotFoundException or ObjectDisposedException)
            {
                continue;
            }

            using (scope)
            {
                try
                {
                    slot.Context.SyncAll();
                }
                catch (DatabaseFailedException ex)
                {
                    first ??= ex;
                }
            }
        }

        if (first is not null)
        {
            throw first;
        }
    }

    private DatabaseSlot[] OpenSlots() => Volatile.Read(ref _openSlots);

    /// <summary>Republishes the open slots. Called under the store gate, after every change to <c>_open</c>.</summary>
    private void PublishOpenSlots() => Volatile.Write(ref _openSlots, [.. _open.Values]);

    /// <summary>
    /// Writes a registry batch, always synced (006 R2.3). A failure fails the registry: it is logged at Critical, and
    /// every later registry change is refused with the way forward, as a failed database refuses writes (006 R5).
    /// </summary>
    private void WriteRegistry(WriteBatch batch)
    {
        if (Volatile.Read(ref _registryFailed) is { } failed)
        {
            throw new DatabaseFailedException(failed.Database, failed.Message, failed.InnerException);
        }

        try
        {
            _registryDb.Write(batch, DurabilityMode.Strict);
        }
        catch (Exception ex)
        {
            var registryFailed = new DatabaseFailedException(
                RegistryLabel,
                $"A write to {RegistryLabel} failed, so whether the disk holds it is unknown (006 R5). The store refuses " +
                "every database create, rename, swap and drop from now on; data in open databases is unaffected. Close " +
                "the store, check the disk, and open the store again: recovery replays what reached the disk.",
                ex);
            if (Interlocked.CompareExchange(ref _registryFailed, registryFailed, null) is null)
            {
                if (_logger is not null)
                {
                    CrystalLog.RegistryFailed(_logger, ex, RegistryLabel);
                }
            }

            throw registryFailed;
        }
    }

    /// <summary>
    /// Shut while this node is not the primary (003 addendum B2): every replicated database's context checks it before a
    /// local write. Set by the store's replication control.
    /// </summary>
    internal ReplicationWriteGate WriteGate { get; } = new();

    /// <summary>Whether metrics are tagged per database (007 R1.3): replication's lag follows the same choice.</summary>
    internal bool TagMetricsWithDatabase => _options.TagMetricsWithDatabase;

    /// <summary>
    /// Begins a new log incarnation for every open database (spec 003 R5.2, 003 addendum B3), at promotion and before
    /// the node accepts writes, so each base is the history this node received, not anything written after.
    /// </summary>
    internal bool BeginNewIncarnations(Func<bool> openWrites) => BeginNewIncarnations(openWrites, out _);

    /// <summary>
    /// As <see cref="BeginNewIncarnations(Func{bool})"/>, also returning every open replicated database that carries a
    /// resync marker, with its reason (spec 011 R2.4): the product decides whether to serve them.
    /// </summary>
    internal bool BeginNewIncarnations(Func<bool> openWrites, out IReadOnlyList<(string Name, string Reason)> pendingResync)
    {
        ArgumentNullException.ThrowIfNull(openWrites);
        var pending = new List<(string Name, string Reason)>();
        pendingResync = pending;

        // Under the store gate, with the write gate opened inside it: no database can open between its new history and
        // the first write it may take, so none is left without a record (tasks addendum, review of the fixes).
        lock (_gate)
        {
            Hook("promote.incarnations");
            foreach (var slot in _open.Values)
            {
                var record = _records[slot.Id];
                var latest = (long)slot.LatestSequence;
                if (record.Replicated && ResyncMarker.ReadReason(_dataDirectory, record.Uid) is { } marked)
                {
                    pending.Add((record.Name, marked));
                }

                // A database that still has a record holds this node's own writes that no other node received (it was
                // never applied to as a standby since): continue that history, so its base stays below them. One that
                // received its data as a standby has no record: a fresh history at what it received.
                var kept = record.Replicated ? IncarnationRecord.Read(_dataDirectory, record.Uid) : null;
                var incarnation = kept?.ContinuedAt(latest) ?? NewHistory(record.Uid, latest);
                if (record.Replicated && (_options.Replication is not null || kept is not null))
                {
                    TakeHistory(record.Uid, incarnation, latest);
                }

                slot.SetIncarnation(incarnation);
            }

            // Closed databases count too (review 2026-10-13, S5): after a restart a marked database is re-bootstrapped
            // without being opened, and the product must still hear about it. Its history is decided when it opens.
            foreach (var (id, record) in _records)
            {
                if (record.Replicated && record.State == LiveState && !_open.ContainsKey(id)
                    && ResyncMarker.ReadReason(_dataDirectory, record.Uid) is { } marked)
                {
                    pending.Add((record.Name, marked));
                }
            }

            return openWrites();
        }
    }

    /// <summary>
    /// The log history of a replicated database opening on a primary (003 addendum B3): its first open as a primary is
    /// a fresh history; a reopen continues the persisted one at what the log recovered to. Persisted before the handle
    /// is published, so before anything is written through it. A standby's databases, a store without replication, and
    /// a database the store does not replicate keep the in-memory fresh history (nothing serves it).
    /// </summary>
    private LogIncarnation? IncarnationOnOpen(bool replicated, string uid, long latest)
    {
        if (!replicated || WriteGate.RefusedByRole is not null)
        {
            return null;
        }

        // Without replication options a record is still continued if one exists: the database takes writes, and a later
        // run with replication must not continue a history that never saw them (tasks addendum, review of the fixes).
        var kept = IncarnationRecord.Read(_dataDirectory, uid);
        if (kept is null && _options.Replication is null)
        {
            return null;
        }

        var incarnation = kept?.ContinuedAt(latest) ?? NewHistory(uid, latest);
        TakeHistory(uid, incarnation, latest);
        return incarnation;
    }

    /// <summary>
    /// A history for a database this node starts serving as a primary with no history of its own (first open as a
    /// primary, or promotion after receiving it as a standby): a new incarnation that names the log its data came from
    /// (its follow record) and that log's history, each shared up to <paramref name="latest"/>, so a standby of that
    /// log continues and any other re-bootstraps (tasks addendum: exact history).
    /// </summary>
    private LogIncarnation NewHistory(string uid, long latest)
    {
        // A database marked for re-bootstrap for any reason but a gap may hold batches the log it followed does not (a
        // divergence, a rejoin): claiming that log as shared would let its other standbys continue on data it never
        // had. A fresh history, so they re-bootstrap from this node instead (spec 011 R2.3). A gap only means behind:
        // what it holds is a prefix of that log, which it continues.
        if (ResyncMarker.ReadReason(_dataDirectory, uid) is { } marked && !ResyncMarker.IsGap(marked))
        {
            return LogIncarnation.Fresh(latest);
        }

        if (FollowRecord.Read(_dataDirectory, uid)?.AsHistory() is not { } followed)
        {
            return LogIncarnation.Fresh(latest);
        }

        var history = new List<IncarnationEnd> { new(followed.Id, latest) };
        history.AddRange(followed.History.Select(h => h with { End = Math.Min(h.End, latest) }));
        return new LogIncarnation(Guid.NewGuid(), latest, history.Take(LogIncarnation.HistoryLimit).ToList());
    }

    /// <summary>
    /// Persists a primary's history and makes it the log this node holds: the follow record now names its own
    /// incarnation, so if it later follows another log it is recognised only where that log continues this one.
    /// </summary>
    private void TakeHistory(string uid, LogIncarnation incarnation, long latest)
    {
        IncarnationRecord.Write(_dataDirectory, uid, incarnation);
        FollowRecord.Write(_dataDirectory, uid, incarnation.Id, latest, incarnation.History);
    }

    private CrystalDatabase Publish(
        CrystalStore store, uint id, string name, string uid, RocksInstance instance, DatabaseContext context, LogIncarnation? incarnation)
    {
        context.WriteGate = _records[id].Replicated ? WriteGate : null;
        var slot = new DatabaseSlot(id, instance, context);
        if (incarnation is not null)
        {
            slot.SetIncarnation(incarnation);
        }

        slot.Handle = new CrystalDatabase(store, slot, name, uid);
        _open[id] = slot;
        PublishOpenSlots();
        return slot.Handle;
    }

    /// <summary>
    /// The record of a live database that <paramref name="database"/> is this store's handle to; refuses a dropped
    /// database, or a handle from another store (R5.7).
    /// </summary>
    private DatabaseRecord ResolveLive(CrystalDatabase database, string operation)
    {
        if (!_records.TryGetValue(database.Id, out var record)
            || record.State != LiveState
            || !_open.TryGetValue(database.Id, out var slot)
            || !ReferenceEquals(slot.Handle, database))
        {
            throw new DatabaseNotFoundException(
                database.Name,
                $"Database '{database.Name}' (id {database.Id}) cannot be {operation}: it has been dropped, or the handle " +
                "belongs to another store (R5.7).");
        }

        return record;
    }

    private void ThrowIfAtOpenLimit(string name)
    {
        if (_open.Count >= _options.MaxOpenDatabases)
        {
            throw new DatabaseLimitException(
                name,
                _options.MaxOpenDatabases,
                $"Database '{name}' cannot be opened: the store already has {_options.MaxOpenDatabases} open databases, " +
                "its MaxOpenDatabases limit (R9.2). Databases stay open until dropped or the store closes (R9.3), so " +
                "raise CrystalOptions.MaxOpenDatabases or drop databases that are no longer needed.");
        }
    }

    private void Hook(string step) => _options.Hook?.Invoke(step);

    /// <summary>
    /// Removes a dropped database's replication records, durably (spec 010 R11.1): its follow record, incarnation record
    /// and resync marker. Left behind, they would describe a history to a database restored later under the same UID,
    /// and a promotion would claim it. Best effort: a record that cannot be removed now is removed at the next open.
    /// </summary>
    internal void ForgetReplicationRecords(string uid, bool strict = false)
    {
        foreach (var path in new[]
        {
            FollowRecord.PathFor(_dataDirectory, uid),
            Path.Combine(IncarnationRecord.Directory(_dataDirectory), uid + ".json"),
            Path.Combine(ResyncMarker.Directory(_dataDirectory), uid),
        })
        {
            try
            {
                DurableFile.Delete(path);
            }
            catch (Exception ex) when (!strict && ex is IOException or UnauthorizedAccessException)
            {
                // Removed at the next open, from the dropped record (RunStartupRecovery).
            }
        }
    }

    // ------------------------------------------------------------------ backup (spec 010)

    /// <summary>The prefix of a restore's staging folder in the data directory; startup recovery deletes one left by a crash.</summary>
    internal const string RestoreStagingPrefix = "restore-";

    /// <summary>A live database as a backup set records it (spec 010 R3.1).</summary>
    internal readonly record struct BackupMember(uint Id, string Name, string Uid, bool Replicated, long CreatedTicks);

    /// <summary>The live databases at this moment, in id order, read under the store gate (spec 010 R3.1).</summary>
    internal IReadOnlyList<BackupMember> LiveDatabasesForBackup()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return [.. _aliases
                .Select(a => (Name: a.Key, Id: a.Value, Record: _records[a.Value]))
                .Where(a => a.Record.State == LiveState)
                .OrderBy(a => a.Id)
                .Select(a => new BackupMember(a.Id, a.Name, a.Record.Uid, a.Record.Replicated, a.Record.CreatedTicks))];
        }
    }

    /// <summary>
    /// The slot of database <paramref name="id"/> for its backup, opened if closed (spec 010 R3.3), or null when it was
    /// dropped meanwhile. <c>OpenedHere</c> says the backup opened it, so it closes it again after.
    /// </summary>
    internal (DatabaseSlot Slot, bool OpenedHere)? OpenForBackup(CrystalStore store, uint id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_records.TryGetValue(id, out var record) || record.State != LiveState)
            {
                return null;
            }

            return _open.TryGetValue(id, out var slot) ? (slot, false) : (OpenSlotLocked(store, id, record.Name), true);
        }
    }

    /// <summary>
    /// Closes a database the backup opened (spec 010 R3.3), unless the product took its handle meanwhile or it is no
    /// longer this store's open slot (dropped, closing).
    /// </summary>
    internal void CloseOpenedForBackup(DatabaseSlot slot)
    {
        lock (_gate)
        {
            if (_disposed || !_open.TryGetValue(slot.Id, out var open) || !ReferenceEquals(open, slot) || slot.HandedOut)
            {
                return;
            }

            // Closed under the gate (review S2): released before the instance is disposed, a concurrent open would find
            // no slot and open a second instance on the same folder while this one still holds its lock. Nobody holds
            // this slot but the backup, whose call has ended, so the wait is short.
            _open.Remove(slot.Id);
            PublishOpenSlots();
            slot.BeginClose(dropped: false);
            slot.WaitForCallsInProgress(_logger);
            slot.Close();
        }
    }

    // ------------------------------------------------------------------ restore (spec 010 design § 7)

    /// <summary>A database a restore will register: its name here, its UID (new for a copy) and its replicated flag.</summary>
    internal readonly record struct RestoreTarget(string Name, string Uid, bool Replicated);

    /// <summary>A database a restore has put in place, ready to register.</summary>
    internal readonly record struct RestoredDatabase(uint Id, string Name, string Uid, bool Replicated, long CreatedTicks);

    /// <summary>
    /// Step 4 (spec 010 design § 7): checks every name is free and every UID absent (R6.4), then advances the allocator
    /// past one id per target in one synced registry write (005 addendum A2), before any folder exists. Returns the ids.
    /// </summary>
    internal uint[] AllocateForRestore(IReadOnlyList<RestoreTarget> targets)
    {
        foreach (var target in targets)
        {
            DatabaseNameRules.Validate(target.Name);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckRestoreTargetsLocked(targets);
            if (_nextDatabaseId + (ulong)targets.Count - 1 > uint.MaxValue)
            {
                throw new InvalidOperationException("The store has issued every database id; ids are never reused (R4.3).");
            }

            var ids = new uint[targets.Count];
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = (uint)(_nextDatabaseId + (ulong)i);
                if (Directory.Exists(DatabasePath(ids[i])))
                {
                    throw new IOException(
                        $"The restore cannot use database id {ids[i]}: folder '{DatabasePath(ids[i])}' already exists and " +
                        "Crystal did not create it. Crystal owns its data directory (R2.2); move it out, then restore again.");
                }
            }

            var next = _nextDatabaseId + (ulong)targets.Count;
            using (var allocation = new WriteBatch())
            {
                allocation.Put(RegistryKey(TagAllocator), UInt64Bytes(next), _registryDb.Family(RegistryFamily));
                WriteRegistry(allocation);
            }

            _nextDatabaseId = next;
            Hook("restore.allocated");
            return ids;
        }
    }

    /// <summary>A staging folder for one restored database, in the data directory so its move into place is a rename.</summary>
    internal string RestoreStagingPath() => Path.Combine(_dataDirectory, RestoreStagingPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Opens a restored folder on its own and loads its catalogue expecting the database <paramref name="expectedUid"/>
    /// (spec 009 R2.3; spec 010 R6.6, R11.6), before it is moved into place. For a copy (<paramref name="newUid"/> set)
    /// rewrites its identity with the new UID (R6.5, R11.7). Returns the identity it now has.
    /// </summary>
    internal DatabaseIdentity CheckRestoredFolder(string staging, string expectedUid, string? newUid, string label)
    {
        var (walTtl, walSize) = WalRetentionFor(replicated: true);
        using var instance = RocksInstance.Open(staging, _resources, EmptyFamilyProfiles, walTtl, walSize);
        using var context = new DatabaseContext(instance, _clock, label);
        var identity = context.LoadCatalogue(DatabaseKind.Database, expectedUid);
        return newUid is null ? identity : context.ReplaceIdentityUid(identity, newUid);
    }

    /// <summary>Moves a checked staging folder into <c>db-{id}</c>, durably (spec 010 R11.5), and writes its name file.</summary>
    internal void MoveRestoredIntoPlace(string staging, uint id, string name)
    {
        var dbPath = DatabasePath(id);
        Directory.Move(staging, dbPath);
        DurableFile.SyncDirectory(_dataDirectory);
        WriteNameFile(dbPath, name);
        Hook("restore.moved");
    }

    /// <summary>
    /// Step 6 (spec 010 design § 7, R6.6): under the store gate, re-checks the names and UIDs, clears any replication
    /// records of the restored UIDs (R11.2), then registers every restored database in one synced registry batch through
    /// the generation path a create uses, so waiting pulls wake (R11.4). <paramref name="registering"/> turns true just
    /// before the batch is written: from there a failure keeps the folders, since the write may be durable (005 A9).
    /// </summary>
    internal void RegisterRestored(IReadOnlyList<RestoredDatabase> restored, ref bool registering)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckRestoreTargetsLocked([.. restored.Select(r => new RestoreTarget(r.Name, r.Uid, r.Replicated))]);
            foreach (var database in restored)
            {
                ForgetReplicationRecords(database.Uid, strict: true);
            }

            var records = restored.Select(r => (r.Id, Record: new DatabaseRecord
            {
                Version = DatabaseRecord.CurrentVersion,
                Name = r.Name,
                Uid = r.Uid,
                Replicated = r.Replicated,
                State = LiveState,
                CreatedTicks = r.CreatedTicks, // the database's own (spec 009 R2.3), read from its identity
                DroppedTicks = null,
            })).ToList();

            var generation = _generation + 1;
            using var batch = new WriteBatch();
            var family = _registryDb.Family(RegistryFamily);
            foreach (var (id, record) in records)
            {
                batch.Put(AliasKey(record.Name), UInt32Bytes(id), family);
                batch.Put(RecordKey(id), DatabaseRecord.Serialize(record), family);
            }

            batch.Put(RegistryKey(TagGeneration), UInt64Bytes((ulong)generation), family);
            Hook("restore.registering");
            registering = true;
            WriteRegistry(batch);

            PublishGeneration(generation);
            foreach (var (id, record) in records)
            {
                _aliases[record.Name] = id;
                _records[id] = record;
            }
        }
    }

    /// <summary>Removes a restore's folder after a failure before its registration (spec 010 design § 7). Best effort.</summary>
    internal void RemoveRestoredFolder(string path) => TryDeleteDirectory(path);

    private void CheckRestoreTargetsLocked(IReadOnlyList<RestoreTarget> targets)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (_aliases.ContainsKey(target.Name) || !names.Add(target.Name))
            {
                throw new DatabaseNameTakenException(
                    target.Name,
                    $"Database '{target.Name}' already exists here, or the restore names it twice (spec 010 R6.4). Restore it " +
                    "under another name (RestoreOptions.Rename), or drop the existing one first.");
            }

            var holder = _records.FirstOrDefault(r => r.Value.State == LiveState && string.Equals(r.Value.Uid, target.Uid, StringComparison.Ordinal));
            if (holder.Value is not null || !uids.Add(target.Uid))
            {
                var existing = holder.Value?.Name ?? target.Name;
                throw new DatabaseUidTakenException(
                    target.Uid,
                    existing,
                    $"The store already has database {target.Uid} (as '{existing}'), the one the restore would bring in as " +
                    $"'{target.Name}' (spec 010 R6.4). Drop the existing one first, or restore it as a copy " +
                    "(RestoreOptions.NewIdentity).");
            }
        }
    }

    /// <summary>Runs the test hook at a named step of replication (design addendum A8); no-op in production.</summary>
    internal void RunHook(string step) => Hook(step);

    internal string DatabasePath(uint id) => Path.Combine(_dataDirectory, DatabaseFolderName(id));

    internal static string DatabaseFolderName(uint id) => $"db-{id:x8}";

    /// <summary>Folders named exactly <c>db-{8 lowercase hex digits}</c>, the only names Crystal gives databases.</summary>
    private static IEnumerable<(string Path, uint Id)> EnumerateDatabaseFolders(string dataDirectory)
    {
        foreach (var path in Directory.EnumerateDirectories(dataDirectory))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 11
                && name.StartsWith("db-", StringComparison.Ordinal)
                && name.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") < 0)
            {
                yield return (path, uint.Parse(name.AsSpan(3), System.Globalization.NumberStyles.AllowHexSpecifier));
            }
        }
    }

    private static void WriteNameFile(string dbPath, string name) =>
        File.WriteAllText(Path.Combine(dbPath, NameFileName), name + "\n", Utf8NoBom);

    private void WriteNameFileBestEffort(uint id, string name)
    {
        try
        {
            WriteNameFile(DatabasePath(id), name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger is not null)
            {
                CrystalLog.NameFileNotWritten(_logger, ex, id);
            }
        }
    }

    /// <summary>Rewrites a name file that disagrees with the registry (R8.4). The registry decides.</summary>
    private void RepairNameFile(uint id, string name)
    {
        var path = Path.Combine(DatabasePath(id), NameFileName);
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path).TrimEnd('\n'), name, StringComparison.Ordinal))
        {
            if (_logger is not null)
            {
                CrystalLog.NameFileRepaired(_logger, id, name);
            }

            WriteNameFileBestEffort(id, name);
        }
    }

    /// <summary>The drop is committed; a folder that will not delete yet is removed by the next open (R8.3).</summary>
    private void TryDeleteDroppedFolder(string path)
    {
        try
        {
            DeleteDirectoryWithRetries(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger is not null)
            {
                CrystalLog.DroppedFolderNotDeleted(_logger, ex, path);
            }
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectoryWithRetries(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger is not null)
            {
                CrystalLog.FailedCreateFolderNotRemoved(_logger, ex, path);
            }
        }
    }

    internal static void DeleteDirectoryWithRetries(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                // Windows releases file handles of a closed instance shortly after the close returns.
                Thread.Sleep(20);
            }
        }
    }

    private static byte[] RegistryKey(byte tag) => BoxKeys.BuildKey(RegistryBoxId, [tag]);

    internal static byte[] AliasKey(string name)
    {
        var escaped = EscapedString.Encode(name);
        var key = new byte[escaped.Length + 1];
        key[0] = TagAlias;
        escaped.CopyTo(key.AsSpan(1));
        return BoxKeys.BuildKey(RegistryBoxId, key);
    }

    internal static byte[] RecordKey(uint id)
    {
        Span<byte> key = stackalloc byte[5];
        key[0] = TagRecord;
        BinaryPrimitives.WriteUInt32BigEndian(key[1..], id);
        return BoxKeys.BuildKey(RegistryBoxId, key);
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt64Bytes(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
