using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Backup;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Observability;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>
/// A Crystal store: one data directory holding any number of databases (spec 005 R1, design § 6).
/// One open store per directory per machine; disposing closes every open database and releases the lock.
/// </summary>
public sealed class CrystalStore : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _nodeId;
    private readonly StoreLock _lock;
    private readonly StoreEngine _engine;
    private readonly ILogger? _logger;
    private readonly CrystalReplication _replication;
    private readonly CancellationTokenSource _closing = new(); // cancelled by Dispose: stops a running backup (spec 010 R3.4)
    private BackupProgress? _backup; // the running backup, if any (spec 010 R1.6)
    private readonly object _operationsGate = new();
    private int _operations; // backups and restores running; Dispose waits for them before closing anything
    private int _disposed;

    private CrystalStore(string dataDirectory, string nodeId, StoreLock storeLock, StoreEngine engine, ReplicationOptions? replication, System.Diagnostics.Metrics.IMeterFactory? meterFactory, ILogger? logger)
    {
        _dataDirectory = dataDirectory;
        _nodeId = nodeId;
        _lock = storeLock;
        _engine = engine;
        _logger = logger;
        _replication = new CrystalReplication(this, engine, nodeId, dataDirectory, replication, meterFactory, logger);
    }

    /// <summary>The store's replication control (spec 003 design § 10): listen, follow, promote.</summary>
    public CrystalReplication Replication => _replication;

    /// <summary>The absolute path of the store's data directory.</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>The node's unique identity GUID (spec 005 R3.4).</summary>
    public string NodeId => _nodeId;

    /// <summary>The current registry generation (spec 005 R4.2).</summary>
    public long RegistryGeneration => _engine.RegistryGeneration;

    internal StoreEngine Engine => _engine;

    /// <summary>
    /// Opens a Crystal store at <paramref name="dataDirectory"/> (spec 005 R1.1; design addendum A1): checks the
    /// format stamp without writing (R3), takes the store lock (R1.3), stamps a new store or verifies the stamp and
    /// node identity under the lock, opens the registry (R4) and runs startup recovery (R8). Product databases stay
    /// closed until asked for. Completes synchronously.
    /// </summary>
    public static CrystalStore Open(string dataDirectory, CrystalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        options ??= new CrystalOptions();
        ValidateOptions(options);

        var fullPath = Path.GetFullPath(dataDirectory);
        var logger = options.LoggerFactory?.CreateLogger<CrystalStore>();
        try
        {
            return Open(fullPath, options, logger);
        }
        catch (CrystalFormatException ex)
        {
            LogRefusal(logger, $"the store at '{fullPath}'", ex);
            throw;
        }
    }

    private static CrystalStore Open(string fullPath, CrystalOptions options, ILogger? logger)
    {
        // 1. Refuse unstamped content or a foreign stamp before writing anything (R3.1, R3.2).
        StoreFormat.CheckBeforeLock(fullPath);

        // 2. The store lock (R1.3).
        Directory.CreateDirectory(fullPath);
        var storeLock = StoreLock.Acquire(fullPath);
        StoreEngine? engine = null;
        CrystalStore? store = null;

        try
        {
            // 3. Under the lock: stamp a new store, or verify the stamp and node.id (R1.4, R3.3, R3.4).
            var nodeId = StoreFormat.StampOrVerify(fullPath);

            // 4. The registry, then startup recovery (R4.4, R8.3).
            engine = StoreEngine.Open(fullPath, options, nodeId);
            if (logger is not null)
            {
                CrystalLog.StoreOpened(logger, fullPath, nodeId, options.Durability);
            }

            store = new CrystalStore(fullPath, nodeId, storeLock, engine, options.Replication, options.MeterFactory, logger);

            // A rejoin scheduled before this start follows its primary now (spec 011 R11.4), once the store is whole.
            store._replication.ResumeScheduledRejoin();
            return store;
        }
        catch
        {
            store?._replication.Dispose(); // its event thread and meter, when the rejoin at open failed (review M6)
            engine?.Dispose();
            storeLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Empties a store offline (spec 005 R10; design addendum A7): deletes the registry and every database folder,
    /// recreates an empty registry, and keeps <c>crystal.format</c>, <c>node.id</c> and <c>node.epoch</c>.
    /// Refused while the store is open anywhere (R10.2), and for a directory that is not a Crystal store this build
    /// reads.
    /// </summary>
    public static void Reset(string dataDirectory, CrystalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        StoreEngine.Reset(dataDirectory, options ?? new CrystalOptions());
    }

    /// <summary>
    /// Creates a new database in the store (spec 005 R5.1).
    /// Allocates an immutable id, creates the folder <c>db-{id}</c>, writes its <c>name</c> file,
    /// and commits the alias and record to the registry.
    /// </summary>
    public CrystalDatabase CreateDatabase(string name, bool replicated = true)
    {
        ThrowIfDisposed();
        if (replicated)
        {
            using var admitted = AdmitIfPrimary(_engine.WriteGate, $"create replicated database '{name}'");
            _engine.RunHook("create.admitted");
            return _engine.CreateDatabase(this, name, replicated);
        }

        return _engine.CreateDatabase(this, name, replicated);
    }

    /// <summary>
    /// Opens an existing database by name (spec 005 R5.2).
    /// Resolves the name through the in-memory registry, and returns the database's one handle if it is already open.
    /// </summary>
    public CrystalDatabase OpenDatabase(string name)
    {
        ThrowIfDisposed();
        try
        {
            return _engine.OpenDatabase(this, name);
        }
        catch (CrystalFormatException ex)
        {
            // A catch, not a filter: a filter runs before the store gate is released (007 review, finding 10).
            LogRefusal(_logger, $"database '{name}'", ex);
            throw;
        }
    }

    /// <summary>
    /// A snapshot of the store and its open databases (spec 007 R4): node, format, durability, the shared cache, and per
    /// database its boxes, failure, last WAL sync and RocksDB's view of it. Reads memory and RocksDB properties only;
    /// synchronous, cost O(open databases).
    /// </summary>
    public StoreStatus Status()
    {
        ThrowIfDisposed();
        var status = _engine.Status(_nodeId);
        return Volatile.Read(ref _backup) is { } running
            ? status with { Backup = new BackupStatus(running.Destination, running.DatabasesDone, running.DatabasesTotal, running.BytesCopied) }
            : status;
    }

    /// <summary>Logs a refused format (spec 007 R3.3, event FormatRefused).</summary>
    private static void LogRefusal(ILogger? logger, string what, CrystalFormatException refusal)
    {
        if (logger is not null)
        {
            CrystalLog.FormatRefused(logger, what, refusal.Message, refusal.WayForward);
        }
    }

    /// <summary>
    /// Syncs the WAL of every open database that has a commit not yet synced (spec 006 R2.4), so a run of
    /// <see cref="DurabilityMode.Buffered"/> or <see cref="DurabilityMode.Interval"/> commits is durable from here.
    /// Synchronous: one WAL sync per such database. Throws <see cref="DatabaseFailedException"/> if a database has
    /// failed or its sync fails, after trying every database.
    /// </summary>
    public void SyncAll()
    {
        ThrowIfDisposed();
        _engine.SyncAll();
    }

    private static void ValidateOptions(CrystalOptions options)
    {
        if (!Enum.IsDefined(options.Durability))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Durability, "CrystalOptions.Durability is not a durability mode.");
        }

        if (options.MaxKeyBytes is < 1 or > 1 << 20)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxKeyBytes, "CrystalOptions.MaxKeyBytes must be from 1 to 1 MiB, or null (spec 012 R9).");
        }

        if (options.KeyLockStripes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.KeyLockStripes, "CrystalOptions.KeyLockStripes must be at least 1 (spec 012 R3.7).");
        }

        if (options.SyncInterval < TimeSpan.FromMilliseconds(1) || options.SyncInterval > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.SyncInterval, "CrystalOptions.SyncInterval must be from 1 ms to 10 s (spec 006 R2.1).");
        }
    }

    /// <summary>
    /// Lists all live databases in the store from the in-memory registry (spec 005 R5.3).
    /// </summary>
    public IReadOnlyList<DatabaseInfo> ListDatabases()
    {
        ThrowIfDisposed();
        return _engine.ListDatabases();
    }

    /// <summary>
    /// Renames a database alias online (spec 005 R5.4).
    /// Updates the alias table, record, and <c>name</c> file. Open handles and data are untouched.
    /// </summary>
    public void RenameDatabase(CrystalDatabase database, string newName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(database);
        using var admitted = AdmitIfPrimary(database.Slot.Context.WriteGate, $"rename replicated database '{database.Name}'");
        _engine.RenameDatabase(database, newName);
    }

    /// <summary>
    /// Atomically swaps the names of two live databases in one registry batch (spec 005 R5.5).
    /// </summary>
    public void SwapDatabases(CrystalDatabase first, CrystalDatabase second)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        using var firstAdmitted = AdmitIfPrimary(first.Slot.Context.WriteGate, $"swap replicated database '{first.Name}'");
        using var secondAdmitted = AdmitIfPrimary(second.Slot.Context.WriteGate, $"swap replicated database '{second.Name}'");
        _engine.SwapDatabases(first, second);
    }

    /// <summary>
    /// Drops a database (spec 005 R5.6).
    /// Commits the record as dropped, removes the alias, waits for in-flight operations, closes the instance,
    /// and deletes its folder.
    /// </summary>
    public void DropDatabase(CrystalDatabase database)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(database);
        using var admitted = AdmitIfPrimary(database.Slot.Context.WriteGate, $"drop replicated database '{database.Name}'");
        _engine.DropDatabase(database);
    }

    /// <summary>
    /// Backs up every live database into the backup root <paramref name="destination"/> (spec 010 R1): an absolute path,
    /// empty or already a backup root, outside the data directory. The store keeps serving throughout. Databases are
    /// backed up one at a time, <b>each captured at its own moment</b>, not one moment across databases (R1.4): a product
    /// that needs an invariant across data keeps that data in one database. Every commit acknowledged before a database's
    /// copy began is in it, whatever its durability mode (R1.3). A database dropped during the backup is listed as
    /// omitted. The set exists once its manifest is written, last; a backup that fails, is cancelled or is killed leaves
    /// no set and earlier sets untouched (R4.1, R4.4).
    /// <para><b>Yields:</b> the native work runs on a thread-pool thread (R8.2). Allowed on any node, primary or
    /// standby: it only reads (R11.3). One backup at a time per store: a second is refused with
    /// <see cref="BackupInProgressException"/> (R1.6).</para>
    /// </summary>
    /// <exception cref="BackupDestinationException">The destination is relative, inside the data directory or containing it, or a non-empty folder that is not a backup root.</exception>
    /// <exception cref="BackupRootLockedException">Another operation holds the root (R2.4).</exception>
    /// <exception cref="BackupInProgressException">A backup of this store is running.</exception>
    /// <exception cref="DatabaseLimitException">A closed database could not be opened for its backup under <c>MaxOpenDatabases</c> (R3.3).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the copy stops promptly (R4.4).</exception>
    /// <exception cref="ObjectDisposedException">The store was closed during the backup (R3.4).</exception>
    public Task<BackupSetInfo> BackupAsync(string destination, BackupOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        options ??= new BackupOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BackgroundOperations, 1, nameof(options));
        if (options.RateLimitBytesPerSecond is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RateLimitBytesPerSecond, "BackupOptions.RateLimitBytesPerSecond cannot be negative.");
        }

        var root = BackupRoot.CheckDestination(destination, _dataDirectory);
        var progress = new BackupProgress(root);
        if (Interlocked.CompareExchange(ref _backup, progress, null) is { } running)
        {
            throw new BackupInProgressException(
                running.Destination,
                $"A backup of this store into '{running.Destination}' is running; one backup at a time (spec 010 R1.6). " +
                "Wait for it to finish, or cancel it.");
        }

        if (!EnterOperation())
        {
            Interlocked.CompareExchange(ref _backup, null, progress);
            ThrowIfDisposed();
        }

        var closing = _closing.Token;
        return Task.Run(
            () =>
            {
                try
                {
                    return BackupRunner.Run(this, root, options, progress, closing, cancellationToken);
                }
                catch (Exception ex)
                {
                    _engine.Metrics?.BackupFailed();
                    if (_logger is not null)
                    {
                        CrystalLog.BackupFailed(_logger, ex, root);
                    }

                    throw;
                }
                finally
                {
                    Interlocked.CompareExchange(ref _backup, null, progress);
                    LeaveOperation();
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Attaches databases of backup set <paramref name="setId"/>, in the backup root <paramref name="source"/>, to this
    /// store (spec 010 R6), all or none: each gets a new id here and keeps its UID, creation time and boxes, unless
    /// <see cref="RestoreOptions.NewIdentity"/> restores it as a copy with a new UID. Before anything is written the set's
    /// checksum and formats are checked, each database is quick-verified, and every name and UID must be free here.
    /// Only once every database is in place and its identity checked are all of them registered, in one synced registry
    /// batch; until then none is visible. The store keeps serving its other databases. Returns the restored databases.
    /// <para><b>Yields:</b> the native work runs on a thread-pool thread (R8.2). Restoring replicated databases needs
    /// this node to be the primary (R11.3); restored as not replicated (<see cref="RestoreOptions.Replicated"/> false),
    /// they may be restored on any node. A restored replicated database starts a fresh history; standbys bootstrap it
    /// like any new database (R11.2, R11.4).</para>
    /// </summary>
    /// <exception cref="BackupDestinationException">The source is not an absolute path to a backup root.</exception>
    /// <exception cref="BackupRootLockedException">Another operation holds the root (R2.4).</exception>
    /// <exception cref="BackupSetNotFoundException">The root has no such set.</exception>
    /// <exception cref="BackupDamagedException">The manifest, or a chosen database's backup, is damaged.</exception>
    /// <exception cref="CrystalFormatException">The set records a format this build does not read (R6.2).</exception>
    /// <exception cref="DatabaseNameTakenException">A restored name is already live here.</exception>
    /// <exception cref="DatabaseUidTakenException">A restored UID is already live here; restore as a copy instead.</exception>
    /// <exception cref="NotPrimaryException">Replicated databases, and this node is not the primary accepting writes.</exception>
    public Task<IReadOnlyList<DatabaseInfo>> RestoreAsync(string source, string setId, RestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(setId);
        options ??= new RestoreOptions();
        if (!EnterOperation())
        {
            ThrowIfDisposed();
        }

        var closing = _closing.Token;
        return Task.Run(
            () =>
            {
                try
                {
                    return RestoreRunner.Run(this, source, setId, options, closing, cancellationToken);
                }
                catch (Exception ex)
                {
                    if (_logger is not null)
                    {
                        CrystalLog.RestoreFailed(_logger, ex, source, setId);
                    }

                    throw;
                }
                finally
                {
                    LeaveOperation();
                }
            },
            CancellationToken.None);
    }

    // Counted under a lock that Dispose takes after marking the store disposed: an operation either enters before and is
    // waited for, or sees the store disposed and never starts.
    private bool EnterOperation()
    {
        lock (_operationsGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            _operations++;
            return true;
        }
    }

    private void LeaveOperation()
    {
        lock (_operationsGate)
        {
            _operations--;
            Monitor.PulseAll(_operationsGate);
        }
    }

    /// <summary>The running backup, for <c>Status()</c> (spec 010 R7.3).</summary>
    internal BackupProgress? RunningBackup => Volatile.Read(ref _backup);

    /// <summary>
    /// The set of replicated databases is the primary's (spec 003 R1.4, R1.6; addendum 2026-10-10 review): a standby,
    /// demoted or fenced node refuses to change it. Replication's own reconcile changes it through the engine. The
    /// change holds the returned admission until it ends, so a demotion or fence waits for it.
    /// </summary>
    internal static Storage.ReplicationWriteGate.Admission AdmitIfPrimary(Storage.ReplicationWriteGate? gate, string operation)
    {
        var admitted = Storage.ReplicationWriteGate.Admit(gate, out var refusedAs);
        if (refusedAs is { } role)
        {
            throw new NotPrimaryException(
                $"Cannot {operation}: this node is {role}, and the set of replicated databases is the " +
                "primary's (R1.4, R1.6). Do it on the primary, or promote this node.");
        }

        return admitted;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>
    /// Closes the store (spec 005 R1.2; design addendum A3): refuses new calls through every handle, waits for calls
    /// and drops in progress, closes every open database, the registry and the shared resources, and releases the
    /// store lock. Blocks for one WAL sync per database with commits not yet synced (spec 006 R4.2). Throws an
    /// <see cref="AggregateException"/> holding a <see cref="DatabaseFailedException"/> for every database, and the
    /// registry, that failed (spec 006 R5.1); the store is still closed and its lock released.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A running backup or restore is stopped and waited for before any database closes (spec 010 R3.4, design § 4).
        _closing.Cancel();
        lock (_operationsGate)
        {
            while (_operations > 0)
            {
                Monitor.Wait(_operationsGate);
            }
        }

        // Stop the listener and follower before the engine closes, so no connection touches a closing database.
        _replication.Dispose();

        try
        {
            _engine.Dispose();
        }
        finally
        {
            _closing.Dispose();
            _lock.Dispose();
            if (_logger is not null)
            {
                CrystalLog.StoreClosed(_logger, _dataDirectory, _nodeId, _engine.Durability);
            }
        }
    }
}
