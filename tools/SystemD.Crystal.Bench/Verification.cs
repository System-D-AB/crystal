using System;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using SystemD.Crystal.Bench.Workloads;

namespace SystemD.Crystal.Bench;

/// <summary>
/// A model of what a workload meant to write, and the read-back that compares it with the store (spec 008 R2,
/// design § 4). Values are generated deterministically from <c>(writer, sequence)</c>, so the model stores only the
/// key and recomputes the value at verification time — small memory even at full size.
/// </summary>
internal sealed class WrittenModel
{
    // Per box: key -> the deterministic seed used to make its value (or a sentinel for an explicit value).
    private readonly Dictionary<uint, Dictionary<byte[], byte[]>> _boxes = [];
    private readonly Dictionary<uint, Box> _handles = [];
    private readonly object _sync = new();

    /// <summary>Records that <paramref name="key"/> in <paramref name="box"/> holds <paramref name="value"/>.</summary>
    public void Put(Box box, byte[] key, byte[] value)
    {
        lock (_sync)
        {
            _handles[box.Id] = box;
            Shard(box.Id)[key] = value;
        }
    }

    /// <summary>Records that <paramref name="key"/> in <paramref name="box"/> was deleted.</summary>
    public void Delete(Box box, byte[] key)
    {
        lock (_sync)
        {
            Shard(box.Id).Remove(key);
        }
    }

    /// <summary>The number of keys the model expects, across every box.</summary>
    public long KeyCount
    {
        get
        {
            lock (_sync)
            {
                return _boxes.Values.Sum(b => (long)b.Count);
            }
        }
    }

    /// <summary>
    /// Verifies the store against the model through a snapshot (spec 008 R2.1): every key present with the right
    /// value, the right count, and nothing extra. Reports the first few differences and the total (R2.2).
    /// </summary>
    public VerificationResult Verify(CrystalDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);

        var firstDifferences = new List<string>();
        long differenceCount = 0;
        long keysChecked = 0;
        int boxCount;

        lock (_sync)
        {
            boxCount = _boxes.Count;
            using var snapshot = db.Snapshot();

            foreach (var (boxId, expected) in _boxes)
            {
                var handle = _handles[boxId];

                // 1. Every expected key present with the right value.
                var seen = new HashSet<byte[]>(ByteArrayComparer.Instance);
                foreach (var (key, expectedValue) in expected)
                {
                    var actual = snapshot.Get(handle, key);
                    keysChecked++;
                    if (actual is null)
                    {
                        differenceCount++;
                        AddDifference(firstDifferences, $"box {boxId}: key {Hex(key)} missing");
                    }
                    else if (!actual.AsSpan().SequenceEqual(expectedValue))
                    {
                        differenceCount++;
                        AddDifference(firstDifferences, $"box {boxId}: key {Hex(key)} value differs");
                    }

                    seen.Add(key);
                }

                // 2. Nothing extra: scan the whole box and confirm every key is expected.
                foreach (var kv in ScanWhole(snapshot, handle))
                {
                    if (!expected.ContainsKey(kv.Key))
                    {
                        differenceCount++;
                        AddDifference(firstDifferences, $"box {boxId}: unexpected key {Hex(kv.Key)}");
                    }
                }
            }
        }

        var passed = differenceCount == 0;
        return new VerificationResult(passed, keysChecked, boxCount, firstDifferences, differenceCount);
    }

    private static IEnumerable<KeyValue> ScanWhole(Snapshot snapshot, Box box)
    {
        // Page through the box so the model never materialises an unbounded scan.
        byte[] from = [];
        const int page = 10_000;
        while (true)
        {
            var batch = snapshot.Scan(box, from, [], page).ToList();
            if (batch.Count == 0)
            {
                yield break;
            }

            foreach (var kv in batch)
            {
                yield return kv;
            }

            if (batch.Count < page)
            {
                yield break;
            }

            // Continue just past the last key.
            var last = batch[^1].Key;
            from = new byte[last.Length + 1];
            last.CopyTo(from, 0);
        }
    }

    private Dictionary<byte[], byte[]> Shard(uint boxId)
    {
        if (!_boxes.TryGetValue(boxId, out var shard))
        {
            shard = new Dictionary<byte[], byte[]>(ByteArrayComparer.Instance);
            _boxes[boxId] = shard;
        }

        return shard;
    }

    private static void AddDifference(List<string> list, string difference)
    {
        if (list.Count < 10)
        {
            list.Add(difference);
        }
    }

    private static string Hex(byte[] key) => Convert.ToHexString(key.AsSpan(0, Math.Min(key.Length, 16)));

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}

/// <summary>The result of a read-back verification (spec 008 R2.2, R2.3).</summary>
internal sealed record VerificationResult(
    bool Passed,
    long KeysChecked,
    int Boxes,
    IReadOnlyList<string> FirstDifferences,
    long DifferenceCount)
{
    /// <summary>The verification line for the results section (R2.3): what was verified, or what was wrong.</summary>
    public string Summary =>
        Passed
            ? $"verified {KeysChecked:N0} keys in {Boxes} box(es)"
            : $"FAILED: {DifferenceCount:N0} difference(s) across {Boxes} box(es): {string.Join("; ", FirstDifferences)}";

    public VerificationOutcome ToOutcome() => new(Passed, Summary);
}
