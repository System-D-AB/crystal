using System;
using System.Linq;
using RocksDbSharp;
using Xunit;

namespace SystemD.Crystal.Architecture.Tests;

/// <summary>
/// Spec 001 R3.4 — each architecture rule, shown to fail when broken on purpose. The playbook's
/// verification rule is that a guard nobody has seen fail protects nothing.
///
/// <para>Rather than mutate the kernel and revert it by hand, this holds deliberate violations in
/// a sample assembly built in-process and confirms the same rules from
/// <see cref="ArchitectureRules"/> catch each one. The real kernel assembly stays clean.</para>
/// </summary>
public sealed class ArchitectureMutationTests
{
    // ---- The deliberately-broken samples the mutation checks run against. ----
    // These live under a fake "SystemD.Crystal.Storage"-ish shape only for the rule inputs; the
    // rules are invoked against this *test* assembly, and these types are the planted violations.

    // R3.1 violation: a public type exposing a RocksDB type on a public member.
    public sealed class LeaksRocksOnPublicSurface
    {
        public DbOptions? Options { get; set; }
    }

    // R3.2 (body) violation: a non-Storage type that calls into RocksDB in its method body.
    public static class UsesRocksOutsideStorage
    {
        public static void Touch()
        {
            var options = new DbOptions();
            GC.KeepAlive(options);
        }
    }

    // Gate violation (spec 004 T10): a write handed to RocksDB outside the one commit point.
    public static class WritesRocksOutsideTheCommitPoint
    {
        public static void Write(RocksDb db) => db.Put("k"u8.ToArray(), "v"u8.ToArray());
    }

    // Gate violation (spec 004 T10): the wall clock read directly, from a lambda, so the walk must reach it.
    public static class ReadsTheClock
    {
        public static Func<long> Ticks => () => DateTimeOffset.UtcNow.Ticks;
    }

    // Observability violation (spec 007 R6.4): a log method that takes a key.
    public static class CrystalLog
    {
        public static void Leak(byte[] key) => GC.KeepAlive(key);
    }

    [Fact]
    public void KeyOrValueGuard_CatchesALogMethodTakingAKey()
    {
        var violations = ArchitectureRules.KeyOrValueParameters(typeof(ArchitectureMutationTests).Assembly).ToList();

        Assert.Contains(violations, v => v.Contains("Leak(key", StringComparison.Ordinal));
    }

    [Fact]
    public void CommitPointGuard_CatchesAWriteOutsideIt()
    {
        var violations = ArchitectureRules.RocksWritesOutside(typeof(ArchitectureMutationTests).Assembly, ArchitectureRules.CommitPoint)
            .ToList();

        Assert.Contains(violations, v => v.Contains(nameof(WritesRocksOutsideTheCommitPoint), StringComparison.Ordinal));
    }

    [Fact]
    public void ClockGuard_CatchesAClockReadInALambda()
    {
        var violations = ArchitectureRules.ClockReadsOutside(typeof(ArchitectureMutationTests).Assembly, ArchitectureRules.ClockDefault)
            .ToList();

        Assert.Contains(violations, v => v.Contains(nameof(ReadsTheClock), StringComparison.Ordinal) && v.Contains("UtcNow", StringComparison.Ordinal));
    }

    [Fact]
    public void R31_Guard_CatchesARocksTypeOnAPublicSurface()
    {
        var violations = ArchitectureRules.PublicSurfaceRocksDbReferences(typeof(ArchitectureMutationTests).Assembly)
            .ToList();

        Assert.Contains(violations, v => v.Contains(nameof(LeaksRocksOnPublicSurface), StringComparison.Ordinal));
    }

    [Fact]
    public void R32_Guard_CatchesARocksCallInAMethodBodyOutsideStorage()
    {
        var violations = ArchitectureRules.RocksDbReferencesOutsideStorage(typeof(ArchitectureMutationTests).Assembly)
            .ToList();

        Assert.Contains(violations, v => v.Contains(nameof(UsesRocksOutsideStorage), StringComparison.Ordinal));
    }

    [Fact]
    public void R32_Guard_CatchesAPublicTypeInTheStorageNamespace()
    {
        // A planted public type whose namespace is the Storage namespace proves the "all internal"
        // check (first half of R3.2) would fail if a kernel Storage type were ever made public.
        var publicStorageTypes = typeof(global::SystemD.Crystal.Storage.PlantedPublicStorageType).Assembly.GetTypes()
            .Where(t => (t.Namespace ?? string.Empty).StartsWith("SystemD.Crystal.Storage", StringComparison.Ordinal))
            .Where(t => t.IsVisible)
            .Select(t => t.FullName!)
            .ToList();

        Assert.Contains(publicStorageTypes, n => n.Contains("PlantedPublicStorageType", StringComparison.Ordinal));
    }

    [Fact]
    public void R33_Guard_RejectsAProductAssemblyName()
    {
        Assert.False(ArchitectureRules.IsAllowedReference("Highway.Server"));
        Assert.False(ArchitectureRules.IsAllowedReference("Stow.Storage"));

        // And accepts the real allow-list, so the rule is not vacuously strict.
        Assert.True(ArchitectureRules.IsAllowedReference("System.Text.Json"));
        Assert.True(ArchitectureRules.IsAllowedReference("Microsoft.Extensions.Logging.Abstractions"));
        Assert.True(ArchitectureRules.IsAllowedReference("RocksDbSharp"));
    }
}
