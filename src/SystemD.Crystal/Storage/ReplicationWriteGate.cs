// Spec 003 R1.6, R6.5; addendum 2026-10-10 B2 — one gate per store, shut whenever the node is not the primary, so a
// standby, a demoted or a fenced node accepts no local write to a replicated database. Replication's own applier writes
// do not pass through it (WalAccess.ApplyRaw), and a database the store does not replicate is never gated.

using System.Threading;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Whether local writes to the store's replicated databases are refused, and why (003 addendum B2). Read lock-free on
/// every write; set by the store's replication control on every role change.
/// </summary>
internal sealed class ReplicationWriteGate
{
    private string? _refusedAs;

    /// <summary>The node's role while writes are refused (<c>standby</c> or <c>fenced</c>), or null while it is primary.</summary>
    public string? RefusedAs => Volatile.Read(ref _refusedAs);

    /// <summary>Opens the gate (primary) or shuts it, naming the role (standby, fenced).</summary>
    public void Set(ReplicationRole role) =>
        Volatile.Write(ref _refusedAs, role switch
        {
            ReplicationRole.Primary => null,
            ReplicationRole.Standby => "standby",
            _ => "fenced",
        });
}
