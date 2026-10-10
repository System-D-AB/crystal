using System;
using System.Buffers.Binary;
using System.Linq;
using SystemD.Crystal;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>
/// `selftest` (spec 008 R2.4, R8.1) — proves the read-back verification can fail. It writes a small set, then,
/// through the public API and behind the model's back, deletes one key and overwrites another, and passes only if
/// verification reports exactly those two differences. A harness whose check cannot fail protects nothing.
/// </summary>
internal sealed class SelfTest : IWorkload
{
    public string Name => "selftest";

    public WorkloadResult Run(WorkloadContext context)
    {
        using var temp = TempStore.Create(context.DataParent);
        var db = temp.Store.CreateDatabase("selftest");
        var box = db.Boxes.Create("box");
        var model = new WrittenModel();

        const int count = 500;
        for (var i = 0; i < count; i++)
        {
            var key = Key(i);
            var value = Values.Make(0, i, 100);
            using var batch = db.NewBatch();
            batch.Put(box, key, value);
            batch.Commit(DurabilityMode.Buffered);
            model.Put(box, key, value);
        }

        temp.Store.SyncAll();

        // First, a clean verification must pass.
        var clean = model.Verify(db);
        if (!clean.Passed)
        {
            return Fail($"clean verification should pass but reported: {clean.Summary}");
        }

        // Now tamper behind the model's back: delete key 100, overwrite key 200 with a wrong value.
        using (var batch = db.NewBatch())
        {
            batch.Delete(box, Key(100));
            batch.Put(box, Key(200), Values.Make(999, 999, 100)); // a value the model does not expect
            batch.Commit(DurabilityMode.Strict);
        }

        var tampered = model.Verify(db);

        // It must now FAIL, with exactly two differences: the missing key and the changed value.
        var passed = !tampered.Passed && tampered.DifferenceCount == 2;
        var summary = passed
            ? $"verification correctly caught 2 planted differences ({tampered.Summary})"
            : $"verification did NOT catch the planted differences as expected: {tampered.Summary}";

        var table = new BenchTable(
            "selftest",
            ["check", "result"],
            [
                ["clean verify passes", clean.Passed ? "yes" : "no"],
                ["tamper detected", tampered is { Passed: false } ? "yes" : "no"],
                ["differences", tampered.DifferenceCount.ToString()],
            ],
            []);

        return new WorkloadResult(Name, [table], new VerificationOutcome(passed, summary));
    }

    private static byte[] Key(int i)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, i);
        return key;
    }

    private WorkloadResult Fail(string message) =>
        new(Name, [new BenchTable("selftest", ["check", "result"], [["error", message]], [])],
            new VerificationOutcome(false, message));
}
