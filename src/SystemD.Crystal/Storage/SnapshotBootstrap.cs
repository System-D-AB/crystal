// Ported from Highway: src/Highway.Server/Storage/Rocks/ReplicaPuller.cs (DownloadSnapshot)
// Spec 003 T10 / R7; design § 7 — the standby side of snapshot bootstrap: on its own connection, SNAPSHOT BEGIN for
// one database's UID, download each file in 1 MiB chunks verifying size and CRC-32C (R7.2), into a sibling
// bootstrap directory; then the caller installs it atomically. RocksDB-free download/verify; the install (folder
// swap, reopen) is the engine's. Ported in spirit from Highway's ReplicaPuller.DownloadSnapshot, with Crystal's
// checksums added (design § 11: Highway shipped no checksums).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Storage;

/// <summary>The snapshot a BEGIN described (design § 7): the session, the serving incarnation/base, and the manifest.</summary>
internal readonly record struct SnapshotBegin(
    long Epoch,
    string Uid,
    string Session,
    Guid Incarnation,
    long Base,
    long CheckpointSeq,
    IReadOnlyList<SnapshotFile> Manifest);

/// <summary>
/// Downloads a primary snapshot of one database (spec 003 R7). <c>DownloadAsync</c> runs over an authenticated
/// <see cref="ReplicationClient"/>: SNAPSHOT BEGIN, GET every file in 1 MiB chunks verifying size and CRC-32C, SNAPSHOT
/// END, into a destination directory. A verification failure throws; the caller discards the directory (nothing is
/// installed), so a corrupted chunk never reaches a live database (R7.2).
/// </summary>
internal static class SnapshotBootstrap
{
    private const int ChunkBytes = 1 << 20; // 1 MiB (design § 7; Highway used 64 KiB)

    /// <summary>How many times one download reconnects after its connection is lost (spec 011 R3.3).</summary>
    internal const int Reconnects = 3;

    /// <summary>
    /// Downloads the snapshot of <paramref name="uid"/> into <paramref name="destinationDirectory"/> (created fresh) over
    /// one connection, without reconnecting. Returns the BEGIN metadata (session already ended). Throws
    /// <see cref="ReplicationProtocolException"/> on a protocol or verification failure.
    /// </summary>
    public static Task<SnapshotBegin> DownloadAsync(
        ReplicationClient client, string uid, string destinationDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        return DownloadAsync(_ => Task.FromResult(client), uid, destinationDirectory, ct, reconnects: 0);
    }

    /// <summary>
    /// Downloads the snapshot of <paramref name="uid"/> into <paramref name="destinationDirectory"/>, connecting through
    /// <paramref name="connect"/> (an authenticated client each time). A lost connection reconnects, at most
    /// <paramref name="reconnects"/> times, and continues the same session from the file and offset reached (spec 011
    /// R3.3); the bytes already written are kept and not fetched again. <paramref name="checkBegin"/> sees the BEGIN
    /// reply before anything is downloaded and may refuse it by throwing (R3.4: its epoch). <paramref name="hook"/>
    /// runs after each chunk is written (<c>bootstrap.chunk</c>, tests).
    /// </summary>
    public static async Task<SnapshotBegin> DownloadAsync(
        Func<CancellationToken, Task<ReplicationClient>> connect,
        string uid,
        string destinationDirectory,
        CancellationToken ct,
        int reconnects = Reconnects,
        Action<SnapshotBegin>? checkBegin = null,
        Action<string>? hook = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        var client = await connect(ct).ConfigureAwait(false);
        var begin = await BeginAsync(client, uid, ct).ConfigureAwait(false);
        checkBegin?.Invoke(begin);
        Directory.CreateDirectory(destinationDirectory);
        var reconnectsLeft = reconnects;
        try
        {
            foreach (var file in begin.Manifest)
            {
                var progress = new FileProgress();
                for (var attempt = 1; ;)
                {
                    try
                    {
                        await DownloadFileAsync(client, begin.Session, destinationDirectory, file, progress, hook, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (SnapshotFileMismatchException) when (attempt < FileAttempts)
                    {
                        // R7.2: refetch the whole file within the session; the bytes written are overwritten.
                        attempt++;
                        progress = new FileProgress();
                    }
                    catch (Exception ex) when (IsConnectionLost(ex) && reconnectsLeft > 0 && !ct.IsCancellationRequested)
                    {
                        // R3.3: a new connection continues the session from where this file got to.
                        reconnectsLeft--;
                        client = await connect(ct).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            // Always end the session so the primary releases the checkpoint (R7.5), even on a verification failure.
            try
            {
                await client.RequestAsync(
                    [Bytes(ReplicationProtocol.Snapshot), Bytes("END"), Bytes(begin.Session)], ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the primary expires the session on its own if END never arrives.
            }
        }

        return begin;
    }

    private static async Task<SnapshotBegin> BeginAsync(ReplicationClient client, string uid, CancellationToken ct)
    {
        var reply = await client.RequestAsync(
            [
                Bytes(ReplicationProtocol.Snapshot), Bytes("BEGIN"), Bytes(uid),
                Bytes(ReplicationProtocol.Format), Bytes(ReplicationProtocol.Layout),
            ],
            ct,
            extraWait: ReplicationProtocol.SnapshotBeginWait).ConfigureAwait(false);

        if (reply.Type == RespType.Error)
        {
            throw new ReplicationProtocolException(reply.Text ?? "SNAPSHOT BEGIN refused");
        }

        // [epoch, uid, session, incarnation, base, checkpointSeq, [[file, size, crc32c]…], format]
        if (reply.Type != RespType.Array || reply.Items is not { Count: 8 } f)
        {
            throw new ReplicationProtocolException("SNAPSHOT BEGIN reply was not an 8-element array");
        }

        var manifest = new List<SnapshotFile>();
        if (f[6].Items is { } rows)
        {
            foreach (var row in rows)
            {
                if (row.Items is { Count: 3 } mf)
                {
                    manifest.Add(new SnapshotFile(
                        Text.UTF8.GetString(mf[0].Bytes ?? []),
                        mf[1].Integer,
                        unchecked((uint)mf[2].Integer)));
                }
            }
        }

        if (!Guid.TryParse(Text.UTF8.GetString(f[3].Bytes ?? []), out var incarnation))
        {
            incarnation = Guid.Empty;
        }

        return new SnapshotBegin(
            f[0].Integer,
            Text.UTF8.GetString(f[1].Bytes ?? []),
            Text.UTF8.GetString(f[2].Bytes ?? []),
            incarnation,
            f[4].Integer,
            f[5].Integer,
            manifest);
    }

    /// <summary>How many times one file is fetched within a session before the download fails (R7.1, R7.2).</summary>
    internal const int FileAttempts = 3;

    /// <summary>What a standby does with a snapshot stated at an epoch (spec 011 R3.4).</summary>
    internal enum EpochVerdict
    {
        /// <summary>The same epoch as this node's: download it.</summary>
        Accept,

        /// <summary>A newer epoch: adopt it, then download.</summary>
        Adopt,

        /// <summary>An older epoch, or one out of range: install nothing from it.</summary>
        Refuse,
    }

    /// <summary>
    /// The verdict on a snapshot stated at <paramref name="snapshotEpoch"/> by a standby at <paramref name="ownEpoch"/>
    /// (spec 011 R3.4, as for a page, 003 R4.3). The main connection's handshake already makes a stale primary stand down;
    /// this guards the snapshot's own connection, which may reach a node whose epoch the standby has since passed.
    /// </summary>
    internal static EpochVerdict JudgeEpoch(long snapshotEpoch, long ownEpoch) =>
        snapshotEpoch < 1 || snapshotEpoch > ReplicationProtocol.MaxEpoch || snapshotEpoch < ownEpoch ? EpochVerdict.Refuse
        : snapshotEpoch > ownEpoch ? EpochVerdict.Adopt
        : EpochVerdict.Accept;

    /// <summary>Whether <paramref name="ex"/> means the connection ended, not that the primary refused or lied.</summary>
    private static bool IsConnectionLost(Exception ex) =>
        ex is ReplicationConnectionLostException or IOException or ObjectDisposedException;

    /// <summary>How far one file got: the next offset to ask for, the bytes written, and their CRC-32C so far.</summary>
    private sealed class FileProgress
    {
        /// <summary>The last chunk was written: a reconnect goes straight to verification, fetching nothing again.</summary>
        public bool Done { get; set; }

        public long Offset { get; set; }

        public long Written { get; set; }

        // A field, not a property: Crc32C is a struct, and a property would hand out copies that drop each update.
        public Crc32C Crc = new();
    }

    /// <summary>
    /// The local path of a manifest file, which must stay inside <paramref name="destinationDirectory"/>: a manifest
    /// naming an absolute path, a drive, or a path that climbs out with <c>..</c> is refused (a primary is not trusted
    /// to name files outside the download).
    /// </summary>
    internal static string ContainedPath(string destinationDirectory, string relativePath)
    {
        var root = Path.GetFullPath(destinationDirectory);
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ReplicationProtocolException($"snapshot manifest names a file outside the download: '{relativePath}'");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ReplicationProtocolException($"snapshot manifest names a file outside the download: '{relativePath}'");
        }

        return full;
    }

    private static async Task DownloadFileAsync(
        ReplicationClient client, string session, string destinationDirectory, SnapshotFile file, FileProgress progress,
        Action<string>? hook, CancellationToken ct)
    {
        var destination = ContainedPath(destinationDirectory, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Continue from what an earlier connection wrote (R3.3), or from the start.
        var written = progress.Written;
        var offset = progress.Offset;
        using (var fs = new FileStream(destination, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(written);
            fs.Seek(written, SeekOrigin.Begin);
            while (!progress.Done)
            {
                var reply = await client.RequestAsync(
                    [
                        Bytes(ReplicationProtocol.Snapshot), Bytes("GET"), Bytes(session), Bytes(file.RelativePath),
                        Bytes(offset.ToString(CultureInfo.InvariantCulture)), Bytes(ChunkBytes.ToString(CultureInfo.InvariantCulture)),
                    ],
                    ct).ConfigureAwait(false);

                if (reply.Type == RespType.Error)
                {
                    throw new ReplicationProtocolException(reply.Text ?? "SNAPSHOT GET refused");
                }

                // [file, offset, data, nextOffset | null]
                if (reply.Type != RespType.Array || reply.Items is not { Count: 4 } r)
                {
                    throw new ReplicationProtocolException("SNAPSHOT GET reply was not a 4-element array");
                }

                var data = r[2].Bytes ?? [];
                if (r[1].Integer != offset || written + data.Length > file.Size)
                {
                    // Never more than the manifest stated, and only the chunk asked for: a primary cannot fill the disk.
                    throw new SnapshotFileMismatchException(
                        $"snapshot file '{file.RelativePath}' was served past its stated size of {file.Size} bytes, or out of order (R7.2)");
                }

                progress.Crc.Append(data);
                fs.Write(data, 0, data.Length);
                written += data.Length;
                var done = r[3].Type == RespType.NullBulkString || r[3].Type == RespType.NullArray;
                if (!done)
                {
                    offset = r[3].Integer;
                }

                // Progress survives this connection: a reconnect continues from here, not from the file's start.
                fs.Flush();
                progress.Written = written;
                progress.Offset = offset;
                progress.Done = done; // end of file: the loop ends
                hook?.Invoke("bootstrap.chunk");
            }

            // On disk before it can be installed (R7.3): a power loss after the install must not find a short file.
            fs.Flush(flushToDisk: true);
        }

        if (written != file.Size)
        {
            throw new SnapshotFileMismatchException(
                $"snapshot file '{file.RelativePath}' is {written} bytes; the manifest said {file.Size} (R7.2)");
        }

        if (progress.Crc.Value != file.Crc32C)
        {
            throw new SnapshotFileMismatchException(
                $"snapshot file '{file.RelativePath}' failed its CRC-32C check (R7.2): the download is corrupt");
        }
    }

    private static byte[] Bytes(string s) => Text.UTF8.GetBytes(s);
}

/// <summary>A downloaded snapshot file that does not match the manifest: fetched again (R7.2).</summary>
internal sealed class SnapshotFileMismatchException(string message) : ReplicationProtocolException(message);
