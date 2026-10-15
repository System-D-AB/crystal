// Spec 012 T5, T6, R3 — key locks and async box locks: keys on one stripe serialise and keys on different stripes do
// not; opposite-order multi-key lockers both complete at the exact interleaving; a cancelled waiter releases what it
// took; 64 async waiters complete on a starved thread pool; an uncontended key's stripe is hashed without allocating;
// box and key locks are independent; lock waits are tagged by scope.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Tests.Adoption;

public sealed class KeyLockTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly TempStoreDirectory _dir = new();
    private readonly MetricsCapture _metrics = new();
    private readonly CrystalStore _store;
    private readonly CrystalDatabase _db;
    private readonly Box _box;

    public KeyLockTests()
    {
        _store = CrystalStore.Open(_dir.Path, new CrystalOptions { KeyLockStripes = 64, MeterFactory = _metrics });
        _db = _store.CreateDatabase("db");
        _box = _db.Boxes.Create("box");
    }

    public void Dispose()
    {
        Assert.Equal(0, _store.Engine.OpenIterators); // spec 012 R8.3: no scan left open
        _store.Dispose();
        _metrics.Dispose();
        _dir.Dispose();
    }

    /// <summary>Two distinct keys of the box on the same stripe, and one on another stripe.</summary>
    private (byte[] A, byte[] B, byte[] Elsewhere) Keys()
    {
        var locks = _db.KeyLocks;
        var first = "k0"u8.ToArray();
        var stripe = locks.StripeOf(_box.Id, first);
        byte[]? same = null, other = null;
        for (var i = 1; same is null || other is null; i++)
        {
            var key = System.Text.Encoding.UTF8.GetBytes($"k{i}");
            if (locks.StripeOf(_box.Id, key) == stripe)
            {
                same ??= key;
            }
            else
            {
                other ??= key;
            }
        }

        return (first, same, other);
    }

    [Fact]
    public async Task KeysOnOneStripe_Serialise_AndKeysOnDifferentStripes_DoNot()
    {
        var (a, b, elsewhere) = Keys();
        using var held = _db.KeyLocks.Lock(_box, a);

        var sameStripe = _db.KeyLocks.LockAsync(_box, b);
        await Task.Delay(100);
        Assert.False(sameStripe.IsCompleted);

        using (await _db.KeyLocks.LockAsync(_box, elsewhere).AsTask().WaitAsync(Bound))
        {
        }

        held.Dispose();
        await using var got = await sameStripe.AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task OppositeOrderMultiKeyLockers_BothComplete_AtTheExactInterleaving()
    {
        var (a, _, other) = Keys();
        var first = new[] { new LockKey(_box, a), new LockKey(_box, other) };
        var second = new[] { new LockKey(_box, other), new LockKey(_box, a) };

        // The first locker takes its first stripe and is held there; the second asks meanwhile, in the opposite order.
        var firstTookOne = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        var lowest = Math.Min(_db.KeyLocks.StripeOf(_box.Id, a), _db.KeyLocks.StripeOf(_box.Id, other));
        var highest = Math.Max(_db.KeyLocks.StripeOf(_box.Id, a), _db.KeyLocks.StripeOf(_box.Id, other));
        var holdOnce = 0;
        _db.KeyLocks.OnStripeWaiting = stripe =>
        {
            if (stripe == highest && Interlocked.Exchange(ref holdOnce, 1) == 0)
            {
                firstTookOne.Set();
                releaseFirst.Wait(Bound);
            }
        };

        var one = Task.Run(() => _db.KeyLocks.LockMany(first));
        Assert.True(firstTookOne.Wait(Bound));
        var two = _db.KeyLocks.LockManyAsync(second).AsTask();
        await Task.Delay(100);
        Assert.False(two.IsCompleted); // waits on the lowest stripe, which the first holds: sorted, so no deadlock
        releaseFirst.Set();

        (await one.WaitAsync(Bound)).Dispose();
        (await two.WaitAsync(Bound)).Dispose();
        _db.KeyLocks.OnStripeWaiting = null;
        Assert.NotEqual(lowest, highest);
    }

    [Fact]
    public async Task ACancelledMultiKeyWaiter_ReleasesWhatItTook_AndTheNextWaiterIsAdmitted()
    {
        var (a, _, other) = Keys();
        var low = _db.KeyLocks.StripeOf(_box.Id, a) < _db.KeyLocks.StripeOf(_box.Id, other) ? a : other;
        var high = ReferenceEquals(low, a) ? other : a;
        var holder = _db.KeyLocks.Lock(_box, high);

        using var cancel = new CancellationTokenSource();
        var waiter = _db.KeyLocks.LockManyAsync([new LockKey(_box, low), new LockKey(_box, high)], cancel.Token).AsTask();
        await Task.Delay(100); // it holds `low` and waits for `high`
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Bound));

        using (await _db.KeyLocks.LockAsync(_box, low).AsTask().WaitAsync(Bound)) // released by the cancelled waiter
        {
        }

        var next = _db.KeyLocks.LockAsync(_box, high).AsTask();
        holder.Dispose();
        (await next.WaitAsync(Bound)).Dispose();
    }

    [Fact]
    public async Task SixtyFourAsyncWaiters_HoldNoThreadWhileTheyWait_AndAllComplete()
    {
        // Each LockAsync on a held stripe returns at once, incomplete, on this one thread: a waiter that blocked a thread
        // would block this one, and the 64 calls could never be made from it. They then complete one after another.
        var (a, _, _) = Keys();
        var holder = _db.KeyLocks.Lock(_box, a);
        var thread = Environment.CurrentManagedThreadId;
        var pending = new List<Task<LockLease>>();
        for (var i = 0; i < 64; i++)
        {
            var waiter = _db.KeyLocks.LockAsync(_box, a);
            Assert.False(waiter.IsCompleted);
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            pending.Add(waiter.AsTask());
        }

        holder.Dispose();
        var done = 0;
        foreach (var waiter in pending)
        {
            await using (await waiter.WaitAsync(Bound))
            {
                done++;
            }
        }

        Assert.Equal(64, done);
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_TakesNothing_EvenOnAFreeStripe()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _db.KeyLocks.LockAsync(_box, "free"u8.ToArray(), cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _db.Locks.LockAsync(_box, cancel.Token).AsTask());
        using (_db.KeyLocks.Lock(_box, "free"u8))
        using (_db.Locks.Lock(_box))
        {
        }
    }

    [Fact]
    public void AKeysStripe_IsComputedWithoutAllocating()
    {
        var key = new byte[64];
        var locks = _db.KeyLocks;
        _ = locks.StripeOf(_box.Id, key); // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = locks.StripeOf(_box.Id, key);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public async Task BoxLocksAndKeyLocks_AreIndependent_AndBoxLocksHaveAsyncForms()
    {
        using (_db.Locks.Lock(_box))
        using (_db.KeyLocks.Lock(_box, "k"u8)) // not excluded by the box lock
        {
        }

        var other = _db.Boxes.Create("other");
        var held = await _db.Locks.LockAsync(_box);
        var waiting = _db.Locks.LockManyAsync([other, _box]).AsTask();
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await held.DisposeAsync();
        (await waiting.WaitAsync(Bound)).Dispose();

        using var cancel = new CancellationTokenSource();
        var holder = _db.Locks.Lock(_box);
        var cancelled = _db.Locks.LockAsync(_box, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        holder.Dispose();
        (await _db.Locks.LockAsync(_box).AsTask().WaitAsync(Bound)).Dispose();
    }

    [Fact]
    public async Task ACancelledMultiBoxWaiter_ReleasesTheBoxItTook()
    {
        var later = _db.Boxes.Create("later"); // a higher id: a higher stripe, taken second
        var holder = _db.Locks.Lock(later);
        using var cancel = new CancellationTokenSource();
        var waiter = _db.Locks.LockManyAsync([_box, later], cancel.Token).AsTask();
        await Task.Delay(100); // it holds _box and waits for `later`
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Bound));

        (await _db.Locks.LockAsync(_box).AsTask().WaitAsync(Bound)).Dispose(); // released by the cancelled waiter
        holder.Dispose();
    }

    [Fact]
    public async Task ContendedWaits_AreRecorded_TaggedWithTheirScope()
    {
        var (a, _, _) = Keys();
        var keyHolder = _db.KeyLocks.Lock(_box, a);
        var keyWaiter = _db.KeyLocks.LockAsync(_box, a).AsTask();
        await Task.Delay(50);
        keyHolder.Dispose();
        (await keyWaiter.WaitAsync(Bound)).Dispose();

        var boxHolder = _db.Locks.Lock(_box);
        var boxWaiter = _db.Locks.LockAsync(_box).AsTask();
        await Task.Delay(50);
        boxHolder.Dispose();
        (await boxWaiter.WaitAsync(Bound)).Dispose();

        Assert.Single(_metrics.Of("crystal.lock.wait.duration", ("crystal.lock.scope", "key")));
        Assert.Single(_metrics.Of("crystal.lock.wait.duration", ("crystal.lock.scope", "box")));
    }

    [Fact]
    public void AForeignBox_IsRefused_BeforeAnythingIsTaken()
    {
        var foreign = _store.CreateDatabase("other").Boxes.Create("box");
        Assert.Throws<BoxNotInDatabaseException>(() => _db.KeyLocks.Lock(foreign, "k"u8));
        Assert.Throws<BoxNotInDatabaseException>(() => _db.KeyLocks.LockMany([new LockKey(_box, "a"u8.ToArray()), new LockKey(foreign, "b"u8.ToArray())]));
        using (_db.KeyLocks.Lock(_box, "a"u8)) // nothing was left held
        {
        }
    }

    [Fact]
    public void TheStripeCount_IsTheStoresOption_AndAtLeastOne()
    {
        Assert.Equal(64, _db.KeyLocks.StripeCount);
        using var dir = new TempStoreDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => CrystalStore.Open(dir.Path, new CrystalOptions { KeyLockStripes = 0 }));
    }
}
