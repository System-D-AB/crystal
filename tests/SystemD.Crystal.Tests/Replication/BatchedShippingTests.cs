// Spec 014 R7.5 — the behaviours of batched shipping that had no test: wakes are coalesced (R2.3), a waiting pull holds
// no thread (R5.1), and a standby near the head is answered at most once per gather window while one catching up is
// answered at once (R2.3, R2.4). The tail's end (R4.2) was not built: T1 measured no WAL page under the harness.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;
using SystemD.Crystal.Tests.Support;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

public sealed class BatchedShippingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WaitForChange_AThousandCommits_WakeTheWaitingPullOnce()
    {
        // R2.3: however many commits land, a waiting pull is woken once, and every commit after the first completes
        // nothing until it waits again.
        var signal = new ChangeSignal();
        var mark = signal.Mark;
        var wait = signal.WaitForChangeAsync(mark, Patience, TimeProvider.System, CancellationToken.None).AsTask();
        Assert.False(wait.IsCompleted);

        for (var i = 0; i < 1000; i++)
        {
            signal.Signal();
        }

        Assert.True(await wait.WaitAsync(Patience));
        Assert.Equal(1, signal.Wakes);
        Assert.Equal(mark + 1000, signal.Mark);
    }

    [Fact]
    public async Task WaitForChange_AfterTheWaiterReturns_ACommitWakesNobody()
    {
        // R2.3: a waiter re-arms only when it is about to wait, so once it has returned a commit completes nothing.
        var signal = new ChangeSignal();
        var wait = signal.WaitForChangeAsync(signal.Mark, Patience, TimeProvider.System, CancellationToken.None).AsTask();
        signal.Signal();
        Assert.True(await wait.WaitAsync(Patience));

        signal.Signal();

        Assert.Equal(1, signal.Wakes);
    }

    [Fact]
    public async Task WaitForChange_ACommitCountedBeforeTheMark_CompletingTheWaiterLate_DoesNotEndTheWait()
    {
        // The exact interleaving, held by the hook: a commit is counted, then a pull takes its mark and waits, then the
        // commit completes the waiter. The counter has not moved past the mark, so the pull keeps waiting.
        var signal = new ChangeSignal();
        using var counted = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        signal.AfterCounted = () => { counted.Set(); release.Wait(); };
        var commit = Task.Run(signal.Signal);
        Assert.True(counted.Wait(Patience));
        signal.AfterCounted = null;

        var wait = signal.WaitForChangeAsync(signal.Mark, TimeSpan.FromMilliseconds(300), TimeProvider.System, CancellationToken.None).AsTask();
        release.Set();
        await commit.WaitAsync(Patience);

        Assert.False(await wait.WaitAsync(Patience), "A commit counted before the mark ended the wait.");
    }

    [Fact]
    public async Task Pull_CaughtUp_ReturnsToItsCallerAtOnce_AndAnswersWhenACommitLands()
    {
        // R5.1 at the handler: a caught-up PULL waiting on its long poll has returned to its caller unfinished, holding
        // no thread, and finishes when a commit lands.
        using var dir = new TempStoreDirectory();
        var options = new ReplicationOptions { Secret = "test-secret", LongPollMax = TimeSpan.FromSeconds(20) };
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = options });
        var db = store.CreateDatabase("shop", replicated: true);
        var box = db.Boxes.Create("orders");
        Commit(db, box, 0);
        using var source = new ReplicationSource(store, store.Engine, store.NodeId,
            ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary), options);
        var at = Pull(source, db.Uid, from: 0, maxBytes: 1 << 20);

        var reply = new RespWriter();
        var args = new List<byte[]>
        {
            Text.ASCII.GetBytes(ReplicationProtocol.Pull), Text.ASCII.GetBytes("1048576"), Text.ASCII.GetBytes("20000"),
            Text.UTF8.GetBytes(db.Uid), Text.ASCII.GetBytes(at.ToString()),
        };
        var pull = source.TryHandleAsync(ReplicationProtocol.Pull, args, "standby-2", reply, CancellationToken.None).AsTask();
        Assert.False(pull.IsCompleted, "The caught-up pull did not return to its caller while it waits.");

        Commit(db, box, 1);

        Assert.True(await pull.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WaitForChange_ManyWaiting_HoldNoThreadAndAllWake()
    {
        // R5.1, R5.2: a wait returns to its caller at once, unfinished, so 500 of them started on this one thread hold
        // no thread at all; a blocking wait would hold this thread on the first. One change wakes every one.
        var signal = new ChangeSignal();
        var mark = signal.Mark;
        var waits = Enumerable.Range(0, 500)
            .Select(_ => signal.WaitForChangeAsync(mark, Patience, TimeProvider.System, CancellationToken.None).AsTask())
            .ToList();
        Assert.All(waits, w => Assert.False(w.IsCompleted));

        signal.Signal();

        var woken = await Task.WhenAll(waits).WaitAsync(Patience);
        Assert.All(woken, Assert.True);
    }

    [Fact]
    public async Task WaitForChange_ChangeBeforeTheMark_DoesNotEndTheWait()
    {
        // A change counted before the waiter took its mark can complete the waiter late; the counter decides, so the
        // pull keeps waiting rather than answering an empty page early.
        var signal = new ChangeSignal();
        signal.Signal();
        var mark = signal.Mark;
        var waited = await signal.WaitForChangeAsync(mark, TimeSpan.FromMilliseconds(100), TimeProvider.System, CancellationToken.None);
        Assert.False(waited);
    }

    [Fact]
    public async Task WaitForUrgent_ACommitDoesNotEndIt_AnUrgentChangeDoes()
    {
        var signal = new ChangeSignal();
        var wait = signal.WaitForUrgentAsync(signal.UrgentMark, Patience, TimeProvider.System, CancellationToken.None).AsTask();

        signal.Signal();
        Assert.False(await Task.WhenAny(wait, Task.Delay(100)) == wait, "A commit ended a wait for an urgent change.");

        signal.SignalUrgent();
        Assert.True(await wait.WaitAsync(Patience));
    }

    [Fact]
    public async Task Pull_StandbyNearTheHead_IsAnsweredAtMostOncePerGatherWindow_OneBehindAtOnce()
    {
        // R2.3: the second pull of a standby whose first reply was a plain tail page waits out the window (the
        // "pull.pacing" hook fires) and ships every commit of the window in one reply. R2.4: a standby whose reply was a
        // full page is answered at once.
        using var dir = new TempStoreDirectory();
        var steps = new List<string>();
        var options = new ReplicationOptions
        {
            Secret = "test-secret",
            GatherWindow = TimeSpan.FromSeconds(1), // long enough to commit inside it, deterministically
            PageBytes = 1 << 20,
        };
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = options,
            Hook = step => { lock (steps) { steps.Add(step); } },
        });
        var db = store.CreateDatabase("shop", replicated: true);
        var box = db.Boxes.Create("orders");
        Commit(db, box, 0);

        using var source = new ReplicationSource(store, store.Engine, store.NodeId,
            ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary), options);
        var uid = db.Uid;

        // The first pull enables the tail and reads the WAL: behind, so the next is not paced.
        var first = Pull(source, uid, from: 0, maxBytes: 1 << 20);
        Commit(db, box, 1);
        var second = Pull(source, uid, from: first, maxBytes: 1 << 20); // from the tail: near the head
        Assert.DoesNotContain("pull.pacing", Snapshot(steps));

        // Near the head: the next pull is paced; five commits made while it waits ship in its one reply.
        var third = Task.Run(() => Pull(source, uid, from: second, maxBytes: 1 << 20, out var batches) is var _ ? batches : 0);
        Assert.True(SpinWait.SpinUntil(() => Snapshot(steps).Contains("pull.pacing"), Patience), "The pull was not paced.");
        for (var i = 2; i < 7; i++)
        {
            Commit(db, box, i);
        }

        Assert.Equal(5, await third.WaitAsync(Patience));

        // A full page leaves the standby behind: its next pull is answered at once, without pacing. (The pull that gets
        // the full page follows a near-head reply, so it is itself paced.)
        for (var i = 7; i < 40; i++)
        {
            Commit(db, box, i);
        }

        var behindAt = Pull(source, uid, from: (long)db.Slot.LatestSequence - 30, maxBytes: 64);
        lock (steps)
        {
            steps.Clear();
        }

        Pull(source, uid, from: behindAt, maxBytes: 64);
        Assert.DoesNotContain("pull.pacing", Snapshot(steps));
    }

    [Fact]
    public async Task Pull_PacedByTheGatherWindow_IsAnsweredAtOnceOnARoleOrEpochChange()
    {
        // The gather window never holds back a change of role, epoch or database set: a paced pull answers at once.
        using var dir = new TempStoreDirectory();
        var steps = new List<string>();
        var options = new ReplicationOptions { Secret = "test-secret", GatherWindow = TimeSpan.FromSeconds(1) }; // the largest allowed
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = options,
            Hook = step => { lock (steps) { steps.Add(step); } },
        });
        var db = store.CreateDatabase("shop", replicated: true);
        var box = db.Boxes.Create("orders");
        Commit(db, box, 0);
        using var source = new ReplicationSource(store, store.Engine, store.NodeId,
            ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary), options);

        var first = Pull(source, db.Uid, from: 0, maxBytes: 1 << 20);   // from the WAL: behind
        Commit(db, box, 1);
        var second = Pull(source, db.Uid, from: first, maxBytes: 1 << 20); // from the tail: near the head
        var paced = Task.Run(() => Pull(source, db.Uid, from: second, maxBytes: 1 << 20));
        Assert.True(SpinWait.SpinUntil(() => Snapshot(steps).Contains("pull.pacing"), Patience), "The pull was not paced.");

        source.SignalRoleOrEpochChange();

        Assert.Equal(second, await paced.WaitAsync(TimeSpan.FromMilliseconds(500))); // well before the 1 s window ends
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Open_GatherWindowOutsideZeroToOneSecond_IsRefused(int milliseconds)
    {
        // A window near the standby's reply timeout would stall every standby: it is refused when the store opens.
        using var dir = new TempStoreDirectory();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "test-secret", GatherWindow = TimeSpan.FromMilliseconds(milliseconds) },
        }));
        Assert.Contains("GatherWindow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_PacedByTheGatherWindow_EndsWhenTheConnectionCloses()
    {
        // A pull pacing itself is cancelled with its connection, as a long poll is.
        using var dir = new TempStoreDirectory();
        var steps = new List<string>();
        var options = new ReplicationOptions { Secret = "test-secret", GatherWindow = TimeSpan.FromSeconds(1) };
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = options,
            Hook = step => { lock (steps) { steps.Add(step); } },
        });
        var db = store.CreateDatabase("shop", replicated: true);
        var box = db.Boxes.Create("orders");
        Commit(db, box, 0);
        using var source = new ReplicationSource(store, store.Engine, store.NodeId,
            ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary), options);
        var first = Pull(source, db.Uid, from: 0, maxBytes: 1 << 20);
        Commit(db, box, 1);
        var second = Pull(source, db.Uid, from: first, maxBytes: 1 << 20);

        using var closed = new CancellationTokenSource();
        var args = new List<byte[]>
        {
            Text.ASCII.GetBytes(ReplicationProtocol.Pull), Text.ASCII.GetBytes("1048576"), Text.ASCII.GetBytes("0"),
            Text.UTF8.GetBytes(db.Uid), Text.ASCII.GetBytes(second.ToString()),
        };
        var paced = source.TryHandleAsync(ReplicationProtocol.Pull, args, "standby-1", new RespWriter(), closed.Token).AsTask();
        Assert.True(SpinWait.SpinUntil(() => Snapshot(steps).Contains("pull.pacing"), Patience), "The pull was not paced.");

        await closed.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paced.WaitAsync(Patience));
    }

    [Fact]
    public void LagTime_ForABatchTheTailDoesNotHold_ComesFromTheTimeline()
    {
        // The timeline records from the database's first serving step, the tail only once a pull enables it: a batch
        // committed between them is dated by the timeline; one committed after, by the tail.
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        var options = new ReplicationOptions { Secret = "test-secret" };
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = options, Clock = clock });
        var db = store.CreateDatabase("shop", replicated: true);
        var box = db.Boxes.Create("orders");
        using var source = new ReplicationSource(store, store.Engine, store.NodeId,
            ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary), options);
        var serving = source.ServingForTest(db.Uid)!;
        var start = serving.Latest;

        Commit(db, box, 0);                 // timeline only: the tail is not capturing yet
        clock.Advance(TimeSpan.FromSeconds(10));
        serving.EnableTail();
        Commit(db, box, 1);                 // the tail too
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(serving.Tail.CommittedAtAfter((ulong)start));
        Assert.Equal(TimeSpan.FromSeconds(15), serving.LagTime(start));      // from the timeline
        Assert.Equal(TimeSpan.FromSeconds(5), serving.LagTime(start + 1));   // from the tail
    }

    [Fact]
    public void Timeline_CommitsBetweenSamples_AreFoldedIntoTheSampleTheyBelongTo()
    {
        // R1.1: between samples a commit takes no lock; what it committed is folded into the newest sample when the next
        // sample is taken, so a batch keeps the time of the interval it was committed in.
        var timeline = new CommitTimeline();
        var ms = TimeSpan.TicksPerMillisecond;
        timeline.Committed(Batch(1), 0);           // sample at 0
        timeline.Committed(Batch(2), 10 * ms);     // between samples: no lock
        timeline.Committed(Batch(5), 50 * ms);
        timeline.Committed(Batch(6), 150 * ms);    // a new sample; 2..5 belong to the one at 0

        Assert.Equal(TimeSpan.FromMilliseconds(200), timeline.AgeAfter(1, 200 * ms));
        Assert.Equal(TimeSpan.FromMilliseconds(50), timeline.AgeAfter(5, 200 * ms));
        Assert.Equal(TimeSpan.Zero, timeline.AgeAfter(6, 200 * ms));
    }

    [Fact]
    public void Timeline_CommitsSinceTheNewestSample_AreSeenByARead()
    {
        var timeline = new CommitTimeline();
        var ms = TimeSpan.TicksPerMillisecond;
        timeline.Committed(Batch(1), 0);
        timeline.Committed(Batch(3), 20 * ms);    // pending: no sample of its own yet

        Assert.Equal(TimeSpan.FromMilliseconds(40), timeline.AgeAfter(1, 40 * ms));
    }

    [Fact]
    public void Tail_CommittedAtAfter_IsTheCommitTimeOfTheNextBatch()
    {
        var tail = new ReplicationTail();
        tail.Enable();
        tail.Add(Batch(5), timestamp: 100);
        tail.Add(Batch(6, count: 2), timestamp: 200); // sequences 6 and 7
        tail.Add(Batch(8), timestamp: 300);

        Assert.Equal(100, tail.CommittedAtAfter(4));
        Assert.Equal(200, tail.CommittedAtAfter(5));
        Assert.Equal(200, tail.CommittedAtAfter(6)); // 7 is inside the batch at 6
        Assert.Equal(300, tail.CommittedAtAfter(7));
        Assert.Null(tail.CommittedAtAfter(8));   // nothing after
        Assert.Null(tail.CommittedAtAfter(2));   // older than the tail: the timeline answers

        var zero = new ReplicationTail();
        zero.Enable();
        zero.Add(Batch(1), timestamp: 0);         // a clock whose timestamps start at 0: 0 is a time, not "unknown"
        Assert.Equal(0, zero.CommittedAtAfter(0));
    }

    /// <summary>A written batch's leading bytes: the sequence and the count (all CommitTimeline and the tail read).</summary>
    private static byte[] Batch(ulong seq, uint count = 1)
    {
        var bytes = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, seq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), count);
        return bytes;
    }

    private static void Commit(CrystalDatabase db, Box box, int i)
    {
        using var batch = db.NewBatch();
        batch.Put(box, [(byte)i], new byte[16]);
        batch.Commit(DurabilityMode.Strict);
    }

    private static List<string> Snapshot(List<string> steps)
    {
        lock (steps)
        {
            return [.. steps];
        }
    }

    private static long Pull(ReplicationSource source, string uid, long from, int maxBytes) =>
        Pull(source, uid, from, maxBytes, out _);

    /// <summary>One PULL (no long poll); returns the standby's new position and how many batches came.</summary>
    private static long Pull(ReplicationSource source, string uid, long from, int maxBytes, out int batches)
    {
        var reply = new RespWriter();
        var args = new List<byte[]>
        {
            Text.ASCII.GetBytes(ReplicationProtocol.Pull),
            Text.ASCII.GetBytes(maxBytes.ToString()),
            Text.ASCII.GetBytes("0"),
            Text.UTF8.GetBytes(uid),
            Text.ASCII.GetBytes(from.ToString()),
        };
        Assert.True(source.TryHandle(ReplicationProtocol.Pull, args, "standby-1", reply));

        var bytes = reply.ToArray();
        var status = RespReader.TryReadReply(new System.Buffers.ReadOnlySequence<byte>(bytes), 16 << 20, out var value, out _, out var error);
        Assert.Equal(RespReadStatus.Complete, status);
        Assert.Null(error);
        var entry = value!.Items![2].Items![0];
        var shipped = entry.Items![5].Items!;
        batches = shipped.Count;
        if (shipped.Count == 0)
        {
            return from;
        }

        var last = shipped[^1].Items!;
        var lastSeq = last[0].Integer;
        var count = BitConverter.ToUInt32(last[1].Bytes!, 8);
        return lastSeq + Math.Max(count, 1) - 1;
    }
}
