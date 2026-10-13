// Ported from Highway: src/Highway.Server/Storage/Rocks/RocksDbStore.cs (WipeForResync: the resync marker)
// Spec 003 R5.3, R5.4; design § 1 — per-database resync markers under {dataDir}/replication/resync/{uid}. A gap or a
// divergence marks that one database for re-bootstrap; the marker survives a crash so a restart re-bootstraps it (the
// same code path as a first bootstrap and a rejoin, R5.4). Node-local, never replicated.

using System;
using System.IO;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A node-local marker that one replicated database must re-bootstrap (spec 003 R5.3). Written when a pull reports a
/// gap or divergence for it, removed once the re-bootstrap installs a fresh snapshot. Present at open → the database
/// is re-bootstrapped before it is followed. Only the affected database carries a marker; the rest keep following.
/// </summary>
internal static class ResyncMarker
{
    /// <summary>The directory holding resync markers: <c>{dataDir}/replication/resync</c>.</summary>
    public static string Directory(string dataDirectory) =>
        Path.Combine(dataDirectory, "replication", "resync");

    private static string PathFor(string dataDirectory, string uid) =>
        Path.Combine(Directory(dataDirectory), uid);

    /// <summary>
    /// Marks <paramref name="uid"/> for re-bootstrap (R5.3), recording why. Idempotent. No timestamp: Crystal reads
    /// the clock only through its injected <see cref="TimeProvider"/> (the architecture gate), and the marker's
    /// existence is what drives the re-bootstrap, not when it was written.
    /// </summary>
    public static void Write(string dataDirectory, string uid, string reason)
    {
        // Written as the other records are, temp, flush, rename, directory synced (review 2026-10-13, M1): a promotion
        // decides a database's history on this marker (spec 011 R2.3), so a power loss must not undo it.
        var dir = Directory(dataDirectory);
        System.IO.Directory.CreateDirectory(dir);
        var path = PathFor(dataDirectory, uid);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes($"reason={reason}\n"));
            stream.Flush(flushToDisk: true);
        }

        DurableFile.Replace(temp, path);
    }

    /// <summary>
    /// Why <paramref name="uid"/> is marked (the text after <c>reason=</c>), or null when it is not marked. An unreadable
    /// marker reads as an empty reason, which is not a gap: a caller deciding on it assumes the worse (spec 011 R2.3).
    /// </summary>
    public static string? ReadReason(string dataDirectory, string uid)
    {
        var path = PathFor(dataDirectory, uid);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.StartsWith("reason=", StringComparison.Ordinal) ? text["reason=".Length..] : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Whether a marker's <paramref name="reason"/> says only that the database is behind (a gap: what it holds is a prefix
    /// of the log it followed), rather than that it may hold batches that log does not (a divergence, or a rejoin).
    /// </summary>
    public static bool IsGap(string reason) => reason.StartsWith("gap", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="uid"/> is marked for re-bootstrap.</summary>
    public static bool Exists(string dataDirectory, string uid) => File.Exists(PathFor(dataDirectory, uid));

    /// <summary>Clears the marker for <paramref name="uid"/> (the re-bootstrap completed). Best effort.</summary>
    public static void Clear(string dataDirectory, string uid)
    {
        try
        {
            File.Delete(PathFor(dataDirectory, uid));
        }
        catch (IOException)
        {
            // Best effort: a stale marker only causes a needless re-bootstrap, never a wrong follow.
        }
    }
}
