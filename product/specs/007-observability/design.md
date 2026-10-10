# 007 — Observability: design

## 1. Where it lives

```
src/SystemD.Crystal/
  CrystalLogEvents.cs            public: the EventId table (R3.1)
  StoreStatus.cs                 public: the snapshot records returned by CrystalStore.Status() (R4)
  Observability/
    CrystalMetrics.cs            internal: the Meter and every instrument (R1, R2)
    CrystalLog.cs                internal: source-generated LoggerMessage methods (R3.2)
    StallWatch.cs                internal: the write-stall transition on the store timer (R5)
  Storage/StorageGauges.cs       existing (001-2); read by the observable gauges
```

`CrystalOptions` gains `MeterFactory` (`IMeterFactory?`), `TagMetricsWithDatabase` (bool) and
`SlowCommitThreshold` (`TimeSpan?`).

## 2. Instruments

One `CrystalMetrics` per store, created at open, disposed with the store:

```csharp
internal sealed class CrystalMetrics : IDisposable
{
    public CrystalMetrics(IMeterFactory? factory, Func<IReadOnlyList<DatabaseSlot>> openDatabases, bool tagDatabase);

    public void Commit(DurabilityMode mode, string outcome, int operations, long startTimestamp);
    public void WalSync(string reason, long startTimestamp, bool failed);
    public void LockWait(long startTimestamp);
    public void LockAll();
    public void Scan(int steps);
}
```

- Counters and histograms are fields; tags are `KeyValuePair<string, object?>` structs built from a fixed set of
  string constants, so recording allocates nothing (R1.4).
- Durations: the caller takes `TimeProvider.GetTimestamp()` before the work and passes it in; `CrystalMetrics` calls
  `GetElapsedTime` (R1.5).
- Observable gauges call `openDatabases()` when collected, enter each slot like any call (a closing database refuses
  and is skipped, R2.7), and read `StorageGauges` for its families.
- `BoxLocks` records a wait only when `SemaphoreSlim.Wait(0)` failed first, so an uncontended lock costs one extra
  non-blocking try and no clock read (R2.3).

**Ported from:** Highway `src/Highway.Server/Observability/HighwayMetrics.cs` (227 lines): the meter-per-component
pattern, the meter-name override for tests, the bounded-tag rule, and its two storage gauges. StowDB
`src/Stow.Server/Observability/StowMeter.cs` (117 lines) and `EngineMetricsPoller.cs`: the RocksDB property set and
the write-stall transition logging. **Changed:** Highway and StowDB each had a static meter and a poller; Crystal reads
gauges when they are collected (no background poller except the stall check) and takes `IMeterFactory`.

## 3. Log events

```csharp
public static class CrystalLogEvents
{
    public static readonly EventId StoreOpened      = new(1001, nameof(StoreOpened));
    public static readonly EventId StoreClosed      = new(1002, nameof(StoreClosed));
    public static readonly EventId FormatRefused    = new(1101, nameof(FormatRefused));
    public static readonly EventId CatalogueDamaged = new(1102, nameof(CatalogueDamaged));
    public static readonly EventId RecoveryRemovedFolder = new(1201, ...);
    // 1300s databases, 1400s boxes, 1500s durability (006), 1600s storage (stalls), 2000s replication (003)
}
```

Ranges per area, so `003` and later specs add events without renumbering. The existing nine `ILogger` calls in
`StoreEngine` and `DatabaseSlot` move onto `CrystalLog` methods with these ids.

## 4. Status

```csharp
public sealed record StoreStatus(
    string NodeId, string Format, DurabilityMode Durability, TimeSpan SyncInterval,
    IReadOnlyList<DatabaseStatus> Databases);            // 003 adds: ReplicationStatus? Replication

public sealed record DatabaseStatus(
    uint Id, string Name, int Boxes, bool Failed, string? FailedCause, DateTimeOffset? LastWalSync,
    StorageStatus Storage);

public sealed record StorageStatus(long MemtableDeletes, long EstimatedKeys, long LiveDataSize,
    long PendingCompactionBytes, long SstFiles, long SstSize, long BlockCacheUsage, bool WriteStopped);
```

Built under no lock except each slot's entry scope; a database closing during `Status()` is left out.

## 5. Testing

- A `MetricsCapture` test helper over `MeterListener` (open question 3), scoped to one store's meter through a test
  `IMeterFactory`, so parallel tests do not see each other's measurements.
- A `CapturingLogger` already fits `ILoggerFactory`; tests assert event id, level and the absence of key bytes.
- The R6.4 architecture rule: proving by IL that no key or value *flows* into a log call or a tag is too costly, so
  the rule checks the shape instead. No `CrystalLog` method and no `CrystalMetrics` method has a `byte[]`,
  `ReadOnlySpan<byte>` or `KeyValue` parameter, so a key or value cannot be handed to one without the build failing.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Metrics slow the commit path | No allocation, one branch when nobody listens; measured with and without a listener (R6.5) |
| A gauge callback races a drop | Gauges enter slots like any call and skip closing databases (R2.7) |
| Cardinality explosion | No box tags; database tags opt-in and bounded by `MaxOpenDatabases` |
| Event ids drift | One public table, ids never reused, tested |
