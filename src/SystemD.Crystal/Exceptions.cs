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

