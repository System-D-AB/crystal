// Ported from Highway: src/Highway.Server/Storage/Boxes/Catalog.cs
// and Highway: src/Highway.Server/Storage/Rocks/RocksDbBatch.cs
// and Highway: src/Highway.Server/Storage/Rocks/RocksDbSnapshot.cs
// Adapted for Crystal: box catalogue, gate, read-your-writes batch, bounded scans (spec 002 R2–R7).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using RocksDbSharp;
using SystemD.Crystal.Encoding;
using SystemD.Crystal.Observability;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Internal context for an open database instance: owns the catalogue in box 0,
/// the catalogue gate, striped locks, batches, and snapshots (spec 002).
/// </summary>
internal sealed class DatabaseContext : IDisposable
{
    private const int MaxBoxNameLength = 1024;
    internal const int CatalogueFormat = 1;
    private const string LiveState = "live";
    private const string DroppedState = "dropped";

    /// <summary>
    /// The highest box id ever issued. The last 4-byte id is never issued, so every box's range has an exclusive
    /// upper bound <c>[box_id + 1]</c> (spec 002 R1.1, R2.2).
    /// </summary>
    internal const uint MaxBoxId = uint.MaxValue - 1;

    private readonly RocksInstance _db;
    private readonly TimeProvider _clock;
    private readonly string _label;
    private readonly ReaderWriterLockSlim _catalogueGate = new(LockRecursionPolicy.NoRecursion);
    private readonly BoxLocks _locks;

    private readonly Dictionary<string, uint> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, BoxRecord> _records = [];

    private ulong _nextBoxId = BoxKeys.FirstProductBoxId;
    private long _generation = 1;
    private readonly DurabilityMode _defaultDurability;
    private readonly ILogger? _logger;
    private readonly Action? _onIntervalWrite;
    private bool _disposed;
    private DatabaseFailedException? _failed;
    private volatile bool _followingPrimary;

    /// <summary>
    /// Test hook (null in production, spec 002 R5.5 / R10.2): fired by <see cref="CommitBatch"/> while it holds the
    /// catalogue gate in shared mode, after it has validated every touched box is live and before it hands the batch
    /// to RocksDB. A concurrency test installs it to pause a commit at exactly that instant and prove that a drop,
    /// which needs the gate exclusively, cannot interleave — it waits until the commit leaves the gate.
    /// </summary>
    internal Action? OnCommitInsideGate { get; set; }

    /// <summary>
    /// Test hook (null in production, spec 002 R10.2): fired by create, rename, swap and drop with the operation's
    /// name while they hold the catalogue gate exclusively, after their checks and before their batch is written.
    /// </summary>
    internal Action<string>? OnCatalogueChangeInsideGate { get; set; }

    /// <param name="db">The database instance.</param>
    /// <param name="clock">The time provider for created and dropped times.</param>
    /// <param name="label">How refusals name this database, such as <c>database 'shop' (id 16)</c>.</param>
    /// <param name="defaultDurability">The store's default mode, for a commit that names none (spec 006 R2.1).</param>
    /// <param name="logger">Where a failure is reported (spec 006 R5.3); null logs nothing.</param>
    /// <param name="onIntervalWrite">Called after an <see cref="DurabilityMode.Interval"/> commit, so the store's
    /// timer runs (spec 006 R4.1).</param>
    /// <param name="metrics">The store's instruments (spec 007), or null for the registry.</param>
    /// <param name="slowCommitThreshold">Log a commit slower than this (spec 007 R3.3); null never.</param>
    /// <param name="keyLockStripes">The stripes of the key locks (spec 012 R3.7).</param>
    /// <param name="maxKeyBytes">The longest key a batch stages (spec 012 R9); null for no limit.</param>
    internal DatabaseContext(
        RocksInstance db,
        TimeProvider clock,
        string label,
        DurabilityMode defaultDurability = DurabilityMode.Strict,
        ILogger? logger = null,
        Action? onIntervalWrite = null,
        CrystalMetrics? metrics = null,
        TimeSpan? slowCommitThreshold = null,
        int keyLockStripes = 256,
        int? maxKeyBytes = null)
    {
        _maxKeyBytes = maxKeyBytes;
        _db = db;
        _clock = clock;
        _label = label;
        _defaultDurability = defaultDurability;
        _logger = logger;
        _onIntervalWrite = onIntervalWrite;
        _metrics = metrics;
        _slowCommitThreshold = slowCommitThreshold;
        _locks = new BoxLocks(metrics);
        _keyLocks = new KeyLocks(keyLockStripes, EnsureOwned, metrics);
    }

    private readonly KeyLocks _keyLocks;
    private readonly int? _maxKeyBytes;

    /// <summary>
    /// Refuses a key (or range end) longer than <see cref="CrystalOptions.MaxKeyBytes"/>, when it is staged (spec 012
    /// R9.1). The box prefix is not counted. Reads are never checked (R9.2), nor is a standby's apply.
    /// </summary>
    public void EnsureKeySize(Box box, int keyBytes)
    {
        if (_maxKeyBytes is { } limit && keyBytes > limit)
        {
            throw new KeyTooLargeException(
                box.Name,
                keyBytes,
                limit,
                $"A key of {keyBytes} bytes in box '{box.Name}' is longer than the {limit} bytes CrystalOptions.MaxKeyBytes " +
                "allows (spec 012 R9). Shorten the key, or hash its tail and keep the full value in the record.");
        }
    }

    /// <summary>Key locks (spec 012 R3), independent of the box locks.</summary>
    public KeyLocks KeyLocks => _keyLocks;

    private readonly CrystalMetrics? _metrics;
    private readonly TimeSpan? _slowCommitThreshold;
    private long _lastWalSyncTicks;   // UTC ticks of the last successful sync, 0 if none since open (spec 007 R4.1)

    /// <summary>When the WAL was last synced since the database opened: a strict write, or a sync (spec 007 R4.1).</summary>
    public DateTimeOffset? LastWalSync =>
        Interlocked.Read(ref _lastWalSyncTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    private void Synced() => Interlocked.Exchange(ref _lastWalSyncTicks, _clock.GetUtcNow().UtcTicks);

    /// <summary>The live boxes, from memory (spec 007 R2.6).</summary>
    public int LiveBoxCount
    {
        get
        {
            _catalogueGate.EnterReadLock();
            try
            {
                return _aliases.Count;
            }
            finally
            {
                _catalogueGate.ExitReadLock();
            }
        }
    }

    /// <summary>The store's default mode (spec 006 R2.1).</summary>
    public DurabilityMode DefaultDurability => _defaultDurability;

    /// <summary>Whether the database has failed (spec 006 R5.1).</summary>
    public bool Failed => Volatile.Read(ref _failed) is not null;

    /// <summary>The first failure, if the database has failed: the store's dispose reports it (006 R5).</summary>
    public DatabaseFailedException? Failure => Volatile.Read(ref _failed);

    public BoxLocks Locks => _locks;

    public long CatalogueGeneration
    {
        get
        {
            _catalogueGate.EnterReadLock();
            try
            {
                return _generation;
            }
            finally
            {
                _catalogueGate.ExitReadLock();
            }
        }
    }

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Writes the catalogue of a database that has just been created (spec 002 R2, R8): format, encoder version,
    /// identity (spec 009 R1.1), allocator and generation, in one synced batch. Refused if box 0 holds anything: an
    /// existing catalogue is only ever loaded, never initialised over.
    /// </summary>
    public void InitializeNewCatalogue(DatabaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _catalogueGate.EnterWriteLock();
        try
        {
            if (ReadCatalogueEntries().Count != 0)
            {
                throw Damaged("it was to be created, but it already holds catalogue entries");
            }

            using var batch = new WriteBatch();
            var defaultCf = _db.Family("default");
            batch.Put(BoxKeys.FormatKey, UInt32Bytes(CatalogueFormat), defaultCf);

            // R7.4: stamp the key-encoder version, so a later build with different encoding bytes refuses this
            // database rather than misreading its keys.
            batch.Put(BoxKeys.EncoderVersionKey, UInt32Bytes((uint)KeyEncoding.Version), defaultCf);

            // Spec 009 R1.1: the database says what it is, so every copy of its folder carries its identity.
            batch.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(identity), defaultCf);
            batch.Put(BoxKeys.AllocatorKey, UInt64Bytes(BoxKeys.FirstProductBoxId), defaultCf);
            batch.Put(BoxKeys.GenerationKey, UInt64Bytes(1), defaultCf);
            _db.Write(batch, sync: true);

            _nextBoxId = BoxKeys.FirstProductBoxId;
            _generation = 1;
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Rewrites the identity record with a new UID, keeping its kind and creation time, in one synced write (spec 010
    /// R6.5, R11.7; the one exception to spec 009 R1.1, recorded in 009's addendum). Used only by a restore as a copy, on
    /// a database no registry names yet, after <see cref="LoadCatalogue"/> checked the identity it replaces.
    /// </summary>
    public DatabaseIdentity ReplaceIdentityUid(DatabaseIdentity loaded, string newUid)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentException.ThrowIfNullOrEmpty(newUid);
        var replaced = loaded with { Uid = newUid };
        _catalogueGate.EnterWriteLock();
        try
        {
            using var batch = new WriteBatch();
            batch.Put(BoxKeys.IdentityKey, DatabaseIdentity.Serialize(replaced), _db.Family("default"));
            _db.Write(batch, sync: true);
            return replaced;
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Loads an existing catalogue from box 0 (spec 002 R2.4, R8.1). Every key is checked: a missing or unreadable
    /// format, encoder version, allocator or generation, an unparsable record, an alias table that disagrees with the
    /// records, or an id the allocator has not passed is refused with <see cref="CrystalFormatException"/>, never
    /// defaulted. A catalogue that is not there is never re-created here.
    /// <para>
    /// Spec 009 R2.3, R3.1: the caller says which database it expects. The identity record must be present and readable,
    /// of <paramref name="expectedKind"/>, and, unless <paramref name="expectedUid"/> is null (the registry, whose UID is
    /// recorded nowhere else), of that UID. Returns the identity read, whose creation time is the source the registry's
    /// copy follows.
    /// </para>
    /// </summary>
    public DatabaseIdentity LoadCatalogue(DatabaseKind expectedKind, string? expectedUid)
    {
        _catalogueGate.EnterWriteLock();
        try
        {
            var entries = ReadCatalogueEntries();
            byte[]? format = null, encoder = null, allocator = null, generation = null, identityBytes = null;
            var aliases = new Dictionary<string, uint>(StringComparer.Ordinal);
            var records = new Dictionary<uint, BoxRecord>();

            foreach (var (key, value) in entries)
            {
                if (key.Length < 5)
                {
                    throw Damaged($"box 0 holds a key of {key.Length} bytes, shorter than a catalogue key");
                }

                switch (key[4])
                {
                    case 0x01:
                        var name = DecodeAliasName(key);
                        if (value.Length != 4)
                        {
                            throw Damaged($"the alias of box '{name}' holds {value.Length} bytes, not a 4-byte id");
                        }

                        aliases[name] = BinaryPrimitives.ReadUInt32BigEndian(value);
                        break;
                    case 0x02 when key.Length == 9:
                        var id = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(5, 4));
                        records[id] = DecodeRecord(id, value);
                        break;
                    case 0x03 when key.Length == 5:
                        allocator = value;
                        break;
                    case 0x04 when key.Length == 5:
                        generation = value;
                        break;
                    case 0x05 when key.Length == 5:
                        format = value;
                        break;
                    case 0x06 when key.Length == 5:
                        encoder = value;
                        break;
                    case 0x07 when key.Length == 5:
                        identityBytes = value;
                        break;
                    default:
                        throw Damaged($"box 0 holds a key in space 0x{key[4]:x2} that this build does not write " +
                            "(a newer build, or damage)");
                }
            }

            if (format is null)
            {
                throw Damaged("it has no format key");
            }

            if (format.Length != 4 || BinaryPrimitives.ReadUInt32BigEndian(format) != CatalogueFormat)
            {
                var found = format.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(format).ToString() : "unreadable";
                throw new CrystalFormatException(
                    $"The catalogue of {_label} has format {found}; this build reads format {CatalogueFormat} (R8.1).",
                    foundFormat: found,
                    supportedFormat: CatalogueFormat.ToString(),
                    wayForward: "Open it with a Crystal build that reads it, restore it from a backup, or drop it if " +
                        "its data is not needed.");
            }

            // R7.4: the encoder version is written in the same batch as the format, so a catalogue without it is
            // damaged, not old.
            if (encoder is null || encoder.Length != 4)
            {
                throw Damaged("its key-encoder version is missing or unreadable");
            }

            var encoderVersion = BinaryPrimitives.ReadUInt32BigEndian(encoder);
            if (encoderVersion != (uint)KeyEncoding.Version)
            {
                throw new CrystalFormatException(
                    $"The catalogue of {_label} was written with key-encoder version {encoderVersion}, but this build " +
                    $"uses version {KeyEncoding.Version} (R7.4). The encoded key bytes differ, so this build would " +
                    "misread the keys.",
                    foundFormat: $"encoder {encoderVersion}",
                    supportedFormat: $"encoder {KeyEncoding.Version}",
                    wayForward: "Open with the matching Crystal version, or export and re-import the data.");
            }

            var identity = CheckIdentity(identityBytes, expectedKind, expectedUid);

            if (allocator is null || allocator.Length != 8)
            {
                throw Damaged("its id allocator is missing or unreadable");
            }

            var next = BinaryPrimitives.ReadUInt64BigEndian(allocator);
            if (next < BoxKeys.FirstProductBoxId || next > (ulong)MaxBoxId + 1)
            {
                throw Damaged($"its id allocator holds {next}, outside {BoxKeys.FirstProductBoxId}–{(ulong)MaxBoxId + 1}");
            }

            if (generation is null || generation.Length != 8 || BinaryPrimitives.ReadUInt64BigEndian(generation) is 0 or > long.MaxValue)
            {
                throw Damaged("its generation is missing or unreadable");
            }

            foreach (var (id, record) in records)
            {
                // R2.2: an id the allocator has not passed would be issued again.
                if (id < BoxKeys.FirstProductBoxId || id >= next)
                {
                    throw Damaged($"it has a record for box id {id}, which the allocator ({next}) has not issued");
                }

                if (record.State == LiveState && (!aliases.TryGetValue(record.Name, out var aliased) || aliased != id))
                {
                    throw Damaged($"live box '{record.Name}' (id {id}) is not named by the alias table");
                }
            }

            foreach (var (name, id) in aliases)
            {
                if (!records.TryGetValue(id, out var record) || record.State != LiveState || record.Name != name)
                {
                    throw Damaged($"the alias '{name}' names box id {id}, which is not a live box of that name");
                }
            }

            _aliases.Clear();
            _records.Clear();
            foreach (var (name, id) in aliases)
            {
                _aliases[name] = id;
            }

            foreach (var (id, record) in records)
            {
                _records[id] = record;
            }

            _nextBoxId = next;
            _generation = (long)BinaryPrimitives.ReadUInt64BigEndian(generation);
            return identity;
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Spec 009 R1.2, R1.3, R2.3, R3.1: the identity record is required, readable, of the expected kind and (unless
    /// <paramref name="expectedUid"/> is null) of the expected UID. Every refusal names this database and its way forward.
    /// </summary>
    private DatabaseIdentity CheckIdentity(byte[]? identityBytes, DatabaseKind expectedKind, string? expectedUid)
    {
        if (identityBytes is null)
        {
            throw Damaged("it has no identity record");
        }

        DatabaseIdentity identity;
        try
        {
            identity = DatabaseIdentity.Deserialize(identityBytes);
        }
        catch (FormatException ex)
        {
            throw Damaged($"its identity record is unreadable ({ex.Message})", ex);
        }
        catch (NotSupportedException ex)
        {
            var version = ReadIdentityVersion(identityBytes);
            throw new CrystalFormatException(
                $"The identity record of {_label} has version {version}; this build reads version " +
                $"{DatabaseIdentity.CurrentVersion} (spec 009 R1.2).",
                foundFormat: $"identity {version}",
                supportedFormat: $"identity {DatabaseIdentity.CurrentVersion}",
                wayForward: "Open it with a Crystal build that reads it.",
                innerException: ex);
        }

        if (identity.Kind != expectedKind)
        {
            throw new CrystalFormatException(
                $"{Capitalised(_label)} holds a {DatabaseIdentity.KindName(identity.Kind)} where a " +
                $"{DatabaseIdentity.KindName(expectedKind)} belongs (spec 009 R3.1). Its folder was moved or copied by hand.",
                foundFormat: DatabaseIdentity.KindName(identity.Kind),
                supportedFormat: DatabaseIdentity.KindName(expectedKind),
                wayForward: "Put the right folder back.");
        }

        if (expectedUid is not null && !string.Equals(identity.Uid, expectedUid, StringComparison.Ordinal))
        {
            throw new CrystalFormatException(
                $"{Capitalised(_label)} holds database {identity.Uid}, but the registry names database {expectedUid} here " +
                "(spec 009 R2.3). Its folder holds another database.",
                foundFormat: $"database {identity.Uid}",
                supportedFormat: $"database {expectedUid}",
                wayForward: "Put the right folder back, or drop this database if its data is not needed.");
        }

        return identity;
    }

    private static string ReadIdentityVersion(byte[] identityBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(identityBytes);
            return document.RootElement.TryGetProperty("v", out var v) ? v.ToString() : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private List<(byte[] Key, byte[] Value)> ReadCatalogueEntries()
    {
        BoxKeys.GetBoxBounds(BoxKeys.CatalogueBoxId, out var lowerBound, out var upperBound);
        using var snapshot = _db.CreateSnapshot();
        using var it = _db.Scan("default", snapshot, lowerBound, upperBound, int.MaxValue);

        var entries = new List<(byte[], byte[])>();
        foreach (var (key, value) in it.Scan(lowerBound))
        {
            entries.Add((key, value));
        }

        return entries;
    }

    private string DecodeAliasName(byte[] key)
    {
        try
        {
            var name = KeyEncoding.DecodeString(key.AsSpan(5), out var consumed);
            if (consumed != key.Length - 5)
            {
                throw new FormatException("bytes follow the name's terminator");
            }

            ValidateBoxName(name);
            return name;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw Damaged($"an alias key is not a valid box name ({ex.Message})", ex);
        }
    }

    private BoxRecord DecodeRecord(uint id, byte[] value)
    {
        BoxRecord record;
        try
        {
            record = BoxRecord.Deserialize(value);
        }
        catch (JsonException ex)
        {
            throw Damaged($"the record of box id {id} is not readable JSON", ex);
        }

        if (record.Version != 1)
        {
            throw new CrystalFormatException(
                $"The record of box id {id} in {_label} has version {record.Version}; this build reads version 1.",
                foundFormat: $"record {record.Version}",
                supportedFormat: "record 1",
                wayForward: "Open it with a Crystal build that reads it.");
        }

        if (record.State is not (LiveState or DroppedState) || !Enum.TryParse<BoxProfile>(record.Profile, out _))
        {
            throw Damaged($"the record of box id {id} has state '{record.State}' and profile '{record.Profile}'");
        }

        try
        {
            ValidateBoxName(record.Name);
        }
        catch (ArgumentException ex)
        {
            throw Damaged($"the record of box id {id} holds an invalid name ({ex.Message})", ex);
        }

        return record;
    }

    private CrystalFormatException Damaged(string what, Exception? inner = null) => new(
        $"The catalogue of {_label} is damaged: {what}. Crystal refuses it rather than guess (R2.2, R8.1).",
        foundFormat: "damaged",
        supportedFormat: CatalogueFormat.ToString(),
        wayForward: "Restore the database from a backup, or drop it if its data is not needed.",
        innerException: inner);

    // ------------------------------------------------------------------ names and handles

    /// <summary>
    /// R3.6: 1–1 024 UTF-8 bytes, no control characters, and valid UTF-16 (no unpaired surrogate, which UTF-8 cannot
    /// represent: two different names would otherwise share one alias key).
    /// </summary>
    internal static void ValidateBoxName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int byteCount;
        try
        {
            byteCount = KeyEncoding.StrictUtf8.GetByteCount(name);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"Box name '{name}' is not valid UTF-16: it has an unpaired surrogate (R3.6).", nameof(name));
        }

        if (byteCount is < 1 or > MaxBoxNameLength)
        {
            throw new ArgumentException(
                $"Box name '{name}' length ({byteCount} bytes) must be between 1 and {MaxBoxNameLength} UTF-8 bytes (R3.6).",
                nameof(name));
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException($"Box name '{name}' cannot contain control characters (R3.6).", nameof(name));
            }
        }
    }

    private Box Handle(uint id, string name) => new(id, name) { Catalogue = this };

    /// <summary>
    /// Refuses a handle this database did not issue: one from another database (whose ids overlap this one's), from
    /// an earlier open of this database, or built by hand. A handle is never redirected (spec 002 R1.2, R4.3).
    /// </summary>
    public void EnsureOwned(Box box)
    {
        if (!ReferenceEquals(box.Catalogue, this))
        {
            throw new BoxNotInDatabaseException(
                box.Id,
                box.Name,
                $"{box} was not obtained from {_label}. A box handle addresses only the database that issued it " +
                "(through Create, Find or Rename); find the box by name in this database instead.");
        }
    }

    /// <summary>Refuses a write once the database is closed or has failed (spec 006 R5.1). Reads only check closing.</summary>
    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _failed) is { } failed)
        {
            throw new DatabaseFailedException(failed.Database, failed.Message, failed.InnerException);
        }
    }

    /// <summary>
    /// Whether this database is following a primary as a standby (spec 003 R1.6): local writes through its handle are
    /// refused, while the replication applier's own writes (through <c>WalAccess.ApplyRaw</c>, which does not come
    /// through here) still land. Set by the follower when it adopts the database, cleared on promotion (T12).
    /// </summary>
    public bool FollowingPrimary
    {
        get => _followingPrimary;
        set => _followingPrimary = value;
    }

    /// <summary>
    /// The store's replication write gate, for a replicated database (003 addendum B2); null for a database the store
    /// does not replicate, which is always writable. Set by the store before the database's handle is published.
    /// </summary>
    internal ReplicationWriteGate? WriteGate { get; set; }

    /// <summary>
    /// Refuses a local write while this database follows a primary (spec 003 R1.6, design § 5), or while it is
    /// replicated and this node is not the primary: a standby, demoted or fenced node (R6.5, 003 addendum B2). An
    /// admitted write holds the returned admission until it ends, so a demotion or fence waits for it.
    /// </summary>
    private ReplicationWriteGate.Admission AdmitLocalWrite()
    {
        if (_followingPrimary)
        {
            throw new NotPrimaryException(
                $"{_label} is a replication standby following a primary; it refuses local writes (R1.6). Writes arrive " +
                "only from the primary, through replication. Promote this node to accept writes here.");
        }

        var admitted = ReplicationWriteGate.Admit(WriteGate, out var refusedAs);
        if (refusedAs is { } role)
        {
            throw new NotPrimaryException(
                $"{_label} is replicated and this node is {role}, so it refuses local writes (R1.6, " +
                "R6.5). Write on the primary, or promote this node to accept writes here.");
        }

        return admitted;
    }

    /// <summary>
    /// Fails the database (spec 006 R5, 002 T16): a write or a WAL sync failed, so whether the disk holds what was
    /// acknowledged is unknown. Every later write is refused; reads go on, since the in-memory state is not wrong. The
    /// first failure is kept and logged at Critical; a failure is never retried.
    /// </summary>
    private DatabaseFailedException Fail(Exception cause, string what, string reason)
    {
        var failed = new DatabaseFailedException(
            _label,
            $"{what} in {_label} failed, so writes acknowledged since its last successful WAL sync may not be on disk " +
            "(006 R5). It refuses every write from now on; reads still work. Close the store, check the disk, and open " +
            "the store again: recovery replays what reached the disk. A commit that failed was not acknowledged, but " +
            "its batch may still have reached the WAL and be present after reopening: check before repeating it.",
            cause);

        if (Interlocked.CompareExchange(ref _failed, failed, null) is null)
        {
            // Counted once, as it is logged once: later callers who meet the same failure are not new failures (review 4).
            _metrics?.WalSyncFailed(reason);
            if (_logger is not null)
            {
                CrystalLog.DatabaseFailed(_logger, cause, _label, what);
            }
        }

        return failed;
    }

    /// <summary>
    /// Writes a catalogue batch, always synced (spec 006 R2.3). If the write fails, whether it reached the disk is
    /// unknown, so the in-memory catalogue may no longer match it: the database fails (R2.4, 006 R5).
    /// </summary>
    private void WriteCatalogue(WriteBatch batch)
    {
        try
        {
            _db.Write(batch, DurabilityMode.Strict);
            Synced();
        }
        catch (Exception ex)
        {
            throw Fail(ex, "A catalogue write", "catalogue");
        }
    }

    // ------------------------------------------------------------------ syncing (spec 006)

    /// <summary>The store timer's tick (006 R4): syncs if an Interval commit is waiting. Never throws.</summary>
    public void SyncForInterval()
    {
        if (_disposed || Failed)
        {
            return;
        }

        var start = _metrics?.Start() ?? 0;
        try
        {
            if (_db.SyncIfIntervalPending())
            {
                Synced();
                _metrics?.WalSync("interval", start);
            }
        }
        catch (Exception ex)
        {
            Fail(ex, "An interval WAL sync", "interval");
        }
    }

    /// <summary><c>CrystalStore.SyncAll</c> (006 R2.4): syncs any unsynced commit now; throws if the database failed.</summary>
    public void SyncAll()
    {
        ThrowIfUnusable();
        var start = _metrics?.Start() ?? 0;
        try
        {
            if (_db.SyncIfUnsynced())
            {
                Synced();
                _metrics?.WalSync("sync_all", start);
            }
        }
        catch (Exception ex)
        {
            throw Fail(ex, "A WAL sync", "sync_all");
        }

        // The writes may have been synced by a concurrent caller whose sync failed; that failure is this caller's too.
        ThrowIfUnusable();
    }

    /// <summary>The final sync before the database closes (006 R4.2). Never throws: closing goes on.</summary>
    public void SyncBeforeClose()
    {
        if (_disposed || Failed)
        {
            return;
        }

        var start = _metrics?.Start() ?? 0;
        try
        {
            if (_db.SyncIfUnsynced())
            {
                Synced();
                _metrics?.WalSync("close", start);
            }
        }
        catch (Exception ex)
        {
            Fail(ex, "The WAL sync before closing", "close");
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public Box CreateBox(string name, BoxProfile profile)
    {
        ValidateBoxName(name);

        _catalogueGate.EnterWriteLock();
        try
        {
            ThrowIfUnusable();
            using var admitted = AdmitLocalWrite();

            if (_aliases.ContainsKey(name))
            {
                throw new BoxNameTakenException(name, $"Box '{name}' already exists (R3.1).");
            }

            if (_nextBoxId > MaxBoxId)
            {
                throw new InvalidOperationException(
                    $"{_label} has issued every box id; ids are never reused (R2.2). Create the box in another database.");
            }

            var id = (uint)_nextBoxId;
            var record = new BoxRecord
            {
                Version = 1,
                Name = name,
                Profile = profile.ToString(),
                State = LiveState,
                CreatedTicks = _clock.GetUtcNow().Ticks,
                DroppedTicks = null,
            };

            var generation = _generation + 1;
            using var batch = new WriteBatch();
            var defaultCf = _db.Family("default");
            batch.Put(BoxKeys.MakeAliasKey(name), UInt32Bytes(id), defaultCf);
            batch.Put(BoxKeys.MakeRecordKey(id), BoxRecord.Serialize(record), defaultCf);
            batch.Put(BoxKeys.AllocatorKey, UInt64Bytes((ulong)id + 1), defaultCf);
            batch.Put(BoxKeys.GenerationKey, UInt64Bytes((ulong)generation), defaultCf);

            OnCatalogueChangeInsideGate?.Invoke("create");
            WriteCatalogue(batch);

            _nextBoxId = (ulong)id + 1;
            _generation = generation;
            _aliases[name] = id;
            _records[id] = record;

            if (_logger is not null)
            {
                CrystalLog.BoxCreated(_logger, name, id, _label);
            }

            return Handle(id, name);
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    public Box? FindBox(string name)
    {
        ValidateBoxName(name);

        _catalogueGate.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _aliases.TryGetValue(name, out var id) ? Handle(id, name) : null;
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }
    }

    public IReadOnlyList<BoxInfo> ListBoxes()
    {
        _catalogueGate.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var list = new List<BoxInfo>(_aliases.Count);
            foreach (var (id, record) in _records)
            {
                if (record.State == LiveState)
                {
                    list.Add(new BoxInfo(id, record.Name, Enum.Parse<BoxProfile>(record.Profile), new DateTimeOffset(record.CreatedTicks, TimeSpan.Zero)));
                }
            }

            return list;
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }
    }

    public Box RenameBox(Box box, string newName)
    {
        EnsureOwned(box);
        ValidateBoxName(newName);

        _catalogueGate.EnterWriteLock();
        try
        {
            ThrowIfUnusable();
            using var admitted = AdmitLocalWrite();

            if (!_records.TryGetValue(box.Id, out var record) || record.State != LiveState)
            {
                throw new BoxNotFoundException(box.Id, $"Box '{box.Name}' (id {box.Id}) is not live (R3.8).");
            }

            if (string.Equals(record.Name, newName, StringComparison.Ordinal))
            {
                return Handle(box.Id, newName);
            }

            if (_aliases.ContainsKey(newName))
            {
                throw new BoxNameTakenException(newName, $"Box name '{newName}' is already taken (R3.8).");
            }

            var updated = record with { Name = newName };
            var generation = _generation + 1;

            using var batch = new WriteBatch();
            var defaultCf = _db.Family("default");
            batch.Delete(BoxKeys.MakeAliasKey(record.Name), defaultCf);
            batch.Put(BoxKeys.MakeAliasKey(newName), UInt32Bytes(box.Id), defaultCf);
            batch.Put(BoxKeys.MakeRecordKey(box.Id), BoxRecord.Serialize(updated), defaultCf);
            batch.Put(BoxKeys.GenerationKey, UInt64Bytes((ulong)generation), defaultCf);

            OnCatalogueChangeInsideGate?.Invoke("rename");
            WriteCatalogue(batch);

            _generation = generation;
            _aliases.Remove(record.Name);
            _aliases[newName] = box.Id;
            _records[box.Id] = updated;
            if (_logger is not null)
            {
                CrystalLog.BoxRenamed(_logger, box.Id, record.Name, newName, _label);
            }

            return Handle(box.Id, newName);
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    public void SwapBoxes(Box first, Box second)
    {
        EnsureOwned(first);
        EnsureOwned(second);

        _catalogueGate.EnterWriteLock();
        try
        {
            ThrowIfUnusable();
            using var admitted = AdmitLocalWrite();

            if (first.Id == second.Id)
            {
                return;
            }

            if (!_records.TryGetValue(first.Id, out var rec1) || rec1.State != LiveState)
            {
                throw new BoxNotFoundException(first.Id, $"Box '{first.Name}' (id {first.Id}) is not live (R3.9).");
            }

            if (!_records.TryGetValue(second.Id, out var rec2) || rec2.State != LiveState)
            {
                throw new BoxNotFoundException(second.Id, $"Box '{second.Name}' (id {second.Id}) is not live (R3.9).");
            }

            var name1 = rec1.Name;
            var name2 = rec2.Name;
            var updated1 = rec1 with { Name = name2 };
            var updated2 = rec2 with { Name = name1 };
            var generation = _generation + 1;

            using var batch = new WriteBatch();
            var defaultCf = _db.Family("default");
            batch.Put(BoxKeys.MakeAliasKey(name1), UInt32Bytes(second.Id), defaultCf);
            batch.Put(BoxKeys.MakeAliasKey(name2), UInt32Bytes(first.Id), defaultCf);
            batch.Put(BoxKeys.MakeRecordKey(first.Id), BoxRecord.Serialize(updated1), defaultCf);
            batch.Put(BoxKeys.MakeRecordKey(second.Id), BoxRecord.Serialize(updated2), defaultCf);
            batch.Put(BoxKeys.GenerationKey, UInt64Bytes((ulong)generation), defaultCf);

            OnCatalogueChangeInsideGate?.Invoke("swap");
            WriteCatalogue(batch);

            _generation = generation;
            _aliases[name1] = second.Id;
            _aliases[name2] = first.Id;
            _records[first.Id] = updated1;
            _records[second.Id] = updated2;
            if (_logger is not null)
            {
                CrystalLog.BoxesSwapped(_logger, first.Id, second.Id, _label);
            }
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    public void DropBox(Box box)
    {
        EnsureOwned(box);

        _catalogueGate.EnterWriteLock();
        try
        {
            ThrowIfUnusable();

            if (!_records.TryGetValue(box.Id, out var record) || record.State != LiveState)
            {
                throw new BoxNotFoundException(box.Id, $"Box '{box.Name}' (id {box.Id}) is not live (R3.4).");
            }

            using var admitted = AdmitLocalWrite();

            var dropped = record with
            {
                State = DroppedState,
                DroppedTicks = _clock.GetUtcNow().Ticks,
            };
            var generation = _generation + 1;

            using var batch = new WriteBatch();
            var defaultCf = _db.Family("default");
            batch.Delete(BoxKeys.MakeAliasKey(record.Name), defaultCf);
            batch.Put(BoxKeys.MakeRecordKey(box.Id), BoxRecord.Serialize(dropped), defaultCf);
            batch.Put(BoxKeys.GenerationKey, UInt64Bytes((ulong)generation), defaultCf);

            // Range delete all keys in the box's column family (R3.4)
            BoxKeys.GetBoxBounds(box.Id, out var lowerBound, out var upperBound);
            batch.DeleteRange(lowerBound, (ulong)lowerBound.Length, upperBound, (ulong)upperBound.Length, _db.Family(FamilyOf(record)));

            OnCatalogueChangeInsideGate?.Invoke("drop");
            WriteCatalogue(batch);

            _generation = generation;
            _aliases.Remove(record.Name);
            _records[box.Id] = dropped;
            if (_logger is not null)
            {
                CrystalLog.BoxDropped(_logger, record.Name, box.Id, _label);
            }
        }
        finally
        {
            _catalogueGate.ExitWriteLock();
        }
    }

    // ------------------------------------------------------------------ data

    private static string FamilyOf(BoxRecord record) => record.Profile == nameof(BoxProfile.DeleteHeavy) ? "delete_heavy" : "general";

    public string FamilyNameOf(uint boxId)
    {
        _catalogueGate.EnterReadLock();
        try
        {
            return _records.TryGetValue(boxId, out var record) ? FamilyOf(record) : "general";
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }
    }

    public void EnsureBoxesLive(IEnumerable<uint> boxIds)
    {
        _catalogueGate.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var id in boxIds)
            {
                if (!_records.TryGetValue(id, out var record) || record.State != LiveState)
                {
                    var name = record?.Name ?? id.ToString();
                    throw new BoxDroppedException(id, name, $"Box '{name}' (id {id}) has been dropped (R4.1).");
                }
            }
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }
    }

    public byte[]? DirectGet(string family, byte[] prefixedKey, StorageSnapshot? snapshot = null)
    {
        _catalogueGate.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var readOptions = snapshot is not null
                ? _db.ReadOptions.RentPointRead(snapshot.Value)
                : _db.ReadOptions.PointRead;

            try
            {
                return _db.Db.Get(prefixedKey, _db.Family(family), readOptions);
            }
            finally
            {
                if (snapshot is not null)
                {
                    _db.ReadOptions.ReturnPointRead(readOptions);
                }
            }
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }
    }

    public void CommitBatch(
        IReadOnlyList<uint> touchedBoxIds,
        IReadOnlyList<(string family, byte[] key, byte[]? value, bool isDelete, bool isRange, byte[]? rangeEnd)> operations,
        DurabilityMode durability)
    {
        if (operations.Count == 0)
        {
            return; // R6.3: empty batch writes nothing and syncs nothing
        }

        using var admitted = AdmitLocalWrite(); // R1.6: a standby refuses local writes before any work

        long? start = _metrics is { TimesCommits: true } || _slowCommitThreshold is not null ? _clock.GetTimestamp() : null;
        var outcome = "refused";
        try
        {
            CommitInsideGate(touchedBoxIds, operations, durability, ref outcome);
            outcome = "ok";
        }
        finally
        {
            _metrics?.Commit(durability, outcome, operations.Count, start);
            if (outcome == "ok" && _slowCommitThreshold is { } threshold && _logger is not null && start is { } began
                && _clock.GetElapsedTime(began) is var elapsed && elapsed > threshold)
            {
                CrystalLog.SlowCommit(_logger, durability, operations.Count, _label, elapsed.TotalMilliseconds);
            }
        }

        if (durability == DurabilityMode.Interval)
        {
            _onIntervalWrite?.Invoke();
        }
    }

    private void CommitInsideGate(
        IReadOnlyList<uint> touchedBoxIds,
        IReadOnlyList<(string family, byte[] key, byte[]? value, bool isDelete, bool isRange, byte[]? rangeEnd)> operations,
        DurabilityMode durability,
        ref string outcome)
    {
        _catalogueGate.EnterReadLock();
        try
        {
            ThrowIfUnusable();

            // Validate all boxes are live at commit (R4.1, R4.2)
            foreach (var id in touchedBoxIds)
            {
                if (!_records.TryGetValue(id, out var record) || record.State != LiveState)
                {
                    var name = record?.Name ?? id.ToString();
                    throw new BoxDroppedException(id, name, $"Cannot commit: box '{name}' (id {id}) has been dropped (R4.1).");
                }
            }

            // Test hook (R10.2): the commit is past validation and still inside the shared gate. A drop
            // on another thread needs the gate exclusively, so it cannot proceed until this returns.
            OnCommitInsideGate?.Invoke();

            using var batch = new WriteBatch();
            foreach (var op in operations)
            {
                var cf = _db.Family(op.family);
                if (op.isRange)
                {
                    batch.DeleteRange(op.key, (ulong)op.key.Length, op.rangeEnd!, (ulong)op.rangeEnd!.Length, cf);
                }
                else if (op.isDelete)
                {
                    batch.Delete(op.key, cf);
                }
                else
                {
                    batch.Put(op.key, op.value!, cf);
                }
            }

            try
            {
                _db.Write(batch, durability);
            }
            catch (Exception ex)
            {
                outcome = "failed";
                throw Fail(ex, durability == DurabilityMode.Strict ? "A synced commit" : "A commit", "commit");
            }

            if (durability == DurabilityMode.Strict)
            {
                Synced();
            }
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }
    }

    public StorageSnapshot CreateSnapshot() => new(_db.CreateSnapshot());

    /// <summary>
    /// RocksDB's estimate of the bytes each of <paramref name="ranges"/> of box <paramref name="boxId"/> occupies,
    /// memtables and table files included (spec 012 R2). Never scans. Empty bounds are the box's start and end; a
    /// range whose start sorts after its end is refused, an empty range is 0 without a native call.
    /// </summary>
    public ulong[] ApproximateBytes(uint boxId, IReadOnlyList<(byte[] From, byte[] To)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        string family;
        _catalogueGate.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_records.TryGetValue(boxId, out var record) || record.State != LiveState)
            {
                var name = record?.Name ?? boxId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                throw new BoxDroppedException(boxId, name, $"Box '{name}' (id {boxId}) has been dropped (R4.1).");
            }

            family = FamilyOf(record);
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }

        BoxKeys.GetBoxBounds(boxId, out var boxLower, out var boxUpper);
        var results = new ulong[ranges.Count];
        var native = new List<(byte[] From, byte[] To)>(ranges.Count);
        var positions = new List<int>(ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            var (from, to) = ranges[i];
            ArgumentNullException.ThrowIfNull(from, nameof(ranges));
            ArgumentNullException.ThrowIfNull(to, nameof(ranges));
            if (from.Length > 0 && to.Length > 0)
            {
                var order = from.AsSpan().SequenceCompareTo(to);
                if (order > 0)
                {
                    throw new ArgumentException($"Range {i} starts after it ends (R2.4).", nameof(ranges));
                }

                if (order == 0)
                {
                    continue; // empty: 0, no native call
                }
            }

            native.Add((from.Length == 0 ? boxLower : BoxKeys.BuildKey(boxId, from), to.Length == 0 ? boxUpper : BoxKeys.BuildKey(boxId, to)));
            positions.Add(i);
        }

        if (native.Count > 0)
        {
            var sizes = NativeRocks.ApproximateSizes(_db.Db, _db.Family(family), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(native));
            for (var j = 0; j < positions.Count; j++)
            {
                results[positions[j]] = sizes[j];
            }
        }

        return results;
    }

    public IEnumerable<KeyValue> ScanBox(
        uint boxId,
        StorageSnapshot snapshot,
        ReadOnlySpan<byte> from,
        ReadOnlySpan<byte> to,
        int limit) => ScanBox(boxId, snapshot, from, to, limit, ScanDirection.Forward);

    public IEnumerable<KeyValue> ScanBox(
        uint boxId,
        StorageSnapshot snapshot,
        ReadOnlySpan<byte> from,
        ReadOnlySpan<byte> to,
        int limit,
        ScanDirection direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        _catalogueGate.EnterReadLock();
        string family;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_records.TryGetValue(boxId, out var record) || record.State != LiveState)
            {
                var name = record?.Name ?? boxId.ToString();
                throw new BoxDroppedException(boxId, name, $"Box '{name}' (id {boxId}) has been dropped (R4.1).");
            }

            family = FamilyOf(record);
        }
        finally
        {
            _catalogueGate.ExitReadLock();
        }

        BoxKeys.GetBoxBounds(boxId, out var boxLower, out var boxUpper);
        var lowerBound = from.IsEmpty ? boxLower : BoxKeys.BuildKey(boxId, from);
        var upperBound = to.IsEmpty ? boxUpper : BoxKeys.BuildKey(boxId, to);

        return ScanBoxCore(boxId, family, snapshot, lowerBound, upperBound, limit, direction);
    }

    private IEnumerable<KeyValue> ScanBoxCore(
        uint boxId,
        string family,
        StorageSnapshot snapshot,
        byte[] lowerBound,
        byte[] upperBound,
        int limit,
        ScanDirection direction)
    {
        using var iterator = _db.Scan(family, snapshot.Value, lowerBound, upperBound, limit);
        try
        {
            var entries = direction == ScanDirection.Backward
                ? iterator.ScanBackward(upperBound, lowerBound)
                : iterator.Scan(lowerBound);
            foreach (var (k, v) in entries)
            {
                // Verify key is inside this box
                if (BoxKeys.ExtractBoxId(k) != boxId)
                {
                    break;
                }

                var userKey = BoxKeys.ExtractUserKey(k).ToArray();
                yield return new KeyValue(userKey, v);
            }
        }
        finally
        {
            _metrics?.Scan(iterator.Steps);   // spec 007 R2.4, also when the caller stops early
        }
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt64Bytes(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _catalogueGate.Dispose();
    }
}
