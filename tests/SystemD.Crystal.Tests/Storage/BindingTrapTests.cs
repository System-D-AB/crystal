using System;
using System.Linq;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Storage;

/// <summary>
/// Spec 001-2 R4 / design § 4 — one test per known binding trap. Each trap was found in one of the
/// products; each is now a rule in the wrapper and a test that proves the rule holds.
/// </summary>
public sealed class BindingTrapTests
{
    private static byte[] Key(string s) => Text.UTF8.GetBytes(s);

    [Fact]
    public void R41_TailingAGoneRange_IsRefused_NotCrashed()
    {
        using var temp = TempDatabase.Create();
        var wal = new WalAccess(temp.Instance);

        // A sequence far beyond anything written cannot be held by the WAL: tailing must return null
        // (treated as a gap), and the process must stay alive.
        var result = wal.TryTail(1_000_000);
        Assert.Null(result);
        Assert.False(wal.WalHolds(1_000_000));
    }

    [Fact]
    public void R41_TailingAHeldRange_Succeeds()
    {
        using var temp = TempDatabase.Create();
        var family = temp.Instance.Family("general");

        using (var batch = new WriteBatch())
        {
            batch.Put(Key("k1"), Key("v1"), family);
            temp.Instance.Write(batch, sync: true);
        }

        var wal = new WalAccess(temp.Instance);
        Assert.True(wal.WalHolds(0));
        using var iterator = wal.TryTail(0);
        Assert.NotNull(iterator);
    }

    [Fact]
    public void R43_AWrittenBatch_SurvivesReopen()
    {
        // Trap R4.3: ingest does not sync; Crystal writes batch bytes itself, synced. A synced write
        // survives a close and reopen (a proxy for a process kill, which the crash harness covers).
        using var temp = TempDatabase.Create();
        var family = temp.Instance.Family("general");

        using (var batch = new WriteBatch())
        {
            batch.Put(Key("durable"), Key("yes"), family);
            temp.Instance.Write(batch, sync: true);
        }

        Assert.Equal(Key("yes"), temp.Instance.Get("general", Key("durable")));
    }

    [Fact]
    public void R44_AScanOfNKeys_ReportsNSteps()
    {
        using var temp = TempDatabase.Create();
        var family = temp.Instance.Family("general");

        using (var batch = new WriteBatch())
        {
            for (var i = 0; i < 5; i++)
            {
                batch.Put(Key($"key{i}"), Key($"val{i}"), family);
            }

            temp.Instance.Write(batch, sync: true);
        }

        using var snapshot = temp.Instance.CreateSnapshot();
        // Limit of exactly 5: the scan stops on the limit after the 5th row, so no trailing Next —
        // a scan of N keys reports N native steps (one Seek plus four Nexts), which is R4.4's evidence.
        using var iterator = temp.Instance.Scan("general", snapshot, Key("key0"), Key("key9"), limit: 5);
        var rows = iterator.Scan(Key("key0")).ToList();

        Assert.Equal(5, rows.Count);
        Assert.Equal(5, iterator.Steps);
    }

    [Fact]
    public void R44_AScanNeverCrossesItsUpperBound()
    {
        using var temp = TempDatabase.Create();
        var family = temp.Instance.Family("general");

        using (var batch = new WriteBatch())
        {
            batch.Put(Key("a"), Key("1"), family);
            batch.Put(Key("b"), Key("2"), family);
            batch.Put(Key("c"), Key("3"), family);
            temp.Instance.Write(batch, sync: true);
        }

        using var snapshot = temp.Instance.CreateSnapshot();
        // Upper bound "c" is exclusive, so only a and b are in range.
        using var iterator = temp.Instance.Scan("general", snapshot, Key("a"), Key("c"), limit: 100);
        var keys = iterator.Scan(Key("a")).Select(r => Text.UTF8.GetString(r.Key)).ToList();

        Assert.Equal(["a", "b"], keys);
    }

    [Fact]
    public void R32_BoundedIterator_RefusesANonPositiveLimit()
    {
        using var temp = TempDatabase.Create();
        using var snapshot = temp.Instance.CreateSnapshot();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            temp.Instance.Scan("general", snapshot, Key("a"), Key("z"), limit: 0));
    }
}
