// Spec 011 R5 — no deadlock in role changes or events, each forced at the exact interleaving with hooks (playbook § 4),
// and run with a normal and a starved thread pool. Every wait is bounded: a deadlock fails the test, never hangs the run.

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class DeadlockTests
{
    private const string Secret = "test-secret";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The three-way cycle of the 2026-10-12 review (R5.1): a re-promotion of a primary holds the store gate and waits for
    /// the epoch's lock; a higher epoch from an authenticated peer holds the epoch's lock and shuts the write gate, which
    /// waits for an admitted write; that write is a replicated CreateDatabase waiting for the store gate.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APromotion_AHigherEpoch_AndAnAdmittedCreate_AllComplete(bool starvedThreadPool)
    {
        using var starve = starvedThreadPool ? new StarvedThreadPool() : null;
        using var promotionInside = new ManualResetEventSlim(false);
        using var releasePromotion = new ManualResetEventSlim(false);
        using var createAdmitted = new ManualResetEventSlim(false);
        var pausePromotion = 0;

        // Not disposed with `using`: after a real deadlock, disposing would hang the run on the stuck threads (background
        // threads, so the process still exits). It is disposed below once the three operations have completed.
        var dir = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = Secret, ListenEndpoint = "127.0.0.1:0" },
            Hook = step =>
            {
                if (step == "promote.incarnations" && Interlocked.Exchange(ref pausePromotion, 0) == 1)
                {
                    promotionInside.Set();
                    releasePromotion.Wait(Bound);
                }
                else if (step == "create.admitted")
                {
                    createAdmitted.Set();
                }
            },
        });
        store.Replication.Listen();
        var epoch = store.Replication.Epoch;

        // Read before the promotion: the facade's members take its lock, which a promotion holds throughout.
        var endpoint = store.Replication.ListenerEndpoint!;
        var port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        // 1. A re-promotion of the primary enters the store gate and pauses there, before it takes the epoch's lock.
        Volatile.Write(ref pausePromotion, 1);
        var promote = Task.Factory.StartNew(() => store.Replication.Promote("again"), TaskCreationOptions.LongRunning);
        Assert.True(promotionInside.Wait(Bound), "the promotion did not reach the store gate");

        // 2. A replicated create is admitted by the write gate, then waits for the store gate the promotion holds.
        var create = Task.Factory.StartNew(() => store.CreateDatabase("created", replicated: true), TaskCreationOptions.LongRunning);
        Assert.True(createAdmitted.Wait(Bound), "the create was not admitted");

        // 3. An authenticated peer states a higher epoch: the epoch's lock is taken and the write gate shut.
        var peer = Task.Factory.StartNew(
            () =>
            {
                using var tcp = new TcpClient();
                tcp.Connect("127.0.0.1", port);
                var client = new ReplicationClient(tcp.GetStream(), "peer-node", Secret);
                client.HandshakeAsync(ReplicationProtocol.RoleStandby, epoch + 10, CancellationToken.None).GetAwaiter().GetResult();
            },
            TaskCreationOptions.LongRunning);
        Assert.True(SpinWait.SpinUntil(() => store.Engine.WriteGate.RefusedAs is not null || peer.IsCompleted, Bound), "the gate was not shut");
        Assert.False(peer.IsFaulted, peer.Exception?.ToString());
        Assert.NotNull(store.Engine.WriteGate.RefusedAs);

        // 4. Let the promotion go on: it now needs the epoch's lock. Before the fix, the peer's thread held that lock
        //    while waiting for the create, which waited for the promotion: none of the three ever finished.
        releasePromotion.Set();
        var all = Task.WhenAll(promote, create, peer);
        var finished = await Task.WhenAny(all, Task.Delay(Bound));
        Assert.True(finished == all, "deadlock: the promotion, the create and the higher epoch did not all complete");
        await all;

        // The create was admitted before the gate shut, so it completed; the drain waited for it.
        Assert.Equal(0, store.Engine.WriteGate.InFlight);
        Assert.Contains(store.ListDatabases(), d => d.Name == "created");
        store.Dispose();
        dir.Dispose();
    }

    /// <summary>
    /// R5.2: a handler of an event the follower raised calls Status() and then Promote. Promote stops the follower and
    /// waits for it; before the fix the follower was the thread running the handler, so neither ever finished.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandlerOfAFollowerEvent_CanCallStatusAndPromote(bool starvedThreadPool)
    {
        using var starve = starvedThreadPool ? new StarvedThreadPool() : null;

        // A "primary" that refuses this node's format: the follower raises FormatRefused on its own thread.
        var fake = new TcpListener(System.Net.IPAddress.Loopback, 0);
        fake.Start();
        var refusing = Task.Run(async () =>
        {
            while (true)
            {
                using var peer = await fake.AcceptTcpClientAsync();
                var buffer = new byte[4096];
                _ = await peer.GetStream().ReadAsync(buffer);
                var refusal = System.Text.Encoding.ASCII.GetBytes($"-{ReplicationProtocol.ErrFormat} ours=crystal 999 theirs={ReplicationProtocol.Format}\r\n");
                await peer.GetStream().WriteAsync(refusal);
            }
        });

        // Disposed only on success (as above): after a deadlock, disposing would hang the run.
        var dir = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = Secret },
        });
        var handled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Replication.Events += (_, e) =>
        {
            if (e.Kind == ReplicationEventKind.FormatRefused && !handled.Task.IsCompleted)
            {
                _ = store.Replication.Status();
                handled.TrySetResult(store.Replication.Promote("from a handler"));
            }
        };

        store.Replication.Follow($"127.0.0.1:{((System.Net.IPEndPoint)fake.LocalEndpoint).Port}");
        var finished = await Task.WhenAny(handled.Task, Task.Delay(Bound));
        fake.Stop();
        Assert.True(finished == handled.Task, "deadlock: a handler calling Promote on a follower event never finished");
        Assert.Equal(ReplicationRole.Primary, store.Replication.Role);
        store.Dispose();
        dir.Dispose();
        _ = refusing;
    }

    [Fact]
    public void AHandlerThatThrows_DoesNotStopTheNextEvent()
    {
        // R5.3: the exception is logged; the next event still reaches every handler.
        using var dir = new TempStoreDirectory();
        using var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = new ReplicationOptions { Secret = Secret },
        });
        var seen = new System.Collections.Concurrent.ConcurrentQueue<ReplicationEventKind>();
        store.Replication.Events += (_, e) =>
        {
            seen.Enqueue(e.Kind);
            if (e.Kind == ReplicationEventKind.Fenced)
            {
                throw new InvalidOperationException("a handler's own bug");
            }
        };

        store.Replication.Fence("first");
        store.Replication.Promote("second");
        EventDelivery.Drain(store);

        Assert.Equal([ReplicationEventKind.Fenced, ReplicationEventKind.Promoted], seen.ToArray());
    }

    /// <summary>Caps the thread pool low for one test, restoring it after (playbook § 4: concurrency under starvation).</summary>
    private sealed class StarvedThreadPool : IDisposable
    {
        // The pool's maximum cannot be set below the processor count (SetMaxThreads then refuses, which the first form of
        // this helper did not notice: review 2026-10-13, M2). Starvation is made instead: the minimum is set to 1, so the
        // pool grows only slowly, and every thread it has is taken by a work item that blocks until disposal.
        private readonly int _minWorkers;
        private readonly int _minIo;
        private readonly ManualResetEventSlim _release = new(false);

        public StarvedThreadPool()
        {
            ThreadPool.GetMinThreads(out _minWorkers, out _minIo);
            // All but two of the pool's threads are taken: the code under test has two, and gains more only slowly.
            // (Taking all of them, or more, stalls every asynchronous continuation for tens of seconds: no deadlock,
            // but nothing a bounded test can tell from one.)
            Assert.True(ThreadPool.SetMinThreads(1, 1), "the pool's minimum could not be lowered");
            for (var i = 0; i < Math.Max(0, ThreadPool.ThreadCount - 2); i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => _release.Wait(), null);
            }
        }

        public void Dispose()
        {
            _release.Set();
            ThreadPool.SetMinThreads(_minWorkers, _minIo);
        }
    }
}
