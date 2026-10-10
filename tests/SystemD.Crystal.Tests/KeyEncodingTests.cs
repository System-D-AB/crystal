// Ported from StowDB: src/Crystal/Crystal.Storage/Encoding/
// Adapted for Crystal: order-preserving key encoding property tests (spec 002 R7, R10.3).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SystemD.Crystal.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class KeyEncodingTests
{
    private static int CompareBytes(byte[] a, byte[] b) =>
        a.AsSpan().SequenceCompareTo(b.AsSpan());

    [Fact]
    public void Booleans_OrderAndRoundtrip()
    {
        Span<byte> fBytes = stackalloc byte[1];
        Span<byte> tBytes = stackalloc byte[1];
        KeyEncoding.EncodeBoolean(fBytes, false);
        KeyEncoding.EncodeBoolean(tBytes, true);

        Assert.True(CompareBytes(fBytes.ToArray(), tBytes.ToArray()) < 0);
        Assert.False(KeyEncoding.DecodeBoolean(fBytes));
        Assert.True(KeyEncoding.DecodeBoolean(tBytes));
    }

    [Fact]
    public void UInt32_OrderAndRoundtrip()
    {
        uint[] values = [0, 1, 2, 42, 1000, 65535, 1_000_000, uint.MaxValue - 1, uint.MaxValue];
        byte[][] encoded = new byte[values.Length][];

        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = new byte[4];
            KeyEncoding.EncodeUInt32(encoded[i], values[i]);
            Assert.Equal(values[i], KeyEncoding.DecodeUInt32(encoded[i]));
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"Byte order failed for {values[i]} vs {values[i + 1]}");
        }
    }

    [Fact]
    public void UInt64_OrderAndRoundtrip()
    {
        ulong[] values = [0, 1, 42, 1000, uint.MaxValue, (ulong)uint.MaxValue + 1, ulong.MaxValue - 1, ulong.MaxValue];
        byte[][] encoded = new byte[values.Length][];

        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = new byte[8];
            KeyEncoding.EncodeUInt64(encoded[i], values[i]);
            Assert.Equal(values[i], KeyEncoding.DecodeUInt64(encoded[i]));
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0);
        }
    }

    [Fact]
    public void Int32_OrderAndRoundtrip()
    {
        int[] values = [int.MinValue, int.MinValue + 1, -100_000, -1, 0, 1, 100_000, int.MaxValue - 1, int.MaxValue];
        byte[][] encoded = new byte[values.Length][];

        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = new byte[4];
            KeyEncoding.EncodeInt32(encoded[i], values[i]);
            Assert.Equal(values[i], KeyEncoding.DecodeInt32(encoded[i]));
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"Byte order failed for {values[i]} vs {values[i + 1]}");
        }
    }

    [Fact]
    public void Int64_OrderAndRoundtrip()
    {
        long[] values = [long.MinValue, long.MinValue + 1, -1_000_000_000_000L, -1, 0, 1, 1_000_000_000_000L, long.MaxValue - 1, long.MaxValue];
        byte[][] encoded = new byte[values.Length][];

        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = new byte[8];
            KeyEncoding.EncodeInt64(encoded[i], values[i]);
            Assert.Equal(values[i], KeyEncoding.DecodeInt64(encoded[i]));
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"Byte order failed for {values[i]} vs {values[i + 1]}");
        }
    }

    [Fact]
    public void Double_OrderAndRoundtrip()
    {
        double[] values =
        [
            double.NegativeInfinity,
            -1e300,
            -1000.5,
            -1.0,
            -1e-300,
            -0.0,
            0.0,
            1e-300,
            1.0,
            1000.5,
            1e300,
            double.PositiveInfinity,
            double.NaN
        ];

        // Note: -0.0 and 0.0 normalize to 0.0, so their encoded bytes are equal
        Span<byte> negZero = stackalloc byte[8];
        Span<byte> posZero = stackalloc byte[8];
        KeyEncoding.EncodeDouble(negZero, -0.0);
        KeyEncoding.EncodeDouble(posZero, 0.0);
        Assert.Equal(0, CompareBytes(negZero.ToArray(), posZero.ToArray()));

        // Check strictly increasing values
        double[] strictValues =
        [
            double.NegativeInfinity,
            -1e300,
            -1000.5,
            -1.0,
            -1e-300,
            0.0,
            1e-300,
            1.0,
            1000.5,
            1e300,
            double.PositiveInfinity,
            double.NaN
        ];

        byte[][] encoded = new byte[strictValues.Length][];
        for (var i = 0; i < strictValues.Length; i++)
        {
            encoded[i] = new byte[8];
            KeyEncoding.EncodeDouble(encoded[i], strictValues[i]);
            var decoded = KeyEncoding.DecodeDouble(encoded[i]);
            if (double.IsNaN(strictValues[i]))
            {
                Assert.True(double.IsNaN(decoded));
            }
            else
            {
                Assert.Equal(strictValues[i], decoded);
            }
        }

        for (var i = 0; i < strictValues.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"Double byte order failed for {strictValues[i]} vs {strictValues[i + 1]}");
        }
    }

    [Fact]
    public void Decimal_OrderAndRoundtrip()
    {
        decimal[] values =
        [
            decimal.MinValue,
            -1_000_000.50m,
            -100m,
            -1.0000000001m,
            -1m,
            -0.00001m,
            0m,
            0.00001m,
            1m,
            1.0000000001m,
            100m,
            1_000_000.50m,
            decimal.MaxValue
        ];

        byte[][] encoded = new byte[values.Length][];
        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = new byte[KeyEncoding.DecimalEncodedLength];
            KeyEncoding.EncodeDecimal(encoded[i], values[i]);
            var decoded = KeyEncoding.DecodeDecimal(encoded[i]);
            Assert.Equal(values[i], decoded);
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"Decimal byte order failed for {values[i]} vs {values[i + 1]}");
        }
    }

    [Fact]
    public void Timestamp_OrderAndRoundtrip()
    {
        DateTimeOffset[] values =
        [
            DateTimeOffset.MinValue,
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 9, 14, 0, 0, 123, TimeSpan.Zero),
            DateTimeOffset.MaxValue
        ];

        byte[][] encoded = new byte[values.Length][];
        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = new byte[8];
            KeyEncoding.EncodeTimestamp(encoded[i], values[i]);
            var decoded = KeyEncoding.DecodeTimestamp(encoded[i]);
            Assert.Equal(values[i], decoded);
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0);
        }
    }

    [Fact]
    public void String_OrderAndEscaping()
    {
        // Spec R7.1: strings UTF-8 with 0x00 escaped as 0x00 0xFF, terminated by 0x00 0x01
        string[] values =
        [
            "",
            "\0",
            "\0\0",
            "\0a",
            "a",
            "a\0",
            "a\0b",
            "aa",
            "ab",
            "b",
            "z",
            "ä",
            "å",
            "ö"
        ];

        byte[][] encoded = new byte[values.Length][];
        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = KeyEncoding.EncodeString(values[i]);
            var decoded = KeyEncoding.DecodeString(encoded[i], out var bytesRead);
            Assert.Equal(values[i], decoded);
            Assert.Equal(encoded[i].Length, bytesRead);
        }

        for (var i = 0; i < values.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"String byte order failed for '{values[i]}' vs '{values[i + 1]}'");
        }
    }

    [Fact]
    public void CompositeKeys_AreUnambiguous_R72()
    {
        // R7.2: ("ab", "c") and ("a", "bc") must NEVER encode to the same bytes.
        Span<byte> buffer1 = stackalloc byte[128];
        Span<byte> buffer2 = stackalloc byte[128];

        var writer1 = new KeyWriter(buffer1);
        writer1.WriteString("ab");
        writer1.WriteString("c");

        var writer2 = new KeyWriter(buffer2);
        writer2.WriteString("a");
        writer2.WriteString("bc");

        Assert.False(buffer1[..writer1.BytesWritten].SequenceEqual(buffer2[..writer2.BytesWritten]));
    }

    [Fact]
    public void Guid_OrderAndRoundtrip()
    {
        Guid[] guids =
        [
            Guid.Parse("00000000-0000-0000-0000-000000000000"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0001-0000-000000000000"),
            Guid.Parse("00000001-0000-0000-0000-000000000000"),
            Guid.Parse("12345678-abcd-ef01-2345-6789abcdef01"),
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")
        ];

        byte[][] encoded = new byte[guids.Length][];
        for (var i = 0; i < guids.Length; i++)
        {
            encoded[i] = new byte[16];
            KeyEncoding.EncodeGuid(encoded[i], guids[i]);
            var decoded = KeyEncoding.DecodeGuid(encoded[i]);
            Assert.Equal(guids[i], decoded);
        }

        for (var i = 0; i < guids.Length - 1; i++)
        {
            Assert.True(CompareBytes(encoded[i], encoded[i + 1]) < 0,
                $"Guid byte order failed for {guids[i]} vs {guids[i + 1]}");
        }
    }

    [Fact]
    public void Descending_InvertsByteOrder()
    {
        int[] asc = [1, 2, 3, 10, 100, 1000];
        byte[][] encodedDesc = new byte[asc.Length][];

        for (var i = 0; i < asc.Length; i++)
        {
            encodedDesc[i] = new byte[4];
            KeyEncoding.EncodeInt32(encodedDesc[i], asc[i]);
            KeyEncoding.Invert(encodedDesc[i]);
        }

        // With complement inverted, order is strictly descending
        for (var i = 0; i < asc.Length - 1; i++)
        {
            Assert.True(CompareBytes(encodedDesc[i], encodedDesc[i + 1]) > 0,
                $"Descending failed for {asc[i]} vs {asc[i + 1]}");
        }
    }

    [Fact]
    public void RandomValues_PropertyTest_OrderPreserved()
    {
        var random = new Random(42);

        // 1000 random int32s
        var ints = Enumerable.Range(0, 1000).Select(_ => random.Next(int.MinValue, int.MaxValue)).Distinct().ToArray();
        Array.Sort(ints);
        Span<byte> aInt = stackalloc byte[4];
        Span<byte> bInt = stackalloc byte[4];
        for (var i = 0; i < ints.Length - 1; i++)
        {
            KeyEncoding.EncodeInt32(aInt, ints[i]);
            KeyEncoding.EncodeInt32(bInt, ints[i + 1]);
            Assert.True(CompareBytes(aInt.ToArray(), bInt.ToArray()) < 0);
        }

        // 1000 random doubles (avoiding NaN for simple sorting)
        var doubles = Enumerable.Range(0, 1000).Select(_ => (random.NextDouble() - 0.5) * 1e20).Distinct().ToArray();
        Array.Sort(doubles);
        Span<byte> aDbl = stackalloc byte[8];
        Span<byte> bDbl = stackalloc byte[8];
        for (var i = 0; i < doubles.Length - 1; i++)
        {
            KeyEncoding.EncodeDouble(aDbl, doubles[i]);
            KeyEncoding.EncodeDouble(bDbl, doubles[i + 1]);
            Assert.True(CompareBytes(aDbl.ToArray(), bDbl.ToArray()) < 0);
        }
    }
}
