// Ported from Highway: tests/Highway.Storage.CrashHarness/Program.cs (DeterministicValue)

using System.Buffers.Binary;

namespace SystemD.Crystal.CrashHarness;

/// <summary>The deterministic stream the harness writes and the tests audit. The tests compile this file too, so the two never disagree.</summary>
internal static class CrashStream
{
    public const string Database = "crash";
    public const string Entries = "entries";
    public const string Marks = "marks";

    /// <summary>The key of batch <paramref name="i"/>: big-endian, so keys sort in batch order.</summary>
    public static byte[] Key(long i)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, i);
        return key;
    }

    /// <summary>32 bytes: the index, then a fixed text, so equal indices give equal bytes in every run.</summary>
    public static byte[] EntryValue(long i)
    {
        var value = new byte[32];
        BinaryPrimitives.WriteInt64BigEndian(value, i);
        System.Text.Encoding.ASCII.GetBytes("crash-payload-").CopyTo(value, 8);
        return value;
    }

    /// <summary>The mark of batch <paramref name="i"/>: its index, negated, so a mark cannot be mistaken for an entry.</summary>
    public static byte[] MarkValue(long i)
    {
        var value = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(value, -i);
        return value;
    }
}
