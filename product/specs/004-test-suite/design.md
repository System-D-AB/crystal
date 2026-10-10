# 004 — Test suite: design

## 1. Layout

```
tests/
  SystemD.Crystal.Tests/
    Encoding/          key encoders, KeyWriter
    Boxes/             catalogue, lifecycle, the reference model, the write-in-a-dropped-box rules
    Locks/             striped locks, LockMany, LockAll, interleavings
    Batches/           read-your-writes, empty batches, snapshots, bounded scans
    Store/             the store, its lock, the registry, database lifecycle, startup recovery, reset (005)
    Format/            stamps and refusals
    Replication/       tail, trim, apply, slots, feeder, catch-up, epochs, rejoin, formats
    Profiles/          delete-heavy churn, tombstone flushing (later spec)
    Support/           TempStore, FakeClock, PoolHog, ReferenceModel, CostProbe
  SystemD.Crystal.Architecture.Tests/
  SystemD.Crystal.Process.Tests/
    Support/           TestNode (out-of-process), CrashHarness runner
    Replication/       two-node pairs, failover, rejoin, gaps
    Crash/             hard kill mid-stream, replay
tools/
  SystemD.Crystal.CrashHarness/   the program the crash tests kill
```

## 2. The inventory: what Crystal inherits from Highway

From Highway's `tests/Highway.Server.Tests/Storage/` and `tests/Highway.Integration.Tests/`. **Port** means: rewrite
against Crystal's API, keep the scenario and the assertion.

### 2.1 Encoding → `002-box-layout`

| Highway test | Port? | Crystal scenario |
|---|---|---|
| `KeyEncodingTests.Score_RoundTrips` | yes | signed 64-bit round trip |
| `KeyEncodingTests.Score_ByteOrder_EqualsNumericOrder` | yes | byte order equals numeric order |
| `KeyEncodingTests.Score_NegativesSortBelowPositives` | yes | sign flip |
| `KeyEncodingTests.Sequence_IsScoreEncoding` | adapt | unsigned and signed encoders agree on non-negatives |
| `KeyEncodingTests.String_Encodes_NonEmpty`, `String_ByteOrder_EqualsOrdinalOrder` | yes | strings, ordinal order |
| `KeyEncodingTests.Compound_IsSelfDelimiting` | yes | `("ab","c")` ≠ `("a","bc")` |
| `KeyEncodingTests.Compound_EmbeddedNull_DoesNotCollideWithTerminator` | yes | `0x00` escaping |
| `KeyEncodingTests.Compound_PrefixName_DoesNotAliasSibling` | yes | a name is never a prefix-alias of another |
| `KeyEncodingTests.KeyWriter_GrowsBeyondInitialBuffer`, `KeyWriter_Growth_PreservesContent` | yes | `KeyWriter` growth |

### 2.2 Boxes, catalogue, boundaries → `002-box-layout` and `001`

| Highway test | Port? | Crystal scenario |
|---|---|---|
| `BoxLayoutTests.Keys_OrderNumerically_AndStayInsideTheirBoxAndSpace` | yes | box ranges never overlap; keys stay in their box |
| `BoxLayoutTests.Catalog_Allocates_Finds_Lists_Retires_AndNeverReusesAnId` | yes | create, find, list, drop; ids never reused; plus re-create of a dropped name |
| `BoxLayoutTests.ALookupStartedBeforeARetirement_DoesNotCacheTheRetiredId` | adapt | a handle from before a drop is refused at commit, never redirected |
| `BoxLayoutTests.AnEpochReset_InvalidatesLookupsInFlight` | adapt | a standby's catalogue reload after applying catalogue changes |
| `BoxLayoutTests.WorkBox_MatchesAReferenceModel_UnderRandomOperations` | **pattern only** | the reference-model framework (R2.8), applied to boxes, not queues |
| `GateG1Tests.Seam_HasNoEngineTypeOnItsSurface` | yes | no RocksDB type on a public signature (`001`) |
| `GateG1Tests.StoreLayer_CommitsOnlyThroughDbWrite_SinglePoint` | yes | one commit path into RocksDB |
| `GateG1Tests.StoreLayer_ReadsNoClock` | yes | the storage layer reads no clock |
| `GateG1Tests.RocksDbNativeLibrary_IsPresentForHostRid` | yes | the native library is present (`001` smoke test) |
| `GateG1Tests.ThereIsExactlyOneCommitMethodOnTheSeam` | adapt | one commit method on the batch |
| `StoreContractTests` (Kv, Increment, List, … families) | **mostly no** | those are Highway's legacy key families. **Port only** the batch contract: last write wins, delete idempotent, a batch sees its own writes, nothing visible before commit |

### 2.3 Locks → `002-box-layout`

| Highway test | Port? | Crystal scenario |
|---|---|---|
| `StripedLockAllTests.LockAll_WaitsForAHeldStripe_AndThenExcludesEveryName` | yes | `LockAll` waits, then excludes |
| `StripedLockAllTests.LockAll_TakesStripesInTheOrderLockManyDoes` | yes | the deterministic ordering test (fails under a descending `LockAll`) |
| `StripedLockAllTests.LockAll_AndLockMany_NeverDeadlock` | yes | stress, no deadlock |

### 2.4 Format → `002-box-layout` and `003-replication`

| Highway test | Port? | Crystal scenario |
|---|---|---|
| `StorageFormatTests.A2xDataDirectory_IsRefused_WithTheUpgradePath` | adapt | a foreign or older format is refused with the way forward |
| `StorageFormatTests.AFormat3Stamp_IsRefused_WithTheSameGuidance` | adapt | a stamp of another version is refused the same way |
| `StorageFormatTests.WipeData_EmptiesTheDirectory_KeepingTheReplicationIdentity` | yes | `Reset` keeps `node.id` and `node.epoch` |
| `StorageFormatTests.WipeData_RefusesWhileABrokerHoldsTheDirectory` | yes | `Reset` refuses a store that is open |
| `StorageFormatTests.ABlankReplica_RefusesASnapshotThatDoesNotStateItsFormat` | yes | `003` R8.2 |
| `StorageFormatTests.AReplica_RefusesAPrimaryThatDoesNotEchoItsFormat` | yes | `003` R8.2 |
| `DurableByDefaultTests.ADataDirectoryFromAnOlderBuild_IsRefusedBeforeRecovery` | yes | refusal before RocksDB opens |
| `DurableByDefaultTests.AMismatchedStorageFormat_IsRefusedNamingWhatItFound` | yes | the message names what was found |
| `DurableByDefaultTests.AFreshDataDirectory_IsStampedSoTheNextBuildCanCheckIt` | yes | a new store is stamped |
| `DurableByDefaultTests.AnUnusableDataDirectory_ThrowsAtBuild_NamingBothWaysOut` | yes | an unusable directory fails at open, naming the ways out |

### 2.5 Replication → `003-replication`

| Highway test | Port? | Crystal scenario |
|---|---|---|
| `ReplicationTailTests` (all 8) | yes, per database | contiguous reads, holes healing, byte caps, eviction to misses, capture only after a pull, identical replicas from tail, WAL or both under concurrent commits, group-commit trim, **pull cost flat with WAL size** |
| `ReplicationBatchApplierTests` (all 6) | yes | primary bytes on the standby; one synced write per page; non-contiguous refused; re-pull idempotent; lower epoch refused; crash after apply still skips |
| `ReplicationApplyAtomicityTests` (all 3) | yes | the derived position: a re-pulled page never double-applies, across a crash |
| `ReplicationFeederTests.Pull_AfterSyncPuts_ReturnsWalPagesInOrder`, `Pull_MaxBytes_SplitsIntoPages_AlwaysMakesProgress` | yes | paging |
| `ReplicationFeederTests.Hello_ThenAck_MinWatermarkIsTheFloor` | yes | slots and the minimum acknowledged position |
| `ReplicationFeederTests.EnforceCap_DropsADeadReplicaWithANamedEvent` | yes | lag cap and the drop event |
| `ReplicationFeederTests.Epoch_PersistsAcrossReopen_SoResurrectionFencingSurvivesARestart` | yes | `node.epoch` persistence |
| `ReplicationFeederTests.HigherEpoch_OnAReplica_AdoptsWithoutDemoting` | yes | adoption on a standby |
| `ReplicationFeederTests.Pull_BehindTheRetainedWal_RefusesWithAGap_NeverAGappedStream` | yes | gap refusal |
| `ReplicationFeederTests.Promote_IncrementsEpoch_PriorityZeroRefuses` | **split** | promote increments the epoch (yes); priority (Highway policy, no) |
| `SlotAgingTests` (all 5) | yes | demotion clears slots; silent slots age to dropped; recent stay active; status refreshes staleness; zero disables |
| `ReplicaCatchUpTests` (all 3) | yes | far-behind catch-up through one held reader; after a primary restart; epoch change drops held readers |
| `ReplicationToolkitSurfaceTests` (all 3) | yes | the pinned RocksDB binding; its replication members exist; put, pull, ingest round trip |
| `AutoRejoinOnDemotionTests.MarkerRoundTrips` | yes | the rejoin marker |
| `AutoRejoinOnDemotionTests.ReplicaAdoptingAHigherEpoch_WritesNoMarker` | yes | adoption writes no marker |
| `AutoRejoinOnDemotionTests` (the 3 others) | **no** | when to schedule a rejoin is Highway's policy |
| `ReplicationPairTests.TwoNode_PullApplies_Promote_ResurrectionDemotes_ZeroLoss` | yes, process test | two nodes, promote, the old primary returns and is demoted, nothing lost |
| `ReplicationPairTests.RawWireWrites_AreRefusedOnAReplica` | yes | writes refused on a standby (`003` R1.5) |

### 2.6 Durability and churn → later specs

| Highway test | Port? | Crystal scenario |
|---|---|---|
| `CrashReplayTests.HardKillMidStream_EveryAckedCommitSurvives` | yes, with the harness | every acknowledged synced commit survives a kill |
| `CrashReplayTests.RecoveredState_IsByteIdenticalToReferenceRun` | yes | recovery is byte-identical to a reference run |
| `DurableByDefaultTests.AllThreeVerbs_SurviveARestart`, `ARecoveredMessage_StillCarriesItsPayload` | adapt | boxes and values survive a restart intact |
| `DurabilityTests.Restart_*` | adapt | the same, with several databases |
| `InFlightChurnTests.Compact_DropsTombstones_AndKeepsTheData` | yes | compaction removes tombstones and keeps data |
| `InFlightChurnTests.ScanOverManyMemtableTombstones_FlushesTheMemtable` | yes | the `DeleteHeavy` profile's flush trigger |
| `InFlightChurnTests.StorageGauges_ReportMemtableDeletes` | yes | storage metrics |
| `InFlightChurnTests.Compact_RefusesADirectoryThatIsNotABrokerDatabase` | adapt | maintenance refuses a directory that is not a Crystal store |

### 2.7 Infrastructure

| Highway | Crystal |
|---|---|
| `tests/Highway.Storage.CrashHarness` | `tools/SystemD.Crystal.CrashHarness` (R2.3) |
| the opt-in pool hog (`HW_TEST_POOL_HOG`) | `CRYSTAL_TEST_POOL_HOG` (R2.4) |
| the lock's stripe-waiting and stripe-releasing hooks | the same hooks (R2.5) |
| the fake clock in the feeder tests | `TimeProvider` everywhere (R2.6) |
| the poll-cost probe | cost probes (R2.7) |
| the temporary-directory helpers in the toolkit tests | `TempStore` (R2.1) |
| the reference model in `BoxLayoutTests` | the reference-model framework (R2.8) |

## 3. Not inherited: Highway's own

These test Highway's failover policy or its broker, and stay in Highway:

| Highway test | Why it stays |
|---|---|
| `HerdNoSplitTests`, `Integration/HerdCohesionTests` | the client herd and peer checks: failover policy |
| `LeadershipVisibilityTests`, `RosterSelfRegisterOnPromoteTests` | leadership reporting and the roster: policy |
| `ReplicationFeederTests.Willingness_*`, `Deadman_*`, `Validation_*`, `RosterRecord_*` | willingness, the deadman fence backstop, the roster: policy |
| `ReplicationPairTests.Client_*`, `ClientHandshake_*`, `HerdArrival_*`, `Goodbye_*`, `JoinAnnounce_*`, `IsolatedPrimary_FenceBackstopStillFences` | client routing, hand-off, membership: policy |
| `AutoRejoinOnDemotionTests.Demotion_*`, `AutoRejoinDisabled_*` | when to rejoin: policy |
| `CacheStoreTests`, `CacheEpochHookTests`, `CacheReplicationIsolationTests` | Highway's broker cache |
| `DeadLetterStorageTests`, `RetryPolicyWorkBoxTests`, `HighwayKeyspaceTests`, `HealthReadinessTests` | queue semantics and broker endpoints |
| `InFlightChurnTests` (ack, idle poll, dead-letter peek, head hint, sweep throttle) | queue behaviour |
| `StoreContractTests` (Kv, Increment, List, sorted-set families) | Highway's legacy key families |
| `Integration/LeaseRecoveryTests`, `RenewalPastCapIntegrationTests` | leases |

## 4. How a port is done

1. Read the Highway test: its scenario, its assertion, and what bug it was written for.
2. Write the Crystal test against Crystal's API, with the same scenario and assertion.
3. Add `// Ported from Highway: <ClassName.TestName>`.
4. Run it. Then break the guarded rule on purpose, see it fail, restore it, and rebuild.
5. Tick the port task in the owning spec, noting the mutation used.
