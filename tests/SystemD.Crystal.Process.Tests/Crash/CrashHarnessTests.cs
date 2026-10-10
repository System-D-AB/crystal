// Ported from Highway: tests/Highway.Server.Tests/Storage/CrashReplayTests.cs
// (HardKillMidStream_EveryAckedCommitSurvives, RecoveredState_IsByteIdenticalToReferenceRun)
// Spec 004 task T4: the crash harness and its runner work. These are the harness's own proof, in Strict mode; the
// kill tests of every durability mode are spec 006's T7, deferred by the maintainer.

using System;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Crash;

public sealed class CrashHarnessTests
{
    // Ported from Highway: CrashReplayTests.HardKillMidStream_EveryAckedCommitSurvives — with several kills of one store,
    // a seeded random kill point each time, and the batch checked whole across two boxes.
    [Fact]
    public void HardKillMidStream_EveryAcknowledgedCommitSurvives_WholeAndInOrder()
    {
        using var dir = new TempDirectory("crystal-crash");
        var random = new Random(4004);
        long lastAcked = -1;

        for (var kill = 0; kill < 3; kill++)
        {
            var run = CrashHarnessRunner.RunAndKill(dir.Path, DurabilityMode.Strict, acks: random.Next(50, 400));
            Assert.True(run.FirstIndex >= lastAcked + 1, $"It resumed at {run.FirstIndex}, repeating batches up to {lastAcked}.");
            lastAcked = run.LastAcked;

            var audit = CrashHarnessRunner.AuditStore(dir.Path);
            Assert.True(audit.Sound, string.Join(Environment.NewLine, audit.Problems));
            Assert.True(audit.Batches >= lastAcked + 1, $"Acknowledged {lastAcked + 1} batches, recovered {audit.Batches}.");
        }
    }

    // Ported from Highway: CrashReplayTests.RecoveredState_IsByteIdenticalToReferenceRun
    [Fact]
    public void RecoveredState_IsByteIdenticalToAReferenceRun()
    {
        const int n = 100;
        using var reference = new TempDirectory("crystal-reference");
        using var crashed = new TempDirectory("crystal-crash");

        CrashHarnessRunner.RunToCompletion(reference.Path, DurabilityMode.Strict, n);
        var run = CrashHarnessRunner.RunAndKill(crashed.Path, DurabilityMode.Strict, acks: n + 1);

        var referenceAudit = CrashHarnessRunner.AuditStore(reference.Path);
        var crashedAudit = CrashHarnessRunner.AuditStore(crashed.Path);

        Assert.True(referenceAudit.Sound, string.Join(Environment.NewLine, referenceAudit.Problems));
        Assert.Equal(n, referenceAudit.Batches);
        Assert.True(crashedAudit.Sound, string.Join(Environment.NewLine, crashedAudit.Problems));
        Assert.True(crashedAudit.Batches >= n);
        Assert.True(run.LastAcked >= n);

        // And byte for byte against each other: the first n batches of the crashed store are the reference store's.
        var (referenceEntries, referenceMarks) = CrashHarnessRunner.Rows(reference.Path);
        var (crashedEntries, crashedMarks) = CrashHarnessRunner.Rows(crashed.Path);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(referenceEntries[i].Key, crashedEntries[i].Key);
            Assert.Equal(referenceEntries[i].Value, crashedEntries[i].Value);
            Assert.Equal(referenceMarks[i].Value, crashedMarks[i].Value);
        }
    }
}
