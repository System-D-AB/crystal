// Spec 003 R1.6, R6.5; addendum 2026-10-10 B2 — one gate per store, shut whenever the node is not the primary, so a
// standby, a demoted or a fenced node accepts no local write to a replicated database. Replication's own applier writes
// do not pass through it (WalAccess.ApplyRaw), and a database the store does not replicate is never gated.

using System.Threading;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Whether local writes to the store's replicated databases are refused, and why (003 addendum B2). A local write is
/// admitted lock-free and counted until it ends. Shutting the gate is two steps: <see cref="Set"/> marks it shut (under
/// the epoch's lock, so the gate always matches the role), and <see cref="WaitForAdmittedWrites"/> waits for the writes
/// already admitted, called after that lock is released (spec 011 R5.1: an admitted write may be waiting on a lock a
/// promotion holds while it waits for the epoch's lock). A demotion or a fence returns only after both, so no local
/// write lands after it (003 review, race windows).
/// </summary>
internal sealed class ReplicationWriteGate
{
    private const string SteppingDown = "a primary handing over to a standby";

    private string? _refusedAs;
    private int _inFlight;
    private int _steppingDown;

    /// <summary>
    /// Why local writes are refused now: the node's role (<c>a standby</c>, <c>fenced</c>), or a primary handing over
    /// (spec 011 R14.1); null while writes are accepted.
    /// </summary>
    public string? RefusedAs => Volatile.Read(ref _refusedAs) ?? (Volatile.Read(ref _steppingDown) == 1 ? SteppingDown : null);

    /// <summary>The role's refusal alone (<c>a standby</c>, <c>fenced</c>), or null on a primary, handing over or not.</summary>
    public string? RefusedByRole => Volatile.Read(ref _refusedAs);

    /// <summary>
    /// Refuses local writes while the node stays primary and keeps serving (spec 011 R14.1). False if a step-down is
    /// already running. Writes admitted before it are waited for with <see cref="WaitForAdmittedWrites"/>.
    /// </summary>
    public bool BeginStepDown() => Interlocked.CompareExchange(ref _steppingDown, 1, 0) == 0;

    /// <summary>Ends a step-down: writes are accepted again if the role allows them (spec 011 R14.3).</summary>
    public void EndStepDown() => Interlocked.Exchange(ref _steppingDown, 0);

    /// <summary>Local writes admitted and not yet ended (tests).</summary>
    internal int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Admits a local write through <paramref name="gate"/> (none: always admitted). On refusal nothing is held and
    /// <paramref name="refusedAs"/> names the role; otherwise the returned admission must be disposed when the write ends.
    /// </summary>
    public static Admission Admit(ReplicationWriteGate? gate, out string? refusedAs)
    {
        refusedAs = null;
        if (gate is null)
        {
            return default;
        }

        // Count first, then read the role; Set writes the role, then reads the count. Both are full fences, so either
        // this write sees the gate shut, or Set sees it counted and waits for it.
        Interlocked.Increment(ref gate._inFlight);
        refusedAs = gate.RefusedAs;
        if (refusedAs is not null)
        {
            Interlocked.Decrement(ref gate._inFlight);
            return default;
        }

        return new Admission(gate);
    }

    /// <summary>
    /// Opens the gate (primary) or shuts it, naming the role (standby, fenced). Never waits: the caller holds the epoch's
    /// lock. Writes admitted before it may still be running; <see cref="WaitForAdmittedWrites"/> waits for them.
    /// </summary>
    public void Set(ReplicationRole role) =>
        Interlocked.Exchange(ref _refusedAs, role switch
        {
            ReplicationRole.Primary => null,
            ReplicationRole.Standby => "a standby",
            _ => "fenced",
        });

    /// <summary>
    /// Returns when every write admitted before the gate was shut has ended. Blocks the calling thread (a spin, then
    /// short sleeps). Must not be called while holding a lock an admitted write may need, nor by a thread holding an
    /// admission.
    /// </summary>
    public void WaitForAdmittedWrites()
    {
        var spin = default(SpinWait);
        while (Volatile.Read(ref _inFlight) != 0)
        {
            spin.SpinOnce();
        }
    }

    /// <summary>One admitted local write; disposing it ends the write.</summary>
    internal readonly struct Admission(ReplicationWriteGate? gate) : System.IDisposable
    {
        public void Dispose()
        {
            if (gate is not null)
            {
                Interlocked.Decrement(ref gate._inFlight);
            }
        }
    }
}
