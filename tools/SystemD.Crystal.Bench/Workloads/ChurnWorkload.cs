using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `churn` (spec 008 R5.4) — profiles under constant insert and delete: a queue-like pattern on one box, run once
/// with General and once with DeleteHeavy. Insert at the tail, delete from the head; scan the first 10 keys after
/// every 100 operations. Reports, every 10 % of the run, the native steps per head scan (from
/// <c>crystal.scan.steps</c>, read through a MeterListener on this store's node) and operations per second. This is
/// the evidence 001-2 T11 could not produce at unit-test scale (R9.3).
/// </summary>
internal sealed class ChurnWorkload : IWorkload
{
    public string Name => "churn";

    public WorkloadResult Run(WorkloadContext context)
    {
        var operations = context.Size == BenchSize.Quick ? 100_000 : 5_000_000;

        var general = RunProfile(context, BoxProfile.General, operations);
        var deleteHeavy = RunProfile(context, BoxProfile.DeleteHeavy, operations);

        // One row per 10 % interval, the two profiles side by side.
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < general.Intervals.Count; i++)
        {
            var g = general.Intervals[i];
            var d = deleteHeavy.Intervals[i];
            rows.Add([
                $"{(i + 1) * 10}%",
                $"{g.OpsPerSec:N0}",
                $"{g.StepsPerHeadScan:N1}",
                $"{d.OpsPerSec:N0}",
                $"{d.StepsPerHeadScan:N1}",
            ]);
        }

        var jsonObjects = new List<IReadOnlyDictionary<string, object>>();
        for (var i = 0; i < general.Intervals.Count; i++)
        {
            jsonObjects.Add(new Dictionary<string, object>
            {
                ["percent"] = (i + 1) * 10,
                ["generalOpsPerSec"] = general.Intervals[i].OpsPerSec,
                ["generalStepsPerScan"] = general.Intervals[i].StepsPerHeadScan,
                ["deleteHeavyOpsPerSec"] = deleteHeavy.Intervals[i].OpsPerSec,
                ["deleteHeavyStepsPerScan"] = deleteHeavy.Intervals[i].StepsPerHeadScan,
            });
        }

        var table = new BenchTable(
            $"{operations:N0} ops, head scan of 10 keys every 100 ops",
            ["Progress", "General ops/s", "General steps/scan", "DeleteHeavy ops/s", "DeleteHeavy steps/scan"],
            rows,
            [],
            jsonObjects);

        var passed = general.Verified && deleteHeavy.Verified;
        var summary = passed
            ? $"verified both boxes hold exactly the model's keys after {operations:N0} ops (General and DeleteHeavy)"
            : "FAILED: a box did not hold exactly the model's keys at the end";

        return new WorkloadResult(Name, [table], new VerificationOutcome(passed, summary));
    }

    private static ProfileRun RunProfile(WorkloadContext context, BoxProfile profile, int operations)
    {
        using var temp = TempStore.Create(context.DataParent);
        var db = temp.Store.CreateDatabase("churn");
        var box = db.Boxes.Create(profile.ToString(), profile);

        // Listen for crystal.scan.steps on this store's node only (design § 5): filter by the meter name and the
        // crystal.node tag equal to this store's NodeId, so a parallel store never pollutes the reading.
        var nodeId = temp.Store.NodeId;
        long stepsSum = 0;
        long stepsCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "SystemD.Crystal"
                && instrument.Name == "crystal.scan.steps"
                && MeterTaggedWithNode(instrument.Meter, nodeId))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            Interlocked.Add(ref stepsSum, measurement);
            Interlocked.Increment(ref stepsCount);
        });
        listener.Start();

        var intervals = new List<ChurnInterval>();
        var intervalSize = operations / 10;
        var head = 0L;
        var tail = 0L;
        var model = new SortedSet<long>();

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var intervalStart = System.Diagnostics.Stopwatch.StartNew();
        var stepsAtIntervalStart = 0L;
        var scansAtIntervalStart = 0L;

        for (var op = 1; op <= operations; op++)
        {
            // Insert at the tail.
            using (var batch = db.NewBatch())
            {
                batch.Put(box, SeqKey(tail), Values.Make(0, tail, 16));
                batch.Commit(DurabilityMode.Buffered);
            }

            model.Add(tail);
            tail++;

            // Delete from the head once there is a backlog, keeping the live window bounded.
            if (tail - head > 1_000)
            {
                using var batch = db.NewBatch();
                batch.Delete(box, SeqKey(head));
                batch.Commit(DurabilityMode.Buffered);
                model.Remove(head);
                head++;
            }

            // A head scan of 10 keys every 100 operations (records crystal.scan.steps).
            if (op % 100 == 0)
            {
                using var snap = db.Snapshot();
                _ = snap.Scan(box, SeqKey(head), [], 10).ToList();
            }

            if (op % intervalSize == 0)
            {
                listener.RecordObservableInstruments();
                var elapsed = intervalStart.Elapsed.TotalSeconds;
                var stepsNow = Interlocked.Read(ref stepsSum);
                var scansNow = Interlocked.Read(ref stepsCount);
                var intervalSteps = stepsNow - stepsAtIntervalStart;
                var intervalScans = scansNow - scansAtIntervalStart;
                intervals.Add(new ChurnInterval(
                    OpsPerSec: elapsed > 0 ? intervalSize / elapsed : 0,
                    StepsPerHeadScan: intervalScans > 0 ? (double)intervalSteps / intervalScans : 0));
                stepsAtIntervalStart = stepsNow;
                scansAtIntervalStart = scansNow;
                intervalStart.Restart();
            }
        }

        temp.Store.SyncAll();

        // Verify the box holds exactly the model's keys.
        var verified = VerifyBox(db, box, model);
        return new ProfileRun(intervals, verified);
    }

    private static bool MeterTaggedWithNode(Meter meter, string nodeId)
    {
        if (meter.Tags is null)
        {
            return false;
        }

        foreach (var tag in meter.Tags)
        {
            if (tag.Key == "crystal.node" && tag.Value is string s && s == nodeId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool VerifyBox(CrystalDatabase db, Box box, SortedSet<long> model)
    {
        using var snap = db.Snapshot();
        var actual = new List<long>();
        byte[] from = [];
        const int page = 10_000;
        while (true)
        {
            var batch = snap.Scan(box, from, [], page).ToList();
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var kv in batch)
            {
                actual.Add(BinaryPrimitives.ReadInt64BigEndian(kv.Key));
            }

            if (batch.Count < page)
            {
                break;
            }

            var last = batch[^1].Key;
            from = new byte[last.Length + 1];
            last.CopyTo(from, 0);
        }

        return actual.Count == model.Count && actual.SequenceEqual(model);
    }

    private static byte[] SeqKey(long n)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, n);
        return key;
    }

    private sealed record ChurnInterval(double OpsPerSec, double StepsPerHeadScan);

    private sealed record ProfileRun(IReadOnlyList<ChurnInterval> Intervals, bool Verified);
}
