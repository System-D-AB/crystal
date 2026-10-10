using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseNameAndGateTests
{
    [Fact]
    public void DatabaseNames_ValidationBoundaries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crystal-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = CrystalStore.Open(dir);

            // 1-byte name: valid
            var db1 = store.CreateDatabase("a");
            Assert.Equal("a", db1.Name);

            // Multibyte UTF-8 names: valid
            var dbSwedish = store.CreateDatabase("räksmörgås");
            Assert.Equal("räksmörgås", dbSwedish.Name);

            var dbJapanese = store.CreateDatabase("データベース");
            Assert.Equal("データベース", dbJapanese.Name);

            // 256-byte name: valid
            var name256 = new string('x', 256);
            var db256 = store.CreateDatabase(name256);
            Assert.Equal(name256, db256.Name);

            // 257-byte name: rejected
            var name257 = new string('x', 257);
            Assert.Throws<ArgumentException>(() => store.CreateDatabase(name257));

            // Multibyte string exceeding 256 bytes: rejected
            var nameMultibyteOver = new string('ä', 129); // 129 * 2 = 258 bytes
            Assert.Throws<ArgumentException>(() => store.CreateDatabase(nameMultibyteOver));

            // Reserved '_' prefix: rejected
            Assert.Throws<ReservedNameException>(() => store.CreateDatabase("_internal"));

            // Path separators rejected
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("sub/dir"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("sub\\dir"));

            // Control characters rejected
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("test\tname"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("test\nname"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("test\rname"));
            Assert.Throws<ArgumentException>(() => store.CreateDatabase("test\0name"));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort in test cleanup
        }
    }
}
