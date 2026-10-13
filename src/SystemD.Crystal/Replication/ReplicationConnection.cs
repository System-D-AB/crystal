using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SystemD.Crystal.Replication.Resp;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Replication;

/// <summary>
/// The server side of one replication connection (spec 003 R10, design § 8), transport-agnostic: it drives the
/// <c>HELLO</c> → <c>AUTH</c> gate and then dispatches authenticated commands, over any <see cref="Stream"/>, so the
/// listener wraps it over a TCP (optionally TLS) stream and tests drive it over an in-memory stream.
///
/// <para><b>The gate (R10.5):</b> before <c>AUTH</c> succeeds only <c>HELLO</c> and <c>AUTH</c> are accepted, frames
/// are capped at 4 KiB, and the connection is closed after 10 s without completing <c>AUTH</c>. A wrong HMAC answers
/// <c>-CRYSTAL_AUTH</c> and closes. A nonce is valid for one <c>AUTH</c> on its connection only, so a response
/// replayed on a new connection (whose nonce differs) fails. A malformed frame or an over-cap length closes the
/// connection without answering (the peer cannot be trusted to read a reply, design § 8.1).</para>
/// </summary>
internal sealed class ReplicationConnection
{
    private readonly Stream _stream;
    private readonly IReplicationSource _source;
    private readonly ILogger? _logger;
    private readonly string _remote;

    private byte[] _buffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;

    private bool _authenticated;
    private byte[]? _nonce;              // the challenge for this connection; cleared after one AUTH attempt
    private string _peerNodeId = "unknown";

    // The peer's role and epoch from HELLO, acted on only once AUTH proves it holds the secret (003 addendum B1).
    private string _peerRole = string.Empty;
    private long _peerEpoch;
    private CancellationToken _preAuthDeadline;

    public ReplicationConnection(
        Stream stream, IReplicationSource source, string remote, ILogger? logger, TimeSpan? preAuthTimeout = null,
        int requestCap = ReplicationProtocol.RequestCap, Action? onFrameRefused = null, CancellationToken? preAuthDeadline = null,
        Action? onAuthenticated = null)
    {
        _onAuthenticated = onAuthenticated;
        _givenPreAuthDeadline = preAuthDeadline;
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _remote = remote;
        _logger = logger;
        _preAuthTimeout = preAuthTimeout ?? ReplicationProtocol.PreAuthTimeout;
        _requestCap = requestCap;
        _onFrameRefused = onFrameRefused;
    }

    private readonly int _requestCap;
    private readonly Action? _onFrameRefused;

    // A deadline begun by the listener at accept, shared with the TLS handshake (spec 011 R11.8); otherwise one begun here.
    private readonly CancellationToken? _givenPreAuthDeadline;

    // Tells the listener this connection no longer counts as unauthenticated (spec 011 R11.9).
    private readonly Action? _onAuthenticated;

    /// <summary>
    /// A request refused for its size (spec 011 R4.3): logged once (the connection then closes) and counted, so a store
    /// whose PULLs outgrow the cap is seen, not silently cut off.
    /// </summary>
    private void FrameRefused(int cap, string detail)
    {
        _onFrameRefused?.Invoke();
        _logger?.LogWarning("Replication request from {Remote} refused: over the {Cap}-byte cap ({Detail}).", _remote, cap, detail);
    }

    private readonly TimeSpan _preAuthTimeout;

    /// <summary>
    /// Serves the connection until it closes or <paramref name="cancellationToken"/> fires. Returns when the peer
    /// disconnects, a protocol error closes the connection, or the pre-<c>AUTH</c> timeout elapses.
    /// </summary>
    /// <remarks>
    /// <b>Blocks a thread</b> (playbook § 7, spec 011 R11.10): a caught-up <c>PULL</c> waits on the primary for a change,
    /// up to <c>LongPollMax</c>, with <c>Monitor.Wait</c> on the thread serving this connection (a thread-pool thread),
    /// rather than an asynchronous wait: the wait must also be woken by commits made on writers' threads, and one thread
    /// per standby connection, bounded by <c>MaxConnections</c>, is the cost accepted. Reads and writes of the stream
    /// are asynchronous.
    /// </remarks>
    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        // One deadline from the moment the connection is served, not restarted by each request (003 should-fix 8): a
        // peer that keeps sending HELLO without authenticating is closed when it runs out.
        using var preAuth = new CancellationTokenSource(_preAuthTimeout);
        _preAuthDeadline = _givenPreAuthDeadline ?? preAuth.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var cap = _authenticated ? _requestCap : ReplicationProtocol.PreAuthFrameCap;
                var request = await ReadRequestAsync(cap, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return; // peer closed, timed out, or a protocol error already closed us
                }

                var command = Text.ASCII.GetString(request[0]).ToUpperInvariant();
                var keepOpen = await DispatchAsync(command, request, cancellationToken).ConfigureAwait(false);
                if (!keepOpen)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A dropped or cancelled connection is normal; nothing to log but the loss.
        }
        finally
        {
            if (_authenticated && _logger is not null)
            {
                Observability.CrystalLog.ReplicationDisconnected(_logger, _source.Epoch, $"{_peerNodeId} at {_remote}");
            }
        }
    }

    private async Task<bool> DispatchAsync(string command, IReadOnlyList<byte[]> request, CancellationToken ct)
    {
        // Before AUTH, only HELLO and AUTH are accepted (R10.5).
        if (!_authenticated && command is not (ReplicationProtocol.Hello or ReplicationProtocol.Auth))
        {
            await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrNoAuth} authenticate first"), ct).ConfigureAwait(false);
            return true;
        }

        switch (command)
        {
            case ReplicationProtocol.Hello when _authenticated:
                // A second HELLO would change the authenticated peer's node id (its slot key) without a new AUTH.
                await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrArg} HELLO is accepted once per connection"), ct).ConfigureAwait(false);
                return false;
            case ReplicationProtocol.Hello:
                return await HandleHelloAsync(request, ct).ConfigureAwait(false);
            case ReplicationProtocol.Auth:
                return await HandleAuthAsync(request, ct).ConfigureAwait(false);
            case ReplicationProtocol.Status:
                await WriteAsync(new RespWriter().BulkString(_source.StatusJson()), ct).ConfigureAwait(false);
                return true;
            default:
            {
                var reply = new RespWriter();
                if (!_source.TryHandle(command, request, _peerNodeId, reply))
                {
                    reply = new RespWriter().Error($"{ReplicationProtocol.ErrArg} unknown command '{command}'");
                }

                await WriteAsync(reply, ct).ConfigureAwait(false);
                return true;
            }
        }
    }

    private async Task<bool> HandleHelloAsync(IReadOnlyList<byte[]> request, CancellationToken ct)
    {
        // HELLO <protocolVersion> <nodeId> <role> <epoch> <format> <layout>
        if (request.Count != 7)
        {
            await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrArg} HELLO takes 6 arguments"), ct).ConfigureAwait(false);
            return true;
        }

        if (!TryParseLong(request[1], out var peerVersion))
        {
            await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrArg} HELLO version is not an integer"), ct).ConfigureAwait(false);
            return true;
        }

        if (peerVersion != ReplicationProtocol.Version)
        {
            await WriteAsync(
                new RespWriter().Error($"{ReplicationProtocol.ErrVersion} ours={ReplicationProtocol.Version} theirs={peerVersion}"),
                ct).ConfigureAwait(false);
            return false; // a peer we cannot speak to is closed (R10.7)
        }

        _peerNodeId = Text.UTF8.GetString(request[2]);
        if (!ReplicationProtocol.IsValidNodeId(_peerNodeId))
        {
            await WriteAsync(
                new RespWriter().Error($"{ReplicationProtocol.ErrArg} HELLO node id must be 1 to {ReplicationProtocol.MaxNodeIdBytes} bytes"),
                ct).ConfigureAwait(false);
            return false;
        }

        // A peer stating this node's own id is refused (spec 011 R1.3): it can only be a relay reflecting this node's own
        // follower, and is never an honest peer.
        if (string.Equals(_peerNodeId, _source.NodeId, StringComparison.Ordinal))
        {
            await WriteAsync(
                new RespWriter().Error($"{ReplicationProtocol.ErrAuth} the peer states this node's own id"),
                ct).ConfigureAwait(false);
            _logger?.LogWarning("Replication HELLO from {Remote} stated this node's own id; refused.", _remote);
            return false;
        }

        var peerRole = Text.UTF8.GetString(request[3]);
        if (!TryParseLong(request[4], out var peerEpoch) || peerEpoch < 0 || peerEpoch > ReplicationProtocol.MaxEpoch)
        {
            await WriteAsync(
                new RespWriter().Error($"{ReplicationProtocol.ErrArg} HELLO epoch must be an integer from 0 to {ReplicationProtocol.MaxEpoch}"),
                ct).ConfigureAwait(false);
            return false;
        }

        var peerFormat = Text.UTF8.GetString(request[5]);
        var peerLayout = Text.UTF8.GetString(request[6]);
        if (peerFormat != ReplicationProtocol.Format || peerLayout != ReplicationProtocol.Layout)
        {
            await WriteAsync(
                new RespWriter().Error($"{ReplicationProtocol.ErrFormat} ours={ReplicationProtocol.Format} theirs={peerFormat}"),
                ct).ConfigureAwait(false);
            return false; // a format mismatch is refused before anything is served (R8.1)
        }

        // R6.3/R6.4 act on the peer's epoch only after AUTH (003 addendum B1): an unauthenticated HELLO must never
        // change this node's epoch or role. Kept until then.
        _peerRole = peerRole;
        _peerEpoch = peerEpoch;

        // A fresh nonce for this connection; valid for one AUTH only.
        _nonce = ReplicationProtocol.NewNonce();

        // Reply: [protocolVersion, nodeId, role, epoch, format, nonce]
        var reply = new RespWriter();
        reply.ArrayHeader(6)
            .Integer(ReplicationProtocol.Version)
            .BulkString(_source.NodeId)
            .BulkString(_source.Role)
            .Integer(_source.Epoch)
            .BulkString(ReplicationProtocol.Format)
            .BulkString(Convert.ToHexStringLower(_nonce));
        await WriteAsync(reply, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleAuthAsync(IReadOnlyList<byte[]> request, CancellationToken ct)
    {
        // AUTH <hmacHex>
        if (_nonce is null)
        {
            await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrAuth} HELLO before AUTH"), ct).ConfigureAwait(false);
            return false;
        }

        if (request.Count != 3)
        {
            await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrArg} AUTH takes the response and the peer's own nonce"), ct).ConfigureAwait(false);
            return false;
        }

        byte[] peerNonce;
        try
        {
            peerNonce = Convert.FromHexString(Text.ASCII.GetString(request[2]));
        }
        catch (FormatException)
        {
            peerNonce = [];
        }

        if (peerNonce.Length != ReplicationProtocol.NonceBytes)
        {
            await WriteAsync(new RespWriter().Error($"{ReplicationProtocol.ErrArg} AUTH nonce must be {ReplicationProtocol.NonceBytes} bytes, hex"), ct).ConfigureAwait(false);
            return false;
        }

        var serverNonce = _nonce;
        var expected = ReplicationProtocol.ClientAnswer(_source.Secret, serverNonce, peerNonce, _peerNodeId, _source.NodeId);
        var actual = Text.ASCII.GetString(request[1]);

        // The nonce is spent whether or not the response is right: no second guess on this connection.
        _nonce = null;

        if (!ReplicationProtocol.ResponsesEqual(expected, actual))
        {
            await WriteAsync(new RespWriter().Error(ReplicationProtocol.ErrAuth), ct).ConfigureAwait(false);

            // host:port and node id only — never the secret, the nonce or its response (R10.9). The formal
            // replication event catalogue (CrystalLogEvents' 2000s range) is wired in T15; here a plain, data-free
            // warning suffices and keeps the "no key/value in a log method" architecture rule for CrystalLog intact.
            _logger?.LogWarning("Replication AUTH failed from {Remote} (node {PeerNodeId}).", _remote, _peerNodeId);

            return false; // a wrong HMAC closes the connection (R10.5)
        }

        _authenticated = true;
        _onAuthenticated?.Invoke();
        if (_logger is not null)
        {
            // Connected (spec 011 R10.2), and lost when ServeAsync ends: node id and endpoint only (R10.9).
            Observability.CrystalLog.ReplicationConnected(_logger, _source.Epoch, $"{_peerNodeId} at {_remote} authenticated");
        }

        // R6.3/R6.4, now that the peer has proved it holds the secret: adopt a higher epoch (a primary here demotes), or
        // report an equal-epoch conflict with another primary.
        _source.ObservePeer(_peerEpoch, _peerRole, _peerNodeId, _remote);

        // This node's proof in turn: the peer authenticates it too.
        var proof = ReplicationProtocol.ServerProof(_source.Secret, serverNonce, peerNonce, _peerNodeId, _source.NodeId);
        await WriteAsync(new RespWriter().BulkString(Text.ASCII.GetBytes(proof)), ct).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ framing

    /// <summary>
    /// Reads one request frame (an array of bulk strings), growing the buffer as needed, up to <paramref name="cap"/>.
    /// Returns null on peer close, a protocol error (connection to be closed), or — before AUTH — the idle timeout.
    /// </summary>
    private async Task<IReadOnlyList<byte[]>?> ReadRequestAsync(int cap, CancellationToken ct)
    {
        using var timeoutCts = _authenticated
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, _preAuthDeadline);

        while (true)
        {
            // Try to parse a frame from what we already have.
            if (_bufferEnd > _bufferStart)
            {
                var sequence = new ReadOnlySequence<byte>(_buffer, _bufferStart, _bufferEnd - _bufferStart);
                var status = RespReader.TryReadRequest(sequence, cap, out var args, out var consumed, out var error);
                if (status == RespReadStatus.Complete)
                {
                    var offset = (int)sequence.Slice(sequence.Start, consumed).Length;
                    _bufferStart += offset;
                    if (_bufferStart == _bufferEnd)
                    {
                        _bufferStart = _bufferEnd = 0;
                    }

                    return args;
                }

                if (status == RespReadStatus.ProtocolError)
                {
                    // Close without answering: the peer cannot be trusted to read a reply (design § 8.1).
                    if (_authenticated && error is not null && error.Contains("byte", StringComparison.Ordinal))
                    {
                        FrameRefused(cap, error);
                    }

                    return null;
                }
            }

            // Incomplete: read more. Grow or compact the buffer first.
            EnsureReadRoom(cap);

            int read;
            try
            {
                read = await _stream.ReadAsync(_buffer.AsMemory(_bufferEnd), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_authenticated && !ct.IsCancellationRequested)
            {
                return null; // the pre-AUTH idle timeout elapsed (R10.5)
            }

            if (read == 0)
            {
                return null; // peer closed
            }

            _bufferEnd += read;

            // A frame that has already overrun the cap without parsing is refused.
            if (_bufferEnd - _bufferStart > cap + 16)
            {
                if (_authenticated)
                {
                    FrameRefused(cap, "the frame overran it");
                }

                return null;
            }
        }
    }

    private void EnsureReadRoom(int cap)
    {
        if (_bufferStart > 0)
        {
            // Compact.
            Array.Copy(_buffer, _bufferStart, _buffer, 0, _bufferEnd - _bufferStart);
            _bufferEnd -= _bufferStart;
            _bufferStart = 0;
        }

        if (_bufferEnd == _buffer.Length)
        {
            var target = Math.Min(cap + 64, _buffer.Length * 2);
            if (target > _buffer.Length)
            {
                Array.Resize(ref _buffer, target);
            }
        }
    }

    private async Task WriteAsync(RespWriter writer, CancellationToken ct)
    {
        var bytes = writer.ToArray();
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static bool TryParseLong(byte[] bytes, out long value) =>
        long.TryParse(Text.ASCII.GetString(bytes), out value);
}
