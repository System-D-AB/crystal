// Spec 003 R1.6, R6.5; addendum 2026-10-10 B2 — one gate per store, shut whenever the node is not the primary, so a
// standby, a demoted or a fenced node accepts no local write to a replicated database. Replication's own applier writes
// do not pass through it (WalAccess.ApplyRaw), and a database the store does not replicate is never gated.

using System.Threading;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Whether local writes to the store's replicated databases are refused, and why (003 addendum B2). A local write is
/// admitted lock-free and counted until it ends; shutting the gate waits for the writes already admitted, so once a
/// demotion or a fence returns no local write lands after it (003 review, race windows).
/// </summary>
internal sealed class ReplicationWriteGate
{
    private string? _refusedAs;
    private int _inFlight;

    /// <summary>The node's role while writes are refused (<c>standby</c> or <c>fenced</c>), or null while it is primary.</summary>
    public string? RefusedAs => Volatile.Read(ref _refusedAs);

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
        refusedAs = Volatile.Read(ref gate._refusedAs);
        if (refusedAs is not null)
        {
            Interlocked.Decrement(ref gate._inFlight);
            return default;
        }

        return new Admission(gate);
    }

    /// <summary>
    /// Opens the gate (primary) or shuts it, naming the role (standby, fenced). Shutting it returns only when every
    /// write admitted before has ended. It must not be called by a thread holding an admission.
    /// </summary>
    public void Set(ReplicationRole role)
    {
        Interlocked.Exchange(ref _refusedAs, role switch
        {
            ReplicationRole.Primary => null,
            ReplicationRole.Standby => "standby",
            _ => "fenced",
        });

        if (role == ReplicationRole.Primary)
        {
            return;
        }

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
