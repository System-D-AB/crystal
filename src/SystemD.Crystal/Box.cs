using System;

namespace SystemD.Crystal;

/// <summary>
/// A handle to a box inside a database (spec 002 R1.1, R4.3; design § 2), issued by that database's catalogue through
/// <c>Create</c>, <c>Find</c>, <c>Rename</c> or <c>List</c>. It carries the box's immutable id and its name at the time,
/// and addresses only the open database that issued it: any other refuses it with
/// <see cref="BoxNotInDatabaseException"/>, because box ids repeat across databases. It cannot be built or altered by hand
/// (spec 015 R3.1): a handle is the catalogue's word that the box was in this database.
/// <para>Two handles are equal when they address the same box of the same open database, whatever names they carry: a
/// handle from before a rename equals one from after it. The name is informational.</para>
/// </summary>
public readonly struct Box : IEquatable<Box>
{
    /// <summary>A handle no database issued: refused by every database (tests use it to prove so).</summary>
    internal Box(uint id, string name)
        : this(id, name, catalogue: null)
    {
    }

    internal Box(uint id, string name, object? catalogue)
    {
        Id = id;
        _name = name;
        Catalogue = catalogue;
    }

    private readonly string? _name;

    /// <summary>The immutable box id addressing the box's key range.</summary>
    public uint Id { get; }

    /// <summary>The box's name when this handle was issued; informational, since a rename does not change the handle.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>The open database's catalogue that issued this handle; null for a handle no database issued.</summary>
    internal object? Catalogue { get; }

    /// <inheritdoc/>
    public bool Equals(Box other) => Id == other.Id && ReferenceEquals(Catalogue, other.Catalogue);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Box other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Id, Catalogue is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Catalogue));

    /// <summary>Whether two handles address the same box of the same open database.</summary>
    public static bool operator ==(Box left, Box right) => left.Equals(right);

    /// <summary>Whether two handles address different boxes, or the same id in different databases.</summary>
    public static bool operator !=(Box left, Box right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => $"Box {Id} ({Name})";
}
