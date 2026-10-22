using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal.Replication.Resp;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Replication;

/// <summary>
/// The standby side of one replication connection (spec 003 R10, design § 8, § 9): over a <see cref="Stream"/> it
/// sends <c>HELLO</c>, answers the challenge with <c>AUTH</c>, then sends requests and reads replies, one at a time.
/// Transport-agnostic, so the follower wraps it over a TCP (optionally TLS) stream and tests drive it over an
/// in-memory stream against a <see cref="ReplicationConnection"/>.
/// </summary>
internal sealed class ReplicationClient
{
    private readonly Stream _stream;
    private readonly string _nodeId;
    private readonly string _secret;

    private byte[] _buffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;

    public ReplicationClient(Stream stream, string nodeId, string secret, Action? onFrameRefused = null, TimeSpan? requestTimeout = null, Action? onReply = null)
    {
        _requestTimeout = requestTimeout;
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        _onFrameRefused = onFrameRefused;
        _onReply = onReply;
    }

    private readonly Action? _onFrameRefused;

    // Spec 013 R8: called with every reply read, so a follower knows when it last heard from its primary.
    private readonly Action? _onReply;

    // The most any one reply is waited for (review 2026-10-13, S4); null: as long as the caller's token allows.
    private readonly TimeSpan? _requestTimeout;

    /// <summary>The primary's node id, learned from the <c>HELLO</c> reply.</summary>
    public string PrimaryNodeId { get; private set; } = "unknown";

    /// <summary>The primary's role, from the <c>HELLO</c> reply.</summary>
    public string PrimaryRole { get; private set; } = "unknown";

    /// <summary>The primary's epoch, from the <c>HELLO</c> reply.</summary>
    public long PrimaryEpoch { get; private set; }

    /// <summary>
    /// Completes the handshake: <c>HELLO</c> (stating this node's role/epoch/format), then <c>AUTH</c> with the HMAC
    /// response to the primary's nonce (R10.5). Throws <see cref="ReplicationProtocolException"/> on a version or
    /// format refusal, a failed AUTH, or an unexpected reply.
    /// </summary>
    public async Task HandshakeAsync(string role, long epoch, CancellationToken ct)
    {
        var timeout = _requestTimeout ?? Timeout.InfiniteTimeSpan;
        using var deadline = _requestTimeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline?.CancelAfter(timeout);
        try
        {
            await HandshakeCoreAsync(role, epoch, deadline?.Token ?? ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline is not null && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ReplicationConnectionLostException($"no handshake reply from the primary within {timeout}");
        }
    }

    private async Task HandshakeCoreAsync(string role, long epoch, CancellationToken ct)
    {
        // HELLO <protocolVersion> <nodeId> <role> <epoch> <format> <layout>
        await SendAsync(
            [
                Bytes(ReplicationProtocol.Hello),
                Bytes(ReplicationProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Bytes(_nodeId),
                Bytes(role),
                Bytes(epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Bytes(ReplicationProtocol.Format),
                Bytes(ReplicationProtocol.Layout),
            ],
            ct).ConfigureAwait(false);

        var hello = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (hello.Type == RespType.Error)
        {
            throw new ReplicationProtocolException(hello.Text ?? "HELLO refused");
        }

        if (hello.Type != RespType.Array || hello.Items is not { Count: 6 } fields)
        {
            throw new ReplicationProtocolException("HELLO reply was not a 6-element array");
        }

        PrimaryNodeId = Text.UTF8.GetString(fields[1].Bytes ?? []);
        if (!ReplicationProtocol.IsValidNodeId(PrimaryNodeId))
        {
            throw new ReplicationProtocolException($"{ReplicationProtocol.ErrAuth} the peer stated no valid node id");
        }

        // A peer stating this node's own id is refused before anything else (spec 011 R1.3): it can only be this node's
        // own listener, reached through a relay that reflects this node's answers back to it.
        if (string.Equals(PrimaryNodeId, _nodeId, StringComparison.Ordinal))
        {
            throw new ReplicationProtocolException(
                $"{ReplicationProtocol.ErrAuth} the peer states this node's own id ({_nodeId}); nothing is taken from it");
        }

        PrimaryRole = Text.UTF8.GetString(fields[2].Bytes ?? []);
        PrimaryEpoch = fields[3].Integer;
        var nonceHex = Text.UTF8.GetString(fields[5].Bytes ?? []);
        var nonce = Convert.FromHexString(nonceHex);

        // AUTH <hmacHex> <standbyNonceHex>: this node's answer to the primary's challenge, and its own challenge to the
        // primary, which must answer with its proof. Authentication goes both ways: a standby never takes pages, epochs
        // or snapshots from an endpoint that cannot show it holds the secret (003 should-fix 4).
        var myNonce = ReplicationProtocol.NewNonce();
        var response = ReplicationProtocol.ClientAnswer(_secret, nonce, myNonce, _nodeId, PrimaryNodeId);
        await SendAsync([Bytes(ReplicationProtocol.Auth), Bytes(response), Bytes(Convert.ToHexStringLower(myNonce))], ct).ConfigureAwait(false);

        var auth = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (auth.Type == RespType.Error)
        {
            throw new ReplicationProtocolException(auth.Text ?? "AUTH refused");
        }

        var expected = ReplicationProtocol.ServerProof(_secret, nonce, myNonce, _nodeId, PrimaryNodeId);
        var proof = auth.Type == RespType.BulkString ? Text.ASCII.GetString(auth.Bytes ?? []) : string.Empty;
        if (!ReplicationProtocol.ResponsesEqual(expected, proof))
        {
            throw new ReplicationProtocolException(
                $"{ReplicationProtocol.ErrAuth} the peer at this endpoint did not prove it holds the replication secret; nothing is taken from it");
        }
    }

    /// <summary>Sends a request (an array of bulk-string arguments) and reads the one reply (design § 8.1).</summary>
    public Task<RespValue> RequestAsync(IReadOnlyList<byte[]> args, CancellationToken ct) => RequestAsync(args, ct, extraWait: TimeSpan.Zero);

    /// <summary>
    /// Sends a request and reads its reply, allowing <paramref name="extraWait"/> beyond the request timeout (a PULL that
    /// waits on the primary). No reply in time ends the exchange as a lost connection (review 2026-10-13, S4).
    /// </summary>
    public async Task<RespValue> RequestAsync(IReadOnlyList<byte[]> args, CancellationToken ct, TimeSpan extraWait)
    {
        var timeout = _requestTimeout ?? Timeout.InfiniteTimeSpan;
        using var deadline = _requestTimeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline?.CancelAfter(timeout + extraWait);
        var token = deadline?.Token ?? ct;
        try
        {
            await SendAsync(args, token).ConfigureAwait(false);
            return await ReadReplyAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline is not null && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ReplicationConnectionLostException($"no reply from the primary within {timeout + extraWait}");
        }
    }

    private async Task SendAsync(IReadOnlyList<byte[]> args, CancellationToken ct)
    {
        var frame = new RespWriter().Request(args).ToArray();
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<RespValue> ReadReplyAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_bufferEnd > _bufferStart)
            {
                var sequence = new ReadOnlySequence<byte>(_buffer, _bufferStart, _bufferEnd - _bufferStart);
                // Bound the reply frame (R10.8): a hostile or buggy primary that declares a huge bulk length or array
                // count is refused, not allocated. The reader rejects any declared length above the cap.
                var status = RespReader.TryReadReply(sequence, ReplicationProtocol.ReplyFrameCap, out var value, out var consumed, out var error);
                if (status == RespReadStatus.Complete)
                {
                    var offset = (int)sequence.Slice(sequence.Start, consumed).Length;
                    _bufferStart += offset;
                    if (_bufferStart == _bufferEnd)
                    {
                        _bufferStart = _bufferEnd = 0;
                    }

                    _onReply?.Invoke();
                    return value!;
                }

                if (status == RespReadStatus.ProtocolError)
                {
                    if (error is not null && error.Contains("byte", StringComparison.Ordinal))
                    {
                        _onFrameRefused?.Invoke(); // a reply over the cap (spec 011 R4.3)
                    }

                    throw new ReplicationProtocolException(error ?? "malformed reply from the primary");
                }
            }

            if (_bufferStart > 0)
            {
                Array.Copy(_buffer, _bufferStart, _buffer, 0, _bufferEnd - _bufferStart);
                _bufferEnd -= _bufferStart;
                _bufferStart = 0;
            }

            if (_bufferEnd == _buffer.Length)
            {
                // Grow toward the reply cap, never past it: a frame that cannot fit under the cap is a protocol error
                // the reader will have already rejected, so the buffer never exceeds the cap plus one grow step.
                if (_buffer.Length >= ReplicationProtocol.ReplyFrameCap)
                {
                    _onFrameRefused?.Invoke(); // spec 011 R4.3
                    throw new ReplicationProtocolException(
                        $"the primary's reply exceeds the {ReplicationProtocol.ReplyFrameCap}-byte cap (R10.8)");
                }

                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, ReplicationProtocol.ReplyFrameCap + (64 * 1024)));
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_bufferEnd), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ReplicationConnectionLostException("the primary closed the connection");
            }

            _bufferEnd += read;
        }
    }

    private static byte[] Bytes(string s) => Text.UTF8.GetBytes(s);
}

/// <summary>A replication protocol failure a standby cannot recover from on this connection (spec 003 R10).</summary>
internal class ReplicationProtocolException(string message) : Exception(message);

/// <summary>The connection ended mid-exchange (spec 011 R3.3: a snapshot download reconnects and resumes).</summary>
internal sealed class ReplicationConnectionLostException(string message) : ReplicationProtocolException(message);
