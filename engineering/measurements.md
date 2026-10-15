# Crystal measurements

> Appended by the benchmark harness (spec 008). Every section states the conditions it ran under; earlier sections are never rewritten.

## 2026-10-09 22:46 UTC · `quick`

**Conditions:** Release · Microsoft Windows 10.0.26200 · unknown (X64), 22 logical · 31,4 GB · data on C:\ (NTFS) · .NET 10.0.7 · Crystal 0.1.0-preview.1 · RocksDB 11.8.1.5032

### commit — 100 B values, 1-op batches, 2 s cells × 3

| Durability | Writers | Batch | Value | Commits/s (median) | Range | p50 µs | p99 µs | max µs |
|---|---|---|---|---|---|---|---|---|
| Strict | 1 | 1 op | 100 B | 910 | 906–922 | 1 047,7 | 1 879,6 | 4 444,8 |
| Strict | 4 | 1 op | 100 B | 2 088 | 2 068–2 104 | 2 187,8 | 3 288,5 | 32 077,4 |
| Strict | 16 | 1 op | 100 B | 6 938 | 6 874–6 972 | 2 409,8 | 3 099,3 | 3 883,4 |
| Strict | 64 | 1 op | 100 B | 24 204 | 23 700–24 534 | 2 662,1 | 3 317,7 | 6 156,1 |
| Interval | 1 | 1 op | 100 B | 190 516 | 175 804–195 167 | 3,8 | 10,2 | 18 287,9 |
| Interval | 4 | 1 op | 100 B | 279 978 | 259 136–286 735 | 10,0 | 70,0 | 27 035,9 |
| Interval | 16 | 1 op | 100 B | 178 528 | 174 282–193 084 | 72,8 | 179,1 | 18 528,0 |
| Interval | 64 | 1 op | 100 B | 322 586 | 319 901–324 891 | 177,7 | 821,7 | 19 428,0 |
| Buffered | 1 | 1 op | 100 B | 215 444 | 209 417–229 546 | 3,9 | 11,4 | 3 007,6 |
| Buffered | 4 | 1 op | 100 B | 301 938 | 295 088–320 768 | 10,1 | 75,1 | 11 000,9 |
| Buffered | 16 | 1 op | 100 B | 203 682 | 201 601–206 594 | 76,1 | 166,5 | 1 441,0 |
| Buffered | 64 | 1 op | 100 B | 337 558 | 326 060–339 598 | 180,6 | 463,3 | 17 079,5 |

verified 40 000 keys in 4 box(es)

```json
[{"workload":"commit","configuration":"batch=1 value=100","durability":"Strict","writers":1,"opsPerSecMedian":910.5,"opsPerSecMin":905.5,"opsPerSecMax":922.5,"p50Us":1047.7,"p99Us":1879.6,"maxUs":4444.8,"operations":1821},{"workload":"commit","configuration":"batch=1 value=100","durability":"Strict","writers":4,"opsPerSecMedian":2088.5,"opsPerSecMin":2068,"opsPerSecMax":2103.5,"p50Us":2187.8,"p99Us":3288.5,"maxUs":32077.4,"operations":4177},{"workload":"commit","configuration":"batch=1 value=100","durability":"Strict","writers":16,"opsPerSecMedian":6938,"opsPerSecMin":6874,"opsPerSecMax":6972.5,"p50Us":2409.8,"p99Us":3099.3,"maxUs":3883.4,"operations":13876},{"workload":"commit","configuration":"batch=1 value=100","durability":"Strict","writers":64,"opsPerSecMedian":24203.5,"opsPerSecMin":23700.5,"opsPerSecMax":24533.5,"p50Us":2662.1,"p99Us":3317.7,"maxUs":6156.1,"operations":48407},{"workload":"commit","configuration":"batch=1 value=100","durability":"Interval","writers":1,"opsPerSecMedian":190515.5,"opsPerSecMin":175803.5,"opsPerSecMax":195167,"p50Us":3.8,"p99Us":10.2,"maxUs":18287.9,"operations":381031},{"workload":"commit","configuration":"batch=1 value=100","durability":"Interval","writers":4,"opsPerSecMedian":279977.5,"opsPerSecMin":259135.5,"opsPerSecMax":286735,"p50Us":10,"p99Us":70,"maxUs":27035.9,"operations":559955},{"workload":"commit","configuration":"batch=1 value=100","durability":"Interval","writers":16,"opsPerSecMedian":178527.5,"opsPerSecMin":174281.5,"opsPerSecMax":193083.5,"p50Us":72.8,"p99Us":179.1,"maxUs":18528,"operations":357055},{"workload":"commit","configuration":"batch=1 value=100","durability":"Interval","writers":64,"opsPerSecMedian":322585.5,"opsPerSecMin":319901,"opsPerSecMax":324891,"p50Us":177.7,"p99Us":821.7,"maxUs":19428,"operations":645171},{"workload":"commit","configuration":"batch=1 value=100","durability":"Buffered","writers":1,"opsPerSecMedian":215443.5,"opsPerSecMin":209417,"opsPerSecMax":229545.5,"p50Us":3.9,"p99Us":11.4,"maxUs":3007.6,"operations":430887},{"workload":"commit","configuration":"batch=1 value=100","durability":"Buffered","writers":4,"opsPerSecMedian":301938,"opsPerSecMin":295087.5,"opsPerSecMax":320767.5,"p50Us":10.1,"p99Us":75.1,"maxUs":11000.9,"operations":603876},{"workload":"commit","configuration":"batch=1 value=100","durability":"Buffered","writers":16,"opsPerSecMedian":203682.5,"opsPerSecMin":201601,"opsPerSecMax":206593.5,"p50Us":76.1,"p99Us":166.5,"maxUs":1441,"operations":407365},{"workload":"commit","configuration":"batch=1 value=100","durability":"Buffered","writers":64,"opsPerSecMedian":337557.5,"opsPerSecMin":326060.5,"opsPerSecMax":339597.5,"p50Us":180.6,"p99Us":463.3,"maxUs":17079.5,"operations":675115}]
```

### metrics-cost — commit with and without a MeterListener

| Durability | Writers | No listener/s | Listener/s | Δ throughput | p99 µs (none) | p99 µs (listener) | alloc B/commit (none) | alloc B/commit (listener) |
|---|---|---|---|---|---|---|---|---|
| Buffered | 1 | 225 368 | 213 993 | 5,0% | 10,3 | 12,4 | 1 640 | 1 640 |
| Buffered | 16 | 142 813 | 137 236 | 3,9% | 245,1 | 616,3 | 1 527 | 1 472 |
| Strict | 1 | 855 | 828 | 3,1% | 2 104,6 | 2 018,5 | 1 472 | 1 472 |
| Strict | 16 | 6 454 | 6 522 | -1,1% | 5 375,9 | 3 858,7 | 1 472 | 1 472 |

metrics-cost compares throughput, latency and allocation with and without a listener

```json
[{"workload":"metrics-cost","configuration":"no-listener Buffered","durability":"Buffered","writers":1,"opsPerSecMedian":225368.5,"opsPerSecMin":216893.5,"opsPerSecMax":228519,"p50Us":3.8,"p99Us":10.3,"maxUs":2157.2,"operations":450737},{"workload":"metrics-cost","configuration":"listener Buffered","durability":"Buffered","writers":1,"opsPerSecMedian":213993,"opsPerSecMin":208609.5,"opsPerSecMax":223583.5,"p50Us":4.1,"p99Us":12.4,"maxUs":682.8,"operations":427986},{"workload":"metrics-cost","configuration":"no-listener Buffered","durability":"Buffered","writers":16,"opsPerSecMedian":142813,"opsPerSecMin":130692,"opsPerSecMax":153991,"p50Us":108.4,"p99Us":245.1,"maxUs":1544.8,"operations":285626},{"workload":"metrics-cost","configuration":"listener Buffered","durability":"Buffered","writers":16,"opsPerSecMedian":137235.5,"opsPerSecMin":136335,"opsPerSecMax":155826.5,"p50Us":100.8,"p99Us":616.3,"maxUs":8488.4,"operations":274471},{"workload":"metrics-cost","configuration":"no-listener Strict","durability":"Strict","writers":1,"opsPerSecMedian":855,"opsPerSecMin":815,"opsPerSecMax":870,"p50Us":1098.7,"p99Us":2104.6,"maxUs":11393.6,"operations":1710},{"workload":"metrics-cost","configuration":"listener Strict","durability":"Strict","writers":1,"opsPerSecMedian":828.5,"opsPerSecMin":789.5,"opsPerSecMax":862,"p50Us":1110,"p99Us":2018.5,"maxUs":59488.2,"operations":1657},{"workload":"metrics-cost","configuration":"no-listener Strict","durability":"Strict","writers":16,"opsPerSecMedian":6453.5,"opsPerSecMin":6438,"opsPerSecMax":6501.5,"p50Us":2430.3,"p99Us":5375.9,"maxUs":31011,"operations":12907},{"workload":"metrics-cost","configuration":"listener Strict","durability":"Strict","writers":16,"opsPerSecMedian":6522,"opsPerSecMin":6490,"opsPerSecMax":6725.5,"p50Us":2429.7,"p99Us":3858.7,"maxUs":13099.1,"operations":13044}]
```

### boxes — per-call cost by live-box count (µs, median of ≥100 calls)

| Live boxes | Create | Find | Rename | Swap | Drop | List |
|---|---|---|---|---|---|---|
| 10 | 1 113,3 | 0,100 | 0,000 | 1 048,0 | 1 045,0 | 6,0 |
| 1 000 | 1 047,6 | 0,400 | 1 047,0 | 1 051,8 | 1 062,1 | 50,9 |

### boxes — rename and drop by box size (µs) — alias-only, so size-independent

| Keys in box | Rename | Drop |
|---|---|---|
| 0 | 1 087,1 | 1 023,9 |
| 10 000 | 1 724,2 | 1 230,0 |

verified the catalogue (List equals the model) at 10, 1 000 boxes; filled 10 000 keys

```json
[{"workload":"boxes","liveBoxes":10,"createUs":1113.3,"findUs":0.1,"renameUs":0,"swapUs":1048,"dropUs":1045,"listUs":6},{"workload":"boxes","liveBoxes":1000,"createUs":1047.6,"findUs":0.4,"renameUs":1047,"swapUs":1051.8,"dropUs":1062.1,"listUs":50.9},{"workload":"boxes","keysInBox":0,"renameUs":1087.1,"dropUs":1023.9},{"workload":"boxes","keysInBox":10000,"renameUs":1724.2,"dropUs":1230}]
```

### churn — 100 000 ops, head scan of 10 keys every 100 ops

| Progress | General ops/s | General steps/scan | DeleteHeavy ops/s | DeleteHeavy steps/scan |
|---|---|---|---|---|
| 10% | 83 340 | 10,0 | 115 796 | 10,0 |
| 20% | 96 810 | 10,0 | 106 500 | 10,0 |
| 30% | 97 238 | 10,0 | 105 596 | 10,0 |
| 40% | 100 657 | 10,0 | 107 209 | 10,0 |
| 50% | 109 483 | 10,0 | 94 258 | 10,0 |
| 60% | 107 460 | 10,0 | 101 823 | 10,0 |
| 70% | 101 822 | 10,0 | 105 600 | 10,0 |
| 80% | 97 203 | 10,0 | 102 138 | 10,0 |
| 90% | 103 285 | 10,0 | 102 470 | 10,0 |
| 100% | 105 408 | 10,0 | 101 691 | 10,0 |

verified both boxes hold exactly the model's keys after 100 000 ops (General and DeleteHeavy)

```json
[{"workload":"churn","percent":10,"generalOpsPerSec":83339.7227,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":115795.6901,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":20,"generalOpsPerSec":96810.107,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":106499.7886,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":30,"generalOpsPerSec":97237.5777,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":105596.3979,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":40,"generalOpsPerSec":100656.8868,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":107208.7139,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":50,"generalOpsPerSec":109482.8577,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":94257.7251,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":60,"generalOpsPerSec":107459.9792,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":101822.7287,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":70,"generalOpsPerSec":101822.0029,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":105599.6317,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":80,"generalOpsPerSec":97203.4566,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":102137.8473,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":90,"generalOpsPerSec":103284.552,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":102469.5153,"deleteHeavyStepsPerScan":10},{"workload":"churn","percent":100,"generalOpsPerSec":105408.1774,"generalStepsPerScan":10,"deleteHeavyOpsPerSec":101691.02,"deleteHeavyStepsPerScan":10}]
```

### soak — Buffered, 1 kB values, 30 s

| Progress | Writes/s | SST files | SST size | Pending compaction | Memtable deletes | Stall |
|---|---|---|---|---|---|---|
| 10% | 143 077 | 6 | 377,5 MB | 0 B | 0 | no |
| 20% | 142 020 | 12 | 755 MB | 0 B | 0 | no |
| 30% | 144 209 | 19 | 1,2 GB | 0 B | 0 | no |
| 40% | 146 231 | 26 | 1,6 GB | 0 B | 0 | no |
| 50% | 145 259 | 34 | 2 GB | 0 B | 0 | no |
| 60% | 147 939 | 41 | 2,4 GB | 0 B | 0 | no |
| 70% | 141 696 | 48 | 2,9 GB | 0 B | 0 | no |
| 80% | 147 184 | 54 | 3,2 GB | 0 B | 0 | no |
| 90% | 147 527 | 61 | 3,6 GB | 0 B | 0 | no |
| 100% | 145 997 | 68 | 4,1 GB | 0 B | 0 | no |

verified 4 353 888 keys and a sample of 1 000 values

```json
[{"workload":"soak","percent":10,"writesPerSec":143076.8065,"sstFiles":6,"sstSizeBytes":395857404,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":20,"writesPerSec":142020.4431,"sstFiles":12,"sstSizeBytes":791711194,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":30,"writesPerSec":144208.9535,"sstFiles":19,"sstSizeBytes":1253542965,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":40,"writesPerSec":146231.0926,"sstFiles":26,"sstSizeBytes":1715376419,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":50,"writesPerSec":145258.9029,"sstFiles":34,"sstSizeBytes":2137556007,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":60,"writesPerSec":147938.832,"sstFiles":41,"sstSizeBytes":2599391173,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":70,"writesPerSec":141695.5642,"sstFiles":48,"sstSizeBytes":3061221191,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":80,"writesPerSec":147183.82,"sstFiles":54,"sstSizeBytes":3457079208,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":90,"writesPerSec":147527.0649,"sstFiles":61,"sstSizeBytes":3918910991,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"},{"workload":"soak","percent":100,"writesPerSec":145997.1797,"sstFiles":68,"sstSizeBytes":4380743465,"pendingCompactionBytes":0,"memtableDeletes":0,"stall":"no"}]
```

## 2026-10-10 12:20 UTC · `replication --size quick`

**Conditions:** Release · Microsoft Windows 10.0.26200 · unknown (X64), 22 logical · 31,4 GB · data on C:\ (NTFS) · .NET 10.0.7 · Crystal 0.1.0-preview.1 · RocksDB 11.8.1.5032

### replication — replication over a socket — 50 databases, 20 000-batch catch-up

| phase | result |
|---|---|
| bootstrap 50 dbs (blank standby) | 6 600 ms |
| catch-up of 20 000 batches | 26 458 ms (1 322,9 µs/batch) |
| idle 1 s (caught up) | stable |

standby byte-identical in count across 50 databases after a 20 000-batch catch-up

```json
[{"workload":"replication","phase":"bootstrap","databases":50,"ms":6599.9241},{"workload":"replication","phase":"catchup","batches":20000,"catchUpMs":26458.4835,"perBatchUs":1322.9242},{"workload":"replication","phase":"idle","stable":1}]
```

## 2026-10-10 15:32 UTC · `replication --size quick`

**Conditions:** Release · Microsoft Windows 10.0.26200 · unknown (X64), 22 logical · 31,4 GB · data on C:\ (NTFS) · .NET 10.0.7 · Crystal 0.1.0-preview.1 · RocksDB 11.8.1.5032

### replication — replication over a socket — 50 databases, 20 000-batch catch-up

| phase | result |
|---|---|
| bootstrap 50 dbs (blank standby) | 10 378 ms |
| catch-up of 20 000 batches | 829 ms (41,4 µs/batch) |
| idle 1 s (caught up) | stable |

standby byte-identical in count across 50 databases after a 20 000-batch catch-up

```json
[{"workload":"replication","phase":"bootstrap","databases":50,"ms":10378.3087},{"workload":"replication","phase":"catchup","batches":20000,"catchUpMs":828.5986,"perBatchUs":41.4299},{"workload":"replication","phase":"idle","stable":1}]
```


## 2026-10-16 · spec 014 · Highway's load harness

**Conditions:** Release · Microsoft Windows 10.0.26200 · 22 logical · 31,4 GB · .NET 10 · RocksDB 11.8.1.5032 · one
laptop. Highway's harness (`internal/features/076-highway-on-crystal/crystal/profilerun.ps1` in Highway's repository):
the published Highway host as a primary and a standby on the same machine and disk, 70 s of steady load,
`dotnet-counters` on the primary for 30 s past warm-up; durability Strict. Crystal packed locally for each row, except
rc.1 published. Two runs a side unless stated. The harness's offered load is 5 880 RPC calls; this day it did not
saturate (spec 014's table, recorded another day, did).

| Crystal on the primary | Commit p50 | Commit p95 | RPC p50 | RPC p99 | RPC handled / failed | Queue e2e p50 |
|---|---:|---:|---:|---:|---:|---:|
| 1.0.0-rc.1, published (×2) | 3.52–3.54 ms | 3.82–3.85 | 3.71–3.72 ms | 8.59–8.74 | 5 880 / 0 | 67.5–68.7 ms |
| rc.1 source, packed locally (×1) | 3.55 | 3.79 | 3.72 | 8.83 | 5 880 / 0 | 65.7 |
| rc.1, per-commit replication hook removed (×1) | 2.20 | 2.84 | 2.42 | 7.64 | 5 880 / 0 | 22.9 |
| 014 with a 2 ms gather window (×2) | 3.54–3.55 | 3.81–3.82 | 3.72–3.73 | 8.28–8.29 | 5 880 / 0 | 66.2–66.6 |
| **014 as built, 10 ms window (×2)** | **2.18–2.22** | 2.84–2.93 | **2.40–2.45** | 6.21–6.81 | 5 880 / 0 | **22.1–23.4** |
| 014 as built, no standby attached (×1) | 2.17 | 2.71 | 2.47 | 6.87 | 5 880 / 0 | 23.4 |
| 014, `DeleteHeavy` scan-flush triggers off (×2) | 2.07–2.15 | 2.73–2.93 | 2.35–2.51 | 6.29–6.77 | 5 880 / 0 | 24.2–26.2 |
| Highway 3.0 on its own store, reference (×1) | — | — | 3.38 | 8.03 | 5 880 / 0 | 48.1 |

**Pages and the WAL (R4.1).** On rc.1 the primary served 15 854 database pages for 16 070 Strict commits in 30 s, every
one from the tail: one page per commit, no page from the WAL, none missed for a batch still landing. Built, it served
1 837–1 863 pages in 30 s (the 10 ms window rounds up to Windows' 15.6 ms timer). R4.2 was not needed.

**What cost the time.** Not the pulls themselves: each page costs the standby a synced write, and on one disk a page
per commit doubled the fsyncs under the primary's commits. In-process (a primary and a standby store, one writer, 10 s),
Strict commit p50 was 1.04 ms with no standby, 1.84 ms with one at gather 0 or 2 ms, and 1.05 ms at 5 ms or wider;
with eight writers, 2.31, 3.67 and 2.28 ms. A 2 ms window is shorter than the standby's own cycle (reply, synced
apply, next pull), so it gathered nothing.

**Lag (R3.1, not met as defined).** `crystal.replication.lag_time`, read once a second by `dotnet-counters`: median
14–15 ms (R3.1: at most 10 ms), p99 16 and 46 ms, max 46 ms. Those reads tick on the same 15.6 ms timer as the window, so they land at the end of a window, when
lag is at its largest. Sampled at irregular moments by a spinning thread in the in-process pair (one writer, 10 ms
window, about 5 000 samples): median 9.3 ms, p99 21.7 ms, mean 9.5 ms; with no window, median 2.0 ms.

**Mixed versions (R8.1).** A 014 primary with an rc.1 standby, and the reverse, under the same load: the pair stayed
healthy throughout and ended with the standby acknowledging the primary's latest sequence, no slot dropped.
