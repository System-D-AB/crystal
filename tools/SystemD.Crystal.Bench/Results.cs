using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SystemD.Crystal.Bench.Workloads;

namespace SystemD.Crystal.Bench;

/// <summary>
/// Appends one section per run to the results log (spec 008 R6, design § 6): a heading with the date and command,
/// the conditions, one table per workload with the durability in every row, the verification line, and a JSON block
/// with the same numbers. Earlier sections are never rewritten (playbook § 3) — the file is only ever appended to.
/// </summary>
internal static class Results
{
    public static void Append(string outPath, string command, Conditions conditions, IReadOnlyList<WorkloadResult> results)
    {
        ArgumentException.ThrowIfNullOrEmpty(outPath);
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var text = Render(command, conditions, results);

        // Append only. If the file is new, add a title first.
        if (!File.Exists(outPath))
        {
            File.WriteAllText(outPath, "# Crystal measurements\n\n" +
                "> Appended by the benchmark harness (spec 008). Every section states the conditions it ran under; " +
                "earlier sections are never rewritten.\n\n", new UTF8Encoding(false));
        }

        File.AppendAllText(outPath, text, new UTF8Encoding(false));
    }

    /// <summary>Renders a section (exposed for the results test).</summary>
    public static string Render(string command, Conditions conditions, IReadOnlyList<WorkloadResult> results)
    {
        var sb = new StringBuilder();
        var debug = Conditions.IsDebug ? " · DEBUG — not representative" : string.Empty;
        sb.Append("## ").Append(conditions.DateUtc).Append(" · `").Append(command).Append('`').Append(debug).Append('\n');
        sb.Append('\n');
        sb.Append("**Conditions:** ").Append(conditions.Line()).Append('\n');
        sb.Append('\n');

        foreach (var result in results)
        {
            foreach (var table in result.Tables)
            {
                sb.Append("### ").Append(result.Name).Append(" — ").Append(table.Title).Append('\n');
                sb.Append('\n');
                sb.Append("| ").Append(string.Join(" | ", table.Headers)).Append(" |\n");
                sb.Append("|").Append(string.Join("|", Dashes(table.Headers.Count))).Append("|\n");
                foreach (var row in table.Rows)
                {
                    sb.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
                }

                sb.Append('\n');
            }

            sb.Append(result.Verification.Summary).Append('\n');
            sb.Append('\n');

            sb.Append("```json\n");
            sb.Append(Json(result)).Append('\n');
            sb.Append("```\n\n");
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Dashes(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return "---";
        }
    }

    private static string Json(WorkloadResult result)
    {
        // Hand-rolled JSON (one object per cell) so the harness references nothing beyond Crystal (R6.2). Numbers use
        // invariant formatting; strings are escaped.
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var table in result.Tables)
        {
            foreach (var cell in table.Cells)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('{');
                Field(sb, "workload", result.Name, false);
                Field(sb, "configuration", cell.Configuration, false);
                Field(sb, "durability", cell.Durability?.ToString() ?? "n/a", false);
                Number(sb, "writers", cell.Writers);
                Number(sb, "opsPerSecMedian", cell.OpsPerSecMedian);
                Number(sb, "opsPerSecMin", cell.OpsPerSecMin);
                Number(sb, "opsPerSecMax", cell.OpsPerSecMax);
                Number(sb, "p50Us", cell.P50Us);
                Number(sb, "p99Us", cell.P99Us);
                Number(sb, "maxUs", cell.MaxUs);
                Number(sb, "operations", cell.Operations);
                if (cell.AllocatedBytesPerOp is { } alloc)
                {
                    Number(sb, "allocatedBytesPerOp", alloc);
                }

                TrimComma(sb);
                sb.Append('}');
            }

            if (table.JsonObjects is not null)
            {
                foreach (var obj in table.JsonObjects)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append('{');
                    Field(sb, "workload", result.Name, false);
                    foreach (var (key, value) in obj)
                    {
                        switch (value)
                        {
                            case string s:
                                Field(sb, key, s, false);
                                break;
                            case long l:
                                Number(sb, key, l);
                                break;
                            case int n:
                                Number(sb, key, n);
                                break;
                            default:
                                Number(sb, key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                                break;
                        }
                    }

                    TrimComma(sb);
                    sb.Append('}');
                }
            }
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void Field(StringBuilder sb, string name, string value, bool _)
    {
        sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append("\",");
    }

    private static void Number(StringBuilder sb, string name, double value)
    {
        sb.Append('"').Append(name).Append("\":").Append(value.ToString("0.####", CultureInfo.InvariantCulture)).Append(',');
    }

    private static void Number(StringBuilder sb, string name, long value)
    {
        sb.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture)).Append(',');
    }

    private static void TrimComma(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] == ',')
        {
            sb.Length--;
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
