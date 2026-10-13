// Spec 011 R5.2–R5.4 — replication events are delivered on Crystal's own thread, in the order they occur, never on the
// thread of a follower, a listener connection or a control call. A handler may then call any store.Replication member
// (Status, Promote, Fence, Follow, StopFollowing) without deadlocking on a lock the raising thread holds: the review of
// 2026-10-12 found a handler of a follower-raised event that called Promote waiting for the follower to stop, while the
// follower waited for the handler.

using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SystemD.Crystal;

/// <summary>Delivers replication events to a handler on one background thread, in order (spec 011 R5.2).</summary>
internal sealed class ReplicationEventPump : IDisposable
{
    private static readonly TimeSpan DisposeBound = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<ReplicationEvent> _queue = new();
    private readonly Action<ReplicationEvent> _deliver;
    private readonly ILogger? _logger;
    private readonly Thread _thread;
    private long _pending;

    public ReplicationEventPump(Action<ReplicationEvent> deliver, ILogger? logger)
    {
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "crystal-replication-events" };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="e"/> for delivery; dropped once the pump is disposed.</summary>
    public void Post(ReplicationEvent e)
    {
        Interlocked.Increment(ref _pending);
        try
        {
            _queue.Add(e);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref _pending); // disposed: nobody is listening any more
        }
    }

    /// <summary>
    /// Waits until every event posted so far has been delivered, up to <paramref name="timeout"/> (tests: an event is
    /// asserted after the call that raised it). Returns whether it was. Blocks the calling thread.
    /// </summary>
    internal bool Drain(TimeSpan timeout) => SpinWait.SpinUntil(() => Interlocked.Read(ref _pending) == 0, timeout);

    private void Run()
    {
        foreach (var e in _queue.GetConsumingEnumerable())
        {
            try
            {
                _deliver(e);
            }
            catch (Exception ex)
            {
                // R5.3: one handler's failure never stops the events after it.
                if (_logger is not null)
                {
                    Observability.CrystalLog.ReplicationEventHandlerFailed(_logger, e.Kind.ToString(), ex);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    /// <summary>
    /// Stops the pump after delivering the events already queued, waiting at most 5 s (R5.4). From the pump's own thread
    /// (a handler disposing the store) it does not wait for itself.
    /// </summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(DisposeBound);
        }
    }
}
