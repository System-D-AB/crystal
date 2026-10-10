using System;
using System.Runtime.InteropServices;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A failure reported by RocksDB native code, carrying the engine's own message (spec 001-2 R2.2).
/// Native calls hand back a C string on error; this type turns it into a managed exception and frees
/// the string, so a caller sees a typed exception with the way forward, never a leaked pointer or a
/// process crash.
/// </summary>
internal sealed class RocksException : Exception
{
    private RocksException(string message)
        : base(message)
    {
    }

    /// <summary>Builds an exception from a native error pointer and frees it. Returns <c>null</c> for <see cref="IntPtr.Zero"/>.</summary>
    internal static RocksException? TryFrom(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return null;
        }

        var message = Marshal.PtrToStringAnsi(error) ?? "RocksDB reported an error with no message.";
        RocksDbSharp.Native.Instance.rocksdb_free(error);
        return new RocksException("RocksDB: " + message);
    }

    /// <summary>Throws when <paramref name="error"/> is non-null; frees the native string either way.</summary>
    internal static void ThrowIfError(IntPtr error)
    {
        var exception = TryFrom(error);
        if (exception is not null)
        {
            throw exception;
        }
    }
}
