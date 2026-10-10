// Ported from Highway: tests/Highway.Server.Tests/Storage/GateG1Tests.cs (spec 004 design § 2.2, task T10).
// Highway asserted its seam by reflection and its store layer by scanning source text. Crystal asserts the same rules
// on the built assembly, by walking IL, so a rule cannot be dodged by formatting or by a lambda.
// Seam_HasNoEngineTypeOnItsSurface is ArchitectureTests.R31_NoRocksDbTypeOnAnyPublicSurface.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SystemD.Crystal.Architecture.Tests;

public sealed class GateTests
{
    private static readonly Assembly Crystal = typeof(global::SystemD.Crystal.CrystalVersion).Assembly;

    // Ported from Highway: GateG1Tests.ThereIsExactlyOneCommitMethodOnTheSeam (adapted: Crystal's seam is its public
    // API). Batch.Commit is the one public way durable state changes from data writes.
    [Fact]
    public void ThereIsExactlyOneCommitMethodOnThePublicApi()
    {
        var commits = Crystal.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName)   // property accessors such as get_SlowCommitThreshold are not commits
            .Where(m => m.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Distinct()
            .ToList();

        Assert.Equal(["Batch.Commit"], commits);
    }

    // Ported from Highway: GateG1Tests.StoreLayer_CommitsOnlyThroughDbWrite_SinglePoint. Every write reaches RocksDB
    // through RocksInstance, which is where a later spec puts the durability modes and replication's capture.
    [Fact]
    public void EveryRocksDbWrite_GoesThroughTheOneCommitPoint()
    {
        var violations = ArchitectureRules.RocksWritesOutside(Crystal, ArchitectureRules.CommitPoint).ToList();

        Assert.True(violations.Count == 0, "RocksDB is written only through " + ArchitectureRules.CommitPoint +
            ". Found:\n  " + string.Join("\n  ", violations));
    }

    // Ported from Highway: GateG1Tests.StoreLayer_ReadsNoClock. Crystal reads time only through the TimeProvider in
    // CrystalOptions (spec 004 R2.6), so every timestamp it writes can be driven by a fake clock in a test.
    [Fact]
    public void Crystal_ReadsNoClock_ExceptThroughItsTimeProvider()
    {
        var violations = ArchitectureRules.ClockReadsOutside(Crystal, ArchitectureRules.ClockDefault).ToList();

        Assert.True(violations.Count == 0, "Crystal reads time only through CrystalOptions.Clock. Found:\n  " +
            string.Join("\n  ", violations));
    }

    // Spec 007 R3.4, R6.4: logs and metrics never carry a key or a value.
    [Fact]
    public void NoLogOrMetricMethod_TakesAKeyOrAValue()
    {
        var violations = ArchitectureRules.KeyOrValueParameters(Crystal).ToList();

        Assert.True(violations.Count == 0, "Logs and metrics never take a key or a value. Found:\n  " + string.Join("\n  ", violations));
        Assert.Contains(Crystal.GetTypes(), t => t.Name == "CrystalLog");      // the rule is looking at real types
        Assert.Contains(Crystal.GetTypes(), t => t.Name == "CrystalMetrics");
    }

    // Ported from Highway: GateG1Tests.RocksDbNativeLibrary_IsPresentForHostRid
    [Fact]
    public void RocksDbNativeLibrary_IsPresentForHostRid()
    {
        var names = new[] { "rocksdb.dll", "librocksdb.so", "librocksdb.dylib" };
        var present = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Any(f => names.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));

        Assert.True(present, "The RocksDB native library must reach the build output for the host runtime.");
    }
}
