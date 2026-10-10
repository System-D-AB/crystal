using System;
using System.Collections.Generic;
using System.Threading;

namespace SystemD.Crystal.Tests.Support;

/// <summary>
/// A clock that moves only when a test calls <see cref="Advance"/> (spec 004 R2.6). Timers created through it fire on
/// the advancing thread, in due order, so a test of a timer is deterministic and never waits.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves time forward, firing every timer that falls due on the way, each at its due time.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_sync)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_sync)
            {
                foreach (var timer in _timers)
                {
                    if (timer.Due is { } due && due <= target && (next is null || due < next.Due))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    Interlocked.Add(ref _ticks, (target - _now).Ticks);
                    _now = target;
                    return;
                }

                Interlocked.Add(ref _ticks, (next.Due!.Value - _now).Ticks);
                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._sync)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._sync)
            {
                Due = null;
                clock._timers.Remove(this);
            }
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
