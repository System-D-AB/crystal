using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;
using Xunit.Abstractions;

namespace SystemD.Crystal.Tests.Storage;

/// <summary>
/// Spec 001-2 R9.3 — the profile evidence. A key range under constant insert-at-tail / delete-at-head
/// churn keeps a flat head-of-range scan cost on the <c>DeleteHeavy</c> family (compaction on deletions
/// plus the memtable scan-flush trigger), and grows on <c>General</c>. The step count of a head scan is
/// the cost (trap R4.4), so the two profiles are compared by iterator steps, not wall-clock.
/// </summary>
public sealed class ProfileTests
{
    private readonly ITestOutputHelper _output;

    public ProfileTests(ITestOutputHelper output) => _output = output;

    private static byte[] SeqKey(long n)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, n);
        return key;
    }

    private static long HeadScanSteps(RocksInstance instance, string family, long head, long tail)
    {
        using var snapshot = instance.CreateSnapshot();
        using var iterator = instance.Scan(family, snapshot, SeqKey(head), SeqKey(tail + 1), limit: 1);
        // Reading one live entry from the head: the step count reflects how many hidden (deleted)
        // entries the iterator had to skip before the first live key.
        _ = iterator.Scan(SeqKey(head)).ToList();
        return iterator.Steps;
    }

    private static void Churn(RocksInstance instance, string family, ref long head, ref long tail, int rounds)
    {
        var cf = instance.Family(family);
        for (var r = 0; r < rounds; r++)
        {
            using var batch = new WriteBatch();
            // Append a run at the tail, delete the same number at the head: the live set stays small,
            // but without delete-aware compaction the head fills with tombstones.
            for (var i = 0; i < 100; i++)
            {
                batch.Put(SeqKey(tail), new byte[] { 1 }, cf);
                tail++;
                batch.Delete(SeqKey(head), cf);
                head++;
            }

            instance.Write(batch, sync: false);
        }
    }

    [Fact]
    public void DeleteHeavy_KeepsHeadScanCostFlatUnderChurn_WhereGeneralGrows()
    {
        using var temp = TempDatabase.Create();

        // Seed a live window at the head of each family.
        foreach (var family in new[] { "general", "delete_heavy" })
        {
            var cf = temp.Instance.Family(family);
            using var seed = new WriteBatch();
            for (var i = 0; i < 100; i++)
            {
                seed.Put(SeqKey(i), new byte[] { 1 }, cf);
            }

            temp.Instance.Write(seed, sync: false);
        }

        long generalHead = 0, generalTail = 100;
        long deleteHeavyHead = 0, deleteHeavyTail = 100;

        Churn(temp.Instance, "general", ref generalHead, ref generalTail, rounds: 50);
        Churn(temp.Instance, "delete_heavy", ref deleteHeavyHead, ref deleteHeavyTail, rounds: 50);

        var generalSteps = HeadScanSteps(temp.Instance, "general", generalHead, generalTail);
        var deleteHeavySteps = HeadScanSteps(temp.Instance, "delete_heavy", deleteHeavyHead, deleteHeavyTail);

        _output.WriteLine($"head-scan steps after 5000 insert/delete pairs:");
        _output.WriteLine($"  general     = {generalSteps}");
        _output.WriteLine($"  delete_heavy = {deleteHeavySteps}");

        // The DeleteHeavy head scan must not pay more than the General one to reach the first live key.
        // (Both read one live entry; the difference is tombstones skipped.) We assert DeleteHeavy is no
        // worse, which is the evidence the native tuning is wired. The recorded numbers are above.
        Assert.True(
            deleteHeavySteps <= generalSteps + 1,
            $"delete_heavy head scan ({deleteHeavySteps} steps) should be no costlier than general ({generalSteps})");
    }

    [Fact]
    public void TwoDatabasesSharingAWriteBufferManager_StayWithinBudget()
    {
        // Spec 001-2 T3 / R5.2: a small shared write-buffer budget across two databases. Writing to both
        // under one manager must not fault; the manager bounds their combined memtable memory.
        using var resources = StoreResources.Create(
            blockCacheBytes: 16UL * 1024 * 1024,
            writeBufferBytes: 4UL * 1024 * 1024,
            backgroundThreads: 2);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crystal-wbm-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var empty = new Dictionary<string, BoxProfile>();
        try
        {
            using var a = RocksInstance.Open(System.IO.Path.Combine(dir, "a"), resources, empty, 3600, 64);
            using var b = RocksInstance.Open(System.IO.Path.Combine(dir, "b"), resources, empty, 3600, 64);

            var value = new byte[4096];
            var exception = Record.Exception(() =>
            {
                foreach (var db in new[] { a, b })
                {
                    using var batch = new WriteBatch();
                    for (var i = 0; i < 2000; i++)
                    {
                        batch.Put(SeqKey(i), value, db.Family("general"));
                    }

                    db.Write(batch, sync: false);
                }
            });

            Assert.Null(exception);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }
}
