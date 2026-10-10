using System;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Storage;

/// <summary>
/// Spec 001-2 R9.4 — approximate sizes are within a stated tolerance of the real data, including data
/// still in the memtable (no flush before the estimate).
/// </summary>
public sealed class EstimateTests
{
    [Fact]
    public void ApproximateSizes_IncludingMemtable_IsNonZeroForWrittenData()
    {
        using var temp = TempDatabase.Create();
        var family = temp.Instance.Family("general");

        // Write ~1 MB across many keys, do NOT flush, so the data is still in the memtable.
        var value = new byte[1024];
        Array.Fill(value, (byte)'x');
        using (var batch = new WriteBatch())
        {
            for (var i = 0; i < 1000; i++)
            {
                batch.Put(Text.UTF8.GetBytes($"key{i:D6}"), value, family);
            }

            temp.Instance.Write(batch, sync: true);
        }

        var sizes = NativeRocks.ApproximateSizes(
            temp.Instance.Db,
            family,
            [(Text.UTF8.GetBytes("key000000"), Text.UTF8.GetBytes("key999999"))]);

        Assert.Single(sizes);
        // Memtable-inclusive estimate must see the ~1 MB we wrote; tolerate RocksDB's approximation.
        Assert.True(sizes[0] > 256UL * 1024, $"estimate {sizes[0]} should reflect ~1 MB of memtable data");
    }

    [Fact]
    public void ApproximateSizes_OfAnEmptyRangeList_IsEmpty()
    {
        using var temp = TempDatabase.Create();
        var sizes = NativeRocks.ApproximateSizes(temp.Instance.Db, temp.Instance.Family("general"), []);
        Assert.Empty(sizes);
    }
}
