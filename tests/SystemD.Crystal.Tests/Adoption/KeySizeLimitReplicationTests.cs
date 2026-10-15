// Spec 012 T13, R9 — the key size limit guards a product's own writes, never data already committed: a standby whose
// limit is lower than its primary's still applies the primary's longer keys.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;
using Xunit;

namespace SystemD.Crystal.Tests.Adoption;

[Collection("replication-e2e")]
public sealed class KeySizeLimitReplicationTests
{
    private static CrystalOptions Options(int? maxKeyBytes, string? listen = null) => new()
    {
        MaxKeyBytes = maxKeyBytes,
        Replication = new ReplicationOptions
        {
            Secret = "test-secret",
            ListenEndpoint = listen,
            LongPollMax = TimeSpan.FromMilliseconds(300),
        },
    };

    [Fact]
    public void AStandbyWithALowerLimit_StillAppliesThePrimarysLongerKeys()
    {
        using var pdir = new TempStoreDirectory();
        using var sdir = new TempStoreDirectory();
        using var primary = CrystalStore.Open(pdir.Path, Options(maxKeyBytes: null, listen: "127.0.0.1:0"));
        using var standby = CrystalStore.Open(sdir.Path, Options(maxKeyBytes: 8));
        var db = primary.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("box");
        var longKey = new byte[100];
        Random.Shared.NextBytes(longKey);
        using (var batch = db.NewBatch())
        {
            batch.Put(box, longKey, "v"u8);
            batch.Commit();
        }

        primary.Replication.Listen();
        standby.Replication.Follow(primary.Replication.ListenerEndpoint!);

        var watch = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the standby never applied the long key");
            if (standby.ListDatabases().Any(d => d.Uid == db.Uid))
            {
                var copy = standby.OpenDatabase("db");
                if (copy.Boxes.Find("box") is { } copyBox)
                {
                    using var snapshot = copy.Snapshot();
                    if (snapshot.Get(copyBox, longKey) is { } value)
                    {
                        Assert.Equal("v"u8.ToArray(), value);
                        break;
                    }
                }
            }

            Thread.Sleep(50);
        }

        // Its own writes are still held to its limit (were it allowed to write): a batch refuses the key when staged.
        var local = standby.CreateDatabase("local", replicated: false);
        var localBox = local.Boxes.Create("box");
        using var refused = local.NewBatch();
        Assert.Throws<KeyTooLargeException>(() => refused.Put(localBox, longKey, []));
    }
}
