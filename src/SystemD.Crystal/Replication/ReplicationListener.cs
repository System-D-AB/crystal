using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SystemD.Crystal.Replication;

/// <summary>
/// The replication listener (spec 003 R10.1, R10.6, design § 8, § 9): a TCP accept loop on the configured endpoint,
/// optionally wrapped in TLS, serving each connection with a <see cref="ReplicationConnection"/>. A standby listens
/// too, to be promotable (design § 1). Logs <c>host:port</c> and node ids only (R10.9).
/// </summary>
internal sealed class ReplicationListener : IDisposable
{
    private readonly TcpListener _tcp;
    private readonly IReplicationSource _source;
    private readonly ReplicationOptions _options;
    private readonly ILogger? _logger;
    private readonly int _requestCap;
    private readonly Action? _onFrameRefused;
    private readonly TimeSpan _preAuthTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private int _open;
    private int _unauthenticated;

    /// <summary>Connections accepted that have not authenticated yet (tests of the R11.9 cap).</summary>
    internal int Unauthenticated => Volatile.Read(ref _unauthenticated);

    /// <summary>Connections open now (tests of the cap).</summary>
    internal int OpenConnections => Volatile.Read(ref _open);
    private Task? _acceptLoop;
    private int _disposed;

    private ReplicationListener(
        TcpListener tcp, IReplicationSource source, ReplicationOptions options, ILogger? logger, int requestCap, Action? onFrameRefused, TimeSpan preAuthTimeout)
    {
        _preAuthTimeout = preAuthTimeout;
        _tcp = tcp;
        _source = source;
        _options = options;
        _logger = logger;
        _requestCap = requestCap;
        _onFrameRefused = onFrameRefused;
    }

    /// <summary>The endpoint the listener actually bound to (the port is resolved if the configuration asked for 0).</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_tcp.LocalEndpoint;

    /// <summary>
    /// Binds and starts the accept loop on <see cref="ReplicationOptions.ListenEndpoint"/>. Requests after AUTH may be up
    /// to <paramref name="requestCap"/> bytes (spec 011 R4.2); <paramref name="onFrameRefused"/> counts a refused frame.
    /// </summary>
    public static ReplicationListener Start(
        IReplicationSource source, ReplicationOptions options, ILogger? logger, int requestCap = ReplicationProtocol.RequestCap, Action? onFrameRefused = null,
        TimeSpan? preAuthTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ListenEndpoint))
        {
            throw new ArgumentException("ReplicationOptions.ListenEndpoint must be set to listen.", nameof(options));
        }

        var endpoint = ParseEndpoint(options.ListenEndpoint);
        var tcp = new TcpListener(endpoint);
        tcp.Start();
        var listener = new ReplicationListener(tcp, source, options, logger, requestCap, onFrameRefused, preAuthTimeout ?? ReplicationProtocol.PreAuthTimeout);
        listener._acceptLoop = listener.AcceptLoopAsync(listener._stopping.Token);
        return listener;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // At the cap, the newest connection is closed at once (003 should-fix 8): many peers, authenticated or not,
            // cannot hold the listener's threads and memory without bound.
            if (Interlocked.Increment(ref _open) > Math.Max(1, _options.MaxConnections))
            {
                Interlocked.Decrement(ref _open);
                client.Dispose();
                continue;
            }

            // At most a quarter of the connections may be unauthenticated at once (spec 011 R11.9): peers that never
            // authenticate cannot take the places standbys need. One more is closed as soon as it is accepted.
            if (Interlocked.Increment(ref _unauthenticated) > Math.Max(1, _options.MaxConnections / 4))
            {
                Interlocked.Decrement(ref _unauthenticated);
                Interlocked.Decrement(ref _open);
                client.Dispose();
                continue;
            }

            // Serve each connection independently; one bad peer never blocks the others.
            _ = Task.Run(() => ServeClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        var counted = 1; // in the unauthenticated count until it authenticates or ends (R11.9)
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();

            // One deadline from accept for the TLS handshake and AUTH together (spec 011 R11.8): a peer that stalls the
            // handshake has that much less time to authenticate, never a fresh 10 s after it.
            using var preAuth = new CancellationTokenSource(_preAuthTimeout);
            if (_options.Certificate is { } certificate)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct, preAuth.Token))
                {
                    await ssl.AuthenticateAsServerAsync(ReplicationTls.ServerOptions(_options, certificate), handshake.Token).ConfigureAwait(false);
                }

                stream = ssl;
            }

            var connection = new ReplicationConnection(
                stream, _source, remote, _logger, requestCap: _requestCap, onFrameRefused: _onFrameRefused, preAuthDeadline: preAuth.Token,
                onAuthenticated: LeaveUnauthenticated);
            await connection.ServeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            // A dropped, timed-out or mis-TLS'd connection is normal; nothing to record but the loss.
        }
        finally
        {
            client.Dispose();
            LeaveUnauthenticated();
            Interlocked.Decrement(ref _open);
        }

        // Counted out of the unauthenticated once: when it authenticates, or when it ends without having done so.
        void LeaveUnauthenticated()
        {
            if (Interlocked.Exchange(ref counted, 0) == 1)
            {
                Interlocked.Decrement(ref _unauthenticated);
            }
        }
    }

    private static IPEndPoint ParseEndpoint(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(endpoint.AsSpan(colon + 1), out var port))
        {
            throw new ArgumentException($"'{endpoint}' is not a valid host:port endpoint.", nameof(endpoint));
        }

        var hostPart = endpoint[..colon];
        var address = hostPart switch
        {
            "0.0.0.0" or "" => IPAddress.Any,
            "::" or "[::]" => IPAddress.IPv6Any,
            "localhost" => IPAddress.Loopback,
            _ => IPAddress.Parse(hostPart.Trim('[', ']')),
        };

        return new IPEndPoint(address, port);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        try
        {
            _tcp.Stop();
        }
        catch (SocketException)
        {
            // Already stopped.
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends on cancellation; nothing to surface.
        }

        _stopping.Dispose();
    }
}
