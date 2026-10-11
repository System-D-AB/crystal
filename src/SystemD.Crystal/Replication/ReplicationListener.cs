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
    private readonly CancellationTokenSource _stopping = new();
    private int _open;

    /// <summary>Connections open now (tests of the cap).</summary>
    internal int OpenConnections => Volatile.Read(ref _open);
    private Task? _acceptLoop;
    private int _disposed;

    private ReplicationListener(TcpListener tcp, IReplicationSource source, ReplicationOptions options, ILogger? logger)
    {
        _tcp = tcp;
        _source = source;
        _options = options;
        _logger = logger;
    }

    /// <summary>The endpoint the listener actually bound to (the port is resolved if the configuration asked for 0).</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_tcp.LocalEndpoint;

    /// <summary>Binds and starts the accept loop on <see cref="ReplicationOptions.ListenEndpoint"/>.</summary>
    public static ReplicationListener Start(IReplicationSource source, ReplicationOptions options, ILogger? logger)
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
        var listener = new ReplicationListener(tcp, source, options, logger);
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

            // Serve each connection independently; one bad peer never blocks the others.
            _ = Task.Run(() => ServeClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();

            if (_options.Certificate is { } certificate)
            {
                // The TLS handshake shares the pre-AUTH deadline: a peer that stalls it is closed too.
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    handshake.CancelAfter(ReplicationProtocol.PreAuthTimeout);
                    await ssl.AuthenticateAsServerAsync(ReplicationTls.ServerOptions(_options, certificate), handshake.Token).ConfigureAwait(false);
                }

                stream = ssl;
            }

            var connection = new ReplicationConnection(stream, _source, remote, _logger);
            await connection.ServeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            // A dropped, timed-out or mis-TLS'd connection is normal; nothing to record but the loss.
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _open);
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
