// Ported from Highway: src/Highway.Server/Storage/Rocks/WalTailerPool.cs — the held-reader pool for a standby behind
// the in-memory tail (spec 003 R3.5, design § 3). RocksDB-touching (it holds WAL iterators and WAL-file-deletion
// holds), so it lives in Storage.

using System;
using System.Collections.Generic;
using System.Threading;

namespace SystemD.Crystal.Storage;

// Clock reads go through the injected TimeProvider (CrystalOptions.Clock), never Environment.TickCount64, so a fake
// clock in a test drives the idle/max-hold timing — enforced by the architecture gate (spec 004 R2.6).

/// <summary>
/// Holds WAL readers between pulls for a standby catching up (spec 003 design § 3). A pull carries no standby id, so
/// readers are held by the sequence they serve next: a pull from <c>fromSeq</c> continues the reader whose next
/// sequence is <c>fromSeq + 1</c>, else opens a fresh one (one O(file position) read, as before). A reader is taken
/// out of the pool while read, so concurrent pulls never share one. The pool is small and cleared on any epoch or role
/// change, like the tail.
///
/// <para><b>Never a reader over a missing WAL.</b> A reader opened on a range the WAL no longer holds crashes the
/// process in native code on its first read (001-2 R4.1), where <c>GetUpdatesSince</c> simply returns nothing. So a
/// fresh reader is opened only after <see cref="WalAccess.WalHolds"/> confirms the WAL still reaches the sequence
/// (otherwise the caller refuses the pull as a gap), and while any reader is held WAL file deletion is paused
/// (counted). Retention resumes when the last reader closes — and a hold never lasts longer than
/// <see cref="MaxHold"/>: past it a reader is closed after its read instead of being kept, so a standby that pulls
/// forever without catching up cannot pause retention forever (the WAL caps still bound the disk). An idle reader is
/// closed after <see cref="IdleTimeout"/>.</para>
/// </summary>
internal sealed class WalReaderPool : IDisposable
{
    private const int Capacity = 4;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(30);

    private readonly WalAccess _wal;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly List<(WalReader Reader, long IdleSince)> _idle = [];
    private int _live;        // readers open (idle or being read); > 0 holds WAL file deletion off
    private long _heldSince;  // when the current hold began (clock ticks)
    private bool _disposed;

    public WalReaderPool(WalAccess wal, TimeProvider clock)
    {
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // R11.2: an idle reader is released after IdleTimeout even if no pull comes again (a standby that went away),
        // so it never holds WAL file deletion off for good. Pulls release expired readers too.
        _expiryTimer = clock.CreateTimer(_ => ExpireFromTimer(), null, IdleTimeout, IdleTimeout);
    }

    private readonly ITimer _expiryTimer;

    // Held by the timer's expiry pass and by Dispose: releasing a reader calls into the database's native instance, so
    // Dispose (run while the database closes) waits for a pass in progress and no pass runs after it.
    private readonly Lock _expiryGate = new();

    private void ExpireFromTimer()
    {
        lock (_expiryGate)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
            }

            DisposeExpired();
        }
    }

    private long NowTicks => _clock.GetUtcNow().Ticks;

    /// <summary>Readers opened so far — each one read the WAL file from its start once (design § 3 tests).</summary>
    internal int Opened { get; private set; }

    /// <summary>True while a reader holds WAL file deletion off (design § 3 tests).</summary>
    internal bool HoldingWal { get { lock (_lock) { return _live > 0; } } }

    /// <summary>
    /// Reads the next chunk at or after <paramref name="fromSeq"/> + 1: a copy of its bytes and its first and next
    /// sequence, or null when the WAL does not hold that sequence (the caller decides whether that is "caught up" or a
    /// gap). Continues a held reader whose next sequence matches, else opens a fresh one.
    /// </summary>
    public (ulong First, ulong Next, byte[] Data)? Read(long fromSeq, int maxBytes)
    {
        _ = maxBytes; // a chunk is one whole batch; the caller applies the byte cap. Kept for parity with the design.
        var want = (ulong)fromSeq + 1;
        lock (_lock)
        {
            if (_disposed)
            {
                return null; // released with its database: never open or keep a reader on an instance being closed
            }
        }

        DisposeExpired();

        WalReader? reader = null;
        lock (_lock)
        {
            var i = _idle.FindIndex(t => t.Reader.NextSequence == want);
            if (i >= 0)
            {
                reader = _idle[i].Reader;
                _idle.RemoveAt(i);
            }
        }

        reader ??= Open(want);
        if (reader is null)
        {
            return null;
        }

        try
        {
            if (reader.TryReadChunk() is not { } chunk)
            {
                Close(reader);
                return null;
            }

            Return(reader);
            return chunk;
        }
        catch
        {
            Close(reader);
            throw;
        }
    }

    /// <summary>A fresh reader at <paramref name="want"/>, or null when the WAL does not hold it.</summary>
    private WalReader? Open(ulong want)
    {
        Hold(); // before the check, so nothing the check saw can be deleted before the reader opens
        try
        {
            if (!_wal.WalHolds(want - 1))
            {
                Release();
                return null;
            }

            var reader = WalReader.Open(_wal, want);
            if (reader is null)
            {
                Release();
                return null;
            }

            lock (_lock)
            {
                Opened++;
            }

            return reader;
        }
        catch
        {
            Release();
            throw;
        }
    }

    private void Hold()
    {
        lock (_lock)
        {
            if (_live++ == 0)
            {
                _wal.DisableFileDeletions();
                _heldSince = NowTicks;
            }
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            if (--_live == 0)
            {
                _wal.EnableFileDeletions();
            }
        }
    }

    private void Close(WalReader reader)
    {
        reader.Dispose();
        Release();
    }

    private void Return(WalReader reader)
    {
        var close = new List<WalReader>();
        lock (_lock)
        {
            if (_disposed)
            {
                close.Add(reader); // disposed while this reader was out: close it, never keep it
            }
            else
            if (NowTicks - _heldSince > MaxHold.Ticks)
            {
                // Held long enough: close everything so retention runs before the next pull.
                close.Add(reader);
                foreach (var (r, _) in _idle)
                {
                    close.Add(r);
                }

                _idle.Clear();
            }
            else
            {
                _idle.Add((reader, NowTicks));
                if (_idle.Count > Capacity)
                {
                    close.Add(_idle[0].Reader); // the least recently returned
                    _idle.RemoveAt(0);
                }
            }
        }

        foreach (var r in close)
        {
            Close(r);
        }
    }

    /// <summary>Closes readers idle past <see cref="IdleTimeout"/>, releasing the WAL they hold.</summary>
    internal void DisposeExpired()
    {
        var cutoff = NowTicks - IdleTimeout.Ticks;
        List<WalReader>? drop = null;
        lock (_lock)
        {
            for (var i = _idle.Count - 1; i >= 0; i--)
            {
                if (_idle[i].IdleSince > cutoff)
                {
                    continue;
                }

                (drop ??= []).Add(_idle[i].Reader);
                _idle.RemoveAt(i);
            }
        }

        if (drop is not null)
        {
            foreach (var r in drop)
            {
                Close(r);
            }
        }
    }

    /// <summary>Test hook (spec 011 R6.3): runs after <see cref="Clear"/> took the idle readers, before it closes them.</summary>
    internal Action? OnClearTaken { get; set; }

    /// <summary>
    /// Drops every held reader (epoch or role change: a new history, like the tail). Under the expiry gate, as
    /// <see cref="Dispose"/> is (spec 011 R6.2): closing a reader calls into the database's native instance, so a close of
    /// the database waits for a clear in progress, and no clear closes a reader after the pool is disposed.
    /// </summary>
    public void Clear()
    {
        lock (_expiryGate)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
            }

            CloseIdle();
        }
    }

    private void CloseIdle()
    {
        List<WalReader> drop;
        lock (_lock)
        {
            drop = new List<WalReader>(_idle.Count);
            foreach (var (r, _) in _idle)
            {
                drop.Add(r);
            }

            _idle.Clear();
        }

        OnClearTaken?.Invoke();
        foreach (var r in drop)
        {
            Close(r);
        }
    }

    public void Dispose()
    {
        _expiryTimer.Dispose();
        lock (_expiryGate)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            CloseIdle();
        }
    }
}
