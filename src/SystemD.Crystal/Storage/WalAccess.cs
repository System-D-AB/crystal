using System;
using System.Collections.Generic;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>
/// The write-ahead-log and replication-support surface of one database (spec 001-2 R3.4): the latest
/// sequence, updates since a sequence, WAL tailing <b>guarded</b> by trap R4.1, checkpoints, and
/// file-deletion holds. Used by <c>003-replication</c>.
///
/// <para><b>Trap R4.1.</b> Tailing the WAL from a sequence the log no longer holds crashes the process
/// in native code. <see cref="TryTail"/> first confirms the WAL still reaches the requested position;
/// a gone range is reported, never tailed, so a gap becomes a re-bootstrap, not a crash.</para>
/// </summary>
internal sealed class WalAccess
{
    private readonly RocksInstance _instance;
    private readonly RocksDb _db;

    internal WalAccess(RocksInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = instance;
        _db = instance.Db;
    }

    /// <summary>The database's latest committed WAL sequence number (R3.4). On a standby this is the position itself.</summary>
    internal ulong LatestSequence() => _db.GetLatestSequenceNumber();

    /// <summary>
    /// Tails the WAL from just after <paramref name="fromSequence"/>, if the log still holds it (R4.1).
    /// Returns the iterator on success, or <c>null</c> when the range is gone — the caller then treats it
    /// as a gap. The caller disposes a returned iterator.
    /// </summary>
    internal TransactionLogIterator? TryTail(ulong fromSequence)
    {
        // GetUpdatesSince(n) yields batches with sequence >= n. We want everything after fromSequence,
        // i.e. starting at fromSequence + 1. The binding throws (not returns invalid) when that sequence
        // is beyond the WAL; either signal means the range is gone and must be refused, never tailed.
        var want = fromSequence + 1;
        TransactionLogIterator iterator;
        try
        {
            iterator = _db.GetUpdatesSince(want);
        }
        catch (RocksDbException)
        {
            return null;
        }

        try
        {
            if (!iterator.Valid())
            {
                iterator.Dispose();
                return null;
            }

            iterator.GetBatch(out var firstSequence);
            if (firstSequence > want)
            {
                // The first batch the WAL can serve starts after what we need: the range is gone.
                iterator.Dispose();
                return null;
            }

            return iterator;
        }
        catch (RocksDbException)
        {
            iterator.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Opens a WAL iterator just after <paramref name="fromSequence"/> for a held reader (spec 003 design § 3),
    /// without taking its first batch — the caller reads it. Null when the range is gone (R4.1). The caller must have
    /// confirmed <see cref="WalHolds"/> first; this re-checks only that the iterator is valid, not the first batch's
    /// sequence (reading it here would take the batch, which the binding then refuses to hand out again). The caller
    /// disposes the iterator.
    /// </summary>
    internal TransactionLogIterator? OpenTail(ulong fromSequence)
    {
        var want = fromSequence + 1;
        TransactionLogIterator iterator;
        try
        {
            iterator = _db.GetUpdatesSince(want);
        }
        catch (RocksDbException)
        {
            return null;
        }

        try
        {
            if (!iterator.Valid())
            {
                iterator.Dispose();
                return null;
            }

            return iterator;
        }
        catch (RocksDbException)
        {
            iterator.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Whether the WAL can still serve everything after <paramref name="fromSequence"/> (the R4.1 check,
    /// without opening a tail to keep). A gone range is reported as <c>false</c>, never a crash.
    /// </summary>
    internal bool WalHolds(ulong fromSequence)
    {
        var want = fromSequence + 1;
        TransactionLogIterator iterator;
        try
        {
            iterator = _db.GetUpdatesSince(want);
        }
        catch (RocksDbException)
        {
            return false;
        }

        using (iterator)
        {
            if (!iterator.Valid())
            {
                return false;
            }

            iterator.GetBatch(out var firstSequence);
            return firstSequence <= want;
        }
    }

    /// <summary>
    /// Reads committed batches from the WAL from just after <paramref name="fromSequence"/> (spec 003 R3.5, design
    /// § 4), up to <paramref name="maxBytes"/> across the page (the first batch always fits). Fills
    /// <paramref name="into"/> with the contiguous run as <c>(seq, bytes)</c>, and returns whether it succeeded, or
    /// the range is gone / the straddling first record could not be trimmed (both a gap). The first record is trimmed
    /// to start exactly at <c>fromSequence + 1</c> if it straddles the cursor (R3.6). Does not hold the WAL from
    /// deletion.
    /// </summary>
    internal WalPageStatus ReadPage(ulong fromSequence, int maxBytes, List<(ulong Seq, byte[] Data)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var want = fromSequence + 1;

        // Caught up: the cursor is at or beyond the latest committed sequence, so there is nothing after it yet. This
        // is distinct from a gap (data discarded below the cursor): the standby is listed with an empty page, and
        // asking GetUpdatesSince for a sequence past the end can return nothing or an unusable iterator (R4.1), which
        // must not be mistaken for a gone range (design § 4).
        if (fromSequence >= LatestSequence())
        {
            return WalPageStatus.Ok;
        }

        TransactionLogIterator iterator;
        try
        {
            iterator = _db.GetUpdatesSince(want);
        }
        catch (RocksDbException)
        {
            return WalPageStatus.Gap; // the range is gone
        }

        using (iterator)
        {
            long pageBytes = 0;
            var first = true;
            var expected = want;

            while (iterator.Valid())
            {
                using var batch = iterator.GetBatch(out var seq);
                var bytes = batch.ToBytes();

                if (first)
                {
                    first = false;
                    if (seq > want)
                    {
                        // The first batch the WAL can serve starts after the cursor: the range is gone (R5.1).
                        return WalPageStatus.Gap;
                    }

                    if (seq < want)
                    {
                        // The cursor is inside this record (a group-commit merge): trim it to start at `want` (R3.6).
                        if (!Replication.WriteBatchTrim.TryStartAt(bytes, want, out var trimmed))
                        {
                            return WalPageStatus.Gap; // untrimmable → a gap, the standby re-bootstraps
                        }

                        bytes = trimmed;
                        seq = want;
                    }
                }
                else if (seq != expected)
                {
                    break; // a hole in the WAL — stop; the next pull resumes
                }

                if (into.Count > 0 && pageBytes + bytes.Length > maxBytes)
                {
                    break; // the byte cap; the next pull continues from here
                }

                var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
                into.Add((seq, bytes));
                pageBytes += bytes.Length;
                expected = seq + Math.Max(count, 1u);
                iterator.Next();
            }

            return WalPageStatus.Ok;
        }
    }

    /// <summary>
    /// Applies raw committed batch bytes to this database, synced (spec 003 R4.2, design § 5). The bytes are a single
    /// batch or a concatenated page; RocksDB writes them with their own sequences (its <c>IngestBatch</c> does not
    /// sync and ignores its sequence argument, so Crystal writes the bytes itself, 001-2 R4.3). The watermark after
    /// is <see cref="LatestSequence"/>.
    /// </summary>
    internal void ApplyRaw(byte[] batchBytes)
    {
        ArgumentNullException.ThrowIfNull(batchBytes);
        using var batch = new WriteBatch(batchBytes, batchBytes.Length);
        // Through the one commit point (spec 004 T10, 001 R3.2), synced (R4.2). The batch carries the primary's own
        // sequences; RocksDB writes them as given (its IngestBatch does not sync and ignores the sequence, 001-2 R4.3).
        _instance.Write(batch, sync: true);
    }

    /// <summary>Holds off WAL file deletion (for a snapshot capture or a held tailer). Pair with <see cref="EnableFileDeletions"/>.</summary>
    internal void DisableFileDeletions() => _db.DisableFileDeletions();

    /// <summary>Releases a WAL file-deletion hold.</summary>
    internal void EnableFileDeletions() => _db.EnableFileDeletions();

    /// <summary>
    /// Takes a checkpoint of the database into <paramref name="destinationPath"/> (R3.4), on the
    /// database's own filesystem so it can hard-link instead of copy. The directory must not exist.
    /// </summary>
    internal void Checkpoint(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var checkpoint = _db.Checkpoint();
        checkpoint.Save(destinationPath, 0);
    }
}

/// <summary>The outcome of reading a page from the WAL (spec 003 R3, R5).</summary>
internal enum WalPageStatus
{
    /// <summary>Batches were read (possibly none, if the database is caught up).</summary>
    Ok,

    /// <summary>The requested range is gone, or a straddling record could not be trimmed: a gap (R5.1).</summary>
    Gap,
}
