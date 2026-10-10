using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A database's record in the registry (spec 005 R4.2, design § 2; spec 003 R1.3 added <c>uid</c>).
/// Format v2: { "v": 2, "name": "shop", "uid": "…", "replicated": true, "state": "live", "created": ..., "dropped": null }
/// Version 1 records (no <c>uid</c>) still load; the registry upgrades each to version 2 with a fresh UID on open
/// (spec 003 T1, design § 2).
/// </summary>
internal sealed record DatabaseRecord
{
    /// <summary>The current record version written by this build.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The record version; 0 when the field is missing, which loading refuses (design addendum A4).</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The database's stable UID (spec 003 R1.3): a random 128-bit identifier assigned at create, kept across rename,
    /// swap, reopen and promotion. Empty on a version 1 record until the registry upgrades it on load. The wire names
    /// databases by this, never by the numeric id.
    /// </summary>
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = string.Empty;

    [JsonPropertyName("replicated")]
    public bool Replicated { get; init; } = true;

    [JsonPropertyName("state")]
    public string State { get; init; } = "live";

    [JsonPropertyName("created")]
    public long CreatedTicks { get; init; }

    [JsonPropertyName("dropped")]
    public long? DroppedTicks { get; init; }

    /// <summary>A fresh UID string in the canonical 36-character form (spec 003 design § 8.1).</summary>
    public static string NewUid() => Guid.NewGuid().ToString("D");

    public static byte[] Serialize(DatabaseRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record);

    public static DatabaseRecord Deserialize(byte[] bytes) =>
        JsonSerializer.Deserialize<DatabaseRecord>(bytes)
        ?? throw new JsonException("Failed to deserialize DatabaseRecord JSON payload.");
}
