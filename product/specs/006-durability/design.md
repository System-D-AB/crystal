# 006 — Durability modes: design

## 1. Public API

```csharp
namespace SystemD.Crystal;

public enum DurabilityMode { Strict = 0, Interval = 1, Buffered = 2 }   // Strict is the default (open question 1)

public sealed class CrystalOptions
{
    public DurabilityMode Durability { get; init; } = DurabilityMode.Strict;
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMilliseconds(50);   // 1 ms – 10 s
    // existing: BlockCacheBytes, WriteBufferBudgetBytes, MaxOpenDatabases, BackgroundThreads, Clock, LoggerFactory
}

public sealed class Batch
{
    public void Commit();                          // the store's default mode
    public void Commit(DurabilityMode durability); // replaces Commit(bool sync)
}

public sealed class CrystalStore
{
    public void SyncAll();                         // every open database's WAL, now (R2.4)
}

public sealed class DatabaseFailedException : Exception   // R5.1: names the database, the cause and the way forward
{
    public string DatabaseName { get; }
}
```

Every method here is synchronous and blocks for native work: a `Strict` commit for one fsync (milliseconds), the
others for a memory write (microseconds). This is documented on each method (playbook § 7).

## 2. The commit path

```
Batch.Commit(mode)
  └─ DatabaseContext.CommitBatch(ops, mode)      catalogue gate, SHARED; validates boxes (002 R4)
       └─ RocksInstance.Write(batch, mode)        the one commit point (004 gate test)
            ├─ Strict:   _db.Write(batch, _syncedWrite)      fsync; joins RocksDB's group commit (R3.1)
            └─ else:     _db.Write(batch, _unsyncedWrite);  mark the database "unsynced since last sync"
```

- `RocksInstance` holds two `WriteOptions` for its lifetime (R3.2), as StowDB's `RocksDatabase` does.
- The "unsynced" mark is one `Volatile.Write` of a flag; the interval timer clears it before it syncs, so a commit that
  lands during a sync is caught by the next one.
- The catalogue and registry write through the same point with `Strict` (R2.3); they already pass `sync: true`.

## 3. The interval timer (R4)

One `PeriodicTimer` per store, created from `CrystalOptions.Clock`, started when the store opens in `Interval` mode or
on the first `Interval` commit. Each tick:

```
for each open database slot (enter the slot like any call: a closing database is not entered)
    if the database's unsynced flag is set:
        clear the flag
        NativeRocks.FlushWal(db, sync: true)
        on failure: fail the database (§ 4)
```

- **Closing:** `DatabaseSlot.Close` (drop, dispose) syncs the database if its flag is set, before it closes the
  instance; `CrystalStore.Dispose` stops the timer, then closes every slot, so every database gets its final sync.
- **Ported from:** StowDB `src/Stow.Storage.Crystal/CrystalEngine.cs` `SyncWalEveryIntervalAsync` and
  `SyncAllWalsAsync` (the loop over open databases, the final sync on shutdown). **Changed:** StowDB's loop has no
  error handling, so one failed sync stops interval syncing silently; here a failure fails the database and the
  timer goes on for the others. Its gate becomes `DatabaseSlot.Enter`, so the timer never holds up a drop.
- The timer runs on the thread pool for microseconds per tick plus the syncs themselves. A sync slower than the
  interval delays the next tick; it never overlaps it.

## 4. A failed sync (R5)

`DatabaseContext` already refuses use after a failed catalogue write (`002` T16, `_faulted`). That becomes the
general mechanism:

- `RocksInstance.Write` (in `Strict`) and `FlushWal` failures call `context.Fail(cause)`.
- A failed database refuses every commit with `DatabaseFailedException`, whose message names the database, says
  acknowledged writes since the last good sync may be lost, and gives the way forward: close the store, check the
  disk, open again.
- Reads keep working: the in-memory state is not wrong, only the disk's copy of it is in doubt.
- `007` logs it at `Critical` (event `DatabaseFailed`) and counts it (`crystal.wal.sync.failures`).

A sync is never retried (R5.3). The reason, from PostgreSQL's 2018 "fsyncgate": after a failed `fsync`, Linux may
drop the dirty pages and report the next `fsync` as successful.

## 5. Kill tests (R7)

**Ported from:** Highway `tests/Highway.Storage.CrashHarness/Program.cs` (151 lines) and
`tests/Highway.Server.Tests/Storage/CrashReplayTests.cs` (208 lines), which are `004` T4 and two of `004` T14's ports.
**Changed:** Highway's harness writes queue entries through its own store; Crystal's writes a deterministic stream of
batches through the public API, each touching two boxes (so atomicity across boxes is checked), and takes the mode as
an argument. It lives at `tools/SystemD.Crystal.CrashHarness` (`004` design § 1) and extends the existing
`tests/SystemD.Crystal.CrashHost` rather than duplicating its process plumbing.

```
harness write --dir D --mode Strict|Interval|Buffered [--count N]
  prints "ACK i" after batch i commits; the test kills the process tree at a random ACK
test: reopen D; every batch 0..lastAck present; no batch half-present; bytes equal a reference run cut at the same i
```

The torn-tail test truncates the last WAL file by a few bytes before reopening.

**What this does not prove** (R1.2): power loss. A process kill leaves the OS page cache intact, which is why all
three modes are expected to pass; the difference between them only shows when the machine loses power.

## 6. Test seams

- `RocksInstance.OnBeforeWrite` and `OnBeforeSync` (internal, null in production) let a test throw a sync failure
  (R7.5) without a faulty disk.
- `RocksInstance.SyncCount` (internal) counts WAL syncs Crystal issued, and the RocksDB property
  `rocksdb.wal-file-synced` counts RocksDB's own, for the group-commit test (R7.6).

## 7. What changes elsewhere

- `002` design § 2: `Commit(bool sync = true)` becomes `Commit()` / `Commit(DurabilityMode)`.
- `engineering/box-layout.md` § 6: "Planned" becomes "as built", with the R1.1 table.
- `003` R12.1 reads "with the durability chosen for it"; open question 3 is handed to it.

## 8. Risks

| Risk | Mitigation |
|---|---|
| `Strict` throughput disappoints | It is the disk, not Crystal (R3.1 keeps group commit); the harness shows it per writer count, and products choose `Interval` where they can |
| The timer falls behind under a slow disk | Ticks never overlap; the lag is visible in `crystal.wal.sync.duration` (`007`) |
| `Buffered` misunderstood as durable | The R1.1 table is on the API and in the intro; the default is `Strict` |
| A product mixes modes and expects ordering across them | A later `Strict` commit syncs the WAL, so every earlier commit of that database is durable with it; documented |
