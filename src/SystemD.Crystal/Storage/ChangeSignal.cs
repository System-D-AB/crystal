// Spec 003 R3.4 (should-fix 5 of the 2026-10-10 review) — wakes every pull waiting on the primary when anything it
// waits for changes: a commit, the database set, the epoch or the role. A counter, not a semaphore: a single release
// woke one waiter of several, and the others slept out their wait with data ready.
// Spec 014 R2.3, R5 — awaitable and coalescing: a waiting pull holds no thread, and however many commits land, a
// waiting pull is woken once; a commit pays an interlocked increment and, when a pull is waiting, one completion.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A change counter waiters can await (spec 003 R3.4, spec 014 R2.3, R5). A waiter takes the <see cref="Mark"/> before it
/// looks for work, then waits for the counter to move past it, so a change between the look and the wait is never lost.
/// Every waiter wakes on the first change after its mark; later changes before it re-arms complete nothing. Changes of
/// role, epoch or database set are urgent and also move <see cref="UrgentMark"/>, which a pull pacing itself (the gather
/// window) waits on alone, so a commit never cuts its pacing short and an urgent change always does.
/// </summary>
internal sealed class ChangeSignal
{
    private readonly Channel _any = new();
    private readonly Channel _urgent = new();

    /// <summary>The current count of changes of any kind, taken before looking for work.</summary>
    public long Mark => _any.Count;

    /// <summary>The current count of urgent changes (role, epoch, database set).</summary>
    public long UrgentMark => _urgent.Count;

    /// <summary>How many times a waiter was woken (tests of coalescing, spec 014 R2.3).</summary>
    internal long Wakes => _any.Wakes;

    /// <summary>
    /// Test hook: runs inside <see cref="Signal"/> after the change is counted and before any waiter is completed, so a
    /// test can hold a commit at that point (playbook § 4: interleavings by hooks). Null in production.
    /// </summary>
    internal Action? AfterCounted
    {
        set => _any.AfterCounted = value;
    }

    /// <summary>Records a commit and wakes every waiter for any change. No lock, never waits: called on the committing thread.</summary>
    public void Signal() => _any.Signal();

    /// <summary>Records a change of role, epoch or database set, and wakes every waiter.</summary>
    public void SignalUrgent()
    {
        _urgent.Signal();
        _any.Signal();
    }

    /// <summary>
    /// Waits, without holding a thread, until any change after <paramref name="mark"/>, or <paramref name="timeout"/> passes
    /// on <paramref name="clock"/>. True if a change came.
    /// </summary>
    public ValueTask<bool> WaitForChangeAsync(long mark, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken) =>
        _any.WaitAsync(mark, timeout, clock, cancellationToken);

    /// <summary>
    /// Waits, without holding a thread, until an urgent change after <paramref name="urgentMark"/>, or
    /// <paramref name="timeout"/> passes. True if an urgent change came.
    /// </summary>
    public ValueTask<bool> WaitForUrgentAsync(long urgentMark, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken) =>
        _urgent.WaitAsync(urgentMark, timeout, clock, cancellationToken);

    /// <summary>One counter and the waiters on it.</summary>
    private sealed class Channel
    {
        private long _count;
        private long _wakes;
        private TaskCompletionSource _next = NewWaiter();

        public long Count => Interlocked.Read(ref _count);

        public long Wakes => Interlocked.Read(ref _wakes);

        public Action? AfterCounted { get; set; }

        public void Signal()
        {
            Interlocked.Increment(ref _count);
            AfterCounted?.Invoke();

            // Completed already when nobody has waited since the last wake: a read and a branch. A waiter's continuation
            // never runs here (RunContinuationsAsynchronously), so a commit never serves a pull on its own thread.
            var next = Volatile.Read(ref _next);
            if (!next.Task.IsCompleted && next.TrySetResult())
            {
                Interlocked.Increment(ref _wakes);
            }
        }

        public async ValueTask<bool> WaitAsync(long mark, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
        {
            var started = clock.GetTimestamp();
            while (true)
            {
                // The waiter is read before the counter: a change counted after this read completes `next` (Signal reads
                // _next after its increment), and one counted before it is seen in the counter.
                var next = Volatile.Read(ref _next);
                if (Count != mark)
                {
                    return true; // never re-arms on the way out, so a later commit completes nothing for nobody
                }

                if (next.Task.IsCompleted)
                {
                    // Completed by a change since the last waiter re-armed, and this one is about to wait: re-arm, then
                    // look at the counter again.
                    Interlocked.CompareExchange(ref _next, NewWaiter(), next);
                    continue;
                }

                var left = timeout - clock.GetElapsedTime(started);
                if (left <= TimeSpan.Zero)
                {
                    return false;
                }

                try
                {
                    // A timer, not a clock read (as Monitor.Wait was before 014): Crystal reads time only through its
                    // TimeProvider, which decides how much of the wait is left after an early wake.
                    await next.Task.WaitAsync(left, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return Count != mark;
                }

                // Woken. A change counted before this waiter's mark can complete `next` late, so the counter decides: the
                // loop re-arms and returns at once if it moved past the mark, and otherwise waits out what is left.
            }
        }

        private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
