// Tests for database format key in catalogue (spec 002 T9, R8.1, R10.5).

using System;
using System.Buffers.Binary;
using System.IO;
using RocksDbSharp;
using SystemD.Crystal.Storage;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class DatabaseFormatTests : IDisposable
{
    private readonly string _dir;

    public DatabaseFormatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crystal-dbformat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void Open_WithFutureDatabaseFormat_ThrowsCrystalFormatException_R81_R105()
    {
        uint dbId;
        string dbPath;

        // 1. Create database normally
        using (var store = CrystalStore.Open(_dir))
        {
            var db = store.CreateDatabase("sales");
            dbId = db.Id;
            dbPath = Path.Combine(_dir, $"db-{dbId:x8}");
        }

        // 2. Tamper with the format key in the database catalogue (box 0, format tag 0x05)
        var options = new DbOptions();
        var cfs = new ColumnFamilies
        {
            { "general", new ColumnFamilyOptions() },
            { "delete_heavy", new ColumnFamilyOptions() }
        };
        using (var rocks = RocksDb.Open(options, dbPath, cfs))
        {
            var formatVal = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(formatVal, 99); // future format 99
            rocks.Put(BoxKeys.FormatKey, formatVal);
        }

        // 3. Reopen store and attempt to open "sales" database
        using (var store = CrystalStore.Open(_dir))
        {
            var ex = Assert.Throws<CrystalFormatException>(() => store.OpenDatabase("sales"));
            Assert.Equal("99", ex.FoundFormat);
            Assert.Equal("1", ex.SupportedFormat);
            Assert.NotNull(ex.WayForward);
            Assert.Contains("sales", ex.Message);
        }
    }

    [Fact]
    public void Open_WithMissingDatabaseFormatKey_ThrowsCrystalFormatException_R81()
    {
        uint dbId;
        string dbPath;

        using (var store = CrystalStore.Open(_dir))
        {
            var db = store.CreateDatabase("inventory");
            dbId = db.Id;
            dbPath = Path.Combine(_dir, $"db-{dbId:x8}");
        }

        // Remove the format key
        var options = new DbOptions();
        var cfs = new ColumnFamilies
        {
            { "general", new ColumnFamilyOptions() },
            { "delete_heavy", new ColumnFamilyOptions() }
        };
        using (var rocks = RocksDb.Open(options, dbPath, cfs))
        {
            rocks.Remove(BoxKeys.FormatKey);
        }

        using (var store = CrystalStore.Open(_dir))
        {
            var ex = Assert.Throws<CrystalFormatException>(() => store.OpenDatabase("inventory"));
            Assert.Null(ex.FoundFormat);
            Assert.Equal("1", ex.SupportedFormat);
            Assert.NotNull(ex.WayForward);
        }
    }

    [Fact]
    public void Open_WithForeignEncoderVersion_ThrowsCrystalFormatException_R74()
    {
        uint dbId;
        string dbPath;

        using (var store = CrystalStore.Open(_dir))
        {
            var db = store.CreateDatabase("events");
            dbId = db.Id;
            dbPath = Path.Combine(_dir, $"db-{dbId:x8}");
        }

        // Tamper with the encoder-version key (box 0, tag 0x06) to a version this build does not read.
        var options = new DbOptions();
        var cfs = new ColumnFamilies
        {
            { "general", new ColumnFamilyOptions() },
            { "delete_heavy", new ColumnFamilyOptions() }
        };
        using (var rocks = RocksDb.Open(options, dbPath, cfs))
        {
            var encoderVal = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoderVal, 2); // a future encoder version
            rocks.Put(BoxKeys.EncoderVersionKey, encoderVal);
        }

        using (var store = CrystalStore.Open(_dir))
        {
            var ex = Assert.Throws<CrystalFormatException>(() => store.OpenDatabase("events"));
            Assert.Equal("encoder 2", ex.FoundFormat);
            Assert.Equal("encoder 1", ex.SupportedFormat);
            Assert.NotNull(ex.WayForward);
        }
    }
}
