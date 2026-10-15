using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemD.Crystal;

/// <summary>
/// A held box or key lock (spec 012 R3). Dispose it, or <c>await using</c> it, to release every stripe it holds, in
/// reverse order of acquisition; on any thread; disposing it twice releases once.
/// </summary>
public sealed class LockLease : IDisposable, IAsyncDisposable
{
    private Action? _release;

    internal LockLease(Action release) => _release = release;

    /// <summary>Releases the lock.</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();

    /// <summary>Releases the lock; completes at once.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
