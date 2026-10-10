using System;

namespace SystemD.Crystal;

/// <summary>
/// Information about a live database in the store (spec 005 R5.3, design § 6).
/// </summary>
/// <param name="Id">The immutable database id.</param>
/// <param name="Name">The current database name alias.</param>
/// <param name="Uid">The database's stable UID (spec 003 R1.3): survives rename, swap, reopen and promotion.</param>
/// <param name="Replicated">Whether the database is replicated.</param>
/// <param name="Created">When the database was created.</param>
public sealed record DatabaseInfo(uint Id, string Name, string Uid, bool Replicated, DateTimeOffset Created);
