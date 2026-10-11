// Spec 003 T6 / R4.1–R4.5 — the pure apply logic of BatchApplier over a fake target: skip batches at or below the
// watermark, concatenate a contiguous run into one write, refuse a stale epoch, and flag box-0 (catalogue) writes.
// Ported from Highway's BatchApplier behaviour. The live-database apply (one synced write through RocksInstance.Write,
// a box visible by name on the standby, standby write refusal) is T8; here the decision logic, unit-tested.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RocksDbSharp;
using SystemD.Crystal.Replication;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

public sealed class BatchApplierTests
{
    /// <summary>A fake apply target: records every raw write and reports a settable watermark.</summary>
    private sealed class FakeTarget : IApplyTarget
    {
        public ulong Watermark { get; set; }

        public List<byte[]> Writes { get; } = [];

        public void ApplyRaw(byte[] batchBytes) => Writes.Add(batchBytes);
    }

    // A batch of `count` ops starting at `seq`, built with a real WriteBatch so its bytes parse like the WAL's. The
    // key's first 4 bytes are the box id, big-endian (box-layout § 3.2): box 0 is 00 00 00 00 (the catalogue).
    private static byte[] Batch(ulong seq, int count, uint boxId = 0)
    {
        using var wb = new WriteBatch();
        for (var i = 0; i < count; i++)
        {
            var key = new byte[5];
            BinaryPrimitives.WriteUInt32BigEndian(key, boxId);
            key[4] = (byte)i;
            wb.Put(key, new byte[] { 9 });
        }

        var data = wb.ToBytes();
        BinaryPrimitives.WriteUInt64LittleEndian(data, seq);
        return data;
    }

    private static (ulong, byte[]) Entry(ulong seq, int count, uint boxId = 0) => (seq, Batch(seq, count, boxId));

    [Fact]
    public void SkipsBatches_AtOrBelowTheWatermark()
    {
        var target = new FakeTarget { Watermark = 5 };
        var applier = new BatchApplier(target);

        // Seqs 3, 4 are already applied (<= 5); 6, 7 are fresh.
        var result = applier.ApplyPage(epoch: 1, [Entry(3, 1), Entry(4, 1), Entry(6, 1), Entry(7, 1)]);

        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.Equal(2, result.Applied);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public void ConcatenatesAContiguousRun_IntoOneWrite()
    {
        var target = new FakeTarget { Watermark = 0 };
        var applier = new BatchApplier(target);

        // 1 (count 2 => 1..2), 3 (count 1), 4 (count 3 => 4..6): contiguous, so one merged write.
        var result = applier.ApplyPage(epoch: 1, [Entry(1, 2), Entry(3, 1), Entry(4, 3)]);

        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.Equal(3, result.Applied);
        Assert.Single(target.Writes);

        var merged = target.Writes[0];
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(merged));          // first sequence
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(merged.AsSpan(8))); // 2 + 1 + 3 ops
    }

    [Fact]
    public void APageWithAHole_IsRefusedWhole_WritingNothing()
    {
        // 1 (count 1) then 5 (count 1): 2..4 are missing. Writing it would number the second batch 2 on the standby, and
        // every position after it would stop matching the primary's (003 should-fix 7; this test used to expect both
        // batches written one by one).
        var target = new FakeTarget { Watermark = 0 };
        var applier = new BatchApplier(target);

        var result = applier.ApplyPage(epoch: 1, [Entry(1, 1), Entry(5, 1)]);

        Assert.Equal(ApplyStatus.NotContiguous, result.Status);
        Assert.Equal(0, result.Applied);
        Assert.Empty(target.Writes);
    }

    [Fact]
    public void APageThatDoesNotStartAfterThePosition_IsRefused()
    {
        var target = new FakeTarget { Watermark = 3 };
        var applier = new BatchApplier(target);

        var result = applier.ApplyPage(epoch: 1, [Entry(6, 2)]); // 4 and 5 are missing

        Assert.Equal(ApplyStatus.NotContiguous, result.Status);
        Assert.Empty(target.Writes);
    }

    [Fact]
    public void ABatchStraddlingThePosition_IsRefused_NotSkippedWhole()
    {
        // 3..6 straddles a position of 4: skipping it would silently lose 5 and 6 (nothing after it shows the hole);
        // writing it would apply 3 and 4 twice.
        var target = new FakeTarget { Watermark = 4 };
        var applier = new BatchApplier(target);

        var result = applier.ApplyPage(epoch: 1, [Entry(3, 4)]);

        Assert.Equal(ApplyStatus.NotContiguous, result.Status);
        Assert.Empty(target.Writes);
    }

    [Fact]
    public void ARePulledPage_ThenItsContinuation_IsAppliedFromThePosition()
    {
        var target = new FakeTarget { Watermark = 4 };
        var applier = new BatchApplier(target);

        var result = applier.ApplyPage(epoch: 1, [Entry(1, 2), Entry(3, 2), Entry(5, 3), Entry(8, 1)]);

        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(2, result.Applied);
        Assert.Single(target.Writes); // 5..8 as one write
    }

    [Fact]
    public void RefusesAStaleEpoch_WritingNothing()
    {
        var target = new FakeTarget { Watermark = 0 };
        var applier = new BatchApplier(target, initialEpoch: 7);

        var result = applier.ApplyPage(epoch: 6, [Entry(1, 1)]);

        Assert.Equal(ApplyStatus.EpochRefused, result.Status);
        Assert.Equal(0, result.Applied);
        Assert.Empty(target.Writes);
        Assert.Equal(7UL, applier.SeenEpoch);
    }

    [Fact]
    public void AdoptsAHigherEpoch_OnApply()
    {
        var target = new FakeTarget { Watermark = 0 };
        var applier = new BatchApplier(target, initialEpoch: 3);

        applier.ApplyPage(epoch: 9, [Entry(1, 1)]);

        Assert.Equal(9UL, applier.SeenEpoch);
    }

    [Fact]
    public void FlagsCatalogueWrites_WhenBoxZeroIsTouched()
    {
        var target = new FakeTarget { Watermark = 0 };
        var applier = new BatchApplier(target);

        // Box 0 (catalogue): the key's first 4 bytes are 00 00 00 00.
        var touches = applier.ApplyPage(epoch: 1, [Entry(1, 1, boxId: 0)]);
        Assert.True(touches.TouchedCatalogue);

        // A non-zero box id: no catalogue reload needed.
        var target2 = new FakeTarget { Watermark = 0 };
        var applier2 = new BatchApplier(target2);
        var clean = applier2.ApplyPage(epoch: 1, [Entry(2, 1, boxId: 1)]);
        Assert.False(clean.TouchedCatalogue);
    }

    [Fact]
    public void AnEmptyPage_WritesNothing_AndStaysApplied()
    {
        var target = new FakeTarget { Watermark = 10 };
        var applier = new BatchApplier(target);

        var result = applier.ApplyPage(epoch: 1, []);

        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.Equal(0, result.Applied);
        Assert.Empty(target.Writes);
    }
}
