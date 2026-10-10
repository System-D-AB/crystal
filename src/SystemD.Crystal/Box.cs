namespace SystemD.Crystal;

/// <summary>
/// A handle to a box inside a database (spec 002 R1.1, R4.3; design § 2).
/// Carries the immutable box id and informational alias name. Validated at commit (R4.1).
/// A handle addresses only the open database that issued it, through <c>Create</c>, <c>Find</c> or <c>Rename</c>;
/// any other database refuses it with <see cref="BoxNotInDatabaseException"/>, because box ids repeat across databases.
/// </summary>
/// <param name="Id">The immutable box id addressing the box's key range.</param>
/// <param name="Name">The informational name alias of the box at the time the handle was created.</param>
public readonly record struct Box(uint Id, string Name)
{
    /// <summary>The open database's catalogue that issued this handle; null for a handle built by hand.</summary>
    internal object? Catalogue { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"Box {Id} ({Name})";
}
