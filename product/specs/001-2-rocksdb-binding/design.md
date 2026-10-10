# 001-2 — RocksDB binding: design

## 1. Where it lives

```
src/SystemD.Crystal/Storage/          internal only
  NativeRocks.cs                      every native call (R2)
  RocksInstance.cs                    one database: open with families, close, Get, Write, snapshots
  BoundedIterator.cs                  iterator with mandatory bounds and a step counter (R3.2, R4.4, R6.2)
  ReadOptionsPool.cs                  reused read options (R4.2)
  StoreResources.cs                   shared cache, environment, write-buffer budget (R5)
  ProfileOptions.cs                   General and DeleteHeavy column-family options (R2.3)
  StorageGauges.cs                    property reads (R6)
  OfflineCompaction.cs                compact everything (R7)
  WalAccess.cs                        latest sequence, updates since, tailing with the holds check (R3.4, R4.1)
tests/SystemD.Crystal.Tests/Storage/
  BindingSurfaceTests.cs              R1.3
  NativeRocksTests.cs                 R2.2, R9.5
  BindingTrapTests.cs                 R4
  ProfileTests.cs                     R9.3
  EstimateTests.cs                    R9.4
```

Nothing here is public. The public API (`005`, `002`, `003`) calls these types.

## 2. `NativeRocks`

```csharp
internal static class NativeRocks
{
    public static void CompactOnDeletions(ColumnFamilyOptions options, ulong window, ulong trigger, double ratio);
    public static void MemtableScanFlushTriggers(ColumnFamilyOptions options, uint perOperation, uint average);
    public static ulong[] ApproximateSizes(RocksDb db, ColumnFamilyHandle family, ReadOnlySpan<KeyRange> ranges);
    public static void FlushWal(RocksDb db, bool sync);
    public static IntPtr CreateWriteBufferManager(ulong bytes, Cache cache);      // R2.4: native export, all platforms
    public static void SetWriteBufferManager(DbOptions options, IntPtr manager);
}
```

**Every method follows one pattern:**

```csharp
var native = Native.Instance;
var obj = native.create(...);                 // when it creates something
try
{
    native.call(handle, ..., out var error);
    if (error != IntPtr.Zero) throw RocksException.From(error);   // frees the error string
}
finally
{
    if (obj != IntPtr.Zero) native.destroy(obj);
    GC.KeepAlive(owner);
}
```

**The write-buffer manager (R2.4).** `rocksdb_write_buffer_manager_create` is resolved once from the loaded native
library (`NativeLibrary.GetExport` on the handle the binding loaded) and called through an unmanaged function
pointer. It is exported by every native library the package ships. Its destructor
(`rocksdb_write_buffer_manager_destroy`) is resolved the same way and called when the store closes.

## 3. Profiles

| Option | `General` | `DeleteHeavy` |
|---|---|---|
| compaction on deletions (window, trigger, ratio) | — | 1024, 512, 0.5 |
| memtable scan flush triggers (per operation, average) | — | 1000, 16 |
| everything else | RocksDB defaults | RocksDB defaults |

These values come from the products' measurements, where they took a queue's poll cost from growing with uptime to
flat.

## 4. The traps

| Trap | Rule in the code | Test |
|---|---|---|
| tailing a deleted WAL range crashes | `WalAccess.TryTail(from)` checks the WAL holds `from` before tailing; returns "gone" otherwise | delete WAL files under a held position; tailing returns "gone", the process lives |
| `ReadOptions` finalizer-freed | `ReadOptionsPool` rents per scan; point reads share one | allocations per million point reads stay under a bound |
| ingest does not sync or honour its sequence | batch bytes are written with `Write(batch, sync)` | write, kill the process, reopen: the batch is there |
| one native call per iterator step | `BoundedIterator` requires bounds and a limit, counts steps | a scan of N keys reports N steps |
| WAL compression must be off | `RocksInstance` sets it off explicitly | the option reads back as off |

## 5. Surface test

```csharp
[Fact] public void PinnedVersion_Is_11_8_1_5032()
[Fact] public void EveryManagedMemberCrystalUses_Exists()        // a list, by reflection
[Fact] public void EveryNativeEntryPointCrystalCalls_Exists()   // the Native class's delegates, plus TryGetExport
[Fact] public void WriteBufferManagerExports_ArePresent()          // create and destroy, in the loaded native library
```

The list of managed members and native entry points in the test is the authority for what Crystal depends on. Adding
a call means adding it to the list.

## 6. Upgrade checklist (R8)

A future RocksDB upgrade spec copies this list into its tasks:

1. Bump the pin; run the surface test; list every difference.
2. Write data with the new version and open it with the old, and the reverse.
3. Compare WAL batch bytes; run a mixed-version primary and standby both ways.
4. Full suite, crash harness, replication tests.
5. Record the results and the rollback position in the upgrade spec.
