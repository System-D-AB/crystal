using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A box's record in the catalogue (box 0) (spec 002 R2.1, design § 1.2).
/// Format: { "v": 1, "name": "queue/orders", "profile": "General", "state": "live", "created": ..., "dropped": null }
/// </summary>
internal sealed record BoxRecord
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "General";

    [JsonPropertyName("state")]
    public string State { get; init; } = "live";

    [JsonPropertyName("created")]
    public long CreatedTicks { get; init; }

    [JsonPropertyName("dropped")]
    public long? DroppedTicks { get; init; }

    public static byte[] Serialize(BoxRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record);

    public static BoxRecord Deserialize(byte[] bytes) =>
        JsonSerializer.Deserialize<BoxRecord>(bytes)
        ?? throw new JsonException("Failed to deserialize BoxRecord JSON payload.");
}
