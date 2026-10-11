// Spec 003 R3.4 (should-fix 5 of the 2026-10-10 review) — wakes every pull waiting on the primary when anything it
// waits for changes: a commit, the database set, the epoch or the role. A counter, not a semaphore: a single release
// woke one waiter of several, and the others slept out their wait with data ready.

using System.Threading;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A change counter waiters can block on (spec 003 R3.4). A waiter takes the <see cref="Mark"/> before it looks for work,
/// then waits for the counter to move past it, so a change between the look and the wait is never lost. Every waiter
/// wakes on every change. Signalling costs one interlocked increment when nobody waits.
/// </summary>
internal sealed class ChangeSignal
{
    private readonly object _gate = new();
    private long _changes;
    private int _waiters;

    /// <summary>The current count of changes, taken before looking for work.</summary>
    public long Mark => Interlocked.Read(ref _changes);

    /// <summary>Records a change and wakes every waiter.</summary>
    public void Signal()
    {
        Interlocked.Increment(ref _changes);
        if (Volatile.Read(ref _waiters) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>
    /// Waits until a change after <paramref name="mark"/>, or <paramref name="milliseconds"/> pass. True if a change came.
    /// </summary>
    public bool WaitForChange(long mark, int milliseconds)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _waiters);
            try
            {
                // One timed wait, no clock read (Crystal reads time only through its TimeProvider): Monitor.Wait does not
                // wake spuriously, and only Signal pulses, after the counter moved, so the check below is exact.
                if (Interlocked.Read(ref _changes) == mark)
                {
                    Monitor.Wait(_gate, milliseconds);
                }

                return Interlocked.Read(ref _changes) != mark;
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }
    }
}
