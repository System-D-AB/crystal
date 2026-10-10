// Spec 003 R9.2; design § 1, § 11 — the demote reconciliation report: what a demoting primary committed beyond the
// highest position any standby acknowledged, decoded per box, written as JSON under
// {dataDir}/replication/reconciliation/. An operator or product reads it to see what a failover may have lost. The
// raw WAL always holds the full tail; this is the legible index. Ported in spirit from Highway's report (feeder +
// WalTailSummary); Crystal writes JSON per box, never queue spaces.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SystemD.Crystal.Storage;

/// <summary>One database's unreplicated tail in the reconciliation report (spec 003 R9.2).</summary>
internal sealed record ReconciliationDatabase(
    string Uid,
    long LastAckedSeq,
    long LastLocalSeq,
    bool TailUndecodableGap,
    int UndecodedBatches,
    IReadOnlyList<BoxTailOps> Boxes);

/// <summary>Writes the demote reconciliation report as JSON (spec 003 R9.2).</summary>
internal static class Reconciliation
{
    /// <summary>
    /// Writes a report for <paramref name="databases"/> under <c>{dataDir}/replication/reconciliation/</c>, named by a
    /// GUID (unique, clock-free so it stays off the architecture gate's clock rule). Returns the file path.
    /// </summary>
    public static string Write(string dataDirectory, long epoch, IReadOnlyList<ReconciliationDatabase> databases)
    {
        var dir = Path.Combine(dataDirectory, "replication", "reconciliation");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"demote-e{epoch}-{Guid.NewGuid():N}.json");

        var dbArray = new JsonArray();
        foreach (var db in databases)
        {
            var boxes = new JsonArray();
            foreach (var box in db.Boxes)
            {
                boxes.Add(new JsonObject
                {
                    ["box"] = box.BoxId,
                    ["name"] = box.BoxName,
                    ["puts"] = box.Puts,
                    ["deletes"] = box.Deletes,
                    ["putBytes"] = box.PutBytes,
                });
            }

            dbArray.Add(new JsonObject
            {
                ["uid"] = db.Uid,
                ["lastReplicatedSeq"] = db.LastAckedSeq,
                ["lastLocalSeq"] = db.LastLocalSeq,
                ["gap"] = db.TailUndecodableGap,
                ["undecodedBatches"] = db.UndecodedBatches,
                ["boxes"] = boxes,
            });
        }

        var root = new JsonObject
        {
            ["epoch"] = epoch,
            ["note"] = "the unreplicated tail is preserved in the data directory; operator replay only — Crystal never merges",
            ["databases"] = dbArray,
        };

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
