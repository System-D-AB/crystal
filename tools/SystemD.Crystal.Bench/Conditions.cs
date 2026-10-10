// Ported from StowDB: tests/Stow.Storage.Measurements/EnvironmentReport.cs and MeasurementArtifact.cs (branch
// rocksdb). Carried: the #if DEBUG build detection, the OS/CPU/RAM/.NET readings, "unknown" over a guess. Changed:
// Crystal version; the RocksDB version is read by scanning loaded assemblies (not typeof(RocksDbSharp.RocksDb)),
// because this project references only SystemD.Crystal (R1.1); the data drive and file system are added (R4.1).

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench;

/// <summary>
/// The conditions a run was measured under (spec 008 R4), stated with every result. The build configuration is
/// detected by <c>#if DEBUG</c> — as compiled, never as assumed (R4.3). A value that cannot be read is
/// <see cref="Unknown"/>, never a guess (R4.4).
/// </summary>
internal sealed class Conditions
{
    internal const string Unknown = "unknown";

#if DEBUG
    /// <summary>True when this harness was compiled Debug — its numbers are not representative (R4.3).</summary>
    public const bool IsDebug = true;
#else
    public const bool IsDebug = false;
#endif

    public required string DateUtc { get; init; }
    public required string BuildConfiguration { get; init; }
    public required string Os { get; init; }
    public required string Processor { get; init; }
    public required int LogicalCores { get; init; }
    public required string Memory { get; init; }
    public required string DataDrive { get; init; }
    public required string FileSystem { get; init; }
    public required string DotnetVersion { get; init; }
    public required string CrystalVersion { get; init; }
    public required string RocksDbVersion { get; init; }

    /// <summary>Gathers the conditions for a run whose data directory is under <paramref name="dataParent"/>.</summary>
    public static Conditions Capture(string dataParent)
    {
        return new Conditions
        {
            DateUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            BuildConfiguration = IsDebug ? "Debug" : "Release",
            Os = SafeOsDescription(),
            Processor = ProcessorModel(),
            LogicalCores = Environment.ProcessorCount,
            Memory = TotalMemory(),
            DataDrive = DriveOf(dataParent),
            FileSystem = FileSystemOf(dataParent),
            DotnetVersion = RuntimeInformation.FrameworkDescription,
            CrystalVersion = SystemD.Crystal.CrystalVersion.Value,
            RocksDbVersion = RocksDbPackageVersion(),
        };
    }

    /// <summary>The one-line <c>**Conditions:**</c> string for the results section (design § 6).</summary>
    public string Line()
    {
        var debug = IsDebug ? " · DEBUG — not representative" : string.Empty;
        return $"{BuildConfiguration} · {Os} · {Processor}, {LogicalCores} logical · {Memory} · " +
               $"data on {DataDrive} ({FileSystem}) · {DotnetVersion} · Crystal {CrystalVersion} · " +
               $"RocksDB {RocksDbVersion}{debug}";
    }

    private static string SafeOsDescription()
    {
        try
        {
            return RuntimeInformation.OSDescription.Trim();
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static string ProcessorModel()
    {
        // The processor model is not exposed portably by .NET. Report the architecture, which is reliable;
        // the full model is "unknown" rather than a guess (R4.4).
        try
        {
            return $"{Unknown} ({RuntimeInformation.ProcessArchitecture})";
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static string TotalMemory()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
            {
                return FormatBytes(info.TotalAvailableMemoryBytes);
            }

            return Unknown;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static string DriveOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? Unknown : root;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static string FileSystemOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return Unknown;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.DriveFormat : Unknown;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static string RocksDbPackageVersion()
    {
        // Read the version of the loaded RocksDbSharp assembly, so the number is the one actually in use.
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName();
                if (string.Equals(name.Name, "RocksDbSharp", StringComparison.Ordinal))
                {
                    return name.Version?.ToString() ?? Unknown;
                }
            }

            // Not yet loaded: force a reference through the public API path is not possible here, so report unknown.
            return Unknown;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
