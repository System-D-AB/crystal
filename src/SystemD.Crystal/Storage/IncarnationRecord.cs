// Spec 003 R5.2; addendum 2026-10-10 (B3, after the review of its fix) — a primary's log history per replicated
// database, node-local in {dataDir}/replication/incarnation/{uid}.json, never replicated. A restart begins a new
// incarnation (a primary that lost unsynced batches to a power loss must not pass as the same log), but its base never
// moves forward past the promotion, and the incarnations it continues are listed with how far each survived, so a
// standby that followed this primary before the restart continues instead of re-bootstrapping.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A database's log history on this node (spec 003 R5.2): its incarnation, its base, and the earlier incarnations of
/// this node's log it continues, each with the last sequence the current log still shares with it.
/// </summary>
/// <param name="Id">A random identifier, new at every open and promotion.</param>
/// <param name="Base">
/// The sequence up to which this log agrees with any log anywhere: the latest at promotion (or at the database's first
/// open as a primary), and never above it afterwards. A standby above it, on an incarnation not in
/// <paramref name="History"/>, diverged.
/// </param>
/// <param name="History">Earlier incarnations of this node's log, newest first, each with how far the current log shares it.</param>
internal sealed record LogIncarnation(Guid Id, long Base, IReadOnlyList<IncarnationEnd> History)
{
    /// <summary>How many earlier incarnations are kept: a standby away across more primary restarts re-bootstraps.</summary>
    public const int HistoryLimit = 16;

    /// <summary>A new, unrelated history at <paramref name="latest"/> (promotion, first open as a primary, a standby).</summary>
    public static LogIncarnation Fresh(long latest) => new(Guid.NewGuid(), latest, []);

    /// <summary>
    /// The history after a restart that recovered the log to <paramref name="latest"/>: a new incarnation; the base is
    /// the lower of the old base and <paramref name="latest"/>; the old incarnation and its history are continued up to
    /// at most <paramref name="latest"/>, since a log only ever loses a suffix in a crash.
    /// </summary>
    public LogIncarnation ContinuedAt(long latest)
    {
        var history = new List<IncarnationEnd> { new(Id, latest) };
        history.AddRange(History.Select(h => h with { End = Math.Min(h.End, latest) }));
        return new LogIncarnation(Guid.NewGuid(), Math.Min(Base, latest), history.Take(HistoryLimit).ToList());
    }
}

/// <summary>An earlier incarnation and the last sequence the current log shares with it.</summary>
internal sealed record IncarnationEnd(Guid Id, long End);

/// <summary>Reads and writes a primary's <see cref="LogIncarnation"/> per database, atomically (temp, flush, rename).</summary>
internal static class IncarnationRecord
{
    private const string TempSuffix = ".tmp";

    /// <summary><c>{dataDir}/replication/incarnation</c>.</summary>
    public static string Directory(string dataDirectory) => Path.Combine(dataDirectory, "replication", "incarnation");

    /// <summary>The record for <paramref name="uid"/>, or null if there is none.</summary>
    /// <exception cref="IOException">The record exists and cannot be read: refused rather than guessed.</exception>
    public static LogIncarnation? Read(string dataDirectory, string uid)
    {
        var path = Path.Combine(Directory(dataDirectory), uid + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllBytes(path))
                ?? throw new JsonException("empty");
            return new LogIncarnation(
                Guid.ParseExact(payload.Id, "D"),
                payload.Base,
                payload.History.Select(h => new IncarnationEnd(Guid.ParseExact(h.Id, "D"), h.End)).ToList());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException)
        {
            throw new IOException(
                $"The incarnation record '{path}' is unreadable. Delete it to give the database a fresh history; its " +
                "standbys then re-bootstrap if they are past the database's latest sequence.", ex);
        }
    }

    /// <summary>Writes the record for <paramref name="uid"/> atomically.</summary>
    public static void Write(string dataDirectory, string uid, LogIncarnation incarnation)
    {
        var dir = Directory(dataDirectory);
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, uid + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Payload
        {
            Id = incarnation.Id.ToString("D"),
            Base = incarnation.Base,
            History = incarnation.History.Select(h => new HistoryPayload { Id = h.Id.ToString("D"), End = h.End }).ToList(),
        });

        var temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        DurableFile.Replace(temp, path); // the rename made durable (spec 011 R9.1)
    }

    /// <summary>Whether a record exists for <paramref name="uid"/>.</summary>
    public static bool Exists(string dataDirectory, string uid) =>
        File.Exists(Path.Combine(Directory(dataDirectory), uid + ".json"));

    /// <summary>
    /// Deletes the record for <paramref name="uid"/>: the database now holds what this node received as a standby (a
    /// page applied, a snapshot installed), so none of its own earlier history may be continued. Called before the
    /// received data lands, so a crash in between leaves no record of a log that is no longer this node's alone.
    /// </summary>
    public static void Delete(string dataDirectory, string uid)
    {
        var path = Path.Combine(Directory(dataDirectory), uid + ".json");
        if (File.Exists(path))
        {
            File.Delete(path); // File.Delete throws when the folder is missing, so the check comes first
        }
    }

    private sealed class Payload
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("base")]
        public long Base { get; init; }

        [JsonPropertyName("history")]
        public List<HistoryPayload> History { get; init; } = [];
    }

    private sealed class HistoryPayload
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("end")]
        public long End { get; init; }
    }
}
