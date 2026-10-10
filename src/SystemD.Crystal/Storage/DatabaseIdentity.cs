using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemD.Crystal.Storage;

/// <summary>What a database is for (spec 009 R1.1, R3.1): a product's database, or the store's registry.</summary>
internal enum DatabaseKind
{
    Database,
    Registry,
}

/// <summary>
/// A database's identity record, in its own box 0 at space <c>0x07</c> (spec 009 R1, design § 1). Written once, in the
/// catalogue's first batch, and never changed: it travels with every copy of the database's folder, so a database
/// taken out of its store still says what it is. Its id and name belong to the store and are not recorded here (R1.4).
/// Format: { "v": 1, "kind": "database", "uid": "…", "created": 638… }
/// </summary>
/// <param name="Uid">The database's UID (spec 003 R1.3), canonical 36-character GUID form.</param>
/// <param name="Kind">A product's database or the registry.</param>
/// <param name="CreatedTicks">When the database was created, in UTC ticks.</param>
internal sealed record DatabaseIdentity(string Uid, DatabaseKind Kind, long CreatedTicks)
{
    /// <summary>The record version this build writes and reads.</summary>
    public const int CurrentVersion = 1;

    private const string DatabaseKindName = "database";
    private const string RegistryKindName = "registry";

    public static byte[] Serialize(DatabaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonSerializer.SerializeToUtf8Bytes(new Payload
        {
            Version = CurrentVersion,
            Kind = identity.Kind == DatabaseKind.Registry ? RegistryKindName : DatabaseKindName,
            Uid = identity.Uid,
            CreatedTicks = identity.CreatedTicks,
        });
    }

    /// <summary>
    /// Reads an identity record. A record that is not JSON, lacks its version, has a kind or UID this build does not
    /// read, or lacks its creation time throws <see cref="FormatException"/>; a version this build does not read throws
    /// <see cref="NotSupportedException"/> (R1.2). The caller turns both into a refusal naming the database.
    /// </summary>
    public static DatabaseIdentity Deserialize(byte[] bytes)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(bytes);
        }
        catch (JsonException ex)
        {
            throw new FormatException("the identity record is not readable JSON", ex);
        }

        if (payload is null || payload.Version is null)
        {
            throw new FormatException("the identity record has no version");
        }

        if (payload.Version != CurrentVersion)
        {
            throw new NotSupportedException($"the identity record has version {payload.Version}; this build reads version {CurrentVersion}");
        }

        var kind = payload.Kind switch
        {
            DatabaseKindName => DatabaseKind.Database,
            RegistryKindName => DatabaseKind.Registry,
            _ => throw new FormatException($"the identity record has kind '{payload.Kind}'"),
        };

        if (payload.Uid is null || !Guid.TryParseExact(payload.Uid, "D", out var parsed)
            || !string.Equals(parsed.ToString("D"), payload.Uid, StringComparison.Ordinal))
        {
            throw new FormatException($"the identity record's UID '{payload.Uid}' is not a GUID in canonical form");
        }

        if (payload.CreatedTicks is not { } created || created < DateTime.MinValue.Ticks || created > DateTime.MaxValue.Ticks)
        {
            throw new FormatException("the identity record has no valid creation time");
        }

        return new DatabaseIdentity(payload.Uid, kind, created);
    }

    /// <summary>The kind as its record spells it, for messages.</summary>
    public static string KindName(DatabaseKind kind) => kind == DatabaseKind.Registry ? RegistryKindName : DatabaseKindName;

    private sealed class Payload
    {
        [JsonPropertyName("v")]
        public int? Version { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("uid")]
        public string? Uid { get; init; }

        [JsonPropertyName("created")]
        public long? CreatedTicks { get; init; }
    }
}
