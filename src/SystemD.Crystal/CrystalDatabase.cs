using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Per-key striped locks (spec 012 R3), for a read-decide-write on one record and the records that depend on it.
    /// Independent of <see cref="Locks"/>: a box lock does not exclude key locks in that box.
    /// </summary>
    public KeyLocks KeyLocks => _slot.Context.KeyLocks;

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
    /// RocksDB's estimate of the bytes the keys of <paramref name="box"/> in <c>[from, to)</c> occupy, memtables and
    /// table files included (spec 012 R2). Empty bounds are the box's start and end, so the whole box is a valid range;
    /// <paramref name="from"/> after <paramref name="to"/> is refused with <see cref="ArgumentException"/>, and equal
    /// bounds give 0.
    /// <para><b>An estimate, not a count:</b> approximate, not consistent with any snapshot, and <b>0 does not mean
    /// empty</b> (a small range, or one held only in memory, can be below the estimate's resolution). Turning bytes into
    /// a number of entries belongs to the product, which knows its entries' widths. It never scans: its cost does not
    /// grow with the range. A read: allowed on a standby and on a failed database.</para>
    /// </summary>
    public long ApproximateBytes(Box box, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        if (!from.IsEmpty && !to.IsEmpty && from.SequenceCompareTo(to) > 0)
        {
            throw new ArgumentException("The range's start sorts after its end (spec 012 R2.4).", nameof(from));
        }

        return ApproximateBytes(box, [new KeyRange(from.ToArray(), to.ToArray())])[0];
    }

    /// <summary>Estimates one <see cref="KeyRange"/> of <paramref name="box"/> (spec 012 R2, R4.1).</summary>
    public long ApproximateBytes(Box box, KeyRange range) => ApproximateBytes(box, [range])[0];

    /// <summary>
    /// Estimates several ranges of one box in one native call (spec 012 R2.3): one value per range, in order. See
    /// <see cref="ApproximateBytes(Box, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> for what the numbers mean.
    /// </summary>
    public long[] ApproximateBytes(Box box, IReadOnlyList<KeyRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        using var _ = EnterOperation();
        _slot.Context.EnsureOwned(box);
        // A long like every other byte count of the API (spec 015 R3.1); an estimate past long's range is not a real size.
        return [.. _slot.Context.ApproximateBytes(box.Id, [.. ranges.Select(r => (r.From ?? [], r.To ?? []))]).Select(b => (long)Math.Min(b, long.MaxValue))];
    }

    /// <summary>
    /// Enters one call through this database, lock-free (spec 005 design addendum A3). Refused with
    /// <see cref="DatabaseNotFoundException"/> once the database is dropped. Dispose the scope when the call ends.
    /// </summary>
    internal IDisposable EnterOperation() => _slot.Enter(Name);

    internal DatabaseSlot Slot => _slot;
}
