// Spec 012 T1, T2 (snapshots), R1 — backward scans: the keys of the forward scan of a range, in descending order; a key
// at `to` never returned, one at `from` always; no key of another box, whatever the bounds; the limit; steps counted;
// in the memtable, after a flush, with range deletes, and with data split between the two.

using System;
using System.Collections.Generic;
using System.Linq;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Adoption;

public sealed class BackwardScanTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();
    private readonly CrystalStore _store;
    private readonly CrystalDatabase _db;

    public BackwardScanTests()
    {
        _store = CrystalStore.Open(_dir.Path);
        _db = _store.CreateDatabase("db");
    }

    public void Dispose()
    {
        Assert.Equal(0, _store.Engine.OpenIterators); // spec 012 R8.3: no scan left open
        _store.Dispose();
        _dir.Dispose();
    }

    private static byte[] K(string s) => Text.UTF8.GetBytes(s);

    private static string S(byte[] b) => Text.UTF8.GetString(b);

    private void Put(Box box, params string[] keys)
    {
        using var batch = _db.NewBatch();
        foreach (var key in keys)
        {
            batch.Put(box, K(key), K("v-" + key));
        }

        batch.Commit();
    }

    private List<string> Scan(Box box, string from, string to, int limit, ScanDirection direction)
    {
        using var snapshot = _db.Snapshot();
        return [.. snapshot.Scan(box, K(from), K(to), limit, direction).Select(kv => S(kv.Key))];
    }

    [Fact]
    public void ABackwardScan_ReturnsTheForwardKeysInDescendingOrder_ExcludingTo_IncludingFrom()
    {
        var box = _db.Boxes.Create("box");
        Put(box, "a", "b", "c", "d", "e");

        Assert.Equal(["d", "c", "b"], Scan(box, "b", "e", 100, ScanDirection.Backward)); // "e" is the end: excluded
        Assert.Equal(["e", "d", "c", "b", "a"], Scan(box, "", "", 100, ScanDirection.Backward));
        Assert.Equal(["e", "d"], Scan(box, "", "", 2, ScanDirection.Backward)); // the limit counts from the top
        Assert.Equal(["c", "b"], Scan(box, "b", "c\0", 100, ScanDirection.Backward));
        Assert.Empty(Scan(box, "c", "c", 100, ScanDirection.Backward));
        Assert.Empty(Scan(box, "x", "", 100, ScanDirection.Backward));
        Assert.Equal(["a"], Scan(box, "", "b", 100, ScanDirection.Backward));
        Assert.Equal(["e"], Scan(box, "d\0", "", 100, ScanDirection.Backward)); // `to` between keys
    }

    [Fact]
    public void ABackwardScan_NeverReturnsAKeyOfAnotherBox()
    {
        var before = _db.Boxes.Create("before");
        var middle = _db.Boxes.Create("middle");
        var after = _db.Boxes.Create("after");
        var empty = _db.Boxes.Create("empty");
        Put(before, "a", "z", "ÿ");
        Put(after, "", "a", "m");
        Put(middle, "k");

        Assert.Equal(["k"], Scan(middle, "", "", 100, ScanDirection.Backward));
        Assert.Equal(["k"], Scan(middle, "", "zzzz", 100, ScanDirection.Backward));
        Assert.Empty(Scan(middle, "", "k", 100, ScanDirection.Backward));
        Assert.Empty(Scan(empty, "", "", 100, ScanDirection.Backward));
    }

    [Theory]
    [InlineData("memtable")]
    [InlineData("flushed")]
    [InlineData("split")]
    [InlineData("range deletes")]
    public void ForRandomContentsAndRanges_BackwardIsForwardReversed(string layout)
    {
        var random = new Random(1201 + layout.Length);
        var box = _db.Boxes.Create("box");
        var neighbour = _db.Boxes.Create("neighbour");
        var keys = Enumerable.Range(0, 600).Select(_ => RandomKey(random)).Distinct().ToList();
        Put(neighbour, [.. keys.Take(50).Select(Text.UTF8.GetString)]);

        void Write(IEnumerable<byte[]> part)
        {
            using var batch = _db.NewBatch();
            foreach (var key in part)
            {
                batch.Put(box, key, key);
            }

            batch.Commit();
        }

        switch (layout)
        {
            case "memtable":
                Write(keys);
                break;
            case "flushed":
                Write(keys);
                _db.Slot.Instance.Flush();
                break;
            case "split":
                Write(keys.Take(300));
                _db.Slot.Instance.Flush();
                Write(keys.Skip(300));
                break;
            case "range deletes":
                Write(keys);
                _db.Slot.Instance.Flush();
                using (var batch = _db.NewBatch())
                {
                    for (var i = 0; i < 5; i++)
                    {
                        var (a, b) = Ordered(RandomKey(random), RandomKey(random));
                        batch.DeleteRange(box, a, b);
                    }

                    batch.Commit();
                }

                break;
        }

        using var snapshot = _db.Snapshot();
        for (var round = 0; round < 200; round++)
        {
            var (from, to) = Ordered(random.Next(5) == 0 ? [] : RandomKey(random), random.Next(5) == 0 ? [] : RandomKey(random));
            if (to.Length == 0 && from.Length > 0 && random.Next(2) == 0)
            {
                (from, to) = (to, from);
            }

            var limit = random.Next(3) == 0 ? random.Next(1, 20) : int.MaxValue;
            var forward = snapshot.Scan(box, from, to, int.MaxValue).Select(kv => Convert.ToHexString(kv.Key)).ToList();
            var backward = snapshot.Scan(box, from, to, limit, ScanDirection.Backward).Select(kv => Convert.ToHexString(kv.Key)).ToList();
            forward.Reverse();
            Assert.Equal(forward.Take(limit), backward);
        }
    }

    [Fact]
    public void BackwardSteps_AreCounted_AndBounded()
    {
        var metrics = new Support.MetricsCapture();
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { MeterFactory = metrics });
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("box");
        using (var batch = db.NewBatch())
        {
            for (var i = 0; i < 10; i++)
            {
                batch.Put(box, K($"k{i}"), K("v"));
            }

            batch.Commit();
        }

        using (var snapshot = db.Snapshot())
        {
            Assert.Equal(5, snapshot.Scan(box, K("k2"), K("k7"), 100, ScanDirection.Backward).Count());
        }

        var steps = Assert.Single(metrics.Of("crystal.scan.steps")).Value;
        Assert.InRange(steps, 5, 7); // the seek, the adjustment past `to`, one step per key
        metrics.Dispose();
    }

    [Fact]
    public void AnUndefinedDirection_IsRefused()
    {
        var box = _db.Boxes.Create("box");
        using var snapshot = _db.Snapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Scan(box, [], [], 1, (ScanDirection)7).ToList());
    }

    private static byte[] RandomKey(Random random)
    {
        var key = new byte[random.Next(1, 4)];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(random.Next(4) switch { 0 => 0x00, 1 => 0xFF, _ => random.Next(256) });
        }

        return key;
    }

    private static (byte[], byte[]) Ordered(byte[] a, byte[] b) =>
        a.Length == 0 || b.Length == 0 || a.AsSpan().SequenceCompareTo(b) <= 0 ? (a, b) : (b, a);
}
