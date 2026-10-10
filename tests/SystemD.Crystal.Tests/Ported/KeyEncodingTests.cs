// Ported from Highway: tests/Highway.Server.Tests/Storage/KeyEncodingTests.cs (spec 004 design § 2.1, task T9).
// Each test keeps Highway's scenario and assertion against Crystal's encoder. Highway's "score" is Crystal's signed
// 64-bit encoder; its "sequence" is Crystal's unsigned one. Crystal's string terminator is 0x00 0x01 (spec 002 R7.1),
// not Highway's 0x00 0x00.

using System;
using SystemD.Crystal.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests.Ported;

public sealed class KeyEncodingTests
{
    private static byte[] EncodeInt64(long value)
    {
        var bytes = new byte[8];
        KeyEncoding.EncodeInt64(bytes, value);
        return bytes;
    }

    private static byte[] EncodeUInt64(ulong value)
    {
        var bytes = new byte[8];
        KeyEncoding.EncodeUInt64(bytes, value);
        return bytes;
    }

    private static byte[] EncodeStringThenString(string a, string b) =>
        KeyBuilder.Build((ref KeyWriter w) =>
        {
            w.WriteString(a);
            w.WriteString(b);
        });

    private static int Cmp(byte[] x, byte[] y) => Math.Sign(x.AsSpan().SequenceCompareTo(y));

    // Ported from Highway: KeyEncodingTests.Score_RoundTrips
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(638_000_000_000_000_000L)]
    [InlineData(-638_000_000_000_000_000L)]
    public void Int64_RoundTrips(long value)
    {
        var encoded = EncodeInt64(value);

        Assert.Equal(8, encoded.Length);
        Assert.Equal(value, KeyEncoding.DecodeInt64(encoded));
    }

    // Ported from Highway: KeyEncodingTests.Score_ByteOrder_EqualsNumericOrder
    [Fact]
    public void Int64_ByteOrder_EqualsNumericOrder()
    {
        var values = new long[]
        {
            long.MinValue, -1_000_000_000_000L, -5, -1, 0, 1, 5,
            1_000_000_000_000L, 638_000_000_000_000_000L, long.MaxValue,
        };

        foreach (var x in values)
        {
            foreach (var y in values)
            {
                Assert.True(
                    Cmp(EncodeInt64(x), EncodeInt64(y)) == Math.Sign(x.CompareTo(y)),
                    $"The encoding of {x} vs {y} must order like the numbers.");
            }
        }
    }

    // Ported from Highway: KeyEncodingTests.Score_NegativesSortBelowPositives
    [Fact]
    public void Int64_NegativesSortBelowPositives()
    {
        Assert.True(Cmp(EncodeInt64(-1), EncodeInt64(0)) < 0);
        Assert.True(Cmp(EncodeInt64(long.MinValue), EncodeInt64(long.MaxValue)) < 0);
    }

    // Ported from Highway: KeyEncodingTests.Sequence_IsScoreEncoding (adapted). In Highway the sequence encoder was
    // the score encoder. Crystal's unsigned and signed encoders differ in bytes (the sign bit is flipped), so what a
    // product can rely on is that they agree in order on the values both can hold, and each round-trips.
    [Theory]
    [InlineData(0L, 42L)]
    [InlineData(42L, 7L)]
    [InlineData(0L, long.MaxValue)]
    [InlineData(255L, 256L)]
    public void UnsignedAndSigned_AgreeInOrder_OnNonNegatives(long a, long b)
    {
        Assert.Equal(Cmp(EncodeInt64(a), EncodeInt64(b)), Cmp(EncodeUInt64((ulong)a), EncodeUInt64((ulong)b)));
        Assert.Equal((ulong)a, KeyEncoding.DecodeUInt64(EncodeUInt64((ulong)a)));
        Assert.Equal(a, KeyEncoding.DecodeInt64(EncodeInt64(a)));
    }

    // Ported from Highway: KeyEncodingTests.String_Encodes_NonEmpty
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("invoices")]
    [InlineData("orders@billing")]
    [InlineData("unicode-→-λ-字")]
    public void String_Encodes_NonEmpty_AndTerminated(string value)
    {
        var encoded = KeyEncoding.EncodeString(value);

        Assert.True(encoded.Length >= 2);
        Assert.Equal(0x00, encoded[^2]);
        Assert.Equal(0x01, encoded[^1]);
        Assert.Equal(value, KeyEncoding.DecodeString(encoded, out var consumed));
        Assert.Equal(encoded.Length, consumed);
    }

    // Ported from Highway: KeyEncodingTests.String_ByteOrder_EqualsOrdinalOrder. Crystal orders strings by code point
    // (spec 002 R7.5); for these values that is the same as ordinal order.
    [Fact]
    public void String_ByteOrder_EqualsOrdinalOrder()
    {
        var values = new[] { "", "a", "aa", "ab", "b", "invoices", "orders", "orders@billing", "z" };

        foreach (var x in values)
        {
            foreach (var y in values)
            {
                Assert.True(
                    Cmp(KeyEncoding.EncodeString(x), KeyEncoding.EncodeString(y)) == Math.Sign(string.CompareOrdinal(x, y)),
                    $"The encoding of '{x}' vs '{y}' must order ordinally.");
            }
        }
    }

    // Ported from Highway: KeyEncodingTests.Compound_IsSelfDelimiting
    [Fact]
    public void Compound_IsSelfDelimiting()
    {
        Assert.NotEqual(EncodeStringThenString("ab", "c"), EncodeStringThenString("a", "bc"));
    }

    // Ported from Highway: KeyEncodingTests.Compound_EmbeddedNull_DoesNotCollideWithTerminator. Crystal has a string
    // decoder, so the round trip Highway could not assert is asserted too.
    [Fact]
    public void Compound_EmbeddedNull_DoesNotCollideWithTerminator()
    {
        var withNull = EncodeStringThenString("a\0b", "c");

        Assert.NotEqual(EncodeStringThenString("a", "b"), withNull);
        Assert.NotEqual(0, Cmp(KeyEncoding.EncodeString("a\0b"), KeyEncoding.EncodeString("ab")));

        Assert.Equal("a\0b", KeyEncoding.DecodeString(withNull, out var first));
        Assert.Equal("c", KeyEncoding.DecodeString(withNull.AsSpan(first), out _));
    }

    // Ported from Highway: KeyEncodingTests.Compound_PrefixName_DoesNotAliasSibling. A range over the encoded "ab"
    // must not reach the encoded "abc", or a range delete over one name would delete its sibling.
    [Fact]
    public void Compound_PrefixName_DoesNotAliasSibling()
    {
        var ab = KeyEncoding.EncodeString("ab");
        var abc = KeyEncoding.EncodeString("abc");

        Assert.False(abc.AsSpan().StartsWith(ab), "The encoded 'ab' must not be a prefix of the encoded 'abc'.");
    }

    // Ported from Highway: KeyEncodingTests.KeyWriter_GrowsBeyondInitialBuffer
    [Fact]
    public void KeyWriter_GrowsBeyondInitialBuffer()
    {
        var big = new string('x', 500);
        Span<byte> tiny = stackalloc byte[8];
        var writer = new KeyWriter(tiny);
        writer.WriteString(big);
        var encoded = writer.ToArray();

        Assert.Equal(502, encoded.Length);
        Assert.Equal(0x00, encoded[^2]);
        Assert.Equal(0x01, encoded[^1]);
    }

    // Ported from Highway: KeyEncodingTests.KeyWriter_Growth_PreservesContent, extended to every write method and to
    // KeyBuilder, whose default buffer is 256 bytes.
    [Fact]
    public void KeyWriter_Growth_PreservesContent()
    {
        var value = new string('y', 300);

        static void WriteAll(ref KeyWriter w, string value)
        {
            w.WriteBoolean(true);
            w.WriteUInt32(7);
            w.WriteUInt64(8);
            w.WriteInt32(-9);
            w.WriteInt64(-10);
            w.WriteDouble(1.5);
            w.WriteDecimal(2.5m);
            w.WriteTimestamp(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
            w.WriteGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
            w.WriteString(value);
            w.WriteRaw([1, 2, 3]);
            w.InvertLast(3);
        }

        Span<byte> tiny = stackalloc byte[4];
        var grown = new KeyWriter(tiny);
        WriteAll(ref grown, value);
        var fromTiny = grown.ToArray();

        Span<byte> large = stackalloc byte[1024];
        var fixedSize = new KeyWriter(large);
        WriteAll(ref fixedSize, value);
        var fromLarge = fixedSize.ToArray();

        Assert.Equal(fromLarge, fromTiny);
        Assert.Equal(fromLarge, KeyBuilder.Build((ref KeyWriter w) => WriteAll(ref w, value)));
    }
}
