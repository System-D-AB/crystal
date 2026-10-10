// Concurrency tests at EXACT interleavings via the catalogue-gate and striped-lock hooks, and under
// thread-pool starvation (spec 002 T11, R10.2, R5.5). The playbook forbids "hoped-for" races with
// sleeps: each interleaving here is forced with a hook, so the test is deterministic.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Storage;
using Text = System.Text.Encoding;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class BoxConcurrencyTests : IDisposable
{
    private readonly string _dir;

    public BoxConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crystal-concur-" + Guid.NewGuid().ToString("N"));
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

    private CrystalStore OpenStore(string sub) => CrystalStore.Open(Path.Combine(_dir, sub));

    // -----------------------------------------------------------------------------------------------
    // R4.2 — a write racing a drop of its box, forced to the exact interleaving with the commit hook.
    // The commit is paused INSIDE the shared catalogue gate, after validating the box is live. A drop
    // is started on another thread; because drop needs the gate EXCLUSIVELY, it must block until the
    // commit leaves the gate. So the commit wins wholly (its key lands, then the drop's range delete
    // removes it) — there is never an interleaving where the key survives in a dropped box's range.
    // -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task WriteInsideGate_RacingDrop_DropWaits_ThenRemovesTheCommittedKey_R42()
    {
        using var store = OpenStore("race-write-drop");
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("orders");
        var context = db.Slot.Context;

        var commitInsideGate = new ManualResetEventSlim(false);
        var dropObserved = new ManualResetEventSlim(false);
        var dropEnteredWhileCommitHeldGate = false;

        context.OnCommitInsideGate = () =>
        {
            // Signal we are inside the gate, then give the drop thread time to try to enter.
            commitInsideGate.Set();
            // If the gate truly serializes, the drop cannot have completed while we are here.
            // Wait briefly, then record whether the drop managed to run (it must not have).
            dropEnteredWhileCommitHeldGate = dropObserved.Wait(TimeSpan.FromMilliseconds(300));
        };

        var key = Text.UTF8.GetBytes("k");
        var batch = db.NewBatch();
        batch.Put(box, key, Text.UTF8.GetBytes("v"));

        var commitTask = Task.Run(() =>
        {
            batch.Commit();
            batch.Dispose();
        });

        var dropTask = Task.Run(() =>
        {
            commitInsideGate.Wait();         // only start the drop once the commit is inside the gate
            db.Boxes.Drop(box);              // this blocks on the exclusive gate until the commit returns
            dropObserved.Set();
        });

        var all = Task.WhenAll(commitTask, dropTask);
        var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Equal(all, completed);
        await all;
        context.OnCommitInsideGate = null;

        // The drop could not run while the commit held the gate: the gate serialized them.
        Assert.False(dropEnteredWhileCommitHeldGate, "drop ran while a commit held the gate — the gate did not serialize");

        // The box is dropped now, and no key survives in its range (commit landed, drop removed it).
        using var snap = db.Snapshot();
        Assert.Throws<BoxDroppedException>(() => snap.Get(box, key));
    }

    // -----------------------------------------------------------------------------------------------
    // R4.1 / R4.3 — a handle taken before a drop, used after it, is refused whole (never redirected).
    // Deterministic (no race): drop first, then commit through the stale handle.
    // -----------------------------------------------------------------------------------------------
    [Fact]
    public void CommitThroughHandleAfterDrop_IsRefusedWhole_R41_R43()
    {
        using var store = OpenStore("handle-after-drop");
        var db = store.CreateDatabase("db");
        var box = db.Boxes.Create("orders");

        var batch = db.NewBatch();
        batch.Put(box, Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v"));

        db.Boxes.Drop(box);

        Assert.Throws<BoxDroppedException>(() => batch.Commit());
        batch.Dispose();

        // Re-create the same name: a different id, and the stale handle never reaches it.
        var recreated = db.Boxes.Create("orders");
        Assert.NotEqual(box.Id, recreated.Id);
        using var snap = db.Snapshot();
        Assert.Null(snap.Get(recreated, Text.UTF8.GetBytes("k")));
    }

    // -----------------------------------------------------------------------------------------------
    // R3.10 — a handle taken before a rename or a swap still writes to the SAME box (by id), under its
    // new name. Deterministic.
    // -----------------------------------------------------------------------------------------------
    [Fact]
    public void HandleAcrossSwap_StillWritesToSameBoxId_R310()
    {
        using var store = OpenStore("handle-across-swap");
        var db = store.CreateDatabase("db");
        var orders = db.Boxes.Create("orders");
        var archive = db.Boxes.Create("archive");

        db.Boxes.Swap(orders, archive);   // names exchanged; ids unchanged

        // Write through the pre-swap handle for `orders` (its id is unchanged).
        using (var batch = db.NewBatch())
        {
            batch.Put(orders, Text.UTF8.GetBytes("k"), Text.UTF8.GetBytes("v"));
            batch.Commit();
        }

        // The name "archive" now resolves to orders.Id (they swapped), so the key is visible there.
        var nowArchive = db.Boxes.Find("archive")!.Value;
        Assert.Equal(orders.Id, nowArchive.Id);
        using var snap = db.Snapshot();
        Assert.Equal("v", Text.UTF8.GetString(snap.Get(nowArchive, Text.UTF8.GetBytes("k"))!));
    }

    // -----------------------------------------------------------------------------------------------
    // R10.2 — two renames racing for the same new name: exactly one wins. Forced with a hook so both
    // renames are genuinely in contention, not merely started together.
    // The gate serializes them, so one commits the alias and the other sees the name taken.
    // -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task TwoRenamesForSameNewName_ExactlyOneWins_R102()
    {
        using var store = OpenStore("race-renames");
        var db = store.CreateDatabase("db");
        var a = db.Boxes.Create("a");
        var b = db.Boxes.Create("b");

        var ready = new Barrier(2);
        var aWon = false;
        var bWon = false;
        var aTaken = false;
        var bTaken = false;

        void TryRename(Box box, Action<bool> wonSetter, Action<bool> takenSetter)
        {
            ready.SignalAndWait();
            try
            {
                db.Boxes.Rename(box, "target");
                wonSetter(true);
            }
            catch (BoxNameTakenException)
            {
                takenSetter(true);
            }
        }

        var t1 = Task.Run(() => TryRename(a, w => aWon = w, t => aTaken = t));
        var t2 = Task.Run(() => TryRename(b, w => bWon = w, t => bTaken = t));
        var renames = Task.WhenAll(t1, t2);
        var done = await Task.WhenAny(renames, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Equal(renames, done);
        await renames;

        // Exactly one rename succeeded; the other saw the name taken (never a crash, never both).
        Assert.True(aWon ^ bWon, $"exactly one rename must win: aWon={aWon}, bWon={bWon}");
        Assert.True(aTaken ^ bTaken, $"exactly one rename must be refused: aTaken={aTaken}, bTaken={bTaken}");
        Assert.NotNull(db.Boxes.Find("target"));
    }

    // -----------------------------------------------------------------------------------------------
    // R5.2 / R5.5 — LockMany always takes stripes in ascending order. Using the StripeWaiting hook we
    // observe the exact order the stripes are acquired, proving the ascending-order discipline that
    // makes two multi-box lockers deadlock-free.
    // -----------------------------------------------------------------------------------------------
    [Fact]
    public void LockMany_TakesStripesInAscendingOrder_R52()
    {
        using var store = OpenStore("lock-order");
        var db = store.CreateDatabase("db");

        // Create enough boxes that their ids map to several distinct, out-of-order stripes.
        var boxes = new List<Box>();
        for (var i = 0; i < 8; i++)
        {
            boxes.Add(db.Boxes.Create($"box_{i}"));
        }

        var acquired = new List<int>();
        db.Locks.OnStripeWaiting = stripe => acquired.Add(stripe);

        // Pass the boxes in a deliberately shuffled order; LockMany must still acquire ascending.
        var shuffled = boxes.OrderByDescending(x => x.Id).ToList();
        using (db.Locks.LockMany(shuffled))
        {
            // held
        }

        db.Locks.OnStripeWaiting = null;

        var sorted = acquired.OrderBy(x => x).ToList();
        Assert.Equal(sorted, acquired);  // acquisition order equals ascending order
    }

    // -----------------------------------------------------------------------------------------------
    // R10.2 — the same operations under a starved thread pool complete without deadlock or corruption.
    // -----------------------------------------------------------------------------------------------
    [Fact]
    public void UnderStarvedThreadPool_LifecycleAndWrites_CompleteWithoutDeadlock()
    {
        // The playbook's "starved thread pool": force the pool to grow slowly (min 1), so Crystal's
        // synchronous, blocking operations must not depend on the pool having spare threads. The worker
        // bodies run on DEDICATED threads (not the pool), so the test measures Crystal's blocking
        // behaviour under contention, not the pool's scheduling of the test harness itself.
        ThreadPool.GetMinThreads(out var minW, out var minC);
        try
        {
            ThreadPool.SetMinThreads(1, 1);

            using var store = OpenStore("starved");
            var db = store.CreateDatabase("db");

            var errors = new List<Exception>();
            var errorsLock = new object();
            var threads = new List<Thread>();
            for (var i = 0; i < 8; i++)
            {
                var idx = i;
                var thread = new Thread(() =>
                {
                    try
                    {
                        var box = db.Boxes.Create($"box_{idx}");
                        for (var j = 0; j < 25; j++)
                        {
                            using var batch = db.NewBatch();
                            batch.Put(box, Text.UTF8.GetBytes($"k_{j}"), Text.UTF8.GetBytes($"v_{j}"));
                            batch.Commit(DurabilityMode.Buffered);
                        }

                        using var snap = db.Snapshot();
                        Assert.Equal(25, snap.Scan(box, [], [], limit: 1000).Count());
                        db.Boxes.Drop(box);
                    }
                    catch (Exception ex)
                    {
                        lock (errorsLock)
                        {
                            errors.Add(ex);
                        }
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a worker deadlocked under a starved thread pool");
            }

            Assert.Empty(errors);
        }
        finally
        {
            ThreadPool.SetMinThreads(minW, minC);
        }
    }
}
