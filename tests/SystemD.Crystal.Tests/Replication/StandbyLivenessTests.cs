// Review 2026-10-13, S4 — a standby does not wait forever for a primary that vanished without closing the connection
// (power loss, partition, a frozen machine): every reply has a deadline, after which the follower takes the connection
// as lost and reconnects. A proxy between them forwards the primary's bytes until told to stop, then holds the
// connection open and silent.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class StandbyLivenessTests
{
    [Fact]
    public async Task APrimaryThatFallsSilent_IsLeft_AndTheStandbyReconnects()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = "test-secret", ListenEndpoint = "127.0.0.1:0", LongPollMax = TimeSpan.FromMilliseconds(300) },
        });
        var db = primary.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");
        primary.Replication.Listen();
        var target = primary.Replication.ListenerEndpoint!;
        var targetPort = int.Parse(target[(target.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        using var proxy = new SilencingProxy(targetPort);
        using var standby = CrystalStore.Open(sdir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions
            {
                Secret = "test-secret",
                LongPollMax = TimeSpan.FromMilliseconds(300),
                RequestTimeout = TimeSpan.FromSeconds(1),
            },
        });
        standby.Replication.Follow($"127.0.0.1:{proxy.Port}");
        Assert.True(SpinWait.SpinUntil(() => standby.ListDatabases().Any(d => d.Name == "db"), TimeSpan.FromSeconds(30)));
        var connections = proxy.Accepted;

        // The primary falls silent on the open connection: within the deadline the standby gives up on it and connects
        // again (which the proxy serves normally).
        proxy.Silence();
        var sw = Stopwatch.StartNew();
        Assert.True(SpinWait.SpinUntil(() => proxy.Accepted > connections, TimeSpan.FromSeconds(15)),
            "the standby kept waiting on a silent primary");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        await Task.CompletedTask;
    }

    [Fact]
    public void AListenerThatAcceptsButNeverAnswersTls_IsLeft_AndTheStandbyTriesAgain()
    {
        // Re-review 2026-10-13, N2: over TLS the handshake shares the deadline. The "primary" here is a hung process
        // whose kernel still accepts connections: the standby must give up on each and try again.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = 0;
        var held = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    held.Add(await listener.AcceptTcpClientAsync(stop.Token)); // accepted, never read, never answered
                    Interlocked.Increment(ref accepted);
                }
                catch (Exception)
                {
                    return;
                }
            }
        });

        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=standby", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            self.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);

        using var sdir = new TempStoreDirectory();
        try
        {
            using var standby = CrystalStore.Open(sdir.Path, new CrystalOptions
            {
                Replication = new ReplicationOptions
                {
                    Secret = "test-secret",
                    LongPollMax = TimeSpan.FromMilliseconds(300),
                    RequestTimeout = TimeSpan.FromSeconds(1),
                    Certificate = certificate,
                },
            });
            standby.Replication.Follow($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref accepted) >= 2, TimeSpan.FromSeconds(15)),
                "the standby stayed in the TLS handshake with a primary that never answered");
            standby.Replication.StopFollowing();
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            foreach (var c in held)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>Forwards connections to the primary; once silenced, stops forwarding the open ones but keeps them open.</summary>
    private sealed class SilencingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly int _targetPort;
        private int _silencedUpTo;
        private int _accepted;

        public SilencingProxy(int targetPort)
        {
            _targetPort = targetPort;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        /// <summary>Connections open now go silent; new ones are forwarded.</summary>
        public void Silence() => Volatile.Write(ref _silencedUpTo, Accepted);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                var number = Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => ServeAsync(client, number));
            }
        }

        private async Task ServeAsync(TcpClient client, int number)
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, _targetPort);
            var toPrimary = Pump(client.GetStream(), upstream.GetStream(), () => false);
            var toStandby = Pump(upstream.GetStream(), client.GetStream(), () => number <= Volatile.Read(ref _silencedUpTo));
            await Task.WhenAny(toPrimary, toStandby);
            await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { });
            client.Dispose();
        }

        private async Task Pump(NetworkStream from, NetworkStream to, Func<bool> silenced)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = await from.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    if (!silenced())
                    {
                        await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                    }
                }
            }
            catch (Exception)
            {
                // closed
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
