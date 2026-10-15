// Ported from StowDB: src/Crystal/Crystal.Storage/Writes/StripedLock.cs (sorted acquisition, WaitAsync with
// cancellation, release of a partial acquisition, a release object safe to dispose twice).
// Adapted for Crystal: the stripe is a hash of the box id and the key bytes, computed without allocating; box-scoped
// keys; synchronous forms; Crystal's test hooks and lock-wait metric (spec 012 R3).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SystemD.Crystal;

/// <summary>A key to lock: a key inside a box (spec 012 R3).</summary>
/// <param name="Box">The box the key is in.</param>
/// <param name="Key">The key, as the product writes it inside the box.</param>
public readonly record struct LockKey(Box Box, ReadOnlyMemory<byte> Key);

/// <summary>
/// Striped locks on keys, for a product's read-decide-write on one record and the records that depend on it, such as a
/// document and its unique index entries (spec 012 R3). A key's stripe is a hash of its box and its bytes: two keys that
/// share a stripe only serialise, never deadlock. Several keys are taken in ascending stripe order, each stripe once, so
/// two multi-key lockers cannot deadlock.
/// <para><b>Independent of <see cref="BoxLocks"/>:</b> a box lock does not exclude key locks in that box, and a key lock
/// does not exclude a box lock (R3.5). A product that needs both takes both, always in the same order.</para>
/// <para>Like box locks: not re-entrant; released through the object returned, on any thread, so it may be held across
/// an <c>await</c>; never held by Crystal across the product's code. The async forms wait without blocking a thread and
/// honour cancellation; a cancelled multi-key lock releases every stripe it took.</para>
/// <para>Fairness: a stripe released while both synchronous and asynchronous callers wait goes to a synchronous waiter
/// first (<see cref="SemaphoreSlim"/>'s order). A product that mixes heavy synchronous locking with async locking on the
/// same keys should prefer one form.</para>
/// </summary>
public sealed class KeyLocks
{
    private readonly SemaphoreSlim[] _stripes;
    private readonly Action<Box> _ensureOwned;
    private readonly Observability.CrystalMetrics? _metrics;

    internal KeyLocks(int stripeCount, Action<Box> ensureOwned, Observability.CrystalMetrics? metrics = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stripeCount, 1);
        _ensureOwned = ensureOwned;
        _metrics = metrics;
        _stripes = new SemaphoreSlim[stripeCount];
        for (var i = 0; i < stripeCount; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>Test hooks: called with the stripe index just before a lock waits for it, and just before it is released.</summary>
    internal Action<int>? OnStripeWaiting { get; set; }

    /// <inheritdoc cref="OnStripeWaiting"/>
    internal Action<int>? OnStripeReleasing { get; set; }

    /// <summary>Test hook: called when a stripe was found held and the lock is about to wait for it.</summary>
    internal Action<int>? OnStripeContended { get; set; }

    /// <summary>The number of stripes (<see cref="CrystalOptions.KeyLockStripes"/>).</summary>
    public int StripeCount => _stripes.Length;

    /// <summary>Acquires the lock on <paramref name="key"/> in <paramref name="box"/>, blocking. Dispose the result to release it.</summary>
    public LockLease Lock(Box box, ReadOnlySpan<byte> key)
    {
        _ensureOwned(box);
        return Take([StripeOf(box.Id, key)]);
    }

    /// <summary>Acquires the locks on <paramref name="keys"/>, blocking, in ascending stripe order, each stripe once.</summary>
    public LockLease LockMany(IEnumerable<LockKey> keys) => Take(Stripes(keys));

    /// <summary>
    /// Acquires the lock on <paramref name="key"/> in <paramref name="box"/> without blocking a thread. Dispose the
    /// result (or <c>await using</c> it) to release the lock.
    /// </summary>
    public ValueTask<LockLease> LockAsync(Box box, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        _ensureOwned(box);
        return TakeAsync([StripeOf(box.Id, key.Span)], cancellationToken);
    }

    /// <summary>
    /// Acquires the locks on <paramref name="keys"/> without blocking a thread, in ascending stripe order, each stripe
    /// once. A cancellation or failure releases every stripe already taken.
    /// </summary>
    public ValueTask<LockLease> LockManyAsync(IEnumerable<LockKey> keys, CancellationToken cancellationToken = default) =>
        TakeAsync(Stripes(keys), cancellationToken);

    /// <summary>The stripe of a key: FNV-1a over the box id's 4 bytes, big-endian, then the key's bytes (R3.9: no allocation).</summary>
    internal int StripeOf(uint boxId, ReadOnlySpan<byte> key)
    {
        const uint Offset = 2166136261;
        const uint Prime = 16777619;
        var hash = Offset;
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            hash = (hash ^ (byte)(boxId >> shift)) * Prime;
        }

        foreach (var b in key)
        {
            hash = (hash ^ b) * Prime;
        }

        return (int)(hash % (uint)_stripes.Length);
    }

    private int[] Stripes(IEnumerable<LockKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var stripes = new SortedSet<int>();
        foreach (var key in keys)
        {
            _ensureOwned(key.Box);
            stripes.Add(StripeOf(key.Box.Id, key.Key.Span));
        }

        return [.. stripes];
    }

    private LockLease Take(int[] ascendingStripes)
    {
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
                    _stripes[stripe].Wait();
                    _metrics?.LockWait(start, Observability.CrystalMetrics.KeyScope);
                }

                taken.Add(stripe);
            }
        }
        catch
        {
            ReleaseAll(taken);
            throw;
        }

        return Lease(taken);
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
                    // Only a contended stripe is awaited and timed: an uncontended lock reads no clock (R3.8).
                    var start = _metrics?.Start() ?? 0;
                    OnStripeContended?.Invoke(stripe);
                    await _stripes[stripe].WaitAsync(cancellationToken).ConfigureAwait(false);
                    _metrics?.LockWait(start, Observability.CrystalMetrics.KeyScope);
                }

                taken.Add(stripe);
            }
        }
        catch
        {
            ReleaseAll(taken);
            throw;
        }

        return Lease(taken);
    }

    private LockLease Lease(List<int> taken) => new(() =>
    {
        for (var i = taken.Count - 1; i >= 0; i--)
        {
            OnStripeReleasing?.Invoke(taken[i]);
            _stripes[taken[i]].Release();
        }
    });

    private void ReleaseAll(List<int> taken)
    {
        for (var i = taken.Count - 1; i >= 0; i--)
        {
            _stripes[taken[i]].Release();
        }
    }

}
