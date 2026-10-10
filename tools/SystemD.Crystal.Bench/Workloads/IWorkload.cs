using System.Collections.Generic;

namespace SystemD.Crystal.Bench.Workloads;

/// <summary>The context a workload runs in: where to put its store, and how long its cells run (spec 008 R3.1).</summary>
internal sealed record WorkloadContext(string DataParent, BenchSize Size);

/// <summary>The result of a verification (spec 008 R2): whether it passed, and what it checked or found.</summary>
internal sealed record VerificationOutcome(bool Passed, string Summary);

/// <summary>
/// One workload's result: its cells (one table), and the verification line (spec 008 R2.3, R6.1). A workload may
/// produce several tables (e.g. metrics-cost compares two runs); each is a <see cref="BenchTable"/>.
/// </summary>
internal sealed record WorkloadResult(string Name, IReadOnlyList<BenchTable> Tables, VerificationOutcome Verification);

/// <summary>
/// One table in a result section: a title, column headers, and already-formatted rows. The machine-readable JSON
/// (R6.2) comes from <paramref name="Cells"/> for cell-based workloads, or from <paramref name="JsonObjects"/> for
/// workloads that measure something other than timed cells (boxes, churn, soak). One of the two is populated.
/// </summary>
internal sealed record BenchTable(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<CellResult> Cells,
    IReadOnlyList<IReadOnlyDictionary<string, object>>? JsonObjects = null);

/// <summary>A workload the harness can run (spec 008 R5).</summary>
internal interface IWorkload
{
    /// <summary>The command name (e.g. "commit").</summary>
    string Name { get; }

    /// <summary>Runs the workload against a fresh store and returns its tables and verification.</summary>
    WorkloadResult Run(WorkloadContext context);
}
