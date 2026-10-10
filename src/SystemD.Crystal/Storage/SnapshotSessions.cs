// Spec 003 T10 / R7; design § 7 — the primary side of snapshot bootstrap: a session is a RocksDB checkpoint of one
// database (hard-linked beside it) plus a manifest of its files with sizes and CRC-32C, served in chunks and expiring
// after an idle or absolute limit. RocksDB-touching (checkpoints via WalAccess), so it lives in Storage.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SystemD.Crystal.Storage;

/// <summary>One file in a snapshot manifest (design § 7): its path relative to the checkpoint, size and CRC-32C.</summary>
internal readonly record struct SnapshotFile(string RelativePath, long Size, uint Crc32C);

/// <summary>
/// A live snapshot session (spec 003 R7.5): the checkpoint directory, the manifest, the sequence it was taken at, and
/// the serving incarnation and base. Expires after <see cref="SnapshotSessions.IdleLimit"/> idle or
/// <see cref="SnapshotSessions.AbsoluteLimit"/> total, deleting the checkpoint.
/// </summary>
internal sealed class SnapshotSession
{
    public required string Id { get; init; }
    public required string Uid { get; init; }
    public required string CheckpointDirectory { get; init; }
    public required IReadOnlyList<SnapshotFile> Manifest { get; init; }
    public required long CheckpointSeq { get; init; }
    public required Guid Incarnation { get; init; }
    public required long Base { get; init; }
    public long CreatedTicks { get; init; }
    public long LastUsedTicks;
}

/// <summary>
/// The primary's snapshot sessions (spec 003 design § 7). <see cref="Begin"/> checkpoints a database and registers a
/// session; <see cref="Read"/> serves a file's bytes in chunks; <see cref="End"/> or expiry deletes the checkpoint.
/// One per store, owned by the replication source.
/// </summary>
internal sealed class SnapshotSessions : IDisposable
{
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan AbsoluteLimit = TimeSpan.FromHours(1);

    private readonly string _dataDirectory;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, SnapshotSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public SnapshotSessions(string dataDirectory, TimeProvider clock)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // R7.5: an abandoned session's checkpoint is released when it expires, whether or not another snapshot call
        // comes; every call checks too.
        _expiry = clock.CreateTimer(_ => ExpireQuietly(), null, ExpiryCheck, ExpiryCheck);
    }

    /// <summary>How often sessions are checked for expiry.</summary>
    internal static readonly TimeSpan ExpiryCheck = TimeSpan.FromSeconds(15);

    private readonly ITimer _expiry;
    private int _begun;

    /// <summary>Sessions begun since the store opened (tests: a refetch stays within one session).</summary>
    internal int Begun => Volatile.Read(ref _begun);

    /// <summary>Test hook: rewrites a served chunk (file, offset, bytes) — to corrupt one on purpose. Null in production.</summary>
    internal Func<string, long, byte[], byte[]>? ChunkHookForTest { get; set; }

    private void ExpireQuietly()
    {
        try
        {
            ExpireExpired();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A checkpoint that will not delete now is reclaimed by the next check, or at the next store open.
        }
    }

    /// <summary>Open sessions now (for status and tests).</summary>
    internal int Count => _sessions.Count;

    /// <summary>
    /// Checkpoints <paramref name="slot"/>'s database into a hard-linked folder beside it (R7.6) with WAL deletion
    /// held during the checkpoint, computes each file's CRC-32C once, and registers a session for
    /// <paramref name="uid"/>. The incarnation and base are the serving values (design § 7).
    /// </summary>
    public SnapshotSession Begin(DatabaseSlot slot, string uid, Guid incarnation, long @base)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ExpireExpired();

        Interlocked.Increment(ref _begun);
        var sessionId = Guid.NewGuid().ToString("N");
        var checkpointDir = Path.Combine(_dataDirectory, $"{StoreEngine.DatabaseFolderName(slot.Id)}.checkpoint-{sessionId}");

        var wal = new WalAccess(slot.Instance);
        wal.DisableFileDeletions();
        long checkpointSeq;
        try
        {
            // The checkpoint is a consistent image; the sequence it holds is the latest at the moment it is taken.
            checkpointSeq = (long)wal.LatestSequence();
            wal.Checkpoint(checkpointDir);
        }
        finally
        {
            wal.EnableFileDeletions();
        }

        var manifest = BuildManifest(checkpointDir);
        var now = _clock.GetUtcNow().Ticks;
        var session = new SnapshotSession
        {
            Id = sessionId,
            Uid = uid,
            CheckpointDirectory = checkpointDir,
            Manifest = manifest,
            CheckpointSeq = checkpointSeq,
            Incarnation = incarnation,
            Base = @base,
            CreatedTicks = now,
            LastUsedTicks = now,
        };
        _sessions[sessionId] = session;
        return session;
    }

    /// <summary>The session, if live (refreshing its idle clock), or null when unknown or expired (R7.5 → CRYSTAL_SESSION).</summary>
    public SnapshotSession? Get(string sessionId)
    {
        ExpireExpired();
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            Interlocked.Exchange(ref session.LastUsedTicks, _clock.GetUtcNow().Ticks);
            return session;
        }

        return null;
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> of <paramref name="file"/> from <paramref name="offset"/> in a session,
    /// returning the bytes and the next offset (null at end of file). Null when the session or file is unknown.
    /// </summary>
    public (byte[] Data, long? NextOffset)? Read(string sessionId, string file, long offset, int maxBytes)
    {
        var session = Get(sessionId);
        if (session is null)
        {
            return null;
        }

        // The file must be one the manifest lists (never an arbitrary path the peer names).
        var known = false;
        long size = 0;
        foreach (var f in session.Manifest)
        {
            if (string.Equals(f.RelativePath, file, StringComparison.Ordinal))
            {
                known = true;
                size = f.Size;
                break;
            }
        }

        if (!known || offset < 0 || offset > size)
        {
            return null;
        }

        var path = Path.Combine(session.CheckpointDirectory, file.Replace('/', Path.DirectorySeparatorChar));
        var toRead = (int)Math.Min(Math.Max(maxBytes, 1), size - offset);
        var buffer = new byte[toRead];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < toRead)
            {
                var n = fs.Read(buffer, read, toRead - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read != toRead)
            {
                Array.Resize(ref buffer, read);
            }
        }

        var next = offset + buffer.Length;
        if (ChunkHookForTest is { } hook)
        {
            buffer = hook(file, offset, buffer);
        }

        return (buffer, next >= size ? null : next);
    }

    /// <summary>Ends a session (R7.5), deleting its checkpoint. Idempotent.</summary>
    public void End(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            DeleteCheckpoint(session.CheckpointDirectory);
        }
    }

    /// <summary>Closes every session idle past the idle limit or older than the absolute limit (R7.5).</summary>
    internal void ExpireExpired()
    {
        var now = _clock.GetUtcNow().Ticks;
        foreach (var (id, session) in _sessions)
        {
            var idle = now - Interlocked.Read(ref session.LastUsedTicks);
            var age = now - session.CreatedTicks;
            if (idle > IdleLimit.Ticks || age > AbsoluteLimit.Ticks)
            {
                End(id);
            }
        }
    }

    private IReadOnlyList<SnapshotFile> BuildManifest(string checkpointDir)
    {
        var files = new List<SnapshotFile>();
        foreach (var path in Directory.EnumerateFiles(checkpointDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(checkpointDir, path).Replace(Path.DirectorySeparatorChar, '/');
            var crc = new Crc32C();
            long size = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    crc.Append(buffer.AsSpan(0, n));
                    size += n;
                }
            }

            files.Add(new SnapshotFile(relative, size, crc.Value));
        }

        return files;
    }

    private static void DeleteCheckpoint(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leaked checkpoint is reclaimed on the next store open's recovery, not served.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _expiry.Dispose();

        foreach (var (id, _) in _sessions)
        {
            End(id);
        }
    }
}
