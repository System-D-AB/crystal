// Ported from Highway: src/Highway.Server/Storage/StripedLock.cs
// Adapted for Crystal: keyed by box id, ascending LockMany/LockAll, test hooks (spec 002 R5, design § 4).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SystemD.Crystal;

/// <summary>
/// Striped per-box locking for a product's read-decide-write sequences (spec 002 R5, design § 4). Stripes are taken
/// in ascending order, so two multi-box lockers cannot deadlock. Locks are <b>not re-entrant</b> (R5.4): a thread that
/// locks a box it already holds waits like any other. A lock is released through the object it returns, on whichever
/// thread disposes it, so it may be held across an <c>await</c>.
/// <para>The async forms (spec 012 R3.4) wait without blocking a thread and honour cancellation. Box locks are
/// <b>independent of <see cref="KeyLocks"/></b>: a box lock does not exclude key locks in that box (R3.5), and
/// <see cref="LockAll"/> takes every box stripe and no key stripe.</para>
/// </summary>
public sealed class BoxLocks
{
    private const int StripeCount = 256;
    private readonly SemaphoreSlim[] _stripes = new SemaphoreSlim[StripeCount];

    /// <summary>
    /// Test hooks (R5.5): called with the stripe index just before a thread waits for it, and just before a held
    /// stripe is released. Null in production.
    /// </summary>
    internal Action<int>? OnStripeWaiting { get; set; }

    /// <inheritdoc cref="OnStripeWaiting"/>
    internal Action<int>? OnStripeReleasing { get; set; }

    /// <summary>
    /// Test hook (null in production, spec 007 R2.3): called when a stripe was found held and the thread is about to
    /// block for it, so a test can release the holder knowing the wait is real.
    /// </summary>
    internal Action<int>? OnStripeContended { get; set; }

    private readonly Observability.CrystalMetrics? _metrics;

    internal BoxLocks(Observability.CrystalMetrics? metrics = null)
    {
        _metrics = metrics;
        for (var i = 0; i < StripeCount; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    private static int StripeOf(uint boxId) => (int)(boxId % StripeCount);

    /// <summary>Acquires the lock for <paramref name="box"/>. Dispose the result to release it.</summary>
    public IDisposable Lock(Box box) => Take([StripeOf(box.Id)]);

    /// <summary>
    /// Acquires the locks for all distinct stripes of <paramref name="boxes"/>, in ascending stripe order (R5.2). Two
    /// boxes sharing a stripe take it once.
    /// </summary>
    public IDisposable LockMany(IEnumerable<Box> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);

        var stripes = new SortedSet<int>();
        foreach (var box in boxes)
        {
            stripes.Add(StripeOf(box.Id));
        }

        return Take([.. stripes]);
    }

    /// <summary>Acquires every stripe, in the same ascending order as <see cref="LockMany"/> (R5.3).</summary>
    public IDisposable LockAll()
    {
        _metrics?.LockAll();
        var all = new int[StripeCount];
        for (var i = 0; i < StripeCount; i++)
        {
            all[i] = i;
        }

        return Take(all);
    }

    /// <summary>
    /// Acquires the lock for <paramref name="box"/> without blocking a thread (spec 012 R3.4). Dispose the result, or
    /// <c>await using</c> it, to release it.
    /// </summary>
    public ValueTask<LockLease> LockAsync(Box box, CancellationToken cancellationToken = default) =>
        TakeAsync([StripeOf(box.Id)], cancellationToken);

    /// <summary>
    /// Acquires the locks for all distinct stripes of <paramref name="boxes"/> without blocking a thread, in ascending
    /// stripe order (spec 012 R3.4). A cancellation or failure releases every stripe already taken.
    /// </summary>
    public ValueTask<LockLease> LockManyAsync(IEnumerable<Box> boxes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var stripes = new SortedSet<int>();
        foreach (var box in boxes)
        {
            stripes.Add(StripeOf(box.Id));
        }

        return TakeAsync([.. stripes], cancellationToken);
    }

    private async ValueTask<LockLease> TakeAsync(int[] ascendingStripes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); // a cancelled caller takes nothing, contended or not
        var taken = new List<int>(ascendingStripes.Length);
        try
        {
            foreach (var stripe in ascendingStripes)
            {
                OnStripeWaiting?.Invoke(stripe);
                if (!_stripes[stripe].Wait(0))
                {
                    var start = _metrics?.Start() ?? 0;
                    OnStripeContended?.Invoke(stripe);
                    await _stripes[stripe].WaitAsync(cancellationToken).ConfigureAwait(false);
                    _metrics?.LockWait(start, Observability.CrystalMetrics.BoxScope);
                }

                taken.Add(stripe);
            }
        }
        catch
        {
            for (var i = taken.Count - 1; i >= 0; i--)
            {
                _stripes[taken[i]].Release();
            }

            throw;
        }

        var lease = new Lease(this, taken);
        return new LockLease(lease.Dispose);
    }

    private Lease Take(int[] ascendingStripes)
    {
        var taken = new List<int>(ascendingStripes.Length);
        try
        {
            foreach (var stripe in ascendingStripes)
            {
                OnStripeWaiting?.Invoke(stripe);
                if (!_stripes[stripe].Wait(0))
                {
                    // Contended: only now is the wait timed (spec 007 R2.3), so an uncontended lock reads no clock.
                    var start = _metrics?.Start() ?? 0;
                    OnStripeContended?.Invoke(stripe);
                    _stripes[stripe].Wait();
                    _metrics?.LockWait(start, Observability.CrystalMetrics.BoxScope);
                }

                taken.Add(stripe);
            }
        }
        catch
        {
            foreach (var stripe in taken)
            {
                _stripes[stripe].Release();
            }

            throw;
        }

        return new Lease(this, taken);
    }

    private void Release(int stripe)
    {
        OnStripeReleasing?.Invoke(stripe);
        _stripes[stripe].Release();
    }

    private sealed class Lease(BoxLocks owner, List<int> stripes) : IDisposable
    {
        private List<int>? _stripes = stripes;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _stripes, null);
            if (held is null)
            {
                return;
            }

            for (var i = held.Count - 1; i >= 0; i--)
            {
                owner.Release(held[i]);
            }
        }
    }
}
