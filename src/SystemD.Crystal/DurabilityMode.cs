namespace SystemD.Crystal;

/// <summary>
/// When a commit counts as durable (spec 006 R1). Every mode keeps a commit atomic: after any crash a batch is wholly
/// present or wholly absent. The catalogue and the registry are always <see cref="Strict"/>, whatever the mode.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Mode</term><description>A commit returns when — and what it survives</description></listheader>
/// <item><term><see cref="Strict"/></term><description>its batch is in the WAL and the WAL is synced to disk.
/// Survives a process crash, an OS crash and power loss.</description></item>
/// <item><term><see cref="Interval"/></term><description>its batch is in the WAL, handed to the OS; the store syncs
/// the WAL every <see cref="CrystalOptions.SyncInterval"/>. Survives a process crash; an OS crash or power loss
/// can lose at most the last interval and the time to sync the store's databases with interval commits (a starved
/// thread pool, which runs the timer, widens it).</description></item>
/// <item><term><see cref="Buffered"/></term><description>its batch is in the WAL, handed to the OS, and nothing
/// syncs it but a later <see cref="Strict"/> commit, <see cref="CrystalStore.SyncAll"/> or closing. Survives a
/// process crash; no promise for an OS crash or power loss.</description></item>
/// </list>
/// A <see cref="Strict"/> commit syncs the whole WAL of its database, so every earlier commit of that database is
/// durable with it.
/// </remarks>
public enum DurabilityMode
{
    /// <summary>Synced before the commit returns (the default).</summary>
    Strict = 0,

    /// <summary>Synced on the store's timer, every <see cref="CrystalOptions.SyncInterval"/>.</summary>
    Interval = 1,

    /// <summary>Not synced by Crystal on its own.</summary>
    Buffered = 2,
}
