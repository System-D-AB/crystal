using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SystemD.Crystal.Storage;

/// <summary>
/// One open database: its instance, its catalogue, its handle, the calls in progress through it and its closing
/// state (spec 005 R5.6, R5.7, R7.1; design addendum A3).
/// </summary>
/// <remarks>
/// A call enters without a lock: it increments the in-flight counter, then reads the closing state. Closing sets the
/// state, then reads the counter. Both sides use interlocked operations (full fences), so either the call sees the
/// closing state and is refused, or closing sees the call and waits for it.
/// </remarks>
internal sealed class DatabaseSlot
{
    private const int Open = 0;
    private const int Dropped = 1;
    private const int StoreClosed = 2;
    private const int Replaced = 3; // re-created under the same UID by a standby's snapshot install (spec 013 R7)

    private static readonly TimeSpan WaitWarningInterval = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly HashSet<StorageSnapshot> _snapshots = [];
    // Never disposed: a refused call can still set it after the slot is closed.
    private readonly ManualResetEventSlim _drained = new(initialState: false);
    private int _inFlight;
    private int _closing;
    private bool _closed;

    public DatabaseSlot(uint id, RocksInstance instance, DatabaseContext context)
    {
        Id = id;
        Instance = instance;
        Context = context;
        _incarnation = LogIncarnation.Fresh((long)instance.Db.GetLatestSequenceNumber());
    }

    private LogIncarnation _incarnation;

    /// <summary>
    /// This database's log history as served from this node (spec 003 R5.2, design § 3). Set by the store before the
    /// database's handle is published, so before anything can be written through it, and again for every open database
    /// at promotion, before the node accepts writes (003 addendum B3). A fresh, unrelated history until then.
    /// </summary>
    public LogIncarnation Incarnation => Volatile.Read(ref _incarnation);

    /// <summary>Sets the log history (open, promotion).</summary>
    public void SetIncarnation(LogIncarnation incarnation) => Volatile.Write(ref _incarnation, incarnation);

    /// <summary>Whether the slot is closing (dropped or the store closing): anything cached on it must be let go.</summary>
    public bool IsClosing => Volatile.Read(ref _closing) != Open;

    public uint Id { get; }

    public RocksInstance Instance { get; }

    public DatabaseContext Context { get; }

    /// <summary>The instance's latest sequence number: the cost tests' count of what a lifecycle step wrote here.</summary>
    internal ulong LatestSequence => Instance.Db.GetLatestSequenceNumber();

    /// <summary>The database's one handle (design addendum A3). Set once, by the engine, before it is published.</summary>
    public CrystalDatabase Handle { get; set; } = null!;

    /// <summary>
    /// Enters one call. Refused with <see cref="DatabaseNotFoundException"/> once the database is dropped or replaced
    /// (a standby re-installed it from a snapshot), or with <see cref="ObjectDisposedException"/> once the store is
    /// closing (R5.7; spec 013 R7). A store that is still open never reports itself closed.
    /// </summary>
    public IDisposable Enter(string name)
    {
        Interlocked.Increment(ref _inFlight);
        var closing = Volatile.Read(ref _closing);
        if (closing != Open)
        {
            Exit();
            throw closing switch
            {
                Dropped => new DatabaseNotFoundException(
                    name,
                    $"Database '{name}' (id {Id}) has been dropped. A handle is never redirected to a database created " +
                    "later under the same name (R5.7); open that one by name."),
                Replaced => new DatabaseNotFoundException(
                    name,
                    $"Database '{name}' (id {Id}) was re-created from its primary's snapshot (a re-bootstrap on this standby), " +
                    "so this handle and its box handles no longer address it. The store is open: open the database by " +
                    "name again for its new handle."),
                _ => new ObjectDisposedException(nameof(CrystalStore), $"The store holding database '{name}' has been closed."),
            };
        }

        return new Scope(this);
    }

    private readonly List<Action> _closeActions = [];

    /// <summary>
    /// Registers work to run when the slot closes, after the calls in progress have finished and before the instance is
    /// disposed: replication releases its WAL readers here, which would otherwise call into a closed instance.
    /// </summary>
    public void OnClose(Action action)
    {
        lock (_sync)
        {
            if (_closed)
            {
                action();
                return;
            }

            _closeActions.Add(action);
        }
    }

    /// <summary>
    /// Enters one call like <see cref="Enter"/>, or returns null once the slot is closing (replication serving a
    /// database that is being dropped or closed treats it as gone instead of throwing).
    /// </summary>
    public IDisposable? TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _closing) != Open)
        {
            Exit();
            return null;
        }

        return new Scope(this);
    }

    private BackupEngineHandle? _runningBackup;

    /// <summary>
    /// Whether the product holds this database's handle (it created or opened it). A database the backup opened, and
    /// nobody else asked for meanwhile, is closed again after its copy (spec 010 R3.3).
    /// </summary>
    internal bool HandedOut { get; set; }

    /// <summary>
    /// Registers the copy a backup is running on this database (spec 010 design § 5), so a drop or a store close stops it
    /// rather than waiting for it. Returns false when the slot is already closing: the copy must not start.
    /// </summary>
    internal bool BeginBackup(BackupEngineHandle engine)
    {
        // A full fence between this write and the read below, as BeginClose's exchange is on its side: either the
        // closer sees the copy and stops it, or the copy sees the closing and never starts.
        Interlocked.Exchange(ref _runningBackup, engine);
        if (Volatile.Read(ref _closing) != Open)
        {
            Volatile.Write(ref _runningBackup, null);
            return false;
        }

        return true;
    }

    /// <summary>Whether the slot was closed by a drop (not by a store close).</summary>
    internal bool WasDropped => Volatile.Read(ref _closing) == Dropped;

    /// <summary>The copy has ended (spec 010 design § 5).</summary>
    internal void EndBackup() => Volatile.Write(ref _runningBackup, null);

    /// <summary>Stops a backup copy running on this database, if any (a drop or a close, after <see cref="BeginClose"/>).</summary>
    internal void StopRunningBackup() => Volatile.Read(ref _runningBackup)?.Stop();

    /// <summary>Marks the slot closing; from here every new call is refused. Returns false if it already was.</summary>
    public bool BeginClose(bool dropped) =>
        Interlocked.CompareExchange(ref _closing, dropped ? Dropped : StoreClosed, Open) == Open;

    /// <summary>
    /// Marks the slot closing because its database is being re-created in place (a standby's snapshot install, spec 013
    /// R7): every new call through the old handle is refused with <see cref="DatabaseNotFoundException"/>, saying to
    /// open the database again. Returns false if it was already closing.
    /// </summary>
    public bool BeginReplace() => Interlocked.CompareExchange(ref _closing, Replaced, Open) == Open;

    /// <summary>Waits until no call is in progress. Call after <see cref="BeginClose"/>.</summary>
    public void WaitForCallsInProgress(ILogger? logger)
    {
        while (Volatile.Read(ref _inFlight) != 0)
        {
            if (!_drained.Wait(WaitWarningInterval))
            {
                if (logger is not null)
                {
                    Observability.CrystalLog.CloseWaitingForCalls(logger, Id, Volatile.Read(ref _inFlight));
                }
            }
        }
    }

    /// <summary>Opens a native snapshot that closing releases if its holder has not (design addendum A3).</summary>
    public StorageSnapshot OpenSnapshot(string name)
    {
        using var _ = Enter(name);
        lock (_sync)
        {
            var snapshot = Context.CreateSnapshot();
            _snapshots.Add(snapshot);
            return snapshot;
        }
    }

    /// <summary>Releases a snapshot, unless closing already did.</summary>
    public void ReleaseSnapshot(StorageSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshots.Remove(snapshot))
            {
                snapshot.Dispose();
            }
        }
    }

    /// <summary>
    /// Releases snapshots still held, then closes the catalogue and the instance. Idempotent. Call after
    /// <see cref="WaitForCallsInProgress"/>.
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            foreach (var snapshot in _snapshots)
            {
                snapshot.Dispose();
            }

            _snapshots.Clear();

            foreach (var action in _closeActions)
            {
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // Releasing what was registered must not keep the instance open; each action is best effort.
                }
            }

            _closeActions.Clear();

            // Spec 006 R4.2: commits not yet synced are synced before the instance closes; a dropped database's data
            // is deleted, so it is not.
            if (Volatile.Read(ref _closing) != Dropped)
            {
                Context.SyncBeforeClose();
            }

            Context.Dispose();
            Instance.Dispose();
        }
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _closing) != Open)
        {
            _drained.Set();
        }
    }

    private sealed class Scope(DatabaseSlot slot) : IDisposable
    {
        private DatabaseSlot? _slot = slot;

        public void Dispose() => Interlocked.Exchange(ref _slot, null)?.Exit();
    }
}
