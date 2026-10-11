// Spec 003 R10.6 (completed 2026-10-11) — TLS between nodes, both ways. A node given a certificate serves TLS on its
// listener and connects to its primary over TLS presenting the same certificate; peers are validated against the
// configured trust (custom roots for an internal CA) or the operating system's. The shared-secret challenge (R10.5)
// runs inside the TLS stream either way.

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace SystemD.Crystal.Replication;

/// <summary>TLS for replication connections (spec 003 R10.6): the server and client options, and certificate validation.</summary>
internal static class ReplicationTls
{
    /// <summary>
    /// Opens a connection to a primary at <paramref name="host"/>:<paramref name="port"/>: TLS when this node has a
    /// certificate (every node of a TLS cluster has one, since a standby listens too), plain otherwise. Over TLS the
    /// primary's certificate must chain to the trust and carry <paramref name="host"/>, and this node presents its own
    /// certificate for a primary that requires one. The returned stream owns the connection.
    /// </summary>
    public static async Task<Stream> ConnectAsync(string host, int port, ReplicationOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var network = new OwningNetworkStream(tcp);
            if (options.Certificate is not { } certificate)
            {
                return network;
            }

            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        ClientCertificates = [certificate],
                        RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                            IsTrusted(cert, errors, options.TrustedCertificates, checkName: true),
                    },
                    ct).ConfigureAwait(false);
                return ssl;
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>The listener's TLS options: its certificate, and a client certificate when required, validated.</summary>
    public static SslServerAuthenticationOptions ServerOptions(ReplicationOptions options, X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        ClientCertificateRequired = options.RequireClientCertificate,
        RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
            !options.RequireClientCertificate || IsTrusted(cert, errors, options.TrustedCertificates, checkName: false),
    };

    /// <summary>
    /// Whether a peer's certificate is trusted (R10.6): it must be present; against the operating system's trust, valid
    /// with no policy error; against <paramref name="trusted"/>, chained to one of those certificates, revocation not
    /// checked (an internal CA usually publishes no revocation list). A server's certificate must also carry the host
    /// name the client connected to.
    /// </summary>
    internal static bool IsTrusted(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2Collection? trusted, bool checkName)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        if (checkName && errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        if (trusted is not { Count: > 0 })
        {
            return errors == SslPolicyErrors.None;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var peer = new X509Certificate2(certificate);
        return chain.Build(peer);
    }

    /// <summary>A network stream that disposes its TCP client with it.</summary>
    private sealed class OwningNetworkStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
