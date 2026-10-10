using RocksDbSharp;
using SystemD.Crystal.Tests.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

/// <summary>
/// Spec 001 R4 — the native smoke test. It proves the RocksDB native library loads on this
/// platform and that a write survives a read, through the internal
/// <see cref="SystemD.Crystal.Storage.RocksInstance"/> (reached via <c>InternalsVisibleTo</c>). It went through the
/// skeleton's <c>RocksStore</c> seam until spec 002 replaced that seam, as the seam's own comment said it would.
/// The supported platforms are the ones on which this passes (R4.2), established by CI, not assumed.
/// </summary>
public sealed class NativeSmokeTests
{
    [Fact]
    public void OpenPutGet_InATemporaryDirectory_SurvivesAReadAndCleansUp()
    {
        // key [0x00 0x00 0x00 0x10 0x01] = "x" — box id 16 (the first product box), one key byte.
        byte[] key = [0x00, 0x00, 0x00, 0x10, 0x01];
        byte[] value = "x"u8.ToArray();

        // open RocksDB in a fresh temporary directory (through an internal Storage type); disposing deletes it
        using var temp = TempDatabase.Create();
        using (var batch = new WriteBatch())
        {
            batch.Put(key, value, temp.Instance.Family("general"));
            temp.Instance.Write(batch, sync: true);
        }

        var read = temp.Instance.Get("general", key);

        Assert.NotNull(read);
        Assert.Equal(value, read);
    }
}
