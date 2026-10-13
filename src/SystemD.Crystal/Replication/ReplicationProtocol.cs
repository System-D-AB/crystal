using System;
using System.Security.Cryptography;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Replication;

/// <summary>
/// Constants and small helpers of the replication wire protocol, version 2 (spec 003 R10, design § 8; spec 011 R1). The commands,
/// error codes and caps are the single reference for both the listener and the client.
/// </summary>
internal static class ReplicationProtocol
{
    /// <summary>
    /// The protocol version this build speaks (R10.7). Version 1 was spec 003; version 2 (spec 011 R1.4) changed what the
    /// <c>AUTH</c> MACs cover, so a version-1 peer is refused.
    /// </summary>
    public const int Version = 2;

    /// <summary>The store format stated in <c>HELLO</c> (R8.1).</summary>
    public const string Format = "crystal 1";

    /// <summary>The column-family layout stated in <c>HELLO</c> and <c>SNAPSHOT BEGIN</c> (R8.1).</summary>
    public const string Layout = "default,general,delete_heavy";

    // Commands (design § 8.2).
    public const string Hello = "HELLO";
    public const string Auth = "AUTH";
    public const string Databases = "DATABASES";
    public const string Pull = "PULL";
    public const string Snapshot = "SNAPSHOT";
    public const string Status = "STATUS";

    // Roles on the wire (design § 8.2).
    public const string RolePrimary = "primary";
    public const string RoleStandby = "standby";
    public const string RoleFenced = "fenced";

    // PULL per-entry status (design § 8.2): what the primary says about one database in a pull reply.
    public const string EntryOk = "ok";          // batches follow (possibly none, if caught up)
    public const string EntryGap = "gap";         // the WAL no longer reaches the cursor; re-bootstrap (an int follows)
    public const string EntryDiverged = "diverged"; // the standby has batches the primary never had (R5.2)
    public const string EntryGone = "gone";       // no replicated database with that UID (dropped on the primary)
    public const string EntryOversize = "oversize"; // a batch too large for one reply; re-bootstrap (spec 011 R4.1)

    // Error codes (design § 8.3). The listener writes "-CRYSTAL_<CODE> <detail>".
    public const string ErrVersion = "CRYSTAL_VERSION";
    public const string ErrFormat = "CRYSTAL_FORMAT";
    public const string ErrAuth = "CRYSTAL_AUTH";
    public const string ErrNoAuth = "CRYSTAL_NOAUTH";
    public const string ErrNotPrimary = "CRYSTAL_NOTPRIMARY";
    public const string ErrEpoch = "CRYSTAL_EPOCH";
    public const string ErrSession = "CRYSTAL_SESSION";
    public const string ErrArg = "CRYSTAL_ARG";
    public const string ErrInternal = "CRYSTAL_INTERNAL";

    // Caps (design § 8.1). Before AUTH a frame may not exceed 4 KiB; the connection closes after 10 s without AUTH.
    public const int PreAuthFrameCap = 4 * 1024;
    public static readonly TimeSpan PreAuthTimeout = TimeSpan.FromSeconds(10);

    // After AUTH a request may not exceed 64 KiB; a reply's bulk strings are bounded by the page/chunk asked for.
    public const int RequestCap = 64 * 1024;

    // A reply frame is bounded too (R10.8): a PULL serves at most one page (1 MiB) across all databases, a SNAPSHOT
    // GET one chunk (1 MiB), a SNAPSHOT BEGIN manifest is small. 64 MiB is far above any legitimate reply and far
    // below OOM, so a hostile or buggy primary that declares a huge array count or bulk length is refused, not
    // allocated. The length check in the reader rejects any declared length above this — which also bounds an array's
    // element count, since each element is at least one byte on the wire.
    public const int ReplyFrameCap = 64 * 1024 * 1024;

    /// <summary>
    /// The largest batch a primary ships in a page (spec 011 R4.1): half the reply cap. With the page clamped to a quarter
    /// of it (<see cref="MaxPageBytes"/>), a reply (the page, one batch crossing it, and the entries' framing) always fits
    /// under <see cref="ReplyFrameCap"/>. A larger batch is reported <c>oversize</c>, and the standby re-bootstraps that
    /// database, whose snapshot carries it.
    /// </summary>
    public const int MaxShippableBatch = ReplyFrameCap / 2;

    /// <summary>The most a primary puts in one reply's page, whatever <c>PageBytes</c> says (spec 011 R4.1).</summary>
    public const int MaxPageBytes = ReplyFrameCap / 4;

    /// <summary>
    /// How much longer than any other reply a standby waits for <c>SNAPSHOT BEGIN</c> (re-review 2026-10-13, N1): before
    /// answering, the primary takes a checkpoint and reads every byte of it for the CRC-32Cs, minutes for a large
    /// database on a slow disk. A hung primary therefore holds one bootstrap slot this long, not forever.
    /// </summary>
    public static readonly TimeSpan SnapshotBeginWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The request cap after AUTH for a store that can open <paramref name="maxOpenDatabases"/> databases (spec 011 R4.2):
    /// large enough for a PULL naming every one of them (a UID of 36 bytes, a position of at most 20, and RESP framing,
    /// within 128 bytes a pair), and never below <see cref="RequestCap"/>.
    /// </summary>
    public static int RequestCapFor(int maxOpenDatabases) => Math.Max(RequestCap, 4096 + (128 * Math.Max(0, maxOpenDatabases)));

    /// <summary>The nonce length in bytes (design § 8.2: 32 random bytes, sent hex).</summary>
    public const int NonceBytes = 32;

    /// <summary>
    /// The highest epoch a peer may state (2^62). Epochs grow by one per promotion, so no real node reaches it; a
    /// higher value is refused, so no peer can push this node to an epoch it cannot promote past.
    /// </summary>
    public const long MaxEpoch = 1L << 62;

    /// <summary>A fresh random nonce (R10.5), valid for one <c>AUTH</c> on its connection only.</summary>
    public static byte[] NewNonce()
    {
        var nonce = new byte[NonceBytes];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    /// <summary>The label that starts a standby's (client's) answer (spec 011 R1.1).</summary>
    public const string ClientLabel = "crystal-auth-client";

    /// <summary>The label that starts a primary's (server's) proof (spec 011 R1.1).</summary>
    public const string ServerLabel = "crystal-auth-server";

    /// <summary>The longest node id either side accepts, in UTF-8 bytes (spec 011 R1.2: ids are length-prefixed).</summary>
    public const int MaxNodeIdBytes = 256;

    /// <summary>
    /// The client's answer to the server's challenge (spec 003 R10.5, spec 011 R1.1–R1.2), as lowercase hex:
    /// <c>HMAC-SHA256(secret, "crystal-auth-client" ‖ 0 ‖ serverNonce ‖ clientNonce ‖ id(client) ‖ id(server))</c>, where
    /// <c>id(x)</c> is a 2-byte big-endian length and the UTF-8 bytes. Bound to both nonces and both node ids, and
    /// labelled with its direction, so it can never stand for the server's proof: relaying one node's answer back to it
    /// (a reflection) proves nothing. The secret never crosses the wire.
    /// </summary>
    public static string ClientAnswer(string secret, byte[] serverNonce, byte[] clientNonce, string clientNodeId, string serverNodeId) =>
        Mac(secret, ClientLabel, serverNonce, clientNonce, clientNodeId, serverNodeId);

    /// <summary>
    /// The server's proof that it holds the secret too (003 should-fix 4, spec 011 R1.1–R1.2): the same inputs as
    /// <see cref="ClientAnswer"/> under the server's label, sent in reply to a correct <c>AUTH</c>.
    /// </summary>
    public static string ServerProof(string secret, byte[] serverNonce, byte[] clientNonce, string clientNodeId, string serverNodeId) =>
        Mac(secret, ServerLabel, serverNonce, clientNonce, clientNodeId, serverNodeId);

    /// <summary>Whether <paramref name="nodeId"/> may be used in a handshake: present, and at most <see cref="MaxNodeIdBytes"/>.</summary>
    public static bool IsValidNodeId(string? nodeId) =>
        !string.IsNullOrEmpty(nodeId) && Text.UTF8.GetByteCount(nodeId) <= MaxNodeIdBytes;

    private static string Mac(string secret, string label, byte[] serverNonce, byte[] clientNonce, string clientNodeId, string serverNodeId)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(serverNonce);
        ArgumentNullException.ThrowIfNull(clientNonce);
        if (!IsValidNodeId(clientNodeId) || !IsValidNodeId(serverNodeId))
        {
            throw new ArgumentException($"node ids must be 1 to {MaxNodeIdBytes} UTF-8 bytes");
        }

        var labelBytes = Text.UTF8.GetBytes(label);
        var client = Text.UTF8.GetBytes(clientNodeId);
        var server = Text.UTF8.GetBytes(serverNodeId);
        var message = new byte[labelBytes.Length + 1 + serverNonce.Length + clientNonce.Length + 2 + client.Length + 2 + server.Length];
        var at = 0;
        labelBytes.CopyTo(message, at);
        at += labelBytes.Length;
        message[at++] = 0;
        serverNonce.CopyTo(message, at);
        at += serverNonce.Length;
        clientNonce.CopyTo(message, at);
        at += clientNonce.Length;
        at = WriteId(message, at, client);
        WriteId(message, at, server);

        var mac = HMACSHA256.HashData(Text.UTF8.GetBytes(secret), message);
        return Convert.ToHexStringLower(mac);

        static int WriteId(byte[] into, int at, byte[] id)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(into.AsSpan(at), (ushort)id.Length);
            id.CopyTo(into, at + 2);
            return at + 2 + id.Length;
        }
    }

    /// <summary>Constant-time comparison of two HMAC hex responses (R10.5); false on any length or content difference.</summary>
    public static bool ResponsesEqual(string expected, string actual)
    {
        if (expected is null || actual is null)
        {
            return false;
        }

        var a = Text.ASCII.GetBytes(expected);
        var b = Text.ASCII.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
