using System.Collections.Generic;

namespace SystemD.Crystal.Replication;

/// <summary>
/// What a replication connection asks of the node behind it (spec 003 design § 8): the node's role and epoch, the
/// shared secret for the HMAC challenge, and the handlers for the authenticated commands. The listener and the
/// per-connection protocol state machine (<see cref="ReplicationConnection"/>) call this; the primary implements it
/// (<c>ReplicationSource</c>, built across later tasks). Keeping the connection behind this interface lets the
/// protocol be tested without a live store.
/// </summary>
internal interface IReplicationSource
{
    /// <summary>This node's id, sent in <c>HELLO</c> and mixed into the challenge.</summary>
    string NodeId { get; }

    /// <summary>This node's current role on the wire: <c>primary</c>, <c>standby</c> or <c>fenced</c>.</summary>
    string Role { get; }

    /// <summary>This node's current epoch (R6).</summary>
    long Epoch { get; }

    /// <summary>The store's shared replication secret, for the HMAC challenge (R10.5). Never logged, never sent.</summary>
    string Secret { get; }

    /// <summary>The structured status as JSON, for the <c>STATUS</c> command (R13.2).</summary>
    string StatusJson();

    /// <summary>
    /// Reports a peer's <c>HELLO</c> (spec 003 R6.3, R6.4): the connection calls this with the peer's epoch, wire role
    /// and node id. A higher epoch is adopted (a primary demotes); an equal epoch from another primary is a conflict
    /// event. Returns nothing — the node's own role/epoch may change as a result, which the next reply reflects.
    /// </summary>
    void ObservePeer(long peerEpoch, string peerRole, string peerNodeId, string? peerEndpoint = null);

    /// <summary>
    /// Handles an authenticated command other than <c>HELLO</c>, <c>AUTH</c> and <c>STATUS</c> (which the connection
    /// handles itself): <c>DATABASES</c>, <c>PULL</c>, <c>SNAPSHOT</c>. The handler writes the reply (or an error) via
    /// <paramref name="reply"/>. <paramref name="peerNodeId"/> is the authenticated standby's node id (from its
    /// <c>HELLO</c>), so a slot can be keyed per standby (R2.3, R9.2). Returns false for an unknown command, so the
    /// connection answers <c>CRYSTAL_ARG</c>.
    /// </summary>
    bool TryHandle(string command, IReadOnlyList<byte[]> args, string peerNodeId, Replication.Resp.RespWriter reply);
}
