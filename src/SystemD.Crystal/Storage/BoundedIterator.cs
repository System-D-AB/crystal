using System;
using System.Collections.Generic;
using RocksDbSharp;
using RocksSnapshot = RocksDbSharp.Snapshot;

namespace SystemD.Crystal.Storage;

/// <summary>
/// A RocksDB iterator that <b>must</b> carry a lower and an upper bound and a limit (spec 001-2 R3.2),
/// and that counts its native steps (R4.4, R6.2). Trap R4.4: each iterator step is its own native call,
/// so a scan's cost is its step count; counting steps lets a cost test see them. The iterate bounds live
/// on the rented <see cref="ReadOptions"/>, so the native iterator stops at the upper bound on its own
/// and never crosses into another box's keys.
/// </summary>
internal sealed class BoundedIterator : IDisposable
{
    private readonly Iterator _iterator;
    private readonly ReadOptions _options;
    private readonly ReadOptionsPool _pool;
    private readonly int _limit;
    private bool _disposed;

    private BoundedIterator(Iterator iterator, ReadOptions options, ReadOptionsPool pool, int limit)
    {
        _iterator = iterator;
        _options = options;
        _pool = pool;
        _limit = limit;
    }

    /// <summary>The number of native iterator steps taken so far (seek counts as one) — R6.2.</summary>
    internal long Steps { get; private set; }

    /// <summary>
    /// Opens a forward scan of <c>[lowerBound, upperBound)</c> on <paramref name="family"/>, pinned to
    /// <paramref name="snapshot"/>, returning at most <paramref name="limit"/> entries. Both bounds are
    /// required; a non-positive limit is refused (R3.2).
    /// </summary>
    internal static BoundedIterator Open(
        RocksDb db,
        ColumnFamilyHandle family,
        ReadOptionsPool pool,
        RocksSnapshot snapshot,
        byte[] lowerBound,
        byte[] upperBound,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(lowerBound);
        ArgumentNullException.ThrowIfNull(upperBound);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A bounded scan needs a positive limit (R3.2).");
        }

        var options = pool.RentScan(lowerBound, upperBound, snapshot);
        var iterator = db.NewIterator(family, options);
        return new BoundedIterator(iterator, options, pool, limit);
    }

    /// <summary>
    /// Yields up to the limit entries from the lower bound upward, in key order. Each <c>Seek</c>/<c>Next</c>
    /// increments <see cref="Steps"/>. The upper bound (on the options) stops the native iterator.
    /// </summary>
    internal IEnumerable<(byte[] Key, byte[] Value)> Scan(byte[] from)
    {
        ArgumentNullException.ThrowIfNull(from);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _iterator.Seek(from);
        Steps++;

        var yielded = 0;
        while (yielded < _limit && _iterator.Valid())
        {
            var key = _iterator.GetKeySpan().ToArray();
            var value = _iterator.GetValueSpan().ToArray();
            yield return (key, value);
            yielded++;

            if (yielded >= _limit)
            {
                break;
            }

            _iterator.Next();
            Steps++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _iterator.Dispose();
        _pool.ReturnScan(_options);
    }
}
