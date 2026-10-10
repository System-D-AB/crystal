// Ported from Highway: src/Highway.Server/Storage/StripedLock.cs
// Adapted for Crystal: box locks, ascending LockMany/LockAll, test hooks (spec 002 T8, R5).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class BoxLocksTests
{
    [Fact]
    public void Lock_AcquiresAndReleases()
    {
        var locks = new BoxLocks();
        var box = new Box(16, "orders");

        using (locks.Lock(box))
        {
            // Lock held
        }
    }

    [Fact]
    public void LockMany_AcquiresDistinctStripesInStrictlyAscendingOrder()
    {
        var locks = new BoxLocks();
        var waitingStripes = new List<int>();

        locks.OnStripeWaiting = s =>
        {
            lock (waitingStripes)
            {
                waitingStripes.Add(s);
            }
        };

        // Boxes in intentionally random/descending order, with duplicates
        Box[] boxes =
        [
            new(200, "b200"),
            new(50, "b50"),
            new(50, "b50_duplicate"),
            new(10, "b10"),
            new(100, "b100"),
            new(250, "b250")
        ];

        using (locks.LockMany(boxes))
        {
            Assert.True(waitingStripes.Count > 0);

            // Assert strictly ascending
            for (var i = 0; i < waitingStripes.Count - 1; i++)
            {
                Assert.True(waitingStripes[i] < waitingStripes[i + 1],
                    $"Stripes not ascending: {waitingStripes[i]} >= {waitingStripes[i + 1]}");
            }
        }
    }

    [Fact]
    public void LockAll_AcquiresAll256StripesInAscendingOrder()
    {
        var locks = new BoxLocks();
        var acquiredStripes = new List<int>();

        locks.OnStripeWaiting = s => acquiredStripes.Add(s);

        using (locks.LockAll())
        {
            Assert.Equal(256, acquiredStripes.Count);
            for (var i = 0; i < 256; i++)
            {
                Assert.Equal(i, acquiredStripes[i]);
            }
        }
    }

    [Fact]
    public void TestHooks_OnStripeWaiting_And_OnStripeReleasing_Fire()
    {
        var locks = new BoxLocks();
        var waiting = new List<int>();
        var releasing = new List<int>();

        locks.OnStripeWaiting = s => waiting.Add(s);
        locks.OnStripeReleasing = s => releasing.Add(s);

        var box = new Box(42, "b42");
        using (locks.Lock(box))
        {
            Assert.Single(waiting);
            Assert.Empty(releasing);
        }

        Assert.Single(releasing);
        Assert.Equal(waiting[0], releasing[0]);
    }

    [Fact]
    public async Task DeterministicOrdering_PreventsDeadlockBetweenConcurrentLockers()
    {
        var locks = new BoxLocks();
        var box1 = new Box(10, "b10");
        var box2 = new Box(20, "b20");

        var iterations = 500;
        var t1 = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                using (locks.LockMany([box1, box2]))
                {
                    // simulate work
                }
            }
        });

        var t2 = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                // Requesting in reverse order [box2, box1] MUST still acquire ascending stripes
                using (locks.LockMany([box2, box1]))
                {
                    // simulate work
                }
            }
        });

        var allTask = Task.WhenAll(t1, t2);
        var completed = await Task.WhenAny(allTask, Task.Delay(10000));
        Assert.Equal(allTask, completed);
        await allTask;
    }

    /// <summary>
    /// A lock is released through its owner, on whichever thread disposes it (design § 4): a product that locks, awaits
    /// and disposes on another thread must release the stripe, not throw and leave it held for ever.
    /// </summary>
    [Fact]
    public void Lock_ReleasedOnAnotherThread_ReleasesTheStripe()
    {
        var locks = new BoxLocks();
        var box = new Box(16, "orders");

        IDisposable? held = null;
        DedicatedThread.Run(() => held = locks.Lock(box)).Complete(TimeSpan.FromSeconds(10));
        DedicatedThread.Run(() => held!.Dispose()).Complete(TimeSpan.FromSeconds(10));

        using var acquired = new ManualResetEventSlim();
        DedicatedThread.Run(() =>
        {
            using (locks.Lock(box))
            {
                acquired.Set();
            }
        }).Complete(TimeSpan.FromSeconds(10));
        Assert.True(acquired.IsSet);
    }

    /// <summary>R5.4: locks are not re-entrant. A thread that locks a box it already holds waits like any other.</summary>
    [Fact]
    public void Lock_IsNotReentrant()
    {
        var locks = new BoxLocks();
        var box = new Box(16, "orders");
        using var firstHeld = new ManualResetEventSlim();
        using var secondHeld = new ManualResetEventSlim();
        IDisposable? first = null;

        var thread = DedicatedThread.Run(() =>
        {
            first = locks.Lock(box);
            firstHeld.Set();
            using (locks.Lock(box))
            {
                secondHeld.Set();
            }
        });

        Assert.True(firstHeld.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(secondHeld.Wait(TimeSpan.FromMilliseconds(200)), "The same thread must wait for its own lock.");

        first!.Dispose();
        thread.Complete(TimeSpan.FromSeconds(10));
        Assert.True(secondHeld.IsSet);
    }
}
