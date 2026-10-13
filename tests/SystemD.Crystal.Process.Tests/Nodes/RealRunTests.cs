// Spec 011 T16 — a real run (playbook § 4): two separate node processes over mutual TLS, through the whole replication
// life cycle: create and write, follow, a planned handover (R14), a hard kill of the new primary, the old one promoted,
// and the killed node rejoining by a scheduled rejoin honoured at its next start (R11.4). The data is compared by
// digest at the end. The certificates are made for the run in a temporary folder and never kept.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Nodes;

public sealed class RealRunTests
{
    private static readonly TimeSpan CatchUp = TimeSpan.FromSeconds(60);

    [Fact]
    public void TwoProcessesOverMutualTls_FollowHandOverFailOverAndRejoin_EndingIdentical()
    {
        using var certificates = new TempDirectory("crystal-certs");
        Directory.CreateDirectory(certificates.Path);
        var (a, b) = MakeCertificates(certificates.Path);
        using var nodeA = TestNode.Start(tls: a);
        using var nodeB = TestNode.Start(tls: b);

        // A is the primary; B follows it over mutual TLS.
        nodeA.Listen();
        nodeA.CreateDatabase("db");
        nodeA.Box("db", "box");
        for (var i = 0; i < 50; i++)
        {
            nodeA.PutBatch("db", "box", $"a{i:D3}", 4);
        }

        nodeB.Listen();
        nodeB.Follow(nodeA);
        WaitUntilIdentical(nodeA, nodeB);

        // A planned handover: A stops writes, B catches up, A stands down; B is promoted and A follows it.
        Assert.Equal(1, nodeA.StepDown(nodeB));
        Assert.Equal("Standby", nodeA.Role());
        nodeB.Promote();
        nodeA.Follow(nodeB);
        for (var i = 0; i < 50; i++)
        {
            nodeB.PutBatch("db", "box", $"b{i:D3}", 4);
        }

        WaitUntilIdentical(nodeB, nodeA);

        // B, now the primary, is killed; A is promoted and writes on.
        nodeB.Kill();
        nodeA.StopFollowing();
        nodeA.Promote();
        for (var i = 0; i < 50; i++)
        {
            nodeA.PutBatch("db", "box", $"c{i:D3}", 4);
        }

        // B comes back, schedules a rejoin of A, and at its next start follows A by itself.
        nodeB.Restart();
        nodeB.ScheduleRejoin(nodeA);
        nodeB.Stop();
        nodeB.Restart();
        WaitUntilIdentical(nodeA, nodeB);

        Assert.Equal(nodeA.Digest("db", "box"), nodeB.Digest("db", "box"));
        Assert.Equal(600, nodeA.Count("db", "box"));
    }

    private static void WaitUntilIdentical(TestNode primary, TestNode standby)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < CatchUp)
        {
            try
            {
                if (primary.Digest("db", "box") == standby.Digest("db", "box"))
                {
                    return;
                }
            }
            catch (NodeCommandException)
            {
                // not there yet (the database is being bootstrapped)
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"the standby did not reach the primary's data within {CatchUp}");
    }

    /// <summary>A CA and a certificate for each node (loopback names, server and client authentication), as files.</summary>
    private static (NodeTls A, NodeTls B) MakeCertificates(string folder)
    {
        // One clock reading for every bound, and the leaf ends a day before its issuer: two readings a tick apart once
        // made a leaf outlive its CA, which the issuing call refuses (review 2026-10-13, S6).
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Crystal real-run CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
        var trust = Path.Combine(folder, "ca.cer");
        File.WriteAllBytes(trust, ca.Export(X509ContentType.Cert));

        NodeTls Node(string name)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            using var issued = request.Create(ca, now.AddDays(-1), now.AddDays(1), serial);
            using var withKey = issued.CopyWithPrivateKey(key);
            var path = Path.Combine(folder, name + ".pfx");
            File.WriteAllBytes(path, withKey.Export(X509ContentType.Pfx));
            return new NodeTls(path, trust, RequireClientCertificate: true);
        }

        return (Node("node-a"), Node("node-b"));
    }
}
