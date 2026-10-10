using System;
using System.Buffers;

namespace SystemD.Crystal.Encoding;

/// <summary>
/// Helper delegate for writing composite keys with a <see cref="KeyWriter"/>.
/// </summary>
public delegate void KeyWriterAction(ref KeyWriter writer);

/// <summary>
/// Helper for composing keys into byte arrays (spec 002 R7).
/// </summary>
public static class KeyBuilder
{
    /// <summary>
    /// Builds a key byte array using <paramref name="writeAction"/>.
    /// Uses a rented buffer to avoid intermediate allocations.
    /// </summary>
    public static byte[] Build(KeyWriterAction writeAction, int initialCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(writeAction);
        var rented = ArrayPool<byte>.Shared.Rent(initialCapacity);
        try
        {
            var writer = new KeyWriter(rented);
            writeAction(ref writer);
            return writer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
