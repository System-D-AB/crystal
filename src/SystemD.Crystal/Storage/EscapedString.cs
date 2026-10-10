using System;
using SystemD.Crystal.Encoding;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Order-preserving string escaping (box-layout § 5):
/// UTF-8, 0x00 escaped as 0x00 0xFF, terminated by 0x00 0x01.
/// Delegates to <see cref="KeyEncoding"/> as the single string encoder in Crystal (spec 002 T2).
/// </summary>
internal static class EscapedString
{
    public static byte[] Encode(string s) => KeyEncoding.EncodeString(s);

    public static string Decode(ReadOnlySpan<byte> bytes, out int bytesConsumed) =>
        KeyEncoding.DecodeString(bytes, out bytesConsumed);
}
