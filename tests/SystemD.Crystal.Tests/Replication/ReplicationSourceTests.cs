// Spec 003 T7 / R2, R3; design § 4 — the primary's DATABASES and PULL over the IReplicationSource surface, driven
// against a real store (no socket; the connection's RESP framing is covered by T4). The replies are parsed back with
// Crystal's own reply reader. Covers the four T7 "Done when" criteria: a caught-up pull sends no batches; an idle
// long-poll returns after the wait with nothing; a commit wakes a waiting pull at once; and a gap / a from-above-
// latest is reported per database without failing the others.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class ReplicationSourceTests
{
    private static ReplicationOptions Repl() => new()
    {
        Secret = "test-secret",
        WalTtl = TimeSpan.FromHours(24),
        WalBytesPerDatabase = 1L << 30,
        LongPollMax = TimeSpan.FromSeconds(5),
        PageBytes = 1 << 20,
    };

    private sealed class Harness : IDisposable
    {
        public TempStoreDirectory Dir { get; } = new();
        public CrystalStore Store { get; }
        public ReplicationSource Source { get; }

        public Harness(SystemD.Crystal.Storage.ReplicationRole role = SystemD.Crystal.Storage.ReplicationRole.Primary)
        {
            Store = CrystalStore.Open(Dir.Path, new CrystalOptions { Replication = Repl() });
            var epoch = ReplicationEpoch.Load(Dir.Path, role);
            Source = new ReplicationSource(Store, Store.Engine, Store.NodeId, epoch, Repl());
        }

        public void Dispose()
        {
            Source.Dispose();
            Store.Dispose();
            Dir.Dispose();
        }
    }

    // Parses one complete RESP reply the source wrote into a RespWriter.
    private static RespValue Parse(RespWriter reply)
    {
        var bytes = reply.ToArray();
        var status = RespReader.TryReadReply(new ReadOnlySequence<byte>(bytes), 16 << 20, out var value, out _, out var error);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Null(error);
        Assert.NotNull(value);
        return value!;
    }

    private static RespValue Databases(ReplicationSource source)
    {
        var reply = new RespWriter();
        Assert.True(source.TryHandle(ReplicationProtocol.Databases, [Text.ASCII.GetBytes("DATABASES")], "standby-node", reply));
        return Parse(reply);
    }

    private static RespValue Pull(ReplicationSource source, int maxBytes, int waitMs, params (string Uid, long From)[] cursors)
    {
        var args = new List<byte[]>
        {
            Text.ASCII.GetBytes("PULL"),
            Text.ASCII.GetBytes(maxBytes.ToString()),
            Text.ASCII.GetBytes(waitMs.ToString()),
        };
        foreach (var (uid, from) in cursors)
        {
            args.Add(Text.UTF8.GetBytes(uid));
            args.Add(Text.ASCII.GetBytes(from.ToString()));
        }

        var reply = new RespWriter();
        Assert.True(source.TryHandle(ReplicationProtocol.Pull, args, "standby-node", reply));
        return Parse(reply);
    }

    private static (string Status, long Base, long Latest, IReadOnlyList<RespValue> Fields) Entry(RespValue pullReply, int index)
    {
        // [epoch, setVersion, [entry…]]; entry = [uid, status, incarnation, base, latest, batchesOrGap]
        var entries = pullReply.Items![2].Items!;
        var e = entries[index].Items!;
        return (Text.UTF8.GetString(e[1].Bytes!), e[3].Integer, e[4].Integer, e);
    }

    private static void Write(CrystalDatabase db, Box box, int n)
    {
        for (var i = 0; i < n; i++)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes($"k{i:D5}"), Text.UTF8.GetBytes($"v{i:D5}"));
            batch.Commit(DurabilityMode.Strict);
        }
    }

    [Fact]
    public void Databases_ListsEveryReplicatedUidWithNameAndLatest()
    {
        using var h = new Harness();
        var shop = h.Store.CreateDatabase("shop", replicated: true);
        var users = h.Store.CreateDatabase("users", replicated: true);
        h.Store.CreateDatabase("local", replicated: false); // excluded
        var box = shop.Boxes.Create("b");
        Write(shop, box, 3);

        var reply = Databases(h.Source);
        Assert.Equal(2, reply.Items!.Count);                 // [setVersion, [...]]
        var list = reply.Items[1].Items!;
        Assert.Equal(2, list.Count);                         // shop + users, not local

        var names = list.Select(e => Text.UTF8.GetString(e.Items![1].Bytes!)).ToHashSet();
        Assert.Contains("shop", names);
        Assert.Contains("users", names);
        Assert.DoesNotContain("local", names);
    }

    [Fact]
    public void Pull_CaughtUp_SendsNoBatches()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        Write(db, box, 5);
        var latest = (long)db.Slot.LatestSequence;

        var reply = Pull(h.Source, 1 << 20, waitMs: 0, (db.Uid, latest));
        var (status, _, entryLatest, rest) = Entry(reply, 0);
        Assert.Equal(ReplicationProtocol.EntryOk, status);
        Assert.Equal(latest, entryLatest);
        Assert.Empty(rest[5].Items!); // batches array is empty — caught up
    }

    [Fact]
    public void Pull_Behind_ServesTheBatchesAfterTheCursor()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        var start = (long)db.Slot.LatestSequence;
        Write(db, box, 10);
        var latest = (long)db.Slot.LatestSequence;

        var reply = Pull(h.Source, 1 << 20, waitMs: 0, (db.Uid, start));
        var (status, _, entryLatest, rest) = Entry(reply, 0);
        Assert.Equal(ReplicationProtocol.EntryOk, status);
        Assert.Equal(latest, entryLatest);
        var batches = rest[5].Items!;
        Assert.NotEmpty(batches);
        // The first served batch follows the cursor exactly.
        Assert.Equal(start + 1, batches[0].Items![0].Integer);
    }

    [Fact]
    public void Pull_IdleLongPoll_ReturnsAfterTheWait_WithNothing()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        Write(db, box, 2);
        var latest = (long)db.Slot.LatestSequence;

        var sw = Stopwatch.StartNew();
        var reply = Pull(h.Source, 1 << 20, waitMs: 300, (db.Uid, latest));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 250, $"an idle pull must wait out the long-poll (waited {sw.ElapsedMilliseconds} ms)");
        var (status, _, _, rest) = Entry(reply, 0);
        Assert.Equal(ReplicationProtocol.EntryOk, status);
        Assert.Empty(rest[5].Items!);
    }

    [Fact]
    public async Task Pull_WaitingLongPoll_IsWokenByACommit()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        Write(db, box, 1);
        var latest = (long)db.Slot.LatestSequence;

        // Start a long-poll on a caught-up cursor; it should block until the commit below.
        var sw = Stopwatch.StartNew();
        var pullTask = Task.Run(() => Pull(h.Source, 1 << 20, waitMs: 5000, (db.Uid, latest)));

        // Give the pull a moment to enter the wait, then commit — the source must wake it well before 5 s.
        await Task.Delay(150);
        Write(db, box, 1);

        var reply = await pullTask;
        sw.Stop();
        // The point is that the commit woke the pull rather than it waiting out the full 5 s long-poll; a generous
        // bound keeps this robust under a loaded CI while still proving the wake.
        Assert.True(sw.ElapsedMilliseconds < 4500, $"a commit must wake a waiting pull before the long-poll elapses (took {sw.ElapsedMilliseconds} ms)");

        var (status, _, entryLatest, fields) = Entry(reply, 0);
        Assert.Equal(ReplicationProtocol.EntryOk, status);
        Assert.Equal(latest + 1, entryLatest);
        Assert.NotEmpty(fields[5].Items!); // the new batch was served
    }

    [Fact]
    public void Pull_UnknownUid_IsGone_WithoutFailingTheOthers()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        Write(db, box, 3);
        var latest = (long)db.Slot.LatestSequence;

        var reply = Pull(h.Source, 1 << 20, waitMs: 0,
            ("00000000-0000-0000-0000-000000000000", 0),
            (db.Uid, latest));

        var gone = Entry(reply, 0);
        Assert.Equal(ReplicationProtocol.EntryGone, gone.Status);

        var ok = Entry(reply, 1);
        Assert.Equal(ReplicationProtocol.EntryOk, ok.Status); // the real database still answered
    }

    [Fact]
    public void Pull_FromAboveLatest_IsDiverged()
    {
        using var h = new Harness();
        var db = h.Store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        Write(db, box, 3);
        var latest = (long)db.Slot.LatestSequence;

        var reply = Pull(h.Source, 1 << 20, waitMs: 0, (db.Uid, latest + 100));
        var (status, _, _, _) = Entry(reply, 0);
        Assert.Equal(ReplicationProtocol.EntryDiverged, status);
    }

    [Fact]
    public void Pull_OnAStandby_IsRefusedNotPrimary()
    {
        using var h = new Harness(SystemD.Crystal.Storage.ReplicationRole.Standby);
        var db = h.Store.CreateDatabase("db", replicated: true);

        var reply = new RespWriter();
        Assert.True(h.Source.TryHandle(ReplicationProtocol.Pull,
            [Text.ASCII.GetBytes("PULL"), Text.ASCII.GetBytes("1024"), Text.ASCII.GetBytes("0"), Text.UTF8.GetBytes(db.Uid), Text.ASCII.GetBytes("0")], "standby-node", reply));

        var value = Parse(reply);
        Assert.Equal(RespType.Error, value.Type);
        Assert.StartsWith(ReplicationProtocol.ErrNotPrimary, value.Text);
    }

    [Fact]
    public void Pull_UnknownCommand_ReturnsFalse()
    {
        using var h = new Harness();
        var reply = new RespWriter();
        Assert.False(h.Source.TryHandle("BOGUS", [Text.ASCII.GetBytes("BOGUS")], "standby-node", reply));
    }
}
