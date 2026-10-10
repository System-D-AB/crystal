using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SystemD.Crystal.Architecture.Tests;

/// <summary>
/// The reflection and IL machinery behind <see cref="ArchitectureTests"/>, factored out so the
/// mutation checks (R3.4) can drive the same rules against a deliberately-broken sample.
/// </summary>
internal static class ArchitectureRules
{
    private const string RocksNamespacePrefix = "RocksDbSharp";
    private const string StorageNamespace = "SystemD.Crystal.Storage";

    private static bool IsRocksType(Type? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.HasElementType)
        {
            return IsRocksType(type.GetElementType());
        }

        if (type.IsGenericType)
        {
            if (InNamespace(type, RocksNamespacePrefix))
            {
                return true;
            }

            return type.GetGenericArguments().Any(IsRocksType);
        }

        return InNamespace(type, RocksNamespacePrefix);
    }

    private static bool InNamespace(Type type, string prefix)
    {
        var ns = type.Namespace ?? string.Empty;
        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return ns.StartsWith(prefix, StringComparison.Ordinal)
            || assembly.StartsWith(prefix, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ R3.1

    /// <summary>Every RocksDB type reachable from a public/protected surface of a visible type.</summary>
    public static IEnumerable<string> PublicSurfaceRocksDbReferences(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes().Where(t => t.IsVisible))
        {
            if (IsRocksType(type.BaseType))
            {
                yield return $"{type.FullName} : base {type.BaseType!.FullName}";
            }

            foreach (var iface in type.GetInterfaces().Where(IsRocksType))
            {
                yield return $"{type.FullName} : interface {iface.FullName}";
            }

            foreach (var arg in type.GetGenericArguments().Where(IsRocksType))
            {
                yield return $"{type.FullName} : generic arg {arg.FullName}";
            }

            foreach (var field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
            {
                if (IsRocksType(field.FieldType))
                {
                    yield return $"{type.FullName}.{field.Name} field : {field.FieldType.FullName}";
                }
            }

            foreach (var prop in type.GetProperties(flags))
            {
                if (IsRocksType(prop.PropertyType))
                {
                    yield return $"{type.FullName}.{prop.Name} property : {prop.PropertyType.FullName}";
                }
            }

            foreach (var method in type.GetMethods(flags).Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
            {
                if (IsRocksType(method.ReturnType))
                {
                    yield return $"{type.FullName}.{method.Name} returns {method.ReturnType.FullName}";
                }

                foreach (var p in method.GetParameters().Where(p => IsRocksType(p.ParameterType)))
                {
                    yield return $"{type.FullName}.{method.Name} parameter {p.Name} : {p.ParameterType.FullName}";
                }
            }

            foreach (var ctor in type.GetConstructors(flags).Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly))
            {
                foreach (var p in ctor.GetParameters().Where(p => IsRocksType(p.ParameterType)))
                {
                    yield return $"{type.FullName}..ctor parameter {p.Name} : {p.ParameterType.FullName}";
                }
            }
        }
    }

    // ------------------------------------------------------------------ R3.2

    /// <summary>Every RocksDB reference (signature or IL body) from a type outside the Storage namespace.</summary>
    public static IEnumerable<string> RocksDbReferencesOutsideStorage(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes())
        {
            var ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith(StorageNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var field in type.GetFields(flags).Where(f => IsRocksType(f.FieldType)))
            {
                yield return $"{type.FullName}.{field.Name} field : {field.FieldType.FullName}";
            }

            foreach (var method in type.GetMethods(flags))
            {
                foreach (var reference in RocksTokensInBody(method))
                {
                    yield return $"{type.FullName}.{method.Name} body → {reference}";
                }
            }

            foreach (var ctor in type.GetConstructors(flags))
            {
                foreach (var reference in RocksTokensInBody(ctor))
                {
                    yield return $"{type.FullName}..ctor body → {reference}";
                }
            }
        }
    }

    /// <summary>
    /// Walks a method's IL opcode by opcode, resolving the metadata token of every token-carrying
    /// instruction, and yields any that resolves to a RocksDB type or a member declared on one.
    /// This is the "RocksDB used only inside Storage" half of R3.2 — a signature scan alone would
    /// miss a RocksDB call made through a local. A proper opcode walk (not a blind byte scan) is
    /// used so arbitrary IL bytes cannot resolve as spurious tokens.
    /// </summary>
    private static IEnumerable<string> RocksTokensInBody(MethodBase method)
    {
        foreach (var member in MembersInBody(method))
        {
            switch (member)
            {
                case Type t when IsRocksType(t):
                    yield return t.FullName!;
                    break;
                case MemberInfo m when IsRocksType(m.DeclaringType):
                    yield return $"{m.DeclaringType!.FullName}.{m.Name}";
                    break;
            }
        }
    }

    /// <summary>
    /// Every member referenced by the body of every method and constructor of every type in
    /// <paramref name="assembly"/>, with where it was referenced from (spec 004 T10: the single-commit-point and
    /// no-clock rules ported from Highway's gate tests). Compiler-generated types (lambdas, iterators, async state
    /// machines) are included, so a reference cannot hide in one.
    /// </summary>
    public static IEnumerable<(string Where, MemberInfo Member)> MemberReferences(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes())
        {
            var bodies = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
            foreach (var method in bodies)
            {
                foreach (var member in MembersInBody(method))
                {
                    yield return ($"{type.FullName}.{method.Name}", member);
                }
            }
        }
    }

    /// <summary>
    /// Walks a method's IL opcode by opcode and yields the member or type behind every metadata token it carries. A
    /// proper opcode walk (not a blind byte scan) is used so arbitrary IL bytes cannot resolve as spurious tokens.
    /// </summary>
    private static IEnumerable<MemberInfo> MembersInBody(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        var module = method.Module;
        var generic = SafeGenericArguments(method.DeclaringType);
        var methodGeneric = method is MethodInfo mi && mi.IsGenericMethod ? mi.GetGenericArguments() : Type.EmptyTypes;

        var pos = 0;
        while (pos < il.Length)
        {
            // Decode the opcode (one byte, or 0xFE-prefixed two bytes).
            int opcode = il[pos++];
            if (opcode == 0xFE && pos < il.Length)
            {
                opcode = 0xFE00 | il[pos++];
            }

            var operand = IlOperandSize(opcode);
            if (operand == OperandInlineSwitch)
            {
                if (pos + 4 > il.Length)
                {
                    yield break;
                }

                var count = BitConverter.ToInt32(il, pos);
                pos += 4 + (count * 4);
                continue;
            }

            if (operand == OperandToken)
            {
                if (pos + 4 > il.Length)
                {
                    yield break;
                }

                var token = BitConverter.ToInt32(il, pos);
                var member = TryResolve(module, token, generic, methodGeneric);
                if (member is not null)
                {
                    yield return member;
                }

                pos += 4;
                continue;
            }

            pos += operand;
        }
    }

    private const int OperandToken = -1;
    private const int OperandInlineSwitch = -2;

    /// <summary>
    /// The operand size in bytes for an IL opcode: <see cref="OperandToken"/> for a 4-byte metadata
    /// token (the instructions we care about), <see cref="OperandInlineSwitch"/> for the switch
    /// instruction, or a fixed byte count otherwise.
    /// </summary>
    private static int IlOperandSize(int opcode) => opcode switch
    {
        // Token-carrying opcodes (single-byte): call, calli, callvirt, cpobj, ldobj, ldstr, newobj,
        // castclass, isinst, unbox, ldfld, ldflda, stfld, ldsfld, ldsflda, stsfld, stobj, box,
        // newarr, ldelema, ldelem, stelem, unbox.any, ldtoken, mkrefany, refanyval, initobj,
        // constrained-prefixed targets, jmp, ldftn/ldvirtftn (two-byte, handled below), sizeof.
        0x28 or 0x29 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79
            or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D
            or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 => OperandToken,

        // Two-byte (0xFE-prefixed) token-carrying: ldftn (0xFE06), ldvirtftn (0xFE07),
        // initobj (0xFE15), constrained. (0xFE16), sizeof (0xFE1C).
        0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => OperandToken,

        0x45 => OperandInlineSwitch, // switch

        // Inline branch targets (including leave, 0xDD) and 4-byte operands that are NOT tokens.
        0x20 or 0x22 or 0x27 or 0x38 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E
            or 0x3F or 0x40 or 0x41 or 0x42 or 0x43 or 0x44 or 0xDD => 4,
        0x21 or 0x23 => 8,  // ldc.i8 / ldc.r8
        0x1F => 1,          // ldc.i4.s
        // Short branches (including leave.s, 0xDE) and short arg/local ops (1-byte operand):
        // ldarg.s 0x0E, ldarga.s 0x0F, starg.s 0x10, ldloc.s 0x11, ldloca.s 0x12, stloc.s 0x13 (ECMA-335 III).
        0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x34
            or 0x35 or 0x36 or 0x37 or 0xDE or 0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 => 1,
        // Two-byte prefixes with a 1-byte operand: unaligned. (0xFE12), no. (0xFE19).
        0xFE12 or 0xFE19 => 1,
        // Two-byte arg/local ops (2-byte operand).
        0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E => 2,
        _ => 0,
    };

    private static Type[] SafeGenericArguments(Type? type)
    {
        try
        {
            return type is { IsGenericType: true } ? type.GetGenericArguments() : Type.EmptyTypes;
        }
        catch (Exception)
        {
            return Type.EmptyTypes;
        }
    }

    private static MemberInfo? TryResolve(Module module, int token, Type[] typeArgs, Type[] methodArgs)
    {
        try
        {
            return module.ResolveMember(token, typeArgs, methodArgs);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------- ported gate rules (spec 004 T10)

    /// <summary>The one type allowed to hand a write to RocksDB: every commit goes through it.</summary>
    public const string CommitPoint = "SystemD.Crystal.Storage.RocksInstance";

    /// <summary>The one type allowed to name the system clock: the default of <c>CrystalOptions.Clock</c>.</summary>
    public const string ClockDefault = "SystemD.Crystal.CrystalOptions";

    /// <summary>
    /// Every reference to a RocksDB member that changes data (a write, put, delete, merge or ingest, managed or native)
    /// from any type other than <paramref name="allowedType"/> or its compiler-generated nested types.
    /// </summary>
    public static IEnumerable<string> RocksWritesOutside(Assembly assembly, string allowedType) =>
        MemberReferences(assembly)
            .Where(r => IsRocksWrite(r.Member) && !IsInside(r.Where, allowedType))
            .Select(r => $"{r.Where} → {r.Member.DeclaringType?.FullName}.{r.Member.Name}");

    /// <summary>
    /// Every reference to a clock (the wall clock, a stopwatch, the tick count, or the system time provider) from any
    /// type other than <paramref name="allowedType"/>. Time is read only through an injected <c>TimeProvider</c>.
    /// </summary>
    public static IEnumerable<string> ClockReadsOutside(Assembly assembly, string allowedType) =>
        MemberReferences(assembly)
            .Where(r => IsClockRead(r.Member) && !IsInside(r.Where, allowedType))
            .Select(r => $"{r.Where} → {r.Member.DeclaringType?.FullName}.{r.Member.Name}");

    private static bool IsInside(string where, string type) =>
        where.StartsWith(type + ".", StringComparison.Ordinal) || where.StartsWith(type + "+", StringComparison.Ordinal);

    private static bool IsRocksWrite(MemberInfo member)
    {
        var declaring = member.DeclaringType?.FullName;
        var name = member.Name;
        return declaring switch
        {
            "RocksDbSharp.RocksDb" => name is "Write" or "Put" or "Remove" or "Merge" or "SingleDelete"
                or "DeleteRange" or "IngestExternalFiles" or "IngestExternalFile",
            "RocksDbSharp.Native" => name == "rocksdb_write"
                || name.StartsWith("rocksdb_put", StringComparison.Ordinal)
                || name.StartsWith("rocksdb_delete", StringComparison.Ordinal)
                || name.StartsWith("rocksdb_singledelete", StringComparison.Ordinal)
                || name.StartsWith("rocksdb_ingest", StringComparison.Ordinal)
                || name is "rocksdb_merge" or "rocksdb_merge_cf",
            _ => false,
        };
    }

    private static bool IsClockRead(MemberInfo member)
    {
        var declaring = member.DeclaringType?.FullName;
        return declaring switch
        {
            "System.DateTime" or "System.DateTimeOffset" => member.Name is "get_Now" or "get_UtcNow" or "get_Today",
            "System.Environment" => member.Name is "get_TickCount" or "get_TickCount64",
            "System.Diagnostics.Stopwatch" => true,
            "System.TimeProvider" => member.Name == "get_System",
            _ => member is Type t && t.FullName == "System.Diagnostics.Stopwatch",
        };
    }

    // ------------------------------------------------- observability (spec 007 R6.4)

    /// <summary>The types that write logs and record metrics: none of their methods may take a key or a value.</summary>
    public static readonly string[] ObservabilityTypes = ["CrystalLog", "CrystalMetrics"];

    /// <summary>
    /// Every method of a type named in <see cref="ObservabilityTypes"/> with a parameter that could carry a key or value
    /// (<c>byte[]</c>, <c>ReadOnlySpan&lt;byte&gt;</c>, <c>Span&lt;byte&gt;</c>, <c>KeyValue</c>), so one cannot be logged or
    /// tagged without the build's tests failing.
    /// </summary>
    public static IEnumerable<string> KeyOrValueParameters(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes().Where(t => ObservabilityTypes.Contains(t.Name)))
        {
            foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
            {
                foreach (var parameter in method.GetParameters())
                {
                    var t = parameter.ParameterType;
                    if (t == typeof(byte[]) || t == typeof(ReadOnlySpan<byte>) || t == typeof(Span<byte>) || t.Name == "KeyValue")
                    {
                        yield return $"{type.FullName}.{method.Name}({parameter.Name}: {t.Name})";
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ R3.3

    public static bool IsAllowedReference(string assemblyName)
    {
        if (assemblyName.Length == 0)
        {
            return true;
        }

        if (assemblyName is "System" or "mscorlib" or "netstandard" or "System.Private.CoreLib" or "SystemD.Crystal")
        {
            return true;
        }

        return assemblyName.StartsWith("System.", StringComparison.Ordinal)
            || assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || assemblyName.StartsWith(RocksNamespacePrefix, StringComparison.Ordinal)
            || assemblyName.StartsWith("RocksDB", StringComparison.Ordinal);
    }
}
