# Indexer State Machine Flow

This document describes the internal state machine and block processing flow of the Circles Index plugin.

## Block Processing Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           NETHERMIND NODE                                    │
│                                                                              │
│  BlockTree.NewHeadBlock event                                                │
│         │                                                                    │
│         ▼                                                                    │
│  ┌─────────────────────────────────────────┐                                │
│  │ SyncModeSelector.Current                │                                │
│  │  - Full         → Process               │                                │
│  │  - WaitingForBlock → Process            │                                │
│  │  - DbLoad       → Process               │                                │
│  │  - FastSync     → Skip (still syncing)  │                                │
│  │  - StateNodes   → Skip                  │                                │
│  │  - SnapSync     → Skip                  │                                │
│  └─────────────────────────────────────────┘                                │
└─────────────────────────────────────────────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         CIRCLES PLUGIN (Plugin.cs)                           │
│                                                                              │
│  HandleNewHead(blockNo)                                                      │
│         │                                                                    │
│         ├── Set _newItemsArrived = 1                                        │
│         ├── Set _latestHeadToIndex = blockNo                                │
│         │                                                                    │
│         ▼                                                                    │
│  ┌─────────────────────────────────────────┐                                │
│  │ Check: _indexerMachine.CanProcessNewBlocks?                              │
│  │  - true if: WaitForNewBlock, Syncing, NotifySubscribers                  │
│  │  - false if: Error, Initial, Reorg, New                                  │
│  └─────────────────────────────────────────┘                                │
│         │                                                                    │
│         ▼                                                                    │
│  ProcessBlocksAsync() [runs in background Task]                              │
│         │                                                                    │
│         ▼                                                                    │
│  _indexerMachine.HandleEvent(NewHead(blockNo))                              │
└─────────────────────────────────────────────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                     STATE MACHINE (StateMachine.cs)                          │
│                                                                              │
│  See detailed state diagram below                                            │
└─────────────────────────────────────────────────────────────────────────────┘
```

## State Machine Diagram

```
                              ┌─────────┐
                              │   New   │
                              └────┬────┘
                                   │ TransitionTo(Initial) from InitRpcModules
                                   ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              INITIAL STATE                                    │
│                                                                               │
│  1. Check REINDEX_FROM_BLOCK env var (only once per process)                 │
│     └── If set: DeleteAllGreaterOrEqualBlock(reindexFromBlock)               │
│                                                                               │
│  2. Calculate effective resume point:                                         │
│     ├── latestBlock = MAX(blockNumber) from System_Block                     │
│     ├── firstGap = First gap in blockNumber sequence                         │
│     ├── safeResumeBlock = Detect partial writes (events ahead of System_Block)│
│     └── effectiveResumeBlock = MIN(latestBlock, firstGap, safeResumeBlock)   │
│                                                                               │
│  3. Initialize all LogParser caches via InitCaches()                         │
│                                                                               │
│  4. Decision:                                                                 │
│     ├── If effectiveResumeBlock == 0 → WaitForNewBlock (fresh start)         │
│     └── If effectiveResumeBlock > 0  → Reorg(effectiveResumeBlock)           │
└──────────────────────────────────────────────────────────────────────────────┘
                    │
        ┌───────────┴───────────┐
        ▼                       ▼
┌───────────────┐       ┌───────────────────────────────────────────────────────┐
│ WaitForNewBlock│       │                    REORG STATE                        │
└───────┬───────┘       │                                                        │
        │               │  1. DeleteAllGreaterOrEqualBlock(reorgBlock)           │
        │               │     └── DELETE FROM each table WHERE blockNumber >= X  │
        │               │                                                        │
        │               │  2. Clean caches: cache.DeleteAllGreaterOrEqualBlock() │
        │               │     └── If rollback capacity exceeded (>12 blocks):    │
        │               │         → Reinitialize from Initial state              │
        │               │                                                        │
        │               │  3. → WaitForNewBlock                                  │
        │               └───────────────────────────────────────────────────────┘
        │                       │
        │◄──────────────────────┘
        │
        │  NewHead(blockNo) event received
        │
        ▼
┌───────────────────────────────────────────────────────────────────────────────┐
│                         NEWHEAD HANDLING                                      │
│                                                                               │
│  latestBlock = Database.LatestBlock()                                         │
│                                                                               │
│  Decision:                                                                    │
│  ┌────────────────────────────────────────────────────────────────────────┐   │
│  │ if (newHead <= latestBlock)                                            │   │
│  │     → REORG DETECTED! Transition to Reorg(newHead)                     │   │
│  │                                                                        │   │
│  │ else                                                                   │   │
│  │     → Normal forward progress, Transition to Syncing(newHead)          │   │
│  └────────────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────────────┘
        │
        ├─── newHead <= latestBlock ──→ REORG STATE (see above)
        │
        ▼ newHead > latestBlock
┌───────────────────────────────────────────────────────────────────────────────┐
│                           SYNCING STATE                                       │
│                                                                               │
│  1. Create ImportFlow pipeline                                                │
│                                                                               │
│  2. GetBlocksToSync(toBlock):                                                 │
│     ├── Start from: MAX(LatestBlock + 1, Settings.StartBlock)                 │
│     └── Yield blocks from start to toBlock                                    │
│                                                                               │
│  3. ImportFlow.Run() for each block:                                          │
│     ┌─────────────────────────────────────────────────────────────────────┐   │
│     │  Block → BlockWithReceipts → Parse Logs → Sink.AddEvent()           │   │
│     │                                                                     │   │
│     │  For each LogParser:                                                │   │
│     │    1. ParseLog() - extract events from each log entry               │   │
│     │    2. ParseTransaction() - aggregate events (e.g., TransferSummary) │   │
│     │                                                                     │   │
│     │  Sink batches events, flushes when buffer full                      │   │
│     └─────────────────────────────────────────────────────────────────────┘   │
│                                                                               │
│  4. Sink.Flush() - write remaining events                                     │
│  5. ImportFlow.FlushBlocks() - write to System_Block                          │
│                                                                               │
│  → NotifySubscribers(importedBlockRange)                                      │
└───────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────────────┐
│                        NOTIFY SUBSCRIBERS STATE                               │
│                                                                               │
│  If block range span <= 1000:                                                 │
│    → pg_notify('circles_index_events', {fromBlock, toBlock, timestamp})       │
│                                                                               │
│  → WaitForNewBlock                                                            │
└───────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
   WaitForNewBlock (loop continues)
```

## Error State Flow

```
┌───────────────────────────────────────────────────────────────────────────────┐
│                            ERROR STATE                                        │
│                                                                               │
│  Entered when: Any exception during state handling                            │
│                                                                               │
│  1. Check error type:                                                         │
│     ├── Transient (BlockNotAvailable, ReceiptsNotAvailable):                  │
│     │   └── Clean caches via Reorg(LatestBlock+1), retry                      │
│     │                                                                         │
│     └── Other errors:                                                         │
│         └── Full reinitialization via Initial state                           │
│                                                                               │
│  2. Exponential backoff: delay = errorCount² × 1000ms (max 60s)               │
│                                                                               │
│  3. Max 10 consecutive errors → permanent Error state (manual restart needed) │
└───────────────────────────────────────────────────────────────────────────────┘
```

## Common Scenarios

### Fresh Start (Empty Database)

1. Plugin.Init() creates StateMachine
2. InitRpcModules() → TransitionTo(Initial)
3. Initial state: LatestBlock = 0 → WaitForNewBlock (skip Reorg)
4. NewHeadBlock event fires
5. Syncing from Settings.StartBlock to chain head
6. Live sync loop: WaitForNewBlock → Syncing → NotifySubscribers → WaitForNewBlock

### Normal Restart (Database Has Data)

1. Initial state: LatestBlock = 20,000,000
2. → Reorg(20,000,000) to clean any partial data
3. DELETE WHERE blockNumber >= 20,000,000 (deletes nothing if clean)
4. → WaitForNewBlock
5. NewHeadBlock(20,000,005) arrives
6. Syncing blocks 20,000,001 to 20,000,005

### Chain Reorganization

1. Database at block 20,000,100
2. Chain reorgs: NewHeadBlock(20,000,095)
3. WaitForNewBlock receives NewHead(20,000,095)
4. Check: 20,000,095 <= 20,000,100 → **REORG DETECTED**
5. Reorg(20,000,095): DELETE FROM all tables WHERE blockNumber >= 20,000,095
6. Cache rollback (if within 12 blocks)
7. → WaitForNewBlock
8. Chain advances: NewHeadBlock(20,000,096)
9. Syncing(20,000,096) - reindex the reorganized blocks

### Interrupted Sync (Crash Recovery)

1. Crash during Syncing at block 20,000,050
   - Events flushed to tables up to block 20,000,050
   - System_Block only has up to 20,000,048
2. Restart → Initial state detects inconsistency
3. Reorg(20,000,048): DELETE WHERE blockNumber >= 20,000,048
4. Resume syncing from 20,000,049

## SyncMode Reference

| SyncMode          | Description                         | Plugin Behavior |
| ----------------- | ----------------------------------- | --------------- |
| `Full`            | Fully synced, processing new blocks | Process         |
| `WaitingForBlock` | Synced, waiting for next block      | Process         |
| `DbLoad`          | Loading from database               | Process         |
| `FastSync`        | Fast syncing headers                | Skip            |
| `StateNodes`      | Syncing state trie                  | Skip            |
| `SnapSync`        | Snapshot sync                       | Skip            |

## Related Files

- [src/Index/Circles.Index/Plugin.cs](../src/Index/Circles.Index/Plugin.cs) - Plugin entry point
- [src/Index/Circles.Index/StateMachine.cs](../src/Index/Circles.Index/StateMachine.cs) - State machine implementation
- [src/Index/Circles.Index.Common/Settings.cs](../src/Index/Circles.Index.Common/Settings.cs) - Configuration including REINDEX_FROM_BLOCK
