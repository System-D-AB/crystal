using System;

namespace SystemD.Crystal;

/// <summary>
/// Thrown when a data directory contains a format stamp that this build cannot read, or contains
/// files without a format stamp (spec 005 R3.2, design § 6).
/// </summary>
public sealed class CrystalFormatException : Exception
{
    /// <summary>The format string found in the directory, or <c>null</c> if unstamped.</summary>
    public string? FoundFormat { get; }

    /// <summary>The format string supported by this build.</summary>
    public string SupportedFormat { get; }

    /// <summary>The recommended way forward for the operator.</summary>
    public string WayForward { get; }

    /// <summary>Initializes a new instance of <see cref="CrystalFormatException"/>.</summary>
    public CrystalFormatException(
        string message,
        string? foundFormat = null,
        string supportedFormat = "crystal 1",
        string? wayForward = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FoundFormat = foundFormat;
        SupportedFormat = supportedFormat;
        WayForward = wayForward ?? "Check the data directory path or initialize a new store.";
    }
}

/// <summary>
/// Thrown when attempting to open a store whose lock file is already held exclusively by another
/// instance or process (spec 005 R1.3, design § 6).
/// </summary>
public sealed class StoreLockedException : Exception
{
    /// <summary>The path to the store data directory that could not be locked.</summary>
    public string DataDirectory { get; }

    /// <summary>Initializes a new instance of <see cref="StoreLockedException"/>.</summary>
    public StoreLockedException(string dataDirectory, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DataDirectory = dataDirectory;
    }
}

/// <summary>
/// Thrown when a database cannot be found by name or id, or has been dropped (spec 005 R5.2, R5.7; design § 6).
/// </summary>
public sealed class DatabaseNotFoundException : Exception
{
    /// <summary>The requested database name, if known.</summary>
    public string? DatabaseName { get; }

    /// <summary>The requested database id, if known.</summary>
    public uint? DatabaseId { get; }

    /// <summary>Initializes a new instance of <see cref="DatabaseNotFoundException"/> with a message.</summary>
    public DatabaseNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="DatabaseNotFoundException"/> with a database name.</summary>
    public DatabaseNotFoundException(string databaseName, string message)
        : base(message)
    {
        DatabaseName = databaseName;
    }

    /// <summary>Initializes a new instance of <see cref="DatabaseNotFoundException"/> with a database id.</summary>
    public DatabaseNotFoundException(uint databaseId, string message)
        : base(message)
    {
        DatabaseId = databaseId;
    }
}

/// <summary>
/// Thrown when attempting to create or rename to a database name that already exists (spec 005 R5.1, R5.4; design § 6).
/// </summary>
public sealed class DatabaseNameTakenException : Exception
{
    /// <summary>The database name that is already taken.</summary>
    public string DatabaseName { get; }

    /// <summary>Initializes a new instance of <see cref="DatabaseNameTakenException"/>.</summary>
    public DatabaseNameTakenException(string databaseName, string message)
        : base(message)
    {
        DatabaseName = databaseName;
    }
}

/// <summary>
/// Thrown when attempting to create or rename a database with a reserved name (e.g. starting with '_') (spec 005 R6.2, design § 6).
/// </summary>
public sealed class ReservedNameException : Exception
{
    /// <summary>The reserved database name that was rejected.</summary>
    public string DatabaseName { get; }

    /// <summary>Initializes a new instance of <see cref="ReservedNameException"/>.</summary>
    public ReservedNameException(string databaseName, string message)
        : base(message)
    {
        DatabaseName = databaseName;
    }
}

/// <summary>
/// Thrown when creating or opening a database would exceed <see cref="CrystalOptions.MaxOpenDatabases"/>
/// (spec 005 R9.2, R9.3; design addendum A6). Databases are never closed when idle, so the ways forward are a
/// higher limit or fewer databases.
/// </summary>
public sealed class DatabaseLimitException : Exception
{
    /// <summary>The database that could not be created or opened.</summary>
    public string DatabaseName { get; }

    /// <summary>The configured limit that was reached.</summary>
    public int Limit { get; }

    /// <summary>Initializes a new instance of <see cref="DatabaseLimitException"/>.</summary>
    public DatabaseLimitException(string databaseName, int limit, string message)
        : base(message)
    {
        DatabaseName = databaseName;
        Limit = limit;
    }
}

/// <summary>
/// Thrown on a replication standby when attempting operations reserved for the primary (spec 005 R11.3, design § 6).
/// </summary>
public sealed class NotPrimaryException : Exception
{
    /// <summary>Initializes a new instance of <see cref="NotPrimaryException"/>.</summary>
    public NotPrimaryException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when a box cannot be found by name or id, or has been dropped (spec 002 R3.2, R4.3).
/// </summary>
public sealed class BoxNotFoundException : Exception
{
    /// <summary>The box name, if known.</summary>
    public string? BoxName { get; }

    /// <summary>The box id, if known.</summary>
    public uint? BoxId { get; }

    /// <summary>Initializes a new instance of <see cref="BoxNotFoundException"/>.</summary>
    public BoxNotFoundException(string message) : base(message) { }

    /// <summary>Initializes a new instance of <see cref="BoxNotFoundException"/> with a name.</summary>
    public BoxNotFoundException(string boxName, string message) : base(message)
    {
        BoxName = boxName;
    }

    /// <summary>Initializes a new instance of <see cref="BoxNotFoundException"/> with an id.</summary>
    public BoxNotFoundException(uint boxId, string message) : base(message)
    {
        BoxId = boxId;
    }
}

/// <summary>
/// Thrown when attempting to create or rename to a box name that already exists (spec 002 R3.1, R3.8).
/// </summary>
public sealed class BoxNameTakenException : Exception
{
    /// <summary>The box name that is already taken.</summary>
    public string BoxName { get; }

    /// <summary>Initializes a new instance of <see cref="BoxNameTakenException"/>.</summary>
    public BoxNameTakenException(string boxName, string message) : base(message)
    {
        BoxName = boxName;
    }
}

/// <summary>
/// Thrown when a database has failed (spec 006 R5): a write or a WAL sync failed, so writes acknowledged since its last
/// successful sync may not be on disk. The database refuses every write from then on; reads still work. The way
/// forward is in the message: close the store, check the disk, and open the store again.
/// </summary>
public sealed class DatabaseFailedException : Exception
{
    /// <summary>The database, as Crystal names it in messages.</summary>
    public string Database { get; }

    /// <summary>Initializes a new instance of <see cref="DatabaseFailedException"/>.</summary>
    public DatabaseFailedException(string database, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Database = database;
    }
}

/// <summary>
/// Thrown when a box handle is used with a database that did not issue it (spec 002 R1.2, R4.3; Decisions: batches
/// across databases are refused). Box ids repeat across databases, so the handle is refused, never redirected.
/// </summary>
public sealed class BoxNotInDatabaseException : Exception
{
    /// <summary>The handle's box id.</summary>
    public uint BoxId { get; }

    /// <summary>The handle's box name.</summary>
    public string? BoxName { get; }

    /// <summary>Initializes a new instance of <see cref="BoxNotInDatabaseException"/>.</summary>
    public BoxNotInDatabaseException(uint boxId, string? boxName, string message) : base(message)
    {
        BoxId = boxId;
        BoxName = boxName;
    }
}

/// <summary>
/// Thrown when a write batch touches a box that has been dropped (spec 002 R4.1).
/// </summary>
public sealed class BoxDroppedException : Exception
{
    /// <summary>The dropped box name, if known.</summary>
    public string? BoxName { get; }

    /// <summary>The dropped box id.</summary>
    public uint BoxId { get; }

    /// <summary>Initializes a new instance of <see cref="BoxDroppedException"/>.</summary>
    public BoxDroppedException(uint boxId, string message) : base(message)
    {
        BoxId = boxId;
    }

    /// <summary>Initializes a new instance of <see cref="BoxDroppedException"/> with a name and id.</summary>
    public BoxDroppedException(uint boxId, string boxName, string message) : base(message)
    {
        BoxId = boxId;
        BoxName = boxName;
    }
}


/// <summary>
/// Thrown when a backup destination or root cannot be used (spec 010 R2.1, R2.2): not an absolute path, inside the
/// store's data directory or containing it, or a non-empty folder that is not a backup root.
/// </summary>
public sealed class BackupDestinationException : Exception
{
    /// <summary>The destination as the caller named it.</summary>
    public string Destination { get; }

    /// <summary>Initializes a new instance of <see cref="BackupDestinationException"/>.</summary>
    public BackupDestinationException(string destination, string message)
        : base(message)
    {
        Destination = destination;
    }
}

/// <summary>
/// Thrown when a backup root is held by another backup, verify, purge or restore, in this process or another
/// (spec 010 R2.4). Operations on a root are refused at once, never queued.
/// </summary>
public sealed class BackupRootLockedException : Exception
{
    /// <summary>The backup root's path.</summary>
    public string RootPath { get; }

    /// <summary>Initializes a new instance of <see cref="BackupRootLockedException"/>.</summary>
    public BackupRootLockedException(string rootPath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        RootPath = rootPath;
    }
}

/// <summary>Thrown when a store is asked for a backup while one is running (spec 010 R1.6).</summary>
public sealed class BackupInProgressException : Exception
{
    /// <summary>The destination of the backup that is running.</summary>
    public string RunningDestination { get; }

    /// <summary>Initializes a new instance of <see cref="BackupInProgressException"/>.</summary>
    public BackupInProgressException(string runningDestination, string message)
        : base(message)
    {
        RunningDestination = runningDestination;
    }
}

/// <summary>Thrown when a backup root holds no set by the given id (spec 010 R9.8).</summary>
public sealed class BackupSetNotFoundException : Exception
{
    /// <summary>The set id asked for.</summary>
    public string SetId { get; }

    /// <summary>Initializes a new instance of <see cref="BackupSetNotFoundException"/>.</summary>
    public BackupSetNotFoundException(string setId, string message)
        : base(message)
    {
        SetId = setId;
    }
}

/// <summary>
/// Thrown when a backup manifest or root file is damaged (spec 010 R4.2): unreadable, of another format, or failing its
/// checksum. A damaged manifest is never used.
/// </summary>
public sealed class BackupDamagedException : Exception
{
    /// <summary>The damaged file.</summary>
    public string Path { get; }

    /// <summary>Initializes a new instance of <see cref="BackupDamagedException"/>.</summary>
    public BackupDamagedException(string path, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }
}

/// <summary>
/// Thrown when a restore would bring in a database whose UID the store already has (spec 010 R6.4). Drop the existing
/// one first, or restore as a copy with <see cref="RestoreOptions.NewIdentity"/>.
/// </summary>
public sealed class DatabaseUidTakenException : Exception
{
    /// <summary>The UID that is taken.</summary>
    public string Uid { get; }

    /// <summary>The name of the database that has it in this store.</summary>
    public string ExistingName { get; }

    /// <summary>Initializes a new instance of <see cref="DatabaseUidTakenException"/>.</summary>
    public DatabaseUidTakenException(string uid, string existingName, string message)
        : base(message)
    {
        Uid = uid;
        ExistingName = existingName;
    }
}

/// <summary>
/// Thrown when a batch stages a key longer than <see cref="CrystalOptions.MaxKeyBytes"/> (spec 012 R9). Nothing of the
/// operation is staged.
/// </summary>
public sealed class KeyTooLargeException : Exception
{
    /// <summary>The name of the box the key was for.</summary>
    public string BoxName { get; }

    /// <summary>The key's length in bytes.</summary>
    public int KeyBytes { get; }

    /// <summary>The limit it exceeded.</summary>
    public int Limit { get; }

    /// <summary>Initializes a new instance of <see cref="KeyTooLargeException"/>.</summary>
    public KeyTooLargeException(string boxName, int keyBytes, int limit, string message)
        : base(message)
    {
        BoxName = boxName;
        KeyBytes = keyBytes;
        Limit = limit;
    }
}
