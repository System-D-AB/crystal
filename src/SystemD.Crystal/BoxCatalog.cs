// Ported from Highway: src/Highway.Server/Storage/Boxes/Catalog.cs
// Adapted for Crystal: rename, swap, catalogue gate, exact in-memory catalogue, JSON records (spec 002 R2–R4, design § 2, § 3).

using System;
using System.Collections.Generic;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>
/// The catalogue of boxes in a database (spec 002 R2–R4; design § 2).
/// All operations are backed by the catalogue in box 0 and maintained in memory behind the catalogue gate.
/// </summary>
public sealed class BoxCatalog
{
    private readonly CrystalDatabase _database;
    private readonly DatabaseContext _context;

    internal BoxCatalog(CrystalDatabase database, DatabaseContext context)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Creates a new box in this database with the given name and profile (spec 002 R3.1).
    /// Allocates an immutable id, writes the alias and record, and updates the catalogue in one batch.
    /// Throws <see cref="BoxNameTakenException"/> if the name already exists.
    /// </summary>
    public Box Create(string name, BoxProfile profile = BoxProfile.General)
    {
        using var _ = _database.EnterOperation();
        return _context.CreateBox(name, profile);
    }

    /// <summary>
    /// Finds a live box by name (spec 002 R3.2). Returns <c>null</c> if no live box has this name.
    /// </summary>
    public Box? Find(string name)
    {
        using var _ = _database.EnterOperation();
        return _context.FindBox(name);
    }

    /// <summary>
    /// Renames a box to a new name (spec 002 R3.8). The box id, keys, and data are untouched.
    /// Returns a new handle carrying the new name.
    /// </summary>
    public Box Rename(Box box, string newName)
    {
        using var _ = _database.EnterOperation();
        return _context.RenameBox(box, newName);
    }

    /// <summary>
    /// Atomically swaps the names of two live boxes in one batch (spec 002 R3.9).
    /// </summary>
    public void Swap(Box first, Box second)
    {
        using var _ = _database.EnterOperation();
        _context.SwapBoxes(first, second);
    }

    /// <summary>
    /// Lists all live boxes in this database from the in-memory catalogue (spec 002 R3.3).
    /// </summary>
    public IReadOnlyList<BoxInfo> List()
    {
        using var _ = _database.EnterOperation();
        return _context.ListBoxes();
    }

    /// <summary>
    /// Drops a box (spec 002 R3.4). Removes the alias, marks the record dropped, and range-deletes
    /// all keys in the box in one batch.
    /// </summary>
    public void Drop(Box box)
    {
        using var _ = _database.EnterOperation();
        _context.DropBox(box);
    }

    /// <summary>
    /// The durable catalogue generation counter, incremented by every catalogue change (spec 002 R2.5).
    /// </summary>
    public long Generation
    {
        get
        {
            using var _ = _database.EnterOperation();
            return _context.CatalogueGeneration;
        }
    }
}
