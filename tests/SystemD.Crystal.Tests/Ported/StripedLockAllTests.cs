// Ported from Highway: tests/Highway.Server.Tests/Storage/StripedLockAllTests.cs (spec 004 design § 2.3, task T11).
// Crystal's stripes are chosen by box id (id mod 256), so a box with id n sits on stripe n. Highway waited with
// Task.Delay; these tests act on the stripe hooks (spec 002 R5.5) and run their lockers on dedicated threads, so they
// do not depend on the thread pool (playbook § 4). A negative wait ("still blocked") can only pass falsely, never fail
// falsely: a correct lock never reaches the step it waits for.

using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace SystemD.Crystal.Tests.Ported;

public sealed class StripedLockAllTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(200);

    private static Box On(int stripe) => new((uint)stripe, $"n{stripe}");

    // Ported from Highway: StripedLockAllTests.LockAll_WaitsForAHeldStripe_AndThenExcludesEveryName
    [Fact]
    public void LockAll_WaitsForAHeldStripe_AndThenExcludesEveryBox()
    {
        var locks = new BoxLocks();
        using var allWaitingAt5 = new ManualResetEventSlim();
        using var allHeld = new ManualResetEventSlim();
        using var releaseAll = new ManualResetEventSlim();
        var allThread = -1;
        locks.OnStripeWaiting = stripe =>
        {
            if (stripe == 5 && Environment.CurrentManagedThreadId == Volatile.Read(ref allThread))
            {
                allWaitingAt5.Set();
            }
        };

        DedicatedThread all;
        using (locks.Lock(On(5)))
        {
            all = DedicatedThread.Run(() =>
            {
                Volatile.Write(ref allThread, Environment.CurrentManagedThreadId);
                using (locks.LockAll())
                {
                    allHeld.Set();
                    releaseAll.Wait();
                }
            });

            Assert.True(allWaitingAt5.Wait(Patience), "LockAll reaches the held stripe.");
            Assert.False(allHeld.Wait(Blocked), "One stripe is held, so LockAll waits.");
        }

        Assert.True(allHeld.Wait(Patience), "LockAll completes once the stripe is released.");

        using var otherHeld = new ManualResetEventSlim();
        var other = DedicatedThread.Run(() =>
        {
            using (locks.Lock(On(200)))
            {
                otherHeld.Set();
            }
        });
        Assert.False(otherHeld.Wait(Blocked), "Every stripe is held.");

        releaseAll.Set();
        all.Complete(Patience);
        other.Complete(Patience);
        Assert.True(otherHeld.IsSet);
    }

    // Ported from Highway: StripedLockAllTests.LockAll_TakesStripesInTheOrderLockManyDoes. LockMany holds stripe 1 and
    // is about to wait for stripe 2 when LockAll starts. In ascending order LockAll stops at stripe 1 holding only
    // stripe 0, so LockMany gets 2 and both finish; any other order lets LockAll take stripe 2 first, and the two wait
    // on each other for ever.
    [Fact]
    public void LockAll_TakesStripesInTheOrderLockManyDoes()
    {
        var locks = new BoxLocks();
        using var allReachedStripe1 = new ManualResetEventSlim();
        var manyThread = -1;
        var started = 0;
        DedicatedThread? all = null;
        locks.OnStripeWaiting = stripe =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref manyThread))
            {
                if (stripe == 2 && Interlocked.Exchange(ref started, 1) == 0)
                {
                    all = DedicatedThread.Run(() => locks.LockAll().Dispose());
                    allReachedStripe1.Wait(Patience);
                }
            }
            else if (stripe == 1)
            {
                allReachedStripe1.Set();
            }
        };

        var many = DedicatedThread.Run(() =>
        {
            Volatile.Write(ref manyThread, Environment.CurrentManagedThreadId);
            using (locks.LockMany([On(1), On(2)]))
            {
            }
        });

        Assert.True(many.Join(Patience), "LockMany is not blocked by LockAll.");
        Assert.NotNull(all);
        Assert.True(all!.Join(Patience), "Then LockAll completes.");
        locks.OnStripeWaiting = null;
        many.Complete(Patience);
        all.Complete(Patience);
    }

    // Ported from Highway: StripedLockAllTests.LockAll_AndLockMany_NeverDeadlock
    [Fact]
    public void LockAll_AndLockMany_NeverDeadlock()
    {
        var locks = new BoxLocks();
        var boxes = Enumerable.Range(0, 32).Select(i => new Box((uint)(i * 37), $"n{i}")).ToArray();

        var workers = Enumerable.Range(0, 200).Select(i => DedicatedThread.Run(() =>
        {
            using (i % 10 == 0 ? locks.LockAll() : locks.LockMany(boxes.Skip(i % 20).Take(5)))
            {
            }
        })).ToArray();

        foreach (var worker in workers)
        {
            worker.Complete(TimeSpan.FromSeconds(20));
        }
    }
}
