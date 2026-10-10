// Spec 003 R5.2, R5.5; design § 1, § 5 — the per-database record of the incarnation and base a standby last applied
// from, in {dataDir}/replication/follow/{uid}.json. Node-local, never replicated. Written atomically after a page
// lands so a standby can tell, on the next pull, whether a served incarnation it has not seen means it diverged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The incarnation and base a standby last applied from, for one database (spec 003 R5.2). Serialized to
/// <c>{dataDir}/replication/follow/{uid}.json</c>, temp-and-rename so a crash never leaves a torn file (R5.5).
/// </summary>
internal sealed record FollowRecord
{
    /// <summary>The primary incarnation the standby last followed (design § 3): a GUID string.</summary>
    [JsonPropertyName("incarnation")]
    public string Incarnation { get; init; } = string.Empty;

    /// <summary>The base sequence of that incarnation (design § 3), kept for the record; the divergence rule no longer uses it.</summary>
    [JsonPropertyName("base")]
    public long Base { get; init; }

    /// <summary>
    /// The earlier incarnations the followed log continued, each with how far it shares them (tasks addendum: exact
    /// history). Carried into this node's own history if it is promoted, so a standby of an earlier log can still be
    /// recognised.
    /// </summary>
    [JsonPropertyName("history")]
    public List<FollowedEnd> History { get; init; } = [];

    /// <summary>An earlier incarnation and how far the followed log shares it.</summary>
    internal sealed record FollowedEnd
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("end")]
        public long End { get; init; }
    }

    /// <summary>The kept incarnation and history as a log history, or null if the record is unreadable.</summary>
    public LogIncarnation? AsHistory()
    {
        if (!Guid.TryParseExact(Incarnation, "D", out var id))
        {
            return null;
        }

        var history = new List<IncarnationEnd>();
        foreach (var h in History)
        {
            if (Guid.TryParseExact(h.Id, "D", out var hid))
            {
                history.Add(new IncarnationEnd(hid, h.End));
            }
        }

        return new LogIncarnation(id, Base, history);
    }

    private const string TempSuffix = ".tmp";

    /// <summary>The directory holding the follow records: <c>{dataDir}/replication/follow</c>.</summary>
    public static string Directory(string dataDirectory) =>
        Path.Combine(dataDirectory, "replication", "follow");

    /// <summary>The path of one database's follow record.</summary>
    public static string PathFor(string dataDirectory, string uid) =>
        Path.Combine(Directory(dataDirectory), uid + ".json");

    /// <summary>Reads the follow record for <paramref name="uid"/>, or null if there is none (a fresh follow).</summary>
    public static FollowRecord? Read(string dataDirectory, string uid)
    {
        var path = PathFor(dataDirectory, uid);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<FollowRecord>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null; // unreadable or torn: treat as absent, which forces a safe re-check, never a wrong continue
        }
    }

    /// <summary>
    /// Writes the follow record for <paramref name="uid"/> atomically (R5.5): a temp file flushed to disk, then
    /// renamed over the old one, so a reader sees the old record or the new one, never a torn write.
    /// </summary>
    public static void Write(string dataDirectory, string uid, Guid incarnation, long @base, IReadOnlyList<IncarnationEnd>? history = null)
    {
        var dir = Directory(dataDirectory);
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, uid + ".json");
        var record = new FollowRecord
        {
            Incarnation = incarnation.ToString("D"),
            Base = @base,
            History = (history ?? []).Select(h => new FollowedEnd { Id = h.Id.ToString("D"), End = h.End }).ToList(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);

        var temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Deletes the follow record for <paramref name="uid"/> (the database is no longer followed). Best effort.</summary>
    public static void Delete(string dataDirectory, string uid)
    {
        try
        {
            File.Delete(PathFor(dataDirectory, uid));
        }
        catch (IOException)
        {
            // Best effort: a stale follow record is re-checked, never trusted blindly.
        }
    }
}
