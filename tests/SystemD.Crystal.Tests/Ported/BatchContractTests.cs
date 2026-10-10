// Ported from Highway: tests/Highway.Server.Tests/Storage/StoreContractTests.cs (spec 004 design § 2.2, task T10).
// Only the batch contract is ported: last write wins, delete is idempotent, a batch sees its own writes, nothing is
// visible before commit, a fault before commit leaves nothing, and a range delete never reaches a sibling name. The
// key-value, counter, list, set and expiry families are Highway's own.

using System;
using System.Linq;
using SystemD.Crystal.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests.Ported;

public sealed class BatchContractTests : IDisposable
{
    private readonly TempStoreDirectory _dir = new();
    private readonly CrystalStore _store;
    private readonly CrystalDatabase _db;
    private readonly Box _box;

    public BatchContractTests()
    {
        _store = CrystalStore.Open(_dir.Path);
        _db = _store.CreateDatabase("contract");
        _box = _db.Boxes.Create("kv");
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static string? S(byte[]? b) => b is null ? null : System.Text.Encoding.UTF8.GetString(b);

    private void InBatch(Action<Batch> work)
    {
        using var batch = _db.NewBatch();
        work(batch);
        batch.Commit();
    }

    private string? Read(Box box, string key)
    {
        using var snapshot = _db.Snapshot();
        return S(snapshot.Get(box, B(key)));
    }

    // Ported from Highway: StoreContractTests.Kv_SetThenGet
    [Fact]
    public void Put_ThenGet()
    {
        InBatch(b => b.Put(_box, B("rep:req-1"), B("payload")));

        Assert.Equal("payload", Read(_box, "rep:req-1"));
    }

    // Ported from Highway: StoreContractTests.Kv_GetAbsent_ReturnsNull
    [Fact]
    public void Get_Absent_ReturnsNull()
    {
        Assert.Null(Read(_box, "missing"));
    }

    // Ported from Highway: StoreContractTests.Kv_Delete_IsIdempotent
    [Fact]
    public void Delete_IsIdempotent()
    {
        InBatch(b => b.Delete(_box, B("k")));
        InBatch(b => b.Put(_box, B("k"), B("v")));
        InBatch(b => b.Delete(_box, B("k")));
        InBatch(b => b.Delete(_box, B("k")));

        Assert.Null(Read(_box, "k"));
    }

    // Ported from Highway: StoreContractTests.Kv_LastWriteWins, within one batch as well as across two.
    [Fact]
    public void LastWriteWins()
    {
        InBatch(b => b.Put(_box, B("k"), B("first")));
        InBatch(b => b.Put(_box, B("k"), B("second")));
        Assert.Equal("second", Read(_box, "k"));

        InBatch(b =>
        {
            b.Put(_box, B("k"), B("third"));
            b.Delete(_box, B("k"));
            b.Put(_box, B("k"), B("fourth"));
        });
        Assert.Equal("fourth", Read(_box, "k"));
    }

    // Ported from Highway: StoreContractTests.Increment_IsVisibleWithinSameBatch (adapted: Crystal has no counters, so
    // a read-modify-write of a value is the same contract).
    [Fact]
    public void ABatch_SeesItsOwnWrites_BeforeCommit()
    {
        InBatch(b => b.Put(_box, B("counter"), [1]));

        using var batch = _db.NewBatch();
        var value = batch.Get(_box, B("counter"))![0];
        batch.Put(_box, B("counter"), [(byte)(value + 1)]);
        Assert.Equal(2, batch.Get(_box, B("counter"))![0]);

        batch.Put(_box, B("new"), B("x"));
        Assert.Equal(["counter", "new"], batch.Scan(_box, [], [], 10).Select(kv => S(kv.Key)));

        batch.Delete(_box, B("counter"));
        Assert.Null(batch.Get(_box, B("counter")));
    }

    // spec 004 design § 2.2: nothing is visible before commit.
    [Fact]
    public void Nothing_IsVisibleBeforeCommit()
    {
        using var batch = _db.NewBatch();
        batch.Put(_box, B("k"), B("staged"));

        Assert.Null(Read(_box, "k"));
        using (var snapshot = _db.Snapshot())
        {
            Assert.Empty(snapshot.Scan(_box, [], [], 10));
        }

        batch.Commit();
        Assert.Equal("staged", Read(_box, "k"));
    }

    // Ported from Highway: StoreContractTests.UncommittedBatch_ChangesNothing
    [Fact]
    public void UncommittedBatch_ChangesNothing()
    {
        using (var batch = _db.NewBatch())
        {
            batch.Put(_box, B("k"), B("staged"));
        }

        Assert.Null(Read(_box, "k"));
    }

    // Ported from Highway: StoreContractTests.FaultBetweenTwoWrites_LeavesNoPartialState. Highway's queue entry and
    // its byte counter become two boxes: a product's data and its metadata, written in one batch (intro, principle 2).
    [Fact]
    public void FaultBetweenTwoWrites_LeavesNoPartialState()
    {
        var counters = _db.Boxes.Create("counters");

        void FailingCommand()
        {
            using var batch = _db.NewBatch();
            batch.Put(_box, B("entry"), B("msg"));
            batch.Put(counters, B("bytes"), [42]);
            throw new InvalidOperationException("injected fault before commit");
        }

        var thrown = Assert.Throws<InvalidOperationException>(FailingCommand);

        Assert.Equal("injected fault before commit", thrown.Message);
        Assert.Null(Read(_box, "entry"));
        Assert.Null(Read(counters, "bytes"));
    }

    // Ported from Highway: StoreContractTests.SecondBatch_AfterAFaultedFirst_CommitsCleanly
    [Fact]
    public void SecondBatch_AfterAFaultedFirst_CommitsCleanly()
    {
        try
        {
            using var bad = _db.NewBatch();
            bad.Put(_box, B("k"), B("doomed"));
            throw new InvalidOperationException("fault");
        }
        catch (InvalidOperationException)
        {
            // discarded
        }

        InBatch(b => b.Put(_box, B("k"), B("good")));
        Assert.Equal("good", Read(_box, "k"));
    }

    private static byte[] Entry(string name, ulong seq) =>
        KeyBuilder.Build((ref KeyWriter w) =>
        {
            w.WriteString(name);
            w.WriteUInt64(seq);
        });

    private static (byte[] From, byte[] To) PrefixRange(string name)
    {
        var from = KeyEncoding.EncodeString(name);
        var to = (byte[])from.Clone();
        to[^1]++;   // the terminator 0x00 0x01 becomes 0x00 0x02: the first key past every key under the name
        return (from, to);
    }

    // Ported from Highway: StoreContractTests.DeleteRange_RemovesWholePrefix
    [Fact]
    public void DeleteRange_RemovesWholePrefix()
    {
        InBatch(b =>
        {
            b.Put(_box, Entry("invoices", 0), B("a"));
            b.Put(_box, Entry("invoices", 1), B("b"));
        });

        var (from, to) = PrefixRange("invoices");
        InBatch(b => b.DeleteRange(_box, from, to));

        using var snapshot = _db.Snapshot();
        Assert.Empty(snapshot.Scan(_box, [], [], 10));
    }

    // Ported from Highway: StoreContractTests.DeleteRange_DoesNotTouchSiblingName
    [Fact]
    public void DeleteRange_DoesNotTouchSiblingName()
    {
        InBatch(b =>
        {
            b.Put(_box, Entry("invoices", 0), B("victim"));
            b.Put(_box, Entry("invoices2", 0), B("survivor"));
            b.Put(_box, Entry("invoice", 0), B("survivor"));
        });

        var (from, to) = PrefixRange("invoices");
        InBatch(b => b.DeleteRange(_box, from, to));

        using var snapshot = _db.Snapshot();
        Assert.Equal(["survivor", "survivor"], snapshot.Scan(_box, [], [], 10).Select(kv => S(kv.Value)));
        Assert.Null(snapshot.Get(_box, Entry("invoices", 0)));
    }
}
