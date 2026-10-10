// Ported from Highway: src/Highway.Server/Storage/Rocks/WalTailerPool.cs (the WalTailer it holds) — a WAL iterator
// kept open across pulls so a standby catching up page by page does not re-read the live WAL file from its start each
// time (spec 003 R3.5, design § 3). RocksDB-touching (it owns a TransactionLogIterator), so it lives in Storage.

using System;
using System.Buffers.Binary;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// One held WAL reader (spec 003 design § 3): a <see cref="TransactionLogIterator"/> opened once and continued across
/// pulls. <c>GetUpdatesSince</c> re-reads the live WAL file from its start on every call — O(file position) per page,
/// O(pages²) for a whole catch-up; a reader opened once continues where it stopped, so a catch-up costs one such read
/// in total. A chunk is one write batch of whole WAL records (<c>[8B LE first seq][4B LE count]…</c>), starting at the
/// record that contains the requested sequence; the caller trims a straddling head
/// (<see cref="Replication.WriteBatchTrim"/>).
/// </summary>
internal sealed class WalReader : IDisposable
{
    private readonly TransactionLogIterator _iterator;

    private WalReader(TransactionLogIterator iterator, ulong nextSequence)
    {
        _iterator = iterator;
        NextSequence = nextSequence;
    }

    /// <summary>The sequence this reader will serve next (the key the pool holds it by).</summary>
    public ulong NextSequence { get; private set; }

    /// <summary>
    /// Opens a reader positioned at the record containing <paramref name="want"/>, or null when the live WAL no longer
    /// holds it (R4.1 — the binding crashes on a gone range, so the caller must have confirmed the hold first). The
    /// iterator's first batch is not read here: the binding refuses to hand the same batch out twice, so the first
    /// <see cref="TryReadChunk"/> reads it. <see cref="NextSequence"/> starts at <paramref name="want"/> so the pool's
    /// lookup matches before the first read. The caller disposes the reader.
    /// </summary>
    public static WalReader? Open(WalAccess wal, ulong want)
    {
        var iterator = wal.OpenTail(want - 1);
        return iterator is null ? null : new WalReader(iterator, want);
    }

    /// <summary>
    /// Reads the chunk at <see cref="NextSequence"/>: its first and next sequence and a copy of its bytes, or null
    /// when the WAL holds nothing more (the caller decides caught-up vs gap). Advances <see cref="NextSequence"/>.
    /// </summary>
    public (ulong First, ulong Next, byte[] Data)? TryReadChunk()
    {
        if (!_iterator.Valid())
        {
            return null;
        }

        byte[] data;
        ulong first;
        try
        {
            using var batch = _iterator.GetBatch(out first);
            data = batch.ToBytes();
        }
        catch (RocksDbException)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        var next = first + Math.Max(count, 1u);
        NextSequence = next;
        _iterator.Next();
        return (first, next, data);
    }

    public void Dispose() => _iterator.Dispose();
}
