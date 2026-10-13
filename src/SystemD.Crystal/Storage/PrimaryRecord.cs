// Spec 011 R7.1 — the last primary this node followed: its node id, epoch and endpoint, in
// {dataDir}/replication/primary.json, written atomically and durably. A standby that then authenticates a different
// primary stating the same epoch has found two primaries at one epoch (003 R6.4): it reports a conflict and takes
// nothing. Persisted, so a restart between the two does not hide it. Node-local, never replicated.

using System;
using System.IO;
using System.Text.Json;

namespace SystemD.Crystal.Storage;

/// <summary>The last primary this node followed (spec 011 R7.1).</summary>
internal sealed class PrimaryRecord
{
    private const string TempSuffix = ".tmp";

    public string NodeId { get; init; } = string.Empty;

    public long Epoch { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    /// <summary>The record's path: <c>{dataDir}/replication/primary.json</c>.</summary>
    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, "replication", "primary.json");

    /// <summary>The record, or null when there is none or it cannot be read (nothing remembered: no conflict found).</summary>
    public static PrimaryRecord? Read(string dataDirectory)
    {
        var path = PathFor(dataDirectory);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PrimaryRecord>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the record atomically (temp, flush, rename) and durably (the directory synced).</summary>
    public static void Write(string dataDirectory, string nodeId, long epoch, string endpoint)
    {
        var path = PathFor(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PrimaryRecord { NodeId = nodeId, Epoch = epoch, Endpoint = endpoint });
        var temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        DurableFile.Replace(temp, path);
    }
}
