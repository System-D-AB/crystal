// Spec 003 R7.8, R9.1 — the node-wide rejoin marker under {dataDir}/replication/rejoin. A demoting node (or an
// operator) records "on next start, wipe this node's replicated databases and bootstrap from this primary". The
// marker survives a failed download, so a restart retries the rejoin; it is consumed only once the node is following
// again. Node-local, never replicated. One bootstrap path with first bootstrap and resync (R5.4).

using System;
using System.IO;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A node-wide marker that this node must rejoin a primary on its next follow (spec 003 R7.8). It names the primary
/// endpoint to bootstrap from. Distinct from the per-database <see cref="ResyncMarker"/>: a rejoin re-bootstraps every
/// replicated database, where a resync marks one.
/// </summary>
internal static class RejoinMarker
{
    private static string PathFor(string dataDirectory) =>
        Path.Combine(dataDirectory, "replication", "rejoin");

    /// <summary>Records a rejoin of <paramref name="primaryEndpoint"/> on the next follow. Idempotent (overwrites).</summary>
    public static void Write(string dataDirectory, string primaryEndpoint)
    {
        var dir = Path.Combine(dataDirectory, "replication");
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathFor(dataDirectory), $"endpoint={primaryEndpoint}\n");
    }

    /// <summary>The recorded primary endpoint, or null when there is no rejoin marker.</summary>
    public static string? Read(string dataDirectory)
    {
        var path = PathFor(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var i = line.IndexOf('=');
            if (i > 0 && line[..i] == "endpoint")
            {
                var endpoint = line[(i + 1)..].Trim();
                return string.IsNullOrEmpty(endpoint) ? null : endpoint;
            }
        }

        return null;
    }

    /// <summary>Clears the rejoin marker (the rejoin completed). Best effort.</summary>
    public static void Clear(string dataDirectory)
    {
        try
        {
            File.Delete(PathFor(dataDirectory));
        }
        catch (IOException)
        {
            // Best effort: a stale marker only causes a needless rejoin, never a wrong follow.
        }
    }
}
