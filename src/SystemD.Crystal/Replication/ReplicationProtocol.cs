using System;
using System.Security.Cryptography;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Replication;

/// <summary>
/// Constants and small helpers of the replication wire protocol, version 1 (spec 003 R10, design § 8). The commands,
/// error codes and caps are the single reference for both the listener and the client.
/// </summary>
internal static class ReplicationProtocol
{
    /// <summary>The protocol version this build speaks (R10.7). Version 1 is spec 003.</summary>
    public const int Version = 1;

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

    /// <summary>
    /// The HMAC response for a challenge (R10.5): <c>HMAC-SHA256(secret, nonce ‖ requesterNodeId)</c>, as lowercase
    /// hex. The secret never crosses the wire; only this response does.
    /// </summary>
    public static string Response(string secret, byte[] nonce, string requesterNodeId)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(requesterNodeId);

        var key = Text.UTF8.GetBytes(secret);
        var idBytes = Text.UTF8.GetBytes(requesterNodeId);
        var message = new byte[nonce.Length + idBytes.Length];
        nonce.CopyTo(message, 0);
        idBytes.CopyTo(message, nonce.Length);

        var mac = HMACSHA256.HashData(key, message);
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Constant-time comparison of two HMAC hex responses (R10.5); false on any length or content difference.</summary>
    /// <summary>
    /// The primary's proof that it holds the secret too (003 should-fix 4: the standby used to trust any endpoint that
    /// answered): <c>HMAC-SHA256(secret, standbyNonce ‖ primaryNodeId)</c>, sent in reply to an <c>AUTH</c> that carries
    /// the standby's own nonce.
    /// </summary>
    public static string Proof(string secret, byte[] standbyNonce, string primaryNodeId) =>
        Response(secret, standbyNonce, primaryNodeId);

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
