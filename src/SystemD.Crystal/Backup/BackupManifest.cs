// Spec 010 R4.1–R4.3; design § 2 — a backup set's manifest, sets/{setId}.json. A set exists only once its manifest
// does: it is written last, to a temporary file that is flushed, renamed and its directory synced. It carries a SHA-256
// of its own content, so a manifest edited or torn is never used.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemD.Crystal.Encoding;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Backup;

/// <summary>A backup set's manifest (spec 010 design § 2), as written to <c>sets/{setId}.json</c>.</summary>
internal sealed record BackupManifest
{
    /// <summary>The manifest's own format; a reader refuses any other.</summary>
    public const string CurrentFormat = "crystal-backup-set 1";

    /// <summary>The suffix of a manifest being written; ignored by readers, deleted by the next operation.</summary>
    public const string TempSuffix = ".tmp";

    // Full precision (100 ns ticks): a restored database keeps its creation time exactly (R6.3).
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    [JsonPropertyName("format")] public string Format { get; init; } = CurrentFormat;
    [JsonPropertyName("set")] public string Set { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("node")] public string Node { get; init; } = string.Empty;
    [JsonPropertyName("started")] public string Started { get; init; } = string.Empty;
    [JsonPropertyName("completed")] public string Completed { get; init; } = string.Empty;
    [JsonPropertyName("versions")] public ManifestVersions Versions { get; init; } = new();
    [JsonPropertyName("databases")] public List<ManifestDatabase> Databases { get; init; } = [];
    [JsonPropertyName("omitted")] public List<ManifestOmitted> Omitted { get; init; } = [];
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;

    /// <summary>The formats and versions the set was written with (R4.2). A restore checks the first three (R6.2).</summary>
    internal sealed record ManifestVersions
    {
        [JsonPropertyName("store")] public string Store { get; init; } = StoreFormat.SupportedFormat;
        [JsonPropertyName("catalogue")] public int Catalogue { get; init; } = DatabaseContext.CatalogueFormat;
        [JsonPropertyName("encoder")] public int Encoder { get; init; } = KeyEncoding.Version;
        [JsonPropertyName("crystal")] public string Crystal { get; init; } = CrystalVersion.Value;
        [JsonPropertyName("rocksdb")] public string RocksDb { get; init; } = NativeRocks.RocksDbVersion;
    }

    /// <summary>One database in the set (R4.2): its engine backup and the sequence every commit at or below is in (R1.5).</summary>
    internal sealed record ManifestDatabase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("uid")] public string Uid { get; init; } = string.Empty;
        [JsonPropertyName("replicated")] public bool Replicated { get; init; }
        [JsonPropertyName("created")] public string Created { get; init; } = string.Empty;
        [JsonPropertyName("backup")] public uint Backup { get; init; }
        [JsonPropertyName("sequence")] public long Sequence { get; init; }
        [JsonPropertyName("bytes")] public long Bytes { get; init; }
        [JsonPropertyName("files")] public int Files { get; init; }
    }

    /// <summary>A database read at the start but not in the set (R3.2), with why.</summary>
    internal sealed record ManifestOmitted
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("uid")] public string Uid { get; init; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    }

    /// <summary>A UTC time as the manifest writes it.</summary>
    public static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>A time the manifest wrote; throws <see cref="FormatException"/> if it is not one.</summary>
    public static DateTimeOffset ParseTime(string text) =>
        new(DateTime.SpecifyKind(DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc));

    /// <summary>The checksum of this manifest: SHA-256 over it serialised compactly with <c>sha256</c> empty, lower-case hex.</summary>
    public string ComputeSha256() =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this with { Sha256 = string.Empty })));

    /// <summary>This manifest with its checksum filled in.</summary>
    public BackupManifest Sealed() => this with { Sha256 = ComputeSha256() };

    /// <summary>The set as the public API describes it.</summary>
    public BackupSetInfo ToInfo() => new(
        Set,
        Label,
        Node,
        ParseTime(Started),
        ParseTime(Completed),
        [.. Databases.Select(d => new BackupDatabaseInfo(d.Name, d.Uid, d.Replicated, ParseTime(d.Created), d.Sequence, d.Bytes, d.Files))],
        [.. Omitted.Select(o => new OmittedDatabase(o.Name, o.Uid, o.Reason))]);

    /// <summary>
    /// Writes the sealed manifest as <c>{setsDirectory}/{set}.json</c> (R4.1): a temporary file, flushed to disk, renamed
    /// into place, its directory synced (spec 010 R11.5). The set exists from the rename on.
    /// </summary>
    public void WriteTo(string setsDirectory, Action<string>? hook = null)
    {
        Directory.CreateDirectory(setsDirectory);
        var path = Path.Combine(setsDirectory, Set + ".json");
        var temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(Sealed()));
            stream.Flush(flushToDisk: true);
        }

        hook?.Invoke("backup.manifest.written");
        DurableFile.Replace(temp, path);
    }

    /// <summary>
    /// Reads and checks a manifest file. Throws <see cref="BackupDamagedException"/> when it cannot be parsed, is of another
    /// format, or its checksum does not match (R4.2: such a manifest is never used).
    /// </summary>
    public static BackupManifest Read(string path)
    {
        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(path));
        }
        catch (JsonException ex)
        {
            throw Damaged(path, "it is not valid JSON", ex);
        }

        if (manifest is null)
        {
            throw Damaged(path, "it is empty");
        }

        if (!string.Equals(manifest.Format, CurrentFormat, StringComparison.Ordinal))
        {
            throw Damaged(path, $"its format is '{manifest.Format}', not '{CurrentFormat}'");
        }

        if (!string.Equals(manifest.Sha256, manifest.ComputeSha256(), StringComparison.Ordinal))
        {
            throw Damaged(path, "its SHA-256 does not match its content");
        }

        // Every UID names a folder (an engine, replication records): a GUID in canonical form, as spec 009's identity
        // requires, never a path (review S4). The checksum does not prevent this: anyone editing a manifest can reseal it.
        foreach (var uid in manifest.Databases.Select(d => d.Uid).Concat(manifest.Omitted.Select(o => o.Uid)))
        {
            if (!Guid.TryParseExact(uid, "D", out var parsed) || !string.Equals(parsed.ToString("D"), uid, StringComparison.Ordinal))
            {
                throw Damaged(path, $"'{uid}' is not a database UID");
            }
        }

        try
        {
            _ = manifest.ToInfo(); // every time parses
        }
        catch (FormatException ex)
        {
            throw Damaged(path, "a time in it is malformed", ex);
        }

        if (!string.Equals(Path.GetFileName(path), manifest.Set + ".json", StringComparison.Ordinal))
        {
            throw Damaged(path, $"it names set '{manifest.Set}', not the set its file name says");
        }

        return manifest;
    }

    private static BackupDamagedException Damaged(string path, string what, Exception? inner = null) =>
        new(path, $"The backup manifest '{path}' is damaged: {what}. It is never used; restore from another set, or " +
            "delete the manifest once the set is no longer wanted.", inner);
}
