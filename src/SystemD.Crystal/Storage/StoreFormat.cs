// Ported from Highway: src/Highway.Server/HighwayServerBuilder.cs
// Adapted for Crystal: crystal.format stamp and node.id (spec 005 R3); checked before the lock, stamped under it
// (design addendum A1).

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Checks and stamps the Crystal store data directory (spec 005 R2, R3; design addendum A1).
/// </summary>
internal static class StoreFormat
{
    public const string FormatFileName = "crystal.format";
    public const string NodeIdFileName = "node.id";
    public const string SupportedFormat = "crystal 1";

    private const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The files an interrupted creation can leave: the lock, a node.id written before the stamp, and stamping
    /// temporaries. A directory holding only these is stamped again, keeping its node.id.
    /// </summary>
    private static readonly string[] CreationFiles =
    [
        StoreLock.LockFileName,
        NodeIdFileName,
        NodeIdFileName + TempSuffix,
        FormatFileName + TempSuffix,
    ];

    /// <summary>
    /// Read-only check before the lock is taken (R3.1, R3.2): writes nothing. A missing directory, an empty one or
    /// an interrupted creation passes; unstamped content, a foreign or a future stamp is refused.
    /// </summary>
    public static void CheckBeforeLock(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory) || IsNewOrInterrupted(dataDirectory))
        {
            return;
        }

        VerifyStamp(dataDirectory);
    }

    /// <summary>
    /// Under the store lock: stamps a new or interrupted store, or verifies an existing one (R1.4, R3.3, R3.4).
    /// Returns the node id.
    /// </summary>
    public static string StampOrVerify(string dataDirectory)
    {
        if (IsNewOrInterrupted(dataDirectory))
        {
            return Stamp(dataDirectory);
        }

        return VerifyExisting(dataDirectory);
    }

    /// <summary>
    /// Verifies an existing store without stamping anything (R3.2, R3.4): the stamp must be this build's and
    /// node.id a GUID. Returns the node id.
    /// </summary>
    public static string VerifyExisting(string dataDirectory)
    {
        VerifyStamp(dataDirectory);
        return ReadNodeId(dataDirectory)
            ?? throw new CrystalFormatException(
                $"The store at '{dataDirectory}' is stamped '{SupportedFormat}' but its '{NodeIdFileName}' is missing " +
                "or is not a GUID. Crystal never creates a new identity for an existing store, because replication " +
                "knows the node by it (R3.4).",
                foundFormat: SupportedFormat,
                supportedFormat: SupportedFormat,
                wayForward: $"Restore '{NodeIdFileName}' from a backup of this node.");
    }

    private static bool IsNewOrInterrupted(string dataDirectory)
    {
        if (File.Exists(Path.Combine(dataDirectory, FormatFileName)))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(dataDirectory)
            .Select(Path.GetFileName)
            .All(name => CreationFiles.Contains(name, StringComparer.Ordinal));
    }

    private static void VerifyStamp(string dataDirectory)
    {
        var formatPath = Path.Combine(dataDirectory, FormatFileName);
        if (!File.Exists(formatPath))
        {
            throw new CrystalFormatException(
                $"Directory '{dataDirectory}' has content but no '{FormatFileName}' stamp, so it is not a Crystal " +
                "store. Crystal never opens an unstamped directory as if it were empty (R3.2).",
                foundFormat: null,
                supportedFormat: SupportedFormat,
                wayForward: "Open Crystal on an empty or new directory. Crystal owns its whole data directory (R2.2), " +
                    "so keep other files elsewhere.");
        }

        var stamp = File.ReadAllText(formatPath).Trim();
        if (!string.Equals(stamp, SupportedFormat, StringComparison.Ordinal))
        {
            throw new CrystalFormatException(
                $"The store at '{dataDirectory}' is stamped '{stamp}', but this build reads only '{SupportedFormat}' (R3.2).",
                foundFormat: stamp,
                supportedFormat: SupportedFormat,
                wayForward: $"Open it with a Crystal build that reads '{stamp}'.");
        }
    }

    private static string? ReadNodeId(string dataDirectory)
    {
        var nodeIdPath = Path.Combine(dataDirectory, NodeIdFileName);
        if (!File.Exists(nodeIdPath))
        {
            return null;
        }

        var text = File.ReadAllText(nodeIdPath).Trim();
        return Guid.TryParse(text, out var id) ? id.ToString("D") : null;
    }

    /// <summary>
    /// Writes node.id (kept if an interrupted creation left a valid one), then crystal.format, which is the commit
    /// point (design addendum A1).
    /// </summary>
    private static string Stamp(string dataDirectory)
    {
        var nodeId = ReadNodeId(dataDirectory);
        if (nodeId is null)
        {
            nodeId = Guid.NewGuid().ToString("D");
            WriteDurably(Path.Combine(dataDirectory, NodeIdFileName), nodeId + "\n");
        }

        WriteDurably(Path.Combine(dataDirectory, FormatFileName), SupportedFormat + "\n");
        return nodeId;
    }

    private static void WriteDurably(string path, string content)
    {
        var temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8NoBom.GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
