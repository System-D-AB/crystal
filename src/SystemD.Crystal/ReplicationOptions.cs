using System;
using System.Security.Cryptography.X509Certificates;

namespace SystemD.Crystal;

/// <summary>
/// Replication configuration for a store (spec 003 design § 10). Supplied as
/// <see cref="CrystalOptions.Replication"/>. A store with no replication options never listens, follows, retains its
/// WAL for replication, or allocates a tail; the protocol, follower and listener arrive in later tasks of spec 003.
///
/// <para>Spec 003 is built in stages. This record is the full option surface the design names; the fields the current
/// tasks use are honoured now (WAL retention, T2), and the rest bind behaviour as later tasks land. The defaults are
/// the decisions table of the requirements.</para>
/// </summary>
public sealed class ReplicationOptions
{
    /// <summary>Where the node's replication listener binds, e.g. <c>"0.0.0.0:7700"</c>. A standby listens too, to be promotable.</summary>
    public string? ListenEndpoint { get; init; }

    /// <summary>The store's shared replication secret, used by the HMAC challenge (R10.5). Never logged.</summary>
    public required string Secret { get; init; }

    /// <summary>An optional TLS certificate for the listener (R10.6).</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Whether a connecting client must present a certificate validated against the configured trust (R10.6).</summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>A slot's lag cap in sequences (R2.3); past it a standby is dropped.</summary>
    public long LagCap { get; init; } = 100_000;

    /// <summary>How long with no contact before a slot is stale (R2.3).</summary>
    public TimeSpan SlotStaleAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The in-memory tail budget per replicated database (R3.5; decisions table).</summary>
    public long TailBytesPerDatabase { get; init; } = 16L << 20;

    /// <summary>The store-wide in-memory tail budget shared across replicated databases.</summary>
    public long TailBudgetBytes { get; init; } = 256L << 20;

    /// <summary>How long a replicated database retains its WAL by age (R11.1).</summary>
    public TimeSpan WalTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How large a replicated database's retained WAL may grow (R11.1): its share of <see cref="WalBudgetBytes"/>, at
    /// most this and at least 1 MiB, set when the database opens. Zero means no size limit: <see cref="WalTtl"/> alone.
    /// </summary>
    public long WalBytesPerDatabase { get; init; } = 1L << 30;

    /// <summary>
    /// The store-wide WAL retention budget shared equally across replicated databases (R11.1). RocksDB cannot change a
    /// database's limit while it is open, so a share lowered by a later create applies when each database next opens.
    /// </summary>
    public long WalBudgetBytes { get; init; } = 16L << 30;

    /// <summary>The longest a caught-up <c>PULL</c> waits before returning empty (R3.4).</summary>
    public TimeSpan LongPollMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The maximum bytes in one <c>PULL</c> reply (R3.2).</summary>
    public int PageBytes { get; init; } = 1 << 20;
}
