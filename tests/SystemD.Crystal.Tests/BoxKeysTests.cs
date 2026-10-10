// Ported from Highway: src/Highway.Server/Storage/Boxes/BoxKeys.cs
// Adapted for Crystal: box prefixing, reserved IDs, bounds, isolation (spec 002 R1).

using System;
using System.Buffers.Binary;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class BoxKeysTests
{
    private static int CompareBytes(byte[] a, byte[] b) =>
        a.AsSpan().SequenceCompareTo(b.AsSpan());

    [Fact]
    public void ReservedIds_Box0IsCatalogue_ProductsStartAt16()
    {
        Assert.Equal(0u, BoxKeys.CatalogueBoxId);
        Assert.Equal(16u, BoxKeys.FirstProductBoxId);
    }

    [Fact]
    public void BuildKey_PrependsBoxIdInBigEndian()
    {
        uint boxId = 0x12345678;
        byte[] userKey = [0xAA, 0xBB, 0xCC];

        var key = BoxKeys.BuildKey(boxId, userKey);

        Assert.Equal(7, key.Length);
        Assert.Equal(0x12, key[0]);
        Assert.Equal(0x34, key[1]);
        Assert.Equal(0x56, key[2]);
        Assert.Equal(0x78, key[3]);
        Assert.Equal(0xAA, key[4]);
        Assert.Equal(0xBB, key[5]);
        Assert.Equal(0xCC, key[6]);

        Assert.Equal(boxId, BoxKeys.ExtractBoxId(key));
        Assert.True(userKey.AsSpan().SequenceEqual(BoxKeys.ExtractUserKey(key)));
    }

    [Fact]
    public void EmptyUserKey_IsValid_FirstKeyInBox()
    {
        uint boxId = 42;
        var key = BoxKeys.BuildKey(boxId, ReadOnlySpan<byte>.Empty);

        Assert.Equal(4, key.Length);
        Assert.Equal(boxId, BoxKeys.ExtractBoxId(key));
        Assert.True(BoxKeys.ExtractUserKey(key).IsEmpty);

        BoxKeys.GetBoxBounds(boxId, out var lower, out var upper);
        Assert.Equal(0, CompareBytes(key, lower));
    }

    [Fact]
    public void BoxBounds_CoverExactlyTheBoxRange()
    {
        uint boxId = 100;
        BoxKeys.GetBoxBounds(boxId, out var lower, out var upper);

        Assert.Equal(4, lower.Length);
        Assert.Equal(4, upper.Length);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(lower));
        Assert.Equal(101u, BinaryPrimitives.ReadUInt32BigEndian(upper));

        // Any key inside box 100 is >= lower and < upper
        byte[][] sampleKeys =
        [
            BoxKeys.BuildKey(boxId, []),
            BoxKeys.BuildKey(boxId, [0x00]),
            BoxKeys.BuildKey(boxId, [0x00, 0x01]),
            BoxKeys.BuildKey(boxId, [0x7F]),
            BoxKeys.BuildKey(boxId, [0xFF, 0xFF, 0xFF])
        ];

        foreach (var k in sampleKeys)
        {
            Assert.True(CompareBytes(k, lower) >= 0, $"Key should be >= lower bound: {BitConverter.ToString(k)}");
            Assert.True(CompareBytes(k, upper) < 0, $"Key should be < upper bound: {BitConverter.ToString(k)}");
        }

        // Keys from neighboring boxes are outside bounds
        var prevBoxKey = BoxKeys.BuildKey(boxId - 1, [0xFF, 0xFF]);
        var nextBoxKey = BoxKeys.BuildKey(boxId + 1, []);

        Assert.True(CompareBytes(prevBoxKey, lower) < 0);
        Assert.True(CompareBytes(nextBoxKey, upper) >= 0);
    }

    [Fact]
    public void MaxBoxId_BoundsCoverEdgeCase()
    {
        uint boxId = uint.MaxValue - 1;
        BoxKeys.GetBoxBounds(boxId, out var lower, out var upper);

        Assert.Equal(4, lower.Length);
        Assert.Equal(4, upper.Length);

        var keyInMaxBox = BoxKeys.BuildKey(boxId, [0xFF, 0xFF]);
        Assert.True(CompareBytes(keyInMaxBox, lower) >= 0);
        Assert.True(CompareBytes(keyInMaxBox, upper) < 0);

        // The highest issued id is uint.MaxValue - 1, so its exclusive upper bound [FF FF FF FF] covers every key it
        // can hold. uint.MaxValue is never issued: no finite upper bound covers every key under [FF FF FF FF]
        // (independent review of 002, finding 6).
        Assert.Equal(DatabaseContext.MaxBoxId, boxId);
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], upper);
        Assert.Throws<ArgumentOutOfRangeException>(() => BoxKeys.GetBoxBounds(uint.MaxValue, out _, out _));
    }
}
