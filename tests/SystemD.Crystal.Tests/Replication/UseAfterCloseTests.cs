// Spec 011 R6 — clearing a database's tail and held WAL readers (an epoch or role change) never touches a closed native
// instance. Two guards, each tested on its own because either alone stops the review's race: the source enters the
// database to clear it, so a drop waits (R6.1); and the reader pool clears under the gate its disposal takes, so a
// close of the database waits for a clear in progress (R6.2). Each test pauses the clear with a hook at the exact point
// and checks the close waits. A failure leaves the paused thread blocked (a background thread) instead of releasing it
// into a closed instance, and skips disposal (which would wait on that thread), so a regression fails the test rather
// than crashing or hanging the run.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class UseAfterCloseTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static ReplicationOptions Repl() => new()
    {
        Secret = "test-secret",
        WalTtl = TimeSpan.FromHours(24),
        WalBytesPerDatabase = 1L << 30,
        TailBytesPerDatabase = 16L << 20,
    };

    /// <summary>A replicated database written before any pull, and one page served from the WAL: one reader held idle.</summary>
    private static (CrystalDatabase Db, DatabaseReplication Serving) WithAnIdleReader(CrystalStore store, ReplicationSource source)
    {
        var db = store.CreateDatabase("db", replicated: true);
        var box = db.Boxes.Create("b");
        var start = (long)db.Slot.LatestSequence;
        var value = new byte[150];
        for (var i = 0; i < 200; i++)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes($"key-{i:D6}"), value);
            batch.Commit(DurabilityMode.Strict);
        }

        var serving = source.ServingForTest(db.Uid)!;
        var page = new List<(ulong Seq, byte[] Data)>();
        Assert.Equal(ServeResult.Served, serving.Serve(start, 4 * 1024, page, out _));
        Assert.True(serving.HoldingWal, "a reader is held idle");
        return (db, serving);
    }

    [Fact]
    public async Task ADrop_WaitsForAnEpochChangeClearingTheDatabase()
    {
        // R6.1: ClearAllHistory pauses inside the database; the drop must wait for it.
        using var inside = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false); // never disposed: a failing run leaves a thread waiting on it
        var pause = 0;
        var dir = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path, new CrystalOptions
        {
            Replication = Repl(),
            Hook = step =>
            {
                if (step == "clear.entered" && Interlocked.Exchange(ref pause, 0) == 1)
                {
                    inside.Set();
                    release.Wait();
                }
            },
        });
        var epoch = ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
        var source = new ReplicationSource(store, store.Engine, store.NodeId, epoch, Repl());
        var (db, _) = WithAnIdleReader(store, source);

        Volatile.Write(ref pause, 1);
        var clear = Task.Factory.StartNew(source.ClearAllHistory, TaskCreationOptions.LongRunning);
        Assert.True(inside.Wait(Bound), "the clear did not enter the database");

        var drop = Task.Factory.StartNew(() => store.DropDatabase(db), TaskCreationOptions.LongRunning);
        await Task.Delay(300);
        Assert.False(drop.IsCompleted, "the drop closed the database while an epoch change was clearing it");

        release.Set();
        await clear.WaitAsync(Bound);
        await drop.WaitAsync(Bound);
        Assert.DoesNotContain(store.ListDatabases(), d => d.Name == "db");
        Dispose(source, store, dir);
    }

    [Fact]
    public async Task ClosingTheReaderPool_WaitsForAClearClosingItsReaders()
    {
        // R6.2: the pool's Clear pauses after taking its idle readers, before closing them. Releasing the serving state
        // (what a database's close does first, before disposing its instance) disposes the pool, and must wait for the
        // clear, so no reader is closed on a disposed instance. Checked on the release itself: a drop is not a precise
        // signal here, because a reader still open keeps the WAL file in use and stalls the drop's folder delete anyway.
        var dir = new TempStoreDirectory();
        var store = CrystalStore.Open(dir.Path, new CrystalOptions { Replication = Repl() });
        var epoch = ReplicationEpoch.Load(dir.Path, SystemD.Crystal.Storage.ReplicationRole.Primary);
        var source = new ReplicationSource(store, store.Engine, store.NodeId, epoch, Repl());
        var (_, serving) = WithAnIdleReader(store, source);

        using var taken = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false); // never disposed: a failing run leaves a thread waiting on it
        var pause = 1;
        serving.OnReadersClearTaken = () =>
        {
            // Only the clear pauses: the pool's disposal passes the same point and must not be held here.
            if (Interlocked.Exchange(ref pause, 0) == 1)
            {
                taken.Set();
                release.Wait();
            }
        };

        // The pool's own Clear, directly: the database is not entered, so only the pool's gate stands between them.
        var clear = Task.Factory.StartNew(serving.ClearTail, TaskCreationOptions.LongRunning);
        Assert.True(taken.Wait(Bound), "the clear did not take the readers");

        var detach = Task.Factory.StartNew(serving.Detach, TaskCreationOptions.LongRunning);
        await Task.Delay(300);
        Assert.False(detach.IsCompleted, "the reader pool was disposed while a clear was closing its readers");

        serving.OnReadersClearTaken = null;
        release.Set();
        await clear.WaitAsync(Bound);
        await detach.WaitAsync(Bound);
        Assert.False(serving.HoldingWal);
        Dispose(source, store, dir);
    }

    // Only on success: after a failed assertion a thread is still paused holding what disposal would wait for.
    private static void Dispose(ReplicationSource source, CrystalStore store, TempStoreDirectory dir)
    {
        source.Dispose();
        store.Dispose();
        dir.Dispose();
    }
}
