// Spec 003 R10.6 (completed 2026-10-11) — replication over TLS, both ways: a node with a certificate serves TLS and
// follows its primary over TLS; peers are validated against the configured trust; with RequireClientCertificate the
// primary validates the standby's certificate too (mutual TLS). Each refusal leaves the standby with nothing replicated.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using SystemD.Crystal;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class TlsTests
{
    private static readonly Lazy<(X509Certificate2 Ca, X509Certificate2 NodeA, X509Certificate2 NodeB)> Cluster = new(() =>
    {
        var ca = Authority("Crystal test CA");
        return (ca, Issue(ca, "node-a"), Issue(ca, "node-b"));
    });

    private static readonly Lazy<(X509Certificate2 Ca, X509Certificate2 Node)> Stranger = new(() =>
    {
        var ca = Authority("Some other CA");
        return (ca, Issue(ca, "stranger"));
    });

    private static CrystalOptions Options(X509Certificate2? certificate, X509Certificate2? trust, bool requireClient, string? listen = null) => new()
    {
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
            Certificate = certificate,
            RequireClientCertificate = requireClient,
            TrustedCertificates = trust is null ? null : [trust],
        },
    };

    [Fact]
    public void OverMutualTls_AStandbyBootstrapsAndFollows()
    {
        var (ca, a, b) = Cluster.Value;
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(a, ca, requireClient: true, listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options(b, ca, requireClient: true));
        var db = Seeded(primary);
        primary.Replication.Listen();

        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);
        Put(db, "after-follow", "2");

        WaitUntil(() => Rows(standby).SequenceEqual(Rows(primary)), TimeSpan.FromSeconds(30));
        Assert.Equal(["after-follow", "seed"], Rows(standby));
    }

    [Fact]
    public void AStandbyThatDoesNotTrustThePrimarysCertificate_TakesNothing()
    {
        var (ca, a, _) = Cluster.Value;
        var (strangerCa, strangerNode) = Stranger.Value;
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(a, ca, requireClient: false, listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options(strangerNode, strangerCa, requireClient: false));
        Seeded(primary);
        primary.Replication.Listen();

        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        Thread.Sleep(3000);
        Assert.Empty(standby.ListDatabases());
    }

    [Fact]
    public void APrimaryRequiringClientCertificates_RefusesAStandbyWithAnUntrustedOne()
    {
        var (ca, a, _) = Cluster.Value;
        var (_, strangerNode) = Stranger.Value;
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(a, ca, requireClient: true, listen: "127.0.0.1:0"));

        // The standby trusts the primary, but its own certificate comes from a CA the primary does not trust.
        using var standby = CrystalStore.Open(sdir.Path, Options(strangerNode, ca, requireClient: false));
        Seeded(primary);
        primary.Replication.Listen();

        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        Thread.Sleep(3000);
        Assert.Empty(standby.ListDatabases());
    }

    [Fact]
    public void AStandbyWithoutTls_TakesNothingFromATlsPrimary()
    {
        var (ca, a, _) = Cluster.Value;
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(a, ca, requireClient: false, listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options(certificate: null, trust: null, requireClient: false));
        Seeded(primary);
        primary.Replication.Listen();

        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        Thread.Sleep(3000);
        Assert.Empty(standby.ListDatabases());
    }

    [Fact]
    public void AServerCertificateForAnotherHost_IsNotTrusted_EvenFromTheTrustedCa()
    {
        var (ca, a, _) = Cluster.Value;
        Assert.False(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(
            a, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, [ca], checkName: true));
        Assert.True(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(
            a, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, [ca], checkName: true));
        Assert.False(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(
            a, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, [Stranger.Value.Ca], checkName: true));
    }

    // ------------------------------------------------------------------ spec 011 R11.8

    [Fact]
    public void ACertificateThatStatesUsages_MustAllowTheRoleItIsUsedFor()
    {
        // A primary's certificate that allows only client authentication is refused; one stating no usage is not limited.
        var (ca, _, _) = Cluster.Value;
        using var clientOnly = Issue(ca, "client-only", [SystemD.Crystal.Replication.ReplicationTls.ClientAuthentication]);
        using var serverOnly = Issue(ca, "server-only", [SystemD.Crystal.Replication.ReplicationTls.ServerAuthentication]);
        using var unrestricted = Issue(ca, "unrestricted", usages: null);
        var chainError = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        var server = SystemD.Crystal.Replication.ReplicationTls.ServerAuthentication;
        var client = SystemD.Crystal.Replication.ReplicationTls.ClientAuthentication;

        Assert.False(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(clientOnly, chainError, [ca], checkName: true, requiredUsage: server));
        Assert.True(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(serverOnly, chainError, [ca], checkName: true, requiredUsage: server));
        Assert.False(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(serverOnly, chainError, [ca], checkName: false, requiredUsage: client));
        Assert.True(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(unrestricted, chainError, [ca], checkName: true, requiredUsage: server));
    }

    [Fact]
    public void ALeafIssuedByAnIntermediate_IsTrusted_ThroughTheChainThePeerPresented()
    {
        // With custom trust, only the root is configured; the peer sends its intermediate in the handshake.
        using var root = Authority("Crystal test root");
        using var intermediate = SubAuthority(root, "Crystal test intermediate");
        using var leaf = Issue(intermediate, "node-c");
        var chainError = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

        // Without the presented chain, first: once a chain with the intermediate has been built, the platform's chain
        // engine may remember it for later builds in the process, and the check below would pass for the wrong reason
        // (the cause inferred for one failure in a full run, 2026-10-13; not reproduced in eleven runs after).
        Assert.False(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(leaf, chainError, [root], checkName: false));

        using var presented = new X509Chain();
        presented.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        presented.ChainPolicy.CustomTrustStore.Add(root);
        presented.ChainPolicy.ExtraStore.Add(intermediate);
        presented.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(presented.Build(leaf));

        Assert.True(SystemD.Crystal.Replication.ReplicationTls.IsTrusted(leaf, chainError, [root], checkName: false, presented));
    }

    [Fact]
    public async System.Threading.Tasks.Task OneDeadline_CoversTheTlsHandshakeAndAuth()
    {
        // A peer that takes most of the deadline to start TLS has only what is left to authenticate: with a 2 s deadline
        // and the handshake begun after 1.5 s, the connection closes about 2 s after accept, not 2 s after the handshake.
        var (ca, a, _) = Cluster.Value;
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, Options(a, ca, requireClient: false));
        var epoch = SystemD.Crystal.Storage.ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
        var options = Options(a, ca, requireClient: false, listen: "127.0.0.1:0").Replication!;
        using var source = new SystemD.Crystal.Storage.ReplicationSource(store, store.Engine, store.NodeId, epoch, options);
        using var listener = SystemD.Crystal.Replication.ReplicationListener.Start(source, options, logger: null, preAuthTimeout: TimeSpan.FromSeconds(2));

        using var tcp = new System.Net.Sockets.TcpClient();
        var sw = Stopwatch.StartNew();
        await tcp.ConnectAsync(listener.Endpoint.Address, listener.Endpoint.Port);
        await System.Threading.Tasks.Task.Delay(1500);
        using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("127.0.0.1");

        // Never authenticate: wait for the server to close.
        var buffer = new byte[16];
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await ssl.ReadAsync(buffer, bound.Token) > 0)
            {
            }
        }
        catch (System.IO.IOException)
        {
            // closed
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"closed {sw.Elapsed.TotalSeconds:F1} s after connect (a fresh deadline after the handshake would be 3.5 s)");
    }

    // ------------------------------------------------------------------ certificates

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 SubAuthority(X509Certificate2 root, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 Issue(X509Certificate2 authority, string name) =>
        Issue(authority, name, ["1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2"]); // server and client authentication

    private static X509Certificate2 Issue(X509Certificate2 authority, string name, string[]? usages)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (usages is not null)
        {
            var oids = new OidCollection();
            foreach (var usage in usages)
            {
                oids.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        }
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    // ------------------------------------------------------------------ helpers

    private static CrystalDatabase Seeded(CrystalStore store)
    {
        var db = store.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");
        Put(db, "seed", "1");
        return db;
    }

    private static void Put(CrystalDatabase db, string key, string value)
    {
        using var batch = db.NewBatch();
        batch.Put(db.Boxes.Find("box")!.Value, Text.UTF8.GetBytes(key), Text.UTF8.GetBytes(value));
        batch.Commit(DurabilityMode.Strict);
    }

    private static string[] Rows(CrystalStore store)
    {
        try
        {
            var db = store.OpenDatabase("db");
            using var snapshot = db.Snapshot();
            return [.. snapshot.Scan(db.Boxes.Find("box")!.Value, [], [], int.MaxValue).Select(kv => Text.UTF8.GetString(kv.Key))];
        }
        catch (Exception ex) when (ex is DatabaseNotFoundException or NullReferenceException or ObjectDisposedException)
        {
            return [];
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
        {
            Thread.Sleep(50);
        }
    }
}
