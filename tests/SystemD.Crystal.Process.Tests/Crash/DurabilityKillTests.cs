// Spec 006 R7.2, R7.3 (task T7); spec 013 R3; spec 015 T6, T7 — kill tests per durability mode and the torn tail.
// The crash harness (spec 004 T4) commits a deterministic stream of two-box batches in a given mode and prints each
// acknowledged batch; the test hard-kills it and audits the store. A process kill leaves the operating system's page
// cache intact, so in every mode every acknowledged batch must survive; none may be half-present; and every value must
// be the stream's (the audit compares each against the deterministic reference). One class per mode, so the modes run
// in parallel and the whole file stays inside the suite's time budget.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SystemD.Crystal.CrashHarness;
using SystemD.Crystal.Process.Tests.Support;
using Xunit;

namespace SystemD.Crystal.Process.Tests.Crash;

public abstract class DurabilityKillTests
{
    private const int Kills = 20;

    protected abstract DurabilityMode Mode { get; }

    [Fact]
    public void TwentyKills_EveryAcknowledgedBatchSurvives_WholeInOrderAndAsWritten()
    {
        using var dir = new TempDirectory("crystal-kill-" + Mode.ToString().ToLowerInvariant());
        var random = new Random(6007 + (int)Mode);
        long lastAcked = -1;

        for (var kill = 1; kill <= Kills; kill++)
        {
            var run = CrashHarnessRunner.RunAndKill(dir.Path, Mode, acks: random.Next(20, 120));
            Assert.True(run.FirstIndex >= lastAcked + 1, $"Kill {kill}: it resumed at {run.FirstIndex}, repeating batches up to {lastAcked}.");
            lastAcked = run.LastAcked;

            var audit = CrashHarnessRunner.AuditStore(dir.Path);
            Assert.True(audit.Sound, $"Kill {kill} ({Mode}): " + string.Join(Environment.NewLine, audit.Problems));
            Assert.True(audit.Batches >= lastAcked + 1, $"Kill {kill} ({Mode}): acknowledged {lastAcked + 1} batches, recovered {audit.Batches}.");
        }
    }
}

public sealed class StrictKillTests : DurabilityKillTests
{
    protected override DurabilityMode Mode => DurabilityMode.Strict;
}

public sealed class IntervalKillTests : DurabilityKillTests
{
    protected override DurabilityMode Mode => DurabilityMode.Interval;
}

public sealed class BufferedKillTests : DurabilityKillTests
{
    protected override DurabilityMode Mode => DurabilityMode.Buffered;
}

public sealed class TornTailTests
{
    [Fact]
    public void AWalWhoseLastRecordIsTorn_Opens_AndLosesOnlyThatRecord()
    {
        // R7.3: tolerant tail recovery. The harness is killed, the WAL's last record is cut inside its payload (a write
        // torn by a crash), and the store opens with every batch but that one.
        using var dir = new TempDirectory("crystal-torn");
        CrashHarnessRunner.RunAndKill(dir.Path, DurabilityMode.Strict, acks: 200);

        // How many batches the killed store holds, read from a copy: opening a store recovers its WAL, which must not
        // happen to the original before its tail is torn.
        using var copy = new TempDirectory("crystal-torn-copy");
        CopyDirectory(dir.Path, copy.Path);
        var before = CrashHarnessRunner.AuditStore(copy.Path);
        Assert.True(before.Sound, string.Join(Environment.NewLine, before.Problems));

        var wal = NewestWal(dir.Path);
        var (start, header, payload) = LastRecord(File.ReadAllBytes(wal));
        using (var stream = new FileStream(wal, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(start + header + payload / 2); // cut inside the last record's payload
        }

        var after = CrashHarnessRunner.AuditStore(dir.Path);
        Assert.True(after.Sound, string.Join(Environment.NewLine, after.Problems));
        Assert.Equal(before.Batches - 1, after.Batches);
    }

    /// <summary>The newest write-ahead log of the harness's database.</summary>
    private static string NewestWal(string storeDir)
    {
        var database = Directory.GetDirectories(storeDir, "db-*")
            .Single(d => File.ReadAllText(Path.Combine(d, "name")).TrimEnd('\n', '\r') == CrashStream.Database);
        return Directory.GetFiles(database, "*.log")
            .OrderByDescending(f => long.Parse(Path.GetFileNameWithoutExtension(f), System.Globalization.CultureInfo.InvariantCulture))
            .First();
    }

    /// <summary>
    /// Where the last record of a RocksDB log file starts, its header length and its first fragment's payload length.
    /// The format: 32 KiB blocks of fragments, each a header (checksum 4, length 2, type 1; a recyclable log adds a
    /// 4-byte log number, types 5–8) and its payload; a block's last 6 bytes or fewer are zero padding; a zero length
    /// and type end the written part (the rest may be preallocated).
    /// </summary>
    private static (long Start, int Header, int Payload) LastRecord(byte[] log)
    {
        const int Block = 32 * 1024;
        (long Start, int Header, int Payload)? last = null;
        long pos = 0;
        while (pos + 7 <= log.Length)
        {
            var leftInBlock = Block - (int)(pos % Block);
            if (leftInBlock < 7)
            {
                pos += leftInBlock;
                continue;
            }

            int length = log[pos + 4] | (log[pos + 5] << 8);
            int type = log[pos + 6];
            if (type == 0 && length == 0)
            {
                break;
            }

            var header = type >= 5 ? 11 : 7;
            var kind = type >= 5 ? type - 4 : type; // 1 full, 2 first, 3 middle, 4 last
            if (kind is 1 or 2)
            {
                last = (pos, header, length);
            }

            pos += header + length;
        }

        Assert.True(last is not null, "The write-ahead log holds no record.");
        Assert.True(last!.Value.Payload >= 2, "The last record is too short to tear.");
        return last.Value;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) != "crystal.lock")
            {
                File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
            }
        }
    }
}
