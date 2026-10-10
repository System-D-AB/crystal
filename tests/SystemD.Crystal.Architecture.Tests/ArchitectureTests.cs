using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SystemD.Crystal.Architecture.Tests;

/// <summary>
/// Spec 001 R3 — the boundary that keeps RocksDB out of Crystal's public surface and inside one
/// namespace, enforced by reflection over the built assembly with no extra framework (design § 4).
///
/// <para>Each rule is mutation-checked once (R3.4): see <c>ArchitectureMutationTests</c>, where a
/// deliberate violation is confirmed to be caught. A guard nobody has seen fail protects nothing.</para>
/// </summary>
public sealed class ArchitectureTests
{
    private const string StorageNamespace = "SystemD.Crystal.Storage";

    private static readonly Assembly Crystal = typeof(global::SystemD.Crystal.CrystalVersion).Assembly;

    /// <summary>
    /// R3.1 — no public type, and no public or protected member signature, exposes a RocksDB type:
    /// no base type, interface, field, property, parameter, return type or generic argument.
    /// </summary>
    [Fact]
    public void R31_NoRocksDbTypeOnAnyPublicSurface()
    {
        var violations = ArchitectureRules.PublicSurfaceRocksDbReferences(Crystal).ToList();

        Assert.True(
            violations.Count == 0,
            "RocksDB types must not appear on any public/protected surface (R3.1). Found:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// R3.2 (first half) — every type in <see cref="StorageNamespace"/> is <c>internal</c>, never public.
    /// </summary>
    [Fact]
    public void R32_EveryTypeInStorageNamespaceIsInternal()
    {
        var publicStorageTypes = Crystal.GetTypes()
            .Where(t => (t.Namespace ?? string.Empty).StartsWith(StorageNamespace, StringComparison.Ordinal))
            .Where(t => t.IsVisible)
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(
            publicStorageTypes.Count == 0,
            "Types under " + StorageNamespace + " must be internal (R3.2). Found public:\n  " +
            string.Join("\n  ", publicStorageTypes));
    }

    /// <summary>
    /// R3.2 (second half) — no type outside <see cref="StorageNamespace"/> references a RocksDB type,
    /// in a field, a property, a method signature, or a method body (an IL token scan).
    /// </summary>
    [Fact]
    public void R32_RocksDbUsedOnlyInsideStorageNamespace()
    {
        var violations = ArchitectureRules.RocksDbReferencesOutsideStorage(Crystal).ToList();

        Assert.True(
            violations.Count == 0,
            "RocksDB may only be used inside " + StorageNamespace + " (R3.2). Found elsewhere:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// R3.3 — the assembly references only an allow-list: the .NET libraries,
    /// <c>Microsoft.Extensions.*</c> abstractions and the RocksDB binding. No product.
    /// </summary>
    [Fact]
    public void R33_ReferencedAssembliesAreOnTheAllowList()
    {
        var disallowed = Crystal.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !ArchitectureRules.IsAllowedReference(name))
            .ToList();

        Assert.True(
            disallowed.Count == 0,
            "Crystal may reference only the .NET BCL, Microsoft.Extensions.*.Abstractions and RocksDbSharp (R3.3). " +
            "Disallowed:\n  " + string.Join("\n  ", disallowed));
    }
}
