using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemD.Crystal.Replication;

/// <summary>One replicated database as the primary states it in <c>DATABASES</c> (spec 003 design § 8.2).</summary>
/// <param name="Uid">The database's stable UID (R1.3) — how the wire names it.</param>
/// <param name="Name">The database's current name on the primary.</param>
/// <param name="Latest">The primary's latest log position in it.</param>
internal readonly record struct ReplicatedDatabase(string Uid, string Name, long Latest);

/// <summary>What the standby does to one database to match the primary's set (spec 003 R1.4, design § 2).</summary>
internal enum ReconcileAction
{
    /// <summary>Present on the primary, absent locally: create it with the primary's UID and bootstrap it (T10).</summary>
    CreateAndBootstrap,

    /// <summary>Present on both under different names: rename the local database to the primary's name.</summary>
    Rename,

    /// <summary>Present locally and replicated, absent from the primary's set: drop it.</summary>
    Drop,

    /// <summary>
    /// The primary's name is taken by one of the standby's own (not replicated) databases: raise an event and do not
    /// follow this database until the product resolves the clash (R1.4). Nothing is changed.
    /// </summary>
    NameClash,
}

/// <summary>One step the standby takes to reconcile its set with the primary's (spec 003 § 2).</summary>
/// <param name="Action">What to do.</param>
/// <param name="Uid">The database's UID (the primary's for a create; the local one otherwise).</param>
/// <param name="FromName">The current local name, for a rename or drop; null for a create.</param>
/// <param name="ToName">The primary's name, for a create or rename; null for a drop.</param>
internal readonly record struct ReconcileStep(ReconcileAction Action, string Uid, string? FromName, string? ToName);

/// <summary>
/// The standby's local view of one database, for reconciliation (spec 003 § 2).
/// </summary>
/// <param name="Uid">Its UID.</param>
/// <param name="Name">Its current name.</param>
/// <param name="Replicated">Whether it is a replicated database (vs one of the standby's own).</param>
internal readonly record struct LocalDatabase(string Uid, string Name, bool Replicated);

/// <summary>
/// Computes what a standby must do to make its registry match the primary's replicated set (spec 003 R1.4,
/// design § 2): create and bootstrap new UIDs, drop replicated UIDs the primary no longer lists, rename to the
/// primary's names, and flag a name clash with one of the standby's own (not replicated) databases. A pure function
/// of the two sets, so it is tested exhaustively without a live store; the follower (T9) applies the steps.
/// </summary>
internal static class DatabaseSetReconcile
{
    public static IReadOnlyList<ReconcileStep> Plan(
        IReadOnlyList<ReplicatedDatabase> primarySet,
        IReadOnlyList<LocalDatabase> local)
    {
        ArgumentNullException.ThrowIfNull(primarySet);
        ArgumentNullException.ThrowIfNull(local);

        var localByUid = local.Where(d => d.Replicated).ToDictionary(d => d.Uid, StringComparer.Ordinal);
        var localNames = local.ToDictionary(d => d.Name, d => d, StringComparer.Ordinal);
        var primaryUids = new HashSet<string>(primarySet.Select(d => d.Uid), StringComparer.Ordinal);

        var steps = new List<ReconcileStep>();

        foreach (var remote in primarySet)
        {
            if (localByUid.TryGetValue(remote.Uid, out var mine))
            {
                // Known replicated database: rename it if the primary's name differs.
                if (!string.Equals(mine.Name, remote.Name, StringComparison.Ordinal))
                {
                    // The target name must not be held by one of the standby's own non-replicated databases.
                    if (localNames.TryGetValue(remote.Name, out var holder) && !holder.Replicated)
                    {
                        steps.Add(new ReconcileStep(ReconcileAction.NameClash, remote.Uid, mine.Name, remote.Name));
                    }
                    else
                    {
                        steps.Add(new ReconcileStep(ReconcileAction.Rename, remote.Uid, mine.Name, remote.Name));
                    }
                }

                continue;
            }

            // A new replicated database. If its name clashes with one of the standby's own, flag it, do not follow.
            if (localNames.TryGetValue(remote.Name, out var local2) && !local2.Replicated)
            {
                steps.Add(new ReconcileStep(ReconcileAction.NameClash, remote.Uid, null, remote.Name));
                continue;
            }

            steps.Add(new ReconcileStep(ReconcileAction.CreateAndBootstrap, remote.Uid, null, remote.Name));
        }

        // Replicated databases the primary no longer lists are dropped.
        foreach (var mine in local.Where(d => d.Replicated))
        {
            if (!primaryUids.Contains(mine.Uid))
            {
                steps.Add(new ReconcileStep(ReconcileAction.Drop, mine.Uid, mine.Name, null));
            }
        }

        return steps;
    }
}
