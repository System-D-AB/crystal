// Spec 003 R6 / design § 1, § 2 — the node's replication epoch and role, node-local and never shipped on the WAL (a
// replicated write must not clobber a standby's own epoch, design § 1). The epoch lives in `node.epoch` beside the
// store (spec 005's node-local files), rewritten atomically on change. T7 reads it to lead every PULL reply; the full
// promote/demote/fence/adoption machinery is T12 — this holder keeps the state and the on-disk file, nothing more.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SystemD.Crystal.Storage;

/// <summary>This node's replication role (spec 003 R6, design § 1). Minimal for T7; T12 drives the transitions.</summary>
internal enum ReplicationRole
{
    /// <summary>Serves pulls and accepts writes.</summary>
    Primary,

    /// <summary>Follows a primary; refuses local writes (R1.6).</summary>
    Standby,

    /// <summary>Serves nothing (a stood-down former primary).</summary>
    Fenced,
}

/// <summary>What adopting a higher epoch did (spec 003 R6.3).</summary>
internal enum EpochTransition
{
    /// <summary>No change: the observed epoch was not higher.</summary>
    None,

    /// <summary>A standby adopted the newer epoch and keeps following.</summary>
    Adopted,

    /// <summary>A primary or fenced node adopted the newer epoch and demoted to standby.</summary>
    Demoted,
}

/// <summary>
/// The node's epoch and role (spec 003 R6, design § 1). The epoch is a node-local counter persisted in
/// <c>node.epoch</c> at the store root and never replicated; it leads every <c>PULL</c> and <c>SNAPSHOT BEGIN</c>
/// reply and both <c>HELLO</c> epochs. A fresh node is epoch 1. Reads are lock-free; a change (T12) takes the lock,
/// persists atomically, then publishes.
/// </summary>
internal sealed class ReplicationEpoch
{
    internal const string FileName = "node.epoch";
    private const string TempSuffix = ".tmp";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly Lock _gate = new();
    private long _epoch;
    private ReplicationRole _role;

    private ReplicationEpoch(string path, long epoch, ReplicationRole role)
    {
        _path = path;
        _epoch = epoch;
        _role = role;
    }

    /// <summary>
    /// Loads the epoch and role from <c>node.epoch</c> in <paramref name="dataDirectory"/>: <c>"{epoch} {role}"</c>,
    /// where the role is <c>primary</c> or <c>standby</c> (003 addendum B2). A node with no file is fresh: epoch 1, as
    /// <paramref name="freshRole"/>. A fenced node was persisted as a standby: a restart ends the fence (R6.5) but never
    /// makes a node that stood down a primary again. Does not write the file: a node that never changes epoch or role
    /// leaves no <c>node.epoch</c> (spec 005 keeps the filename reserved).
    /// </summary>
    public static ReplicationEpoch Load(string dataDirectory, ReplicationRole freshRole)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var epoch = 1L;
        var role = freshRole;
        try
        {
            if (File.Exists(path))
            {
                var parts = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1
                    || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 1)
                {
                    throw new IOException(
                        $"'{path}' does not hold an epoch. It is written atomically, so this is damage or a hand edit: " +
                        "restore it, or write the node's last known epoch and role into it, such as '7 standby'.");
                }

                epoch = parsed;

                // Fail closed: anything but "primary" (an old one-field file, an unknown word) is a standby, never a
                // node that may write.
                role = parts.Length >= 2 && parts[1] == PersistedPrimary ? ReplicationRole.Primary : ReplicationRole.Standby;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"'{path}' cannot be read: {ex.Message}", ex);
        }

        // An IOException is not caught: a node that cannot read its epoch must not start at epoch 1 (R6.1).
        return new ReplicationEpoch(path, epoch, role);
    }

    private const string PersistedPrimary = "primary";
    private const string PersistedStandby = "standby";

    /// <summary>
    /// Called on every role change, with the new role, inside the lock (003 addendum B2), so two changes racing can never
    /// leave the store's write gate opposite to the role. Must be quick and must not call back into this object.
    /// </summary>
    public Action<ReplicationRole>? RoleChanged { get; set; }

    /// <summary>
    /// Called after every role change, with the new role, once the lock is released (spec 011 R5.1): the place for work
    /// that may wait on other threads, such as the write gate's wait for admitted writes. The call that changed the role
    /// returns only after it.
    /// </summary>
    public Action<ReplicationRole>? AfterRoleChanged { get; set; }

    /// <summary>The current epoch (lock-free read).</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>The current role (read under the lock, so it never sees a role the lock is changing).</summary>
    public ReplicationRole Role
    {
        get { lock (_gate) { return _role; } }
    }

    /// <summary>The role as a wire string (design § 8.2).</summary>
    public string RoleName => Role switch
    {
        ReplicationRole.Primary => Replication.ReplicationProtocol.RolePrimary,
        ReplicationRole.Standby => Replication.ReplicationProtocol.RoleStandby,
        ReplicationRole.Fenced => Replication.ReplicationProtocol.RoleFenced,
        _ => Replication.ReplicationProtocol.RoleFenced,
    };

    /// <summary>
    /// Sets the role (T12 drives promote/demote/fence; T7 uses it only to seed the starting role). Does not touch the
    /// epoch — a promotion that bumps the epoch calls <see cref="BumpTo"/> as well.
    /// </summary>
    public void SetRole(ReplicationRole role)
    {
        // Memory first, then disk: standing down (demote, fence, follow) takes effect at once even if the disk fails;
        // a failure to persist throws to the caller, whose node would otherwise come back with its old role.
        try
        {
            lock (_gate)
            {
                _role = role;
                RoleChanged?.Invoke(role);
                Persist(Interlocked.Read(ref _epoch));
            }
        }
        finally
        {
            AfterRoleChanged?.Invoke(role); // the role changed in memory even if the disk write failed
        }
    }

    /// <summary>
    /// Adopts <paramref name="newEpoch"/> if it is higher than the current one, persisting <c>node.epoch</c> first so
    /// a crash never leaves memory ahead of disk (R6.3; design § 1). Returns the epoch in force after the call. A
    /// value at or below the current epoch is a no-op. A persist failure throws (promotion uses
    /// <see cref="TryPromote"/>, which returns false instead).
    /// </summary>
    public long BumpTo(long newEpoch)
    {
        lock (_gate)
        {
            if (newEpoch <= Interlocked.Read(ref _epoch))
            {
                return Interlocked.Read(ref _epoch);
            }

            Persist(newEpoch);
            Interlocked.Exchange(ref _epoch, newEpoch);
            return newEpoch;
        }
    }

    /// <summary>
    /// Promotes to <paramref name="role"/> at <c>current epoch + 1</c>, persisting the new epoch **before** memory is
    /// updated (R6.2): a failure to persist leaves epoch and role unchanged and returns false with the cause in
    /// <paramref name="failure"/>, so the node never serves as a primary at an epoch the disk does not hold. On success
    /// the new epoch is <paramref name="newEpoch"/>: <c>max(epoch + 1, <paramref name="minimum"/>)</c> (spec 013 R6), one
    /// persisted write however far the floor lies.
    /// </summary>
    public bool TryPromote(ReplicationRole role, out long newEpoch, out IOException? failure, long minimum = 0)
    {
        failure = null;
        lock (_gate)
        {
            var target = Math.Max(Interlocked.Read(ref _epoch) + 1, minimum);
            try
            {
                Persist(target, role); // persist first (R6.2)
            }
            catch (IOException ex)
            {
                newEpoch = Interlocked.Read(ref _epoch);
                failure = ex;
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                newEpoch = Interlocked.Read(ref _epoch);
                failure = new IOException(ex.Message, ex);
                return false;
            }

            Interlocked.Exchange(ref _epoch, target);
            _role = role;
            RoleChanged?.Invoke(role);
            newEpoch = target;
        }

        AfterRoleChanged?.Invoke(role);
        return true;
    }

    /// <summary>
    /// Adopts <paramref name="observedEpoch"/> if higher, persisting first (R6.3), and sets the role as a peer at that
    /// epoch requires: a node that believed itself primary or fenced <b>demotes</b> to standby; a standby stays a
    /// standby (it simply follows the newer primary). Returns the transition that happened, for the caller to raise
    /// the event. An equal or lower epoch is handled by the caller (conflict / ignore); this only adopts a higher one.
    /// </summary>
    public EpochTransition ObserveHigherEpoch(long observedEpoch)
    {
        EpochTransition transition;
        var stoodDown = false;
        try
        {
            lock (_gate)
            {
                if (observedEpoch <= Interlocked.Read(ref _epoch))
                {
                    return EpochTransition.None;
                }

                var wasPrimaryOrFenced = _role is ReplicationRole.Primary or ReplicationRole.Fenced;
                if (wasPrimaryOrFenced)
                {
                    _role = ReplicationRole.Standby; // stand down before the disk write: never a primary at a stale epoch
                    RoleChanged?.Invoke(ReplicationRole.Standby);
                    stoodDown = true;
                }

                Persist(observedEpoch);
                Interlocked.Exchange(ref _epoch, observedEpoch);
                transition = wasPrimaryOrFenced ? EpochTransition.Demoted : EpochTransition.Adopted;
            }
        }
        finally
        {
            if (stoodDown)
            {
                AfterRoleChanged?.Invoke(ReplicationRole.Standby);
            }
        }

        return transition;
    }

    private void Persist(long epoch) => Persist(epoch, _role);

    /// <summary>
    /// Writes <c>"{epoch} {role}"</c> atomically (temp, flush to disk, rename). A fenced node is written as a standby
    /// (see <see cref="Load"/>). Called under <c>_gate</c>.
    /// </summary>
    private void Persist(long epoch, ReplicationRole role)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + TempSuffix;
        var persistedRole = role == ReplicationRole.Primary ? PersistedPrimary : PersistedStandby;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8NoBom.GetBytes(epoch.ToString(CultureInfo.InvariantCulture) + " " + persistedRole + "\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        DurableFile.Replace(temp, _path); // the rename made durable (spec 011 R9.1)
    }
}
