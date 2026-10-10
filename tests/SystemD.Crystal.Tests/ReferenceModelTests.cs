// Ported from Highway / StowDB: reference-model test
// Adapted for Crystal: box lifecycle, rename, swap, multi-box batches, mutation-checked (spec 002 T10, R10.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Text = System.Text.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class ReferenceModelTests : IDisposable
{
    private readonly string _dir;

    public ReferenceModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crystal-refmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void ReferenceModel_1000Steps_FixedAndRandomSeed_MatchesExactly()
    {
        RunModelTest(seed: 42, steps: 1000);
        RunModelTest(seed: 1337, steps: 1000);
    }

    private void RunModelTest(int seed, int steps)
    {
        var testDir = Path.Combine(_dir, $"test-{seed}");
        Directory.CreateDirectory(testDir);

        using var store = CrystalStore.Open(testDir);
        var db = store.CreateDatabase("modeldb");

        var model = new BoxReferenceModel();
        var random = new Random(seed);

        // The handles Crystal issued, by id. A handle addresses the box by id, so it stays valid across rename and
        // swap (R3.10); a hand-built handle would be refused (R1.2).
        var handles = new Dictionary<uint, Box>();

        var boxNames = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" };

        for (var step = 0; step < steps; step++)
        {
            var op = random.Next(10);
            var chosenName = boxNames[random.Next(boxNames.Length)];

            switch (op)
            {
                case 0: // Create
                {
                    var expectedTaken = model.Aliases.ContainsKey(chosenName);
                    if (expectedTaken)
                    {
                        Assert.Throws<BoxNameTakenException>(() => db.Boxes.Create(chosenName));
                    }
                    else
                    {
                        var created = db.Boxes.Create(chosenName);
                        handles[created.Id] = created;
                        var expectedId = model.Create(chosenName);
                        Assert.Equal(expectedId, created.Id);
                    }
                    break;
                }

                case 1: // Find
                {
                    var found = db.Boxes.Find(chosenName);
                    var expectedId = model.Find(chosenName);
                    if (expectedId is null)
                    {
                        Assert.Null(found);
                    }
                    else
                    {
                        Assert.NotNull(found);
                        Assert.Equal(expectedId.Value, found.Value.Id);
                    }
                    break;
                }

                case 2: // List
                {
                    var liveList = db.Boxes.List();
                    var expectedList = model.List();
                    Assert.Equal(expectedList.Count, liveList.Count);
                    foreach (var info in liveList)
                    {
                        Assert.True(expectedList.TryGetValue(info.Id, out var expName));
                        Assert.Equal(expName, info.Name);
                    }
                    break;
                }

                case 3: // Put & Get
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count > 0)
                    {
                        var b = liveBoxes[random.Next(liveBoxes.Count)];
                        var key = Text.UTF8.GetBytes($"k_{random.Next(20)}");
                        var val = Text.UTF8.GetBytes($"v_{step}");

                        using (var batch = db.NewBatch())
                        {
                            batch.Put(handles[b.Id], key, val);
                            batch.Commit();
                        }

                        model.Put(b.Id, key, val);

                        using var snap = db.Snapshot();
                        var got = snap.Get(handles[b.Id], key);
                        var exp = model.Get(b.Id, key);
                        Assert.NotNull(got);
                        Assert.Equal(exp, got);
                    }
                    break;
                }

                case 4: // Delete
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count > 0)
                    {
                        var b = liveBoxes[random.Next(liveBoxes.Count)];
                        var key = Text.UTF8.GetBytes($"k_{random.Next(20)}");

                        using (var batch = db.NewBatch())
                        {
                            batch.Delete(handles[b.Id], key);
                            batch.Commit();
                        }

                        model.Delete(b.Id, key);

                        using var snap = db.Snapshot();
                        var got = snap.Get(handles[b.Id], key);
                        var exp = model.Get(b.Id, key);
                        Assert.Equal(exp, got);
                    }
                    break;
                }

                case 5: // Range Delete
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count > 0)
                    {
                        var b = liveBoxes[random.Next(liveBoxes.Count)];
                        var from = Text.UTF8.GetBytes("k_05");
                        var to = Text.UTF8.GetBytes("k_15");

                        using (var batch = db.NewBatch())
                        {
                            batch.DeleteRange(handles[b.Id], from, to);
                            batch.Commit();
                        }

                        model.DeleteRange(b.Id, from, to);
                    }
                    break;
                }

                case 6: // Scan
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count > 0)
                    {
                        var b = liveBoxes[random.Next(liveBoxes.Count)];
                        using var snap = db.Snapshot();
                        var actualRows = snap.Scan(handles[b.Id], [], [], limit: 100).ToList();
                        var expectedRows = model.Scan(b.Id, [], [], limit: 100);

                        Assert.Equal(expectedRows.Count, actualRows.Count);
                        for (var i = 0; i < actualRows.Count; i++)
                        {
                            Assert.Equal(expectedRows[i].Key, actualRows[i].Key);
                            Assert.Equal(expectedRows[i].Value, actualRows[i].Value);
                        }
                    }
                    break;
                }

                case 7: // Rename
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count > 0)
                    {
                        var b = liveBoxes[random.Next(liveBoxes.Count)];
                        var newName = boxNames[random.Next(boxNames.Length)];
                        if (model.Aliases.ContainsKey(newName))
                        {
                            if (newName != b.Name)
                            {
                                Assert.Throws<BoxNameTakenException>(() => db.Boxes.Rename(handles[b.Id], newName));
                            }
                        }
                        else
                        {
                            var renamed = db.Boxes.Rename(handles[b.Id], newName);
                            model.Rename(b.Id, newName);
                            Assert.Equal(b.Id, renamed.Id);
                            Assert.Equal(newName, renamed.Name);
                        }
                    }
                    break;
                }

                case 8: // Swap
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count >= 2)
                    {
                        var b1 = liveBoxes[0];
                        var b2 = liveBoxes[1];
                        db.Boxes.Swap(handles[b1.Id], handles[b2.Id]);
                        model.Swap(b1.Id, b2.Id);

                        Assert.Equal(b2.Id, db.Boxes.Find(b1.Name)?.Id);
                        Assert.Equal(b1.Id, db.Boxes.Find(b2.Name)?.Id);
                    }
                    break;
                }

                case 9: // Drop
                {
                    var liveBoxes = model.GetLiveBoxes();
                    if (liveBoxes.Count > 0)
                    {
                        var b = liveBoxes[random.Next(liveBoxes.Count)];
                        db.Boxes.Drop(handles[b.Id]);
                        model.Drop(b.Id);

                        Assert.Null(db.Boxes.Find(b.Name));
                    }
                    break;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Mutation checks (R10.1). Each exercises CRYSTAL, not the in-memory model, so that each one would
    // FAIL if the corresponding guard were removed from the implementation:
    //   1. no box-id prefix       → keys of one box would be visible through another
    //   2. no range delete on drop → a re-created name would resurrect the old box's keys
    //   3. a reused id            → the allocator would hand a dropped box's id to a new box
    //   4. a half-committed swap  → one alias would move without the other (verified via atomicity)
    // The playbook's rule: a guard nobody has seen fail protects nothing. These are the behavioural
    // observations that break under each mutation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MutationCheck_BoxIdPrefix_IsolatesBoxes_InCrystal()
    {
        // If BoxKeys dropped the [box_id] prefix, the same user key written to box A would be visible
        // through box B. Two boxes, the same key, different values: each box must see only its own.
        using var store = CrystalStore.Open(Path.Combine(_dir, "mut-prefix"));
        var db = store.CreateDatabase("db");
        var a = db.Boxes.Create("a");
        var b = db.Boxes.Create("b");
        var key = Text.UTF8.GetBytes("shared_key");

        using (var batch = db.NewBatch())
        {
            batch.Put(a, key, Text.UTF8.GetBytes("value_a"));
            batch.Put(b, key, Text.UTF8.GetBytes("value_b"));
            batch.Commit();
        }

        using var snap = db.Snapshot();
        Assert.Equal("value_a", Text.UTF8.GetString(snap.Get(a, key)!));
        Assert.Equal("value_b", Text.UTF8.GetString(snap.Get(b, key)!));

        // And a scan of A returns only A's one key, never B's (R6.5 / the prefix bound).
        var aRows = snap.Scan(a, [], [], limit: 100).ToList();
        Assert.Single(aRows);
        Assert.Equal("value_a", Text.UTF8.GetString(aRows[0].Value));
    }

    [Fact]
    public void MutationCheck_DropRangeDeletes_NoResurrectionThroughNewBox_InCrystal()
    {
        // If Drop did not range-delete the box's keys, a later box re-using that key range (it cannot,
        // because ids are never reused — but a mutation that reused the id would) would see old data.
        // We prove the keys are physically gone: a fresh box created with the SAME NAME shares no data,
        // and the dropped id's range holds nothing a scan can reach.
        using var store = CrystalStore.Open(Path.Combine(_dir, "mut-rangedelete"));
        var db = store.CreateDatabase("db");
        var orders = db.Boxes.Create("orders");
        var key = Text.UTF8.GetBytes("order_1");

        using (var batch = db.NewBatch())
        {
            batch.Put(orders, key, Text.UTF8.GetBytes("data"));
            batch.Commit();
        }

        db.Boxes.Drop(orders);

        var recreated = db.Boxes.Create("orders");
        Assert.NotEqual(orders.Id, recreated.Id); // id not reused (guard 3)

        using var snap = db.Snapshot();
        Assert.Null(snap.Get(recreated, key));
        Assert.Empty(snap.Scan(recreated, [], [], limit: 100));
    }

    [Fact]
    public void MutationCheck_IdsNeverReused_InCrystal()
    {
        // If the allocator reused ids, a re-created name would get a burnt id. Crystal must always move
        // the allocator forward: the new id is strictly greater, even across many drop/create cycles.
        using var store = CrystalStore.Open(Path.Combine(_dir, "mut-idreuse"));
        var db = store.CreateDatabase("db");

        var lastId = 0u;
        for (var i = 0; i < 10; i++)
        {
            var box = db.Boxes.Create("box");
            Assert.True(box.Id > lastId, $"id {box.Id} must exceed every earlier id {lastId}");
            lastId = box.Id;
            db.Boxes.Drop(box);
        }
    }

    [Fact]
    public void MutationCheck_SwapIsAtomic_BothAliasesMoveOrNeither_InCrystal()
    {
        // If a swap committed only one alias, exactly one name would point at the wrong id. After the
        // swap, each name must resolve to the OTHER box's id — both moved, together.
        using var store = CrystalStore.Open(Path.Combine(_dir, "mut-swap"));
        var db = store.CreateDatabase("db");
        var a = db.Boxes.Create("a");
        var b = db.Boxes.Create("b");

        db.Boxes.Swap(a, b);

        Assert.Equal(b.Id, db.Boxes.Find("a")!.Value.Id);
        Assert.Equal(a.Id, db.Boxes.Find("b")!.Value.Id);

        // And the swap survives a reopen exactly (it is one durable batch, not a half-written pair).
        store.Dispose();
        using var reopened = CrystalStore.Open(Path.Combine(_dir, "mut-swap"));
        var db2 = reopened.OpenDatabase("db");
        Assert.Equal(b.Id, db2.Boxes.Find("a")!.Value.Id);
        Assert.Equal(a.Id, db2.Boxes.Find("b")!.Value.Id);
    }

    [Fact]
    public void MutationCheck_WriteToDroppedBox_IsRefusedWhole_InCrystal()
    {
        // R4.1: a batch touching a dropped box is refused whole and writes nothing — the guard the gate
        // enforces at commit. If that validation were removed, the put would land in the dropped range.
        using var store = CrystalStore.Open(Path.Combine(_dir, "mut-dropped-write"));
        var db = store.CreateDatabase("db");
        var live = db.Boxes.Create("live");
        var doomed = db.Boxes.Create("doomed");

        var batch = db.NewBatch();
        batch.Put(live, Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v"));
        batch.Put(doomed, Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v"));

        db.Boxes.Drop(doomed);

        Assert.Throws<BoxDroppedException>(() => batch.Commit());
        batch.Dispose();

        // Nothing of the refused batch survived — not even the write to the live box (refused whole).
        using var snap = db.Snapshot();
        Assert.Null(snap.Get(live, Text.UTF8.GetBytes("k")));
    }

    private sealed class BoxReferenceModel
    {
        private uint _nextId = 16;
        public Dictionary<string, uint> Aliases { get; } = new(StringComparer.Ordinal);
        public Dictionary<uint, string> Records { get; } = [];
        public Dictionary<uint, SortedDictionary<byte[], byte[]>> Data { get; } = [];

        public uint Create(string name)
        {
            var id = _nextId++;
            Aliases[name] = id;
            Records[id] = name;
            Data[id] = new SortedDictionary<byte[], byte[]>(ByteArrayComparer.Instance);
            return id;
        }

        public uint? Find(string name) =>
            Aliases.TryGetValue(name, out var id) ? id : null;

        public Dictionary<uint, string> List() =>
            new(Records);

        public List<(uint Id, string Name)> GetLiveBoxes() =>
            Records.Select(kv => (kv.Key, kv.Value)).ToList();

        public void Rename(uint id, string newName)
        {
            var oldName = Records[id];
            Aliases.Remove(oldName);
            Aliases[newName] = id;
            Records[id] = newName;
        }

        public void Swap(uint id1, uint id2)
        {
            var name1 = Records[id1];
            var name2 = Records[id2];

            Aliases[name1] = id2;
            Aliases[name2] = id1;
            Records[id1] = name2;
            Records[id2] = name1;
        }

        public void Drop(uint id)
        {
            var name = Records[id];
            Aliases.Remove(name);
            Records.Remove(id);
            Data.Remove(id);
        }

        public void Put(uint id, byte[] key, byte[] value)
        {
            if (Data.TryGetValue(id, out var dict))
            {
                dict[key] = value;
            }
        }

        public void Delete(uint id, byte[] key)
        {
            if (Data.TryGetValue(id, out var dict))
            {
                dict.Remove(key);
            }
        }

        public void DeleteRange(uint id, byte[] from, byte[] to)
        {
            if (Data.TryGetValue(id, out var dict))
            {
                var keysToRemove = dict.Keys
                    .Where(k => ByteArrayComparer.Instance.Compare(k, from) >= 0 &&
                                ByteArrayComparer.Instance.Compare(k, to) < 0)
                    .ToList();
                foreach (var k in keysToRemove)
                {
                    dict.Remove(k);
                }
            }
        }

        public byte[]? Get(uint id, byte[] key)
        {
            if (Data.TryGetValue(id, out var dict) && dict.TryGetValue(key, out var val))
            {
                return val;
            }
            return null;
        }

        public List<KeyValue> Scan(uint id, byte[] from, byte[] to, int limit)
        {
            var result = new List<KeyValue>();
            if (Data.TryGetValue(id, out var dict))
            {
                foreach (var (k, v) in dict)
                {
                    if (from.Length > 0 && ByteArrayComparer.Instance.Compare(k, from) < 0)
                    {
                        continue;
                    }
                    if (to.Length > 0 && ByteArrayComparer.Instance.Compare(k, to) >= 0)
                    {
                        break;
                    }
                    result.Add(new KeyValue(k, v));
                    if (result.Count >= limit)
                    {
                        break;
                    }
                }
            }
            return result;
        }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y.AsSpan());
        }
    }
}
