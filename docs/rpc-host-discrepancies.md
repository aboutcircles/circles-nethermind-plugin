# RPC Host Discrepancies and Migration Path

This document outlines the functional differences between the standalone `Circles.Rpc.Host` service and the original Nethermind plugin `Circles.Index.Rpc`, along with a roadmap for restoring full functionality.

## Core Architectural Differences

### Original Architecture (Circles.Index.Rpc)
- **Runs inside Nethermind node** with full access to indexing context
- **In-memory state** via `CirclesV1.LogParser` and `CirclesV2.LogParser`
- **Live blockchain calls** via `IEthRpcModule.eth_call`
- **Type-safe interfaces** using Nethermind types (`Address`, `ResultWrapper<T>`)
- **Direct token balance queries** using ABI encoding and ERC-1155 batch calls

### Current Architecture (Circles.Rpc.Host)
- **Standalone ASP.NET Core web API**
- **PostgreSQL database only** as the single source of truth
- **No blockchain connection** - purely database-driven
- **JSON-RPC neutral** with plain `object` return types
- **Simplified balance calculations** using SQL aggregations

---

## Current Implementation Status

### ✅ Fully Implemented (Database-Only)

These features work without blockchain access or in-memory caches:

1. **Profile Management**
   - `GetProfileByCid` / `GetProfileByCidBatch` ✅
   - `GetProfileByAddress` / `GetProfileByAddressBatch` ✅
   - `SearchProfiles` ✅ (with full-text search)
   - `GetProfileCid` / `GetProfileCidBatch` ✅

2. **Avatar Information**
   - `GetAvatarInfo` / `GetAvatarInfoBatch` ✅
   - Data from `V_CrcV2_Avatars` view

3. **Trust Relations**
   - `GetTrustRelations` ✅ (V1 only)
   - `GetCommonTrust` ✅ (with V2 human detection)

4. **Token Information**
   - `GetTokenInfo` / `GetTokenInfoBatch` ✅

5. **Events**
   - `GetEvents` ✅ (basic filtering)
   - Queries across 13 event tables

6. **Generic Queries**
   - `Query` ✅ (with `SelectDto`)
   - Dynamic table queries with filters, ordering, limits

7. **Pathfinder Proxying**
   - `FindPathV2` ✅ (proxies to external service)
   - `GetNetworkSnapshot` ✅ (proxies to external service)

8. **System**
   - `GetHealth` ✅ (database connectivity check)
   - `GetTables` ✅ (schema introspection)

### ⚠️ Partially Implemented (Limitations)

1. **Token Balances** (`GetTokenBalances`)
   - **Current**: Returns V1 balances only via SQL SUM of transfers
   - **Missing**: V2 balances, demurrage calculations, inflation adjustments
   - **Note**: Returns raw historical balance, not time-adjusted value

2. **Total Balances** (`GetTotalBalanceV1`, `GetTotalBalanceV2`)
   - **Current**: SQL SUM of all transfers
   - **Missing**: Time-based value adjustments (demurrage/inflation)
   - **Note**: V1 balance doesn't account for inflation; V2 doesn't account for demurrage

### ❌ Missing from Original

1. **Advanced Event Filtering**
   - Original had `FilterPredicateDto[]` support
   - Current only supports basic filtering (address, block range, event types)

2. **Batch Avatar Info with V1 Support**
   - Original included V1 signup data
   - Current only queries V2 avatars

3. **Rich Token Balance Details**
   - Original returned `CirclesTokenBalance` with:
     - Multiple value representations (attoCircles, circles, staticCircles, crc)
     - Token metadata (isErc20, isErc1155, isWrapped, isInflationary, isGroup)
     - Owner information
   - Current returns simple `{ token, balance }` for V1 only

---

## Functionality That Cannot Be Fully Replicated (Database-Only)

These features require additional infrastructure:

### 1. Real-Time Token Balance Calculations

**Problem**: True token values are time-dependent:
- **V1 Tokens**: Continuous inflation (CRC grows over time)
- **V2 Demurraged Tokens**: Continuous decay (value decreases)
- **V2 Inflationary Tokens**: Continuous growth (static CRC grows)

**Original Approach**:
- Used `eth_call` to query live balances via `balanceOf` and `balanceOfBatch`
- Applied demurrage calculations using last movement timestamp
- Converted between value representations (attoCircles ↔ staticCircles ↔ CRC)

**Current Limitation**:
- Returns raw sum of historical transfers from database
- Does **not** account for time-based value changes
- Balance will be **stale** and increasingly inaccurate over time

**Solution Path**:
1. **Short-term**: Document limitation clearly in API responses
2. **Medium-term**: Add Redis cache with periodic blockchain sync
3. **Long-term**: Restore `eth_call` capability via separate blockchain connector service

### 2. Live Blockchain State

**Problem**: No access to current blockchain state

**Original Approach**:
- Direct `eth_call` via Nethermind's `IEthRpcModule`
- ABI encoding for complex calls (ERC-1155 `balanceOfBatch`)

**Solution Path**:
- Create a separate **Blockchain Connector Service** that:
  - Connects to Nethermind RPC endpoint
  - Provides `eth_call` proxy
  - Handles ABI encoding/decoding
  - Can be called via HTTP from Rpc.Host

### 3. In-Memory State Performance

**Problem**: Database queries for every lookup vs. instant in-memory access

**Original Approach**:
- `CirclesV1.LogParser.V1Avatars` - instant O(1) lookups
- `CirclesV2.LogParser.V2Avatars` - instant O(1) lookups
- `CirclesV2.LogParser.BalancesByAccountAndToken` - instant balance access
- `CirclesV2.NameRegistry.LogParser` - instant CID/shortname lookups

**Current Performance**:
- Database query per request (slower, but cached by PostgreSQL)
- Profile cache for CIDs (10,000 entry in-memory cache)

**Solution Path**:
- **Redis caching layer** for:
  - Avatar metadata (address → avatar info)
  - Token balances (address+token → balance)
  - Trust relations (address → trusts)
  - Profile CIDs (address → CID)
- **Startup warm-up**: Pre-load critical data from DB into Redis
- **TTL strategy**: Balance data expires quickly, metadata expires slowly

---

## Migration Roadmap

### Phase 1: Database Completeness (Current → Feature Parity)

**Goal**: Implement all features that can work with database access only

#### Tasks:
- [ ] **Advanced Event Filtering**
  - Add `FilterPredicateDto[]` support to `GetEvents`
  - Implement complex filter types (IN, NOT IN, LIKE, etc.)
  - Extend SQL query builder to support predicate composition

- [ ] **V1 Avatar Support**
  - Query `CrcV1_Signup` table in `GetAvatarInfo`
  - Include V1 token address in batch responses
  - Check both `CrcV1_Trust` and V2 trust tables
  - Merge V1 and V2 avatar data in responses

- [ ] **Enhanced Token Balances** (database-only version)
  - Add V2 balance calculation from `CrcV2_TransferSingle`, `CrcV2_TransferBatch`, `CrcV2_Erc20WrapperTransfer`
  - Return rich `CirclesTokenBalance` objects with metadata
  - Query token owner from signup/register tables
  - Detect token type (ERC20, ERC1155, wrapped, inflationary, group)
  - **Note**: Still won't have time-based adjustments

- [ ] **Profile Enrichment**
  - Add short name lookup from `CrcV2_RegisterShortName`
  - Add avatar type inference from `CrcV2_RegisterHuman`/`CrcV2_RegisterGroup`
  - Include V1 CID lookup if `CrcV1_*` name registry tables exist
  - Enrich `GetProfileByAddress` responses with avatar metadata

- [ ] **Type Safety**
  - Create proper DTOs for all responses (see `ICirclesRpcModule.cs`)
  - Implement `ICirclesRpcModule` interface
  - Replace `object` return types with strongly-typed responses
  - Add XML documentation comments for API docs

**Outcome**: Feature-complete API that works entirely from database, with documented limitations on balance accuracy.

---

### Phase 2: Caching Layer (Performance Optimization)

**Goal**: Restore the performance of in-memory state without blockchain connection

#### Prerequisites:
- Redis instance deployed
- Connection pooling configured
- Cache warming strategy defined

#### Tasks:
- [ ] **Redis Integration**
  - Add `StackExchange.Redis` package
  - Configure connection with retry policy
  - Implement cache-aside pattern
  - Add circuit breaker for Redis failures

- [ ] **Avatar Metadata Cache**
  - Cache key: `avatar:{address}` → `AvatarRow` JSON
  - TTL: 1 hour (metadata changes rarely)
  - Warm-up: Load all avatars on startup
  - Fallback: Query database if cache miss

- [ ] **Token Balance Cache**
  - Cache key: `balance:{address}:{token}` → balance value
  - TTL: 5 minutes (note: still historical, not live)
  - Invalidate on: New transfer events (if event stream available)
  - Batch warming for high-value accounts

- [ ] **Trust Relations Cache**
  - Cache key: `trust:{address}` → `CirclesTrustRelations` JSON
  - TTL: 10 minutes
  - Warm-up: Load active users on startup
  - Background refresh for frequently accessed data

- [ ] **Profile Cache Migration**
  - Move from `MemoryCache` to Redis
  - Cache key: `profile:cid:{cid}` → `Profile` JSON
  - TTL: 24 hours (IPFS data is immutable)
  - Distributed cache across multiple instances

- [ ] **Cache Invalidation Strategy**
  - Option A: TTL-based (simple, eventual consistency)
  - Option B: Event-driven (requires database trigger or log subscription)
  - Option C: Hybrid (TTL + manual invalidation endpoints)
  - Implement cache versioning for schema changes

**Outcome**: Sub-millisecond response times for cached data, comparable to original in-memory performance.

---

### Phase 3: Blockchain Connector (Full Functionality Restoration)

**Goal**: Restore live balance calculations and on-chain data access

#### Prerequisites:
- Nethermind RPC endpoint accessible
- Blockchain Connector Service deployed
- ABI definitions for contracts

#### Tasks:
- [ ] **Blockchain Connector Service**
  - Create new microservice: `Circles.Blockchain.Connector`
  - Implement `eth_call` proxy endpoint
  - Add ABI encoding utilities for:
    - `balanceOf(address)` (ERC-20)
    - `balanceOf(address,uint256)` (ERC-1155)
    - `balanceOfBatch(address[],uint256[])` (ERC-1155)
  - Handle batch requests efficiently
  - Add circuit breaker for node failures
  - Implement retry logic with exponential backoff

- [ ] **Balance Calculation Service**
  - Implement demurrage calculation logic (from original)
  - Implement inflation calculation logic (from original)
  - Conversion utilities:
    - `AttoCrcToAttoCircles(value, timestamp)`
    - `AttoCirclesToAttoStaticCircles(value)`
    - `AttoStaticCirclesToAttoCircles(value)`
  - Day-based demurrage with `DAY_ZERO = 1_602_720_000`
  - Unit tests against known test cases

- [ ] **Integration into Rpc.Host**
  - Add HttpClient for Blockchain Connector
  - Modify `GetTokenBalances` to:
    1. Get token exposure from database
    2. Call Blockchain Connector for live balances
    3. Apply time-based calculations
    4. Return full `CirclesTokenBalance` objects
  - Implement V1/V2 balance fetching strategies
  - Add batch optimization for multiple tokens
  - Handle partial failures gracefully

- [ ] **Hybrid Balance Strategy**
  - Database-only mode (current): Fast but stale
  - Live mode (new): Accurate but slower
  - Hybrid mode: Cached with periodic refresh
  - Add `?live=true` query parameter for on-demand live queries
  - Configuration toggle for default mode

**Outcome**: Full feature parity with original implementation, with time-accurate token balances.

---

### Phase 4: Advanced Features (Beyond Original)

**Goal**: Leverage standalone architecture for improvements

#### Tasks:
- [ ] **GraphQL API**
  - Add HotChocolate package
  - Expose same data via GraphQL
  - Allow clients to query only needed fields
  - Implement DataLoader for N+1 query prevention

- [ ] **Webhook Subscriptions**
  - Real-time event notifications
  - Filter by address, event type
  - Webhook delivery with retry
  - Dead letter queue for failed deliveries

- [ ] **Batch Optimization**
  - All `*Batch` methods use parallel DB queries
  - Connection pooling tuned for high concurrency
  - Implement query batching/coalescing

- [ ] **API Versioning**
  - Support both V1 and V2 API contracts
  - Gradual migration path for clients
  - Version negotiation via headers

- [ ] **Metrics & Observability**
  - Prometheus metrics export
  - OpenTelemetry tracing
  - Request/response logging
  - Cache hit/miss rates
  - Database query performance

---

## API Contract Differences

### Return Type Changes

| Original | Current | Notes |
|----------|---------|-------|
| `ResultWrapper<T>` | `object` | Needs strong typing |
| `Address` | `string` | Lowercase hex strings |
| `UInt256` | `string` | Decimal string representation |
| `CirclesTokenBalance[]` | `{ token, balance }[]` | Simplified in current |

### Method Name Mapping

| Original | Current | Status |
|----------|---------|--------|
| `circles_getTotalBalance` | `GetTotalBalanceV1` | ⚠️ Stale balance |
| `circlesV2_getTotalBalance` | `GetTotalBalanceV2` | ⚠️ Stale balance |
| `circles_getTokenBalances` | `GetTokenBalances` | ⚠️ V1 only, stale |
| `circles_getAvatarInfo` | `GetAvatarInfo` | ⚠️ V2 only |
| `circles_getAvatarInfoBatch` | `GetAvatarInfoBatch` | ⚠️ V2 only |
| `circles_getTrustRelations` | `GetTrustRelations` | ✅ |
| `circles_getCommonTrust` | `GetCommonTrust` | ✅ |
| `circles_query` | `Query` | ✅ |
| `circles_events` | `GetEvents` | ⚠️ Missing advanced filters |
| `circles_getProfileCid` | `GetProfileCid` | ✅ |
| `circles_getProfileByCid` | `GetProfileByCid` | ✅ |
| `circles_getProfileByAddress` | `GetProfileByAddress` | ✅ |
| `circlesV2_findPath` | `FindPathV2` | ✅ Proxy only |
| `circles_getNetworkSnapshot` | `GetNetworkSnapshot` | ✅ Proxy only |
| `circles_health` | `GetHealth` | ⚠️ DB only |
| `circles_tables` | `GetTables` | ✅ |

---

## Testing Strategy

### Phase 1: Database Tests
- Unit tests for all SQL queries
- Integration tests against test database
- Verify data consistency with original indexer
- Test edge cases (empty results, null values)

### Phase 2: Cache Tests
- Redis integration tests
- Cache hit/miss scenarios
- TTL expiration tests
- Cache invalidation tests
- Concurrent access tests

### Phase 3: Blockchain Tests
- Mock Nethermind RPC responses
- ABI encoding/decoding correctness
- Demurrage calculation accuracy (compare with known values)
- Performance benchmarks (vs original)
- Circuit breaker behavior

### Phase 4: Load Tests
- Concurrent request handling
- Database connection pool limits
- Redis connection limits
- Response time percentiles (p50, p95, p99)
- Sustained load tests (24h+)

---

## Configuration

### Environment Variables

```bash
# Database
INDEX_READONLY_DB_CONNECTION_STRING="Host=localhost;Database=circles;Username=readonly;Password=***"

# External Services
EXTERNAL_PATHFINDER_URL="http://pathfinder:8080"

# Phase 2: Redis
REDIS_CONNECTION_STRING="localhost:6379,abortConnect=false"
REDIS_CACHE_ENABLED="true"
REDIS_DEFAULT_TTL_SECONDS="300"

# Phase 3: Blockchain Connector
BLOCKCHAIN_CONNECTOR_URL="http://blockchain-connector:8545"
BLOCKCHAIN_CONNECTOR_TIMEOUT_MS="5000"
CIRCLES_V2_HUB_ADDRESS="0x..."
BALANCE_MODE="database|live|hybrid"  # Default: database

# Performance
DB_CONNECTION_POOL_SIZE="100"
HTTP_CLIENT_TIMEOUT_MS="30000"
```

---

## Rollout Strategy

1. **Phase 1**: Deploy database-only version with clear API documentation about limitations
   - Monitor query performance
   - Gather user feedback on missing features
   - Prioritize Phase 2 tasks based on usage

2. **Phase 2**: Add Redis, measure performance improvement (target: <10ms p95)
   - Canary deployment to 10% of traffic
   - Monitor cache hit rates
   - Roll out to 100% if metrics meet targets

3. **Phase 3**: Deploy Blockchain Connector as separate service
   - Gradual rollout with feature flag
   - A/B test database vs. live balance accuracy
   - Monitor cost/latency trade-offs

4. **Phase 4**: Monitor, optimize, add advanced features based on usage patterns
   - GraphQL if clients request flexible queries
   - Webhooks if real-time notifications needed

---

## Documentation Requirements

### API Documentation
- [ ] OpenAPI/Swagger spec
- [ ] Example requests/responses for each endpoint
- [ ] Balance calculation limitations clearly stated
- [ ] Migration guide for clients of original API
- [ ] Changelog documenting all differences

### Operational Documentation
- [ ] Deployment guide (Docker Compose, Kubernetes)
- [ ] Configuration reference
- [ ] Monitoring & alerting setup
- [ ] Troubleshooting guide
- [ ] Disaster recovery procedures

---

## Success Criteria

### Phase 1 Complete When:
- All database-queryable features implemented
- API returns well-structured responses
- Documentation complete with known limitations
- Integration tests passing at 100%

### Phase 2 Complete When:
- p95 response time < 10ms for cached requests
- Cache hit rate > 90% for avatar/profile lookups
- Zero cache-related errors in production for 7 days
- Graceful degradation when Redis unavailable

### Phase 3 Complete When:
- Balance calculations match original to 6 decimal places
- Live balance queries complete in < 500ms (p95)
- Circuit breaker prevents cascade failures
- 99.9% uptime for blockchain connector

### Overall Success When:
- Feature parity with original (with blockchain connector)
- Better performance for read-heavy operations (2x+ improvement)
- Operational independence from Nethermind node
- Improved observability and debugging capabilities
- Zero breaking changes for existing API clients
