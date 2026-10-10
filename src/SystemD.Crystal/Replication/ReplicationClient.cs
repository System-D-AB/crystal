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

    public ReplicationClient(Stream stream, string nodeId, string secret)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
    }

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
        PrimaryRole = Text.UTF8.GetString(fields[2].Bytes ?? []);
        PrimaryEpoch = fields[3].Integer;
        var nonceHex = Text.UTF8.GetString(fields[5].Bytes ?? []);
        var nonce = Convert.FromHexString(nonceHex);

        // AUTH <hmacHex>
        var response = ReplicationProtocol.Response(_secret, nonce, _nodeId);
        await SendAsync([Bytes(ReplicationProtocol.Auth), Bytes(response)], ct).ConfigureAwait(false);

        var auth = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (auth.Type == RespType.Error)
        {
            throw new ReplicationProtocolException(auth.Text ?? "AUTH refused");
        }

        if (auth.Type != RespType.SimpleString || auth.Text != "OK")
        {
            throw new ReplicationProtocolException("AUTH did not return OK");
        }
    }

    /// <summary>Sends a request (an array of bulk-string arguments) and reads the one reply (design § 8.1).</summary>
    public async Task<RespValue> RequestAsync(IReadOnlyList<byte[]> args, CancellationToken ct)
    {
        await SendAsync(args, ct).ConfigureAwait(false);
        return await ReadReplyAsync(ct).ConfigureAwait(false);
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

                    return value!;
                }

                if (status == RespReadStatus.ProtocolError)
                {
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
                    throw new ReplicationProtocolException(
                        $"the primary's reply exceeds the {ReplicationProtocol.ReplyFrameCap}-byte cap (R10.8)");
                }

                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, ReplicationProtocol.ReplyFrameCap + (64 * 1024)));
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_bufferEnd), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ReplicationProtocolException("the primary closed the connection");
            }

            _bufferEnd += read;
        }
    }

    private static byte[] Bytes(string s) => Text.UTF8.GetBytes(s);
}

/// <summary>A replication protocol failure a standby cannot recover from on this connection (spec 003 R10).</summary>
internal class ReplicationProtocolException(string message) : Exception(message);
