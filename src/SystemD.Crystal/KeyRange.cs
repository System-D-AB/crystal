// Ported from StowDB: src/Crystal/Crystal.Storage/Encoding/ScanRange.cs (KeySuccessor.Prefix, ScanRange).
// Adapted for Crystal: an empty bound, not null, means the box's start or end, as Crystal's scans already read it
// (spec 012 R4).

using System;

namespace SystemD.Crystal
{
    /// <summary>
    /// A half-open key range <c>[From, To)</c> inside one box (spec 012 R4.1). An empty <see cref="From"/> is the box's
    /// start and an empty <see cref="To"/> its end. Build one with <see cref="Encoding.KeyRanges"/>.
    /// </summary>
    /// <param name="From">The first key in the range, or empty for the box's start.</param>
    /// <param name="To">The first key past the range, or empty for the box's end.</param>
    public readonly record struct KeyRange(byte[] From, byte[] To)
    {
        /// <summary>The whole box.</summary>
        public static KeyRange All => new([], []);

        /// <summary>A range that holds no key: <c>[0x00, 0x00)</c>. (An empty <c>From</c> and <c>To</c> is the whole box.)</summary>
        public static KeyRange Empty => new([0x00], [0x00]);

        /// <summary>Two ranges are equal when their bounds hold the same bytes.</summary>
        public bool Equals(KeyRange other) =>
            (From ?? []).AsSpan().SequenceEqual(other.From ?? []) && (To ?? []).AsSpan().SequenceEqual(other.To ?? []);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(From ?? []);
            hash.Add(-1);
            hash.AddBytes(To ?? []);
            return hash.ToHashCode();
        }
    }

    /// <summary>How one side of a <see cref="Encoding.KeyRanges.Between"/> range is bounded (spec 012 R4.2).</summary>
    public enum KeyBoundKind
    {
        /// <summary>No bound on this side: the box's start or end.</summary>
        Unbounded = 0,

        /// <summary>The key itself is in the range.</summary>
        Inclusive = 1,

        /// <summary>The key itself is not in the range.</summary>
        Exclusive = 2,
    }

    /// <summary>One side of a <see cref="Encoding.KeyRanges.Between"/> range (spec 012 R4.2).</summary>
    /// <param name="Kind">Unbounded, inclusive or exclusive.</param>
    /// <param name="Key">The bounding key; ignored when unbounded.</param>
    public readonly record struct KeyBound(KeyBoundKind Kind, byte[]? Key)
    {
        /// <summary>No bound.</summary>
        public static KeyBound Unbounded => new(KeyBoundKind.Unbounded, null);

        /// <summary>A bound that includes <paramref name="key"/>.</summary>
        public static KeyBound Inclusive(byte[] key) => new(KeyBoundKind.Inclusive, key);

        /// <summary>A bound that excludes <paramref name="key"/>.</summary>
        public static KeyBound Exclusive(byte[] key) => new(KeyBoundKind.Exclusive, key);
    }
}

namespace SystemD.Crystal.Encoding
{
    /// <summary>
    /// Builders of <see cref="KeyRange"/> values (spec 012 R4.2), so a product never derives a prefix's end or a
    /// successor key by hand. For every builder, a key is in the range it builds if and only if the predicate it names
    /// holds (R4.3).
    /// </summary>
    public static class KeyRanges
    {
        /// <summary>Exactly <paramref name="key"/>: <c>[key, key ‖ 0x00)</c>.</summary>
        public static KeyRange Point(ReadOnlySpan<byte> key) => new(key.ToArray(), Successor(key));

        /// <summary>Every key that starts with <paramref name="prefix"/>; an empty prefix is the whole box.</summary>
        public static KeyRange Prefix(ReadOnlySpan<byte> prefix) => new(prefix.ToArray(), PrefixEnd(prefix));

        /// <summary>
        /// The keys between <paramref name="lower"/> and <paramref name="upper"/>, each side inclusive, exclusive or
        /// unbounded. A range whose lower side sorts after its upper side is empty, not refused: it returns no key.
        /// </summary>
        public static KeyRange Between(KeyBound lower, KeyBound upper)
        {
            var from = lower.Kind switch
            {
                KeyBoundKind.Unbounded => [],
                KeyBoundKind.Inclusive => Required(lower.Key, nameof(lower)),
                KeyBoundKind.Exclusive => Successor(Required(lower.Key, nameof(lower))),
                _ => throw new ArgumentOutOfRangeException(nameof(lower), lower.Kind, "Not a key bound kind."),
            };
            var to = upper.Kind switch
            {
                KeyBoundKind.Unbounded => [],
                KeyBoundKind.Inclusive => Successor(Required(upper.Key, nameof(upper))),
                KeyBoundKind.Exclusive => Required(upper.Key, nameof(upper)),
                _ => throw new ArgumentOutOfRangeException(nameof(upper), upper.Kind, "Not a key bound kind."),
            };

            // Nothing sorts below the empty key, so an exclusive upper bound on it holds no key; nor does a range whose
            // start is at or after its end. Both are KeyRange.Empty, never an empty To, which would mean the box's end.
            var nothingBelow = upper.Kind == KeyBoundKind.Exclusive && to.Length == 0;
            return nothingBelow || (to.Length > 0 && from.AsSpan().SequenceCompareTo(to) >= 0) ? KeyRange.Empty : new KeyRange(from, to);
        }

        /// <summary>
        /// The least key above every key that starts with <paramref name="prefix"/>: the last byte that is not
        /// <c>0xFF</c> incremented, and the <c>0xFF</c> tail dropped. Empty (the box's end) when the prefix is empty or
        /// all <c>0xFF</c>: inside a box, "no such key" is the box's end.
        /// </summary>
        public static byte[] PrefixEnd(ReadOnlySpan<byte> prefix)
        {
            for (var i = prefix.Length - 1; i >= 0; i--)
            {
                if (prefix[i] != 0xFF)
                {
                    var end = prefix[..(i + 1)].ToArray();
                    end[i]++;
                    return end;
                }
            }

            return [];
        }

        /// <summary>The least key strictly above <paramref name="key"/>: <c>key ‖ 0x00</c>.</summary>
        private static byte[] Successor(ReadOnlySpan<byte> key)
        {
            var next = new byte[key.Length + 1];
            key.CopyTo(next);
            return next;
        }

        private static byte[] Required(byte[]? key, string side) =>
            key ?? throw new ArgumentException("An inclusive or exclusive bound needs a key.", side);
    }
}
