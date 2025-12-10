# V1 Transfer Summary Aggregation

## Status: IMPLEMENTED

Implementation completed on 2025-12-10.

## Overview

V1 `LogParser.ParseTransaction` now aggregates transfer events similar to V2's `TransferSummaryAggregator`. Multi-hop transfers (via `HubTransfer` events) show only the net flow between actual sender and receiver, filtering out intermediate hops.

## V1 vs V2 Key Differences

| Aspect | V1 | V2 |
|--------|----|----|
| Transfer event | `Transfer` (ERC20) | `TransferSingle`, `TransferBatch` (ERC1155) |
| Hub transfer marker | `HubTransfer` | `StreamCompleted` |
| Scope markers | None | `FlowEdgesScopeSingleStarted`, `FlowEdgesScopeLastEnded` |
| Token values | Single representation | Inflationary vs Demurraged |

## V1 Transfer Flow

In V1, a multi-hop transfer emits:
1. Multiple `Transfer` events (one per hop through different token contracts)
2. One `HubTransfer` event per actual user-to-user transfer intent

**Example**: Alice sends to Carol via Bob:
- `Transfer` (Alice→Bob, Alice's token)
- `Transfer` (Bob→Carol, Bob's token)
- `HubTransfer` (Alice→Carol, total amount)

The `HubTransfer` already represents the net transfer. The individual `Transfer` events are the implementation details (the hops).

## Implementation

### Files Created/Modified

1. **New**: `src/Index/Circles.Index.CirclesV1/TransferSummaryAggregatorV1.cs`
   - Contains `TransferSummaryAggregatorV1` static class
   - Uses DFS to trace which `Transfer` events belong to each `HubTransfer`
   - Returns `AggregationResultV1` with hub summaries and standalone transfers

2. **Modified**: `src/Index/Circles.Index.CirclesV1/LogParser.cs`
   - `ParseTransaction` now uses the aggregator
   - Emits `TransferSummary` events with synthetic negative log indices
   - JSON serializes the underlying transfer edges

### Key Types

```csharp
// Key for tracking transfers between address pairs
public record TransferKey(string From, string To);

// Net transfer total between two addresses
public record TransferTotal(TransferKey Key, BigInteger Value, ImmutableHashSet<string> Tokens, int Transfers);

// Aggregation result
public record AggregationResultV1(
    List<(HubTransfer Hub, HashSet<Transfer> Edges)> HubTransferSummaries,
    List<Transfer> StandaloneTransfers,
    List<Transfer> AllTransferEvents
);
```

### Algorithm

1. **Separate events**: Extract `HubTransfer` and `Transfer` events from the transaction
2. **Build adjacency**: Create a graph of transfers keyed by `from` address
3. **Trace paths**: For each `HubTransfer`, use DFS to find all `Transfer` events on paths from `hub.From` to `hub.To`
4. **Mark used edges**: Track which transfers are consumed by hub transfers
5. **Emit summaries**:
   - Each `HubTransfer` becomes a `TransferSummary` with its traced edges as JSON
   - Unconsumed `Transfer` events become standalone `TransferSummary` events

### Edge Cases Handled

- **No HubTransfers**: All transfers become standalone summaries
- **Multiple HubTransfers**: Each gets its own summary with its traced edges
- **Overlapping paths**: An edge can belong to multiple HubTransfers
- **Empty events**: Returns immediately without producing summaries

### Log Index Convention

Uses synthetic negative indices like V2:
```csharp
int syntheticLogIndex = -(totalSummaries);
```

## Testing

Build verified with:
```bash
dotnet build src/Index/Circles.Index.CirclesV1/
```

Integration testing requires:
1. Running indexer against Gnosis chain with V1 HubTransfer transactions
2. Verifying `TransferSummary` records in PostgreSQL `CrcV1.TransferSummary` table
3. Checking that the `events` JSON field contains the underlying transfers

## References

- V2 implementation: `src/Index/Circles.Index.CirclesV2/TransferSummaryAggregator.cs`
- V1 aggregator: `src/Index/Circles.Index.CirclesV1/TransferSummaryAggregatorV1.cs`
- V1 event types: `src/Index/Circles.Index.CirclesV1/Events.cs`
- Database schema: `src/Index/Circles.Index.CirclesV1/DatabaseSchema.cs`
