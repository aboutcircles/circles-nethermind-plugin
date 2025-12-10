# Caching Strategy: GetTransactionHistoryEnriched

## Problem Statement

The `circles_getTransactionHistoryEnriched` method is slow:
- **Staging (local)**: 2190-2356ms
- **Production (remote)**: 424-1388ms

This makes staging **416% slower** than production for some test cases.

## Current Implementation Analysis

### Location
[CirclesRpcModule.cs:3477-3684](../src/Rpc/Circles.Rpc.Host/CirclesRpcModule.cs#L3477-L3684)

### What It Does

1. **Main Query** (lines 3493-3542): UNION of transfers + trust events
   - Queries `V_Crc_Transfers` for CrcV2 transfers where address is sender OR receiver
   - Queries `CrcV2_Trust` for trust events where address is truster OR trustee
   - Supports cursor-based pagination
   - Orders by blockNumber DESC, transactionIndex DESC, logIndex DESC

2. **Extract Involved Addresses** (lines 3578-3591):
   - Collects all `from`, `to`, `truster`, `trustee` addresses from results

3. **Batch Enrichment** (lines 3593-3616):
   - `GetAvatarInfoBatchInternal()` - fetches avatar info for all addresses
   - `GetProfileByAddressBatch()` - fetches IPFS profiles for all addresses
   - These run in **parallel** via `Task.WhenAll`

4. **Enrichment Assembly** (lines 3618-3661):
   - Combines events with avatar/profile info per participant

### Performance Bottlenecks

| Component | Estimated Time | Notes |
|-----------|---------------|-------|
| Main UNION query | ~500-1000ms | Complex UNION with multiple conditions |
| `GetAvatarInfoBatchInternal` | ~200-500ms | DB query or cache lookup |
| `GetProfileByAddressBatch` | ~500-1500ms | IPFS fetches, slowest component |
| JSON processing | ~50-100ms | Serialization/deserialization |

### Why Production is Faster

Production does NOT use the cache service for this method (it's a new method). The difference may be:
1. Different DB performance characteristics
2. Network latency to IPFS gateway
3. Database connection pooling differences

## Caching Strategy

### Option A: Full Response Caching (NOT Recommended)

Cache the entire response for address+fromBlock+toBlock+limit combinations.

**Pros:**
- Simplest to implement
- Fastest subsequent lookups

**Cons:**
- Cache invalidation nightmare (new blocks constantly arrive)
- Huge cache size (transaction history is large)
- Cursor pagination makes cache keys complex

### Option B: Component-Level Caching (Recommended)

Cache the individual components that are reused across queries.

#### Components to Cache

1. **Avatar Info** - ALREADY CACHED in `CacheContainer.V2Avatars`
2. **Profile Info** - ALREADY CACHED in `CacheContainer.AvatarCids` + IPFS fetch
3. **Transfer Events** - NOT cached, query per-request
4. **Trust Events** - NOT cached, query per-request

#### Current Cache Usage

The method does NOT use the Cache Service at all! It directly queries the database.

```csharp
// Current: Direct DB connection
await using var connection = await CreateConnectionAsync();
```

### Option C: Hybrid Approach (Best Solution)

1. Use Cache Service for avatar/profile lookups (already available)
2. Optimize the main SQL query with better indexing
3. Add a short-lived query result cache (e.g., 30 seconds) for identical requests

## Implementation Plan

### Phase 1: Use Existing Cache Service (Quick Win)

**Current**: `GetAvatarInfoBatchInternal()` and `GetProfileByAddressBatch()` hit the database directly.

**Fix**: Route through Cache Service when available.

```csharp
// In GetTransactionHistoryEnriched:
if (_settings.UseCacheService && _cacheServiceClient != null)
{
    // Use cache service for batch avatar lookup
    var avatars = await _cacheServiceClient.GetAvatarInfoBatchAsync(addressArray);
    // Use cache service for batch profile lookup
    var profiles = await _cacheServiceClient.GetProfileBatchAsync(addressArray);
}
```

**Estimated Impact**: -500ms to -1000ms

### Phase 2: Add Query Result Caching (Medium Effort)

Add a short-lived cache for the main transaction query results.

```csharp
// Cache key: $"txHistory:{address}:{fromBlock}:{toBlock}:{cursor}"
// TTL: 30-60 seconds (balance freshness vs performance)
```

**Files to Modify:**
- `CacheContainer.cs` - Add new cache dictionary
- `CacheServiceClient.cs` - Add new endpoint
- `TransactionHistoryController.cs` - New controller in Cache Service

**Estimated Impact**: -1000ms for repeat queries

### Phase 3: Optimize SQL Query (Requires Analysis)

The UNION query may benefit from:
1. Materialized view for recent transactions
2. Composite indexes on `(from, blockNumber)` and `(to, blockNumber)`
3. Partitioning by blockNumber range

**Requires**: Query plan analysis with EXPLAIN ANALYZE

## Task Checklist

### Phase 1: Cache Service Integration
- [ ] Check if `GetAvatarInfoBatchInternal` already uses cache service
- [ ] Check if `GetProfileByAddressBatch` already uses cache service
- [ ] If not, add cache service integration for both methods
- [ ] Measure performance improvement

### Phase 2: Query Result Caching
- [ ] Design cache key schema for transaction history
- [ ] Add `TransactionHistoryCache` to `CacheContainer`
- [ ] Implement TTL-based eviction (30-60 seconds)
- [ ] Add API endpoint in Cache Service
- [ ] Update RPC to use cache when available
- [ ] Measure performance improvement

### Phase 3: SQL Optimization
- [ ] Run EXPLAIN ANALYZE on current query
- [ ] Check existing indexes on `V_Crc_Transfers` and `CrcV2_Trust`
- [ ] Create/optimize indexes if needed
- [ ] Consider materialized view for hot paths
- [ ] Measure performance improvement

## Quick Investigation Commands

```bash
# Check existing indexes
ssh indexer-staging2 "docker exec postgres-gnosis psql -U postgres -c \"
  SELECT indexname, indexdef
  FROM pg_indexes
  WHERE tablename IN ('CrcV2_Transfer', 'CrcV2_Trust', 'V_Crc_Transfers')
  ORDER BY tablename, indexname;
\""

# Query plan analysis
ssh indexer-staging2 "docker exec postgres-gnosis psql -U postgres -c \"
  EXPLAIN ANALYZE
  SELECT e.* FROM \\\"V_Crc_Transfers\\\" e
  WHERE e.version = 2
    AND (e.\\\"from\\\" = '0x42cedde51198d1773590311e2a340dc06b24cb37'
      OR e.\\\"to\\\" = '0x42cedde51198d1773590311e2a340dc06b24cb37')
    AND e.\\\"blockNumber\\\" >= 0
  ORDER BY e.\\\"blockNumber\\\" DESC
  LIMIT 20;
\""
```

## Dependencies

- Cache Service must be running
- Cache warmup must include avatar/profile data
- Network connectivity to IPFS gateway for profiles

## Success Metrics

| Metric | Current | Target |
|--------|---------|--------|
| p50 latency | ~2200ms | <500ms |
| p99 latency | ~2500ms | <1000ms |
| Production parity | 416% slower | Within 20% |

## Related Files

- [CirclesRpcModule.cs](../src/Rpc/Circles.Rpc.Host/CirclesRpcModule.cs) - Main implementation
- [CacheContainer.cs](../src/Cache/Circles.Cache.Service/CacheContainer.cs) - Cache definitions
- [CacheServiceClient.cs](../src/Rpc/Circles.Rpc.Host/CacheServiceClient.cs) - Cache service client
- [CacheWarmupService.cs](../src/Cache/Circles.Cache.Service/Services/CacheWarmupService.cs) - Cache warmup
