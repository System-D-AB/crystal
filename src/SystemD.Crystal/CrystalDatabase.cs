using System;
using System.Threading;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal;

/// <summary>
/// The handle to an open Crystal database (spec 005 R5.2, R5.7; spec 002 design § 2). A store has one handle per
/// open database, and <see cref="CrystalStore.OpenDatabase"/> returns it, so every holder sees a rename or swap.
/// It carries the immutable database id; after a drop, every call through it, or through a batch or snapshot taken
/// from it, is refused with <see cref="DatabaseNotFoundException"/>.
/// </summary>
public sealed class CrystalDatabase
{
    private readonly DatabaseSlot _slot;
    private readonly BoxCatalog _boxes;
    private string _name;

    internal CrystalDatabase(CrystalStore store, DatabaseSlot slot, string name, string uid)
    {
        ArgumentNullException.ThrowIfNull(store);
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        Uid = uid ?? throw new ArgumentNullException(nameof(uid));
        _boxes = new BoxCatalog(this, slot.Context);
    }

    /// <summary>The immutable database id.</summary>
    public uint Id => _slot.Id;

    /// <summary>The database's stable UID (spec 003 R1.3): survives rename, swap, reopen and promotion.</summary>
    public string Uid { get; }

    /// <summary>The database's current name; rename and swap update it.</summary>
    public string Name
    {
        get => Volatile.Read(ref _name);
        internal set => Volatile.Write(ref _name, value);
    }

    /// <summary>The box catalogue for this database (spec 002 R2, R3; design § 2).</summary>
    public BoxCatalog Boxes => _boxes;

    /// <summary>Per-box striped locks for product read-decide-write sequences (spec 002 R5; design § 4).</summary>
    public BoxLocks Locks => _slot.Context.Locks;

    /// <summary>Creates a new batch for writes across boxes in this database (spec 002 R6; design § 2).</summary>
    public Batch NewBatch()
    {
        using var _ = EnterOperation();
        return new Batch(this, _slot.Context);
    }

    /// <summary>
    /// Creates a point-in-time snapshot for reads and scans in this database (spec 002 R6.4; design § 2). Dispose
    /// it when done; a snapshot still held when the database is dropped or the store closes is released then.
    /// </summary>
    public Snapshot Snapshot() => new(this, _slot.Context, _slot);

    /// <summary>
    /// Enters one call through this database, lock-free (spec 005 design addendum A3). Refused with
    /// <see cref="DatabaseNotFoundException"/> once the database is dropped. Dispose the scope when the call ends.
    /// </summary>
    internal IDisposable EnterOperation() => _slot.Enter(Name);

    internal DatabaseSlot Slot => _slot;
}
