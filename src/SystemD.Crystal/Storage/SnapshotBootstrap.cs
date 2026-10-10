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
/// Downloads a primary snapshot of one database (spec 003 R7). <see cref="DownloadAsync"/> runs over an authenticated
/// <see cref="ReplicationClient"/>: SNAPSHOT BEGIN, GET every file in 1 MiB chunks verifying size and CRC-32C, SNAPSHOT
/// END, into a destination directory. A verification failure throws; the caller discards the directory (nothing is
/// installed), so a corrupted chunk never reaches a live database (R7.2).
/// </summary>
internal static class SnapshotBootstrap
{
    private const int ChunkBytes = 1 << 20; // 1 MiB (design § 7; Highway used 64 KiB)

    /// <summary>
    /// Downloads the snapshot of <paramref name="uid"/> into <paramref name="destinationDirectory"/> (created fresh).
    /// Returns the BEGIN metadata (session already ended). Throws <see cref="ReplicationProtocolException"/> on a
    /// protocol or verification failure.
    /// </summary>
    public static async Task<SnapshotBegin> DownloadAsync(
        ReplicationClient client, string uid, string destinationDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        var begin = await BeginAsync(client, uid, ct).ConfigureAwait(false);
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            foreach (var file in begin.Manifest)
            {
                await DownloadFileWithRefetchAsync(client, begin.Session, destinationDirectory, file, ct).ConfigureAwait(false);
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
            ct).ConfigureAwait(false);

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

    /// <summary>
    /// Downloads one file, fetching it again within the same session when its size or CRC-32C does not match the
    /// manifest (R7.2: a corrupt chunk is detected and refetched; R7.1: resumable by file within a session). After
    /// <see cref="FileAttempts"/> mismatches the download fails, and the follower retries the bootstrap later.
    /// </summary>
    private static async Task DownloadFileWithRefetchAsync(
        ReplicationClient client, string session, string destinationDirectory, SnapshotFile file, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileAsync(client, session, destinationDirectory, file, ct).ConfigureAwait(false);
                return;
            }
            catch (SnapshotFileMismatchException) when (attempt < FileAttempts)
            {
                // Refetch the whole file: the bytes already written are overwritten.
            }
        }
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
        ReplicationClient client, string session, string destinationDirectory, SnapshotFile file, CancellationToken ct)
    {
        var destination = ContainedPath(destinationDirectory, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var crc = new Crc32C();
        long written = 0;
        long offset = 0;
        using (var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while (true)
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

                crc.Append(data);
                fs.Write(data, 0, data.Length);
                written += data.Length;

                if (r[3].Type == RespType.NullBulkString || r[3].Type == RespType.NullArray)
                {
                    break; // end of file
                }

                offset = r[3].Integer;
            }

            // On disk before it can be installed (R7.3): a power loss after the install must not find a short file.
            fs.Flush(flushToDisk: true);
        }

        if (written != file.Size)
        {
            throw new SnapshotFileMismatchException(
                $"snapshot file '{file.RelativePath}' is {written} bytes; the manifest said {file.Size} (R7.2)");
        }

        if (crc.Value != file.Crc32C)
        {
            throw new SnapshotFileMismatchException(
                $"snapshot file '{file.RelativePath}' failed its CRC-32C check (R7.2): the download is corrupt");
        }
    }

    private static byte[] Bytes(string s) => Text.UTF8.GetBytes(s);
}

/// <summary>A downloaded snapshot file that does not match the manifest: fetched again (R7.2).</summary>
internal sealed class SnapshotFileMismatchException(string message) : ReplicationProtocolException(message);
