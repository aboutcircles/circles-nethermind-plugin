# Circles Cache Service

A high-performance in-memory caching service for Circles protocol data that provides fast, read-optimized access to balances, avatars, and profile information with real-time updates via PostgreSQL LISTEN/NOTIFY.

## Overview

The Cache Service maintains rollback-safe in-memory caches of frequently accessed Circles data, enabling sub-millisecond query responses for balance and avatar lookups. It synchronizes with the database indexer in real-time and handles blockchain reorganizations automatically.

Key features:

- **Fast Balance Queries** - O(1) lookups for token balances (V1 and V2)
- **Avatar & Profile Caching** - Instant access to avatar info and IPFS CIDs
- **Real-time Updates** - Listens to PostgreSQL notifications for new blocks
- **Hash-based Reorg Detection** - Uses block hash comparison for accurate reorganization detection
- **Rollback Safety** - Automatic cache rollback on chain reorganizations
- **Gap Processing** - Handles blocks that arrive during warmup phase
- **REST API** - Simple HTTP endpoints for all cached data
- **Prometheus Metrics** - Built-in observability and monitoring

## Architecture

### Core Components

```
┌─────────────────────────────────────────────────────────────────┐
│                    Circles Cache Service                        │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐           │
│  │   Warmup     │→ │  Listener    │→ │    REST      │           │
│  │   Service    │  │   Service    │  │     API      │           │
│  └──────────────┘  └──────────────┘  └──────────────┘           │
│         ↓                  ↓                   ↓                │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              RollbackCache (In-Memory)                   │   │
│  │   - V1/V2 Balances    - Avatars    - Profile CIDs        │   │
│  │  ┌─────────────────────────────────────────────────────┐ │   │
│  │  │            BlockRingBuffer                          │ │   │
│  │  │   - Recent block tracking    - Reorg detection      │ │   │
│  │  └─────────────────────────────────────────────────────┘ │   │
│  └──────────────────────────────────────────────────────────┘   │
│         ↑                                                       │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │           PostgreSQL (LISTEN/NOTIFY + Queries)           │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

#### 1. CacheWarmupService (`Services/CacheWarmupService.cs`)

Initial cache population from the database:

- **Loads All Caches:** Avatars, balances, token owners, profile CIDs, groups, etc.
- **Batch Processing:** Efficiently loads data in chunks to reduce memory pressure
- **Progress Tracking:** Reports warmup progress and completion status
- **Secondary Indexes:** Builds optimized indexes for O(1) balance lookups by address
- **Block Ring Buffer:** Initializes recent block history for reorg detection
- **Gap Processing:** Handles blocks that arrive during warmup phase

#### 2. NotificationListenerService (`Services/NotificationListenerService.cs`)

Real-time cache updates via PostgreSQL LISTEN/NOTIFY:

- **Notification Pings:** Treats notifications as signals to check for new blocks
- **Block Querying:** Queries recent blocks directly from database on each ping
- **Hash-based Reorg Detection:** Uses BlockRingBuffer to detect reorganizations by comparing block hashes
- **Incremental Updates:** Processes only new blocks since last processed block
- **Event Processing:** Processes V1/V2 transfers, registrations, trusts, etc.

#### 3. BlockRingBuffer (`BlockRingBuffer.cs`)

Thread-safe ring buffer for tracking recent blocks and detecting reorganizations:

- **Block History:** Maintains last N blocks with numbers and hashes
- **Reorg Detection:** Compares block hashes to detect chain reorganizations
- **Rollback Points:** Identifies exact block where reorg occurred
- **Capacity Management:** Automatically trims old blocks to stay within capacity
- **Thread Safety:** All operations are thread-safe with proper locking

#### 4. RollbackCache (`Circles.Index.Common/RollbackCache.cs`)

Thread-safe cache with rollback capabilities:

- **Versioned Entries:** Each entry tracks the block number it was added/modified
- **Rollback Support:** Can revert to any previous block within capacity window
- **O(1) Operations:** Fast lookups, adds, and removals
- **Configurable Capacity:** Maintains history of last N blocks (default: 12)

#### 5. CacheContainer (`Caches/CacheContainer.cs`)

Container managing all cache instances:

- **V1 Caches:** Avatars, token owners, balances, profile CIDs
- **V2 Caches:** Avatars, balances, profile CIDs, short names, groups, memberships
- **Secondary Indexes:** Address-to-token mappings for efficient balance queries
- **Statistics:** Provides cache size and usage metrics

#### 6. IpfsContentCache (`IpfsContentCache.cs`)

LRU cache for IPFS profile content (JSON payloads from `ipfs_files` table):

- **Immutable Content:** IPFS content is content-addressed, so no invalidation needed
- **LRU Eviction:** Automatically evicts least-recently-used entries when full
- **Configurable Size:** Default 50,000 entries (`IPFS_CACHE_MAX_ENTRIES`)
- **Cache Statistics:** Tracks hits, misses, and entry count
- **JSON-LD Cleanup:** Automatically strips `@context`, `namespaces`, `signingKeys` fields

#### 7. REST API Controllers

- **BalancesController** - Token balance queries (individual and aggregate)
- **AvatarsController** - Avatar information and metadata
- **ProfilesController** - Profile IPFS CID lookups and content retrieval

## Quick Start

### Running Locally

```bash
# Set required environment variables
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres"
export POSTGRES_READONLY_CONNECTION_STRING="${POSTGRES_CONNECTION_STRING}"

# Run the service
cd src/Cache/Circles.Cache.Service
dotnet run

# Or use script (if available)
./scripts/run-cache-service.sh
```

Default URL: `http://localhost:3001`

### Configuration

Set environment variables before starting:

```bash
# Required
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres"

# Optional - defaults to main connection if not set
export POSTGRES_READONLY_CONNECTION_STRING="${POSTGRES_CONNECTION_STRING}"

# Optional - PostgreSQL NOTIFY channel name (default: circles_index_events)
export CIRCLES_PG_NOTIFY_CHANNEL="circles_index_events"

# Optional - rollback capacity in blocks (default: 12)
export ROLLBACK_CAPACITY=12

# Optional - max lag before service is "not ready" (default: 10)
export MAX_CATCHUP_LAG=10

# Optional - HTTP port (default: 3001)
export PORT=3001

# Optional - max IPFS profile content cache entries (default: 50000)
export IPFS_CACHE_MAX_ENTRIES=50000
```

## REST API

### Balance Endpoints

#### Get Token Balances

Get all token balances for an address.

```bash
GET /api/balances/{address}

# Example
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7
```

**Response:**

```json
[
  {
    "tokenId": "0x1234...",
    "balance": "100.5",
    "tokenOwner": "0x5678...",
    "version": 1,
    "lastProcessedBlock": 31234567,
    "timestamp": 1638360000
  }
]
```

#### Get Total Balance (V1 Only)

```bash
GET /api/balances/{address}/total/v1

# Example
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7/total/v1
```

**Response:**

```json
{
  "totalBalance": "1234.56",
  "lastProcessedBlock": 31234567,
  "timestamp": 1638360000
}
```

#### Get Total Balance (V2 Only)

```bash
GET /api/balances/{address}/total/v2
```

#### Get Total Balance (All Versions)

```bash
GET /api/balances/{address}/total

# Example
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7/total
```

### Avatar Endpoints

#### Get Avatar Info

Get avatar information for an address.

```bash
GET /api/avatars/{address}

# Example
curl http://localhost:3001/api/avatars/0xde374ece6fa50e781e81aac78e811b33d16912c7
```

**Response:**

```json
{
  "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
  "version": 2,
  "type": "CrcV2_RegisterHuman",
  "cidV0": "QmX...",
  "isHuman": true,
  "name": null,
  "symbol": null,
  "registeredAt": 1638360000,
  "lastProcessedBlock": 31234567,
  "timestamp": 1638360000
}
```

#### Get Avatar Info Batch

Get avatar information for multiple addresses.

```bash
POST /api/avatars/batch
Content-Type: application/json

{
  "addresses": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "0x1234567890123456789012345678901234567890"
  ]
}

# Example
curl -X POST http://localhost:3001/api/avatars/batch \
  -H 'Content-Type: application/json' \
  -d '{"addresses": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"]}'
```

**Response:** Array of `AvatarInfoResponse` (null for non-existent avatars)

**Limits:** Max 100 addresses per request

### Profile Endpoints

#### Get Profile CID

Get IPFS CID for an avatar's profile.

```bash
GET /api/profiles/{address}/cid

# Example
curl http://localhost:3001/api/profiles/0xde374ece6fa50e781e81aac78e811b33d16912c7/cid
```

**Response:**

```json
{
  "cid": "QmX...",
  "lastProcessedBlock": 31234567,
  "timestamp": 1638360000
}
```

#### Get Profile CID Batch

Get profile CIDs for multiple addresses.

```bash
POST /api/profiles/cid/batch
Content-Type: application/json

{
  "addresses": ["0xaddr1...", "0xaddr2..."]
}
```

**Limits:** Max 100 addresses per request

#### Get Profile Content

Get IPFS profile content (JSON payload) by CID. The content is cached in an LRU cache (default 50k entries) for fast repeated lookups.

```bash
GET /api/profiles/content/{cid}

# Example
curl http://localhost:3001/api/profiles/content/QmX...
```

**Response:**

```json
{
  "cid": "QmX...",
  "content": "{\"name\": \"Alice\", \"description\": \"...\"}",
  "lastProcessedBlock": 31234567,
  "timestamp": 1638360000
}
```

**Notes:**
- Content is returned as a JSON string (the raw IPFS payload)
- JSON-LD fields (`@context`, `namespaces`, `signingKeys`) are automatically stripped
- Returns `null` content if CID is not found in the `ipfs_files` table

#### Get Profile Content Batch

Get profile content for multiple CIDs.

```bash
POST /api/profiles/content/batch
Content-Type: application/json

{
  "cids": ["QmX...", "QmY..."]
}
```

**Limits:** Max 100 CIDs per request

### System Endpoints

#### Health Check

```bash
GET /live

# Example
curl http://localhost:3001/live
```

Returns `200 OK` if service is running.

#### Readiness Check

```bash
GET /ready

# Example
curl http://localhost:3001/ready
```

Returns `200 OK` if service is ready to handle requests. Checks:

- Warmup complete
- Listener connected
- Cache lag within acceptable limits (< MAX_CATCHUP_LAG blocks)

**Response:**

```json
{
  "status": "ready",
  "lastProcessedBlock": 31234567,
  "dbHead": 31234570,
  "lag": 3,
  "warmupComplete": true,
  "listenerConnected": true
}
```

Returns `503 Service Unavailable` if not ready.

#### Cache Statistics

```bash
GET /cache/stats

# Example
curl http://localhost:3001/cache/stats
```

**Response:**

```json
{
  "v1_avatars": 12345,
  "v1_token_owners": 12345,
  "v1_avatar_cids": 8901,
  "v2_avatars": 23456,
  "erc20_wrappers": 567,
  "groups": 89,
  "group_memberships": 1234,
  "v2_avatar_cids": 15678,
  "v2_avatar_short_names": 14567,
  "v1_balances": 345678,
  "v2_balances": 456789,
  "last_token_movements": 234567,
  "total_entries": 1125901,
  "v1_indexed_addresses": 8901,
  "v2_indexed_addresses": 15678,
  "lastProcessedBlock": 31234567,
  "warmupComplete": true,
  "listenerConnected": true,
  "ipfs_cache_entries": 25000,
  "ipfs_cache_hits": 150000,
  "ipfs_cache_misses": 5000
}
```

#### Prometheus Metrics

```bash
GET /metrics

# Example
curl http://localhost:3001/metrics
```

Returns Prometheus-formatted metrics for monitoring.

#### Service Info

```bash
GET /

# Example
curl http://localhost:3001/
```

Returns service information and available endpoints.

## How It Works

### 1. Startup Sequence

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Load Settings from Environment                          │
│ 2. Initialize CacheContainer (create all RollbackCaches)   │
│ 3. Start CacheWarmupService                                │
│    ├─ Set warmup target block (current DB head)           │
│    ├─ Load V1 Avatars & Token Owners                       │
│    ├─ Load V1 Balances from database views                 │
│    ├─ Load V2 Avatars & Groups                             │
│    ├─ Load V2 Balances from database views                 │
│    ├─ Load Profile CIDs and Short Names                    │
│    ├─ Build Secondary Indexes (address → token mappings)   │
│    ├─ Initialize BlockRingBuffer with recent blocks       │
│    ├─ Process any blocks that arrived during warmup       │
│    └─ Mark warmup complete                                 │
│ 4. Start NotificationListenerService                       │
│    ├─ Wait for warmup to complete                          │
│    ├─ Connect to PostgreSQL LISTEN channel                 │
│    └─ Begin processing notification pings                  │
│ 5. Start MetricsUpdateService                              │
│ 6. Service Ready (returns 200 on /ready)                   │
└─────────────────────────────────────────────────────────────┘
```

### 2. Real-time Cache Updates

```
┌─────────────────────────────────────────────────────────────┐
│ PostgreSQL Index Plugin (Nethermind)                       │
│         ↓                                                   │
│ INSERT new block events into database                      │
│         ↓                                                   │
│ NOTIFY circles_index_events (ping signal)                 │
│         ↓                                                   │
│ NotificationListenerService receives ping                 │
│         ↓                                                   │
│ Query recent blocks from System_Block table               │
│         ↓                                                   │
│ BlockRingBuffer.UpdateFromBlocks()                         │
│   ├─ Compare block hashes for reorg detection             │
│   ├─ If reorg detected: return rollback point             │
│   └─ Update ring buffer with new blocks                   │
│         ↓                                                   │
│ If reorg detected:                                         │
│   ├─ Rollback all caches to rollback point                │
│   ├─ Rebuild secondary indexes                             │
│   └─ Reset lastProcessedBlock                              │
│         ↓                                                   │
│ Process new blocks (from lastProcessedBlock + 1)           │
│   ├─ Query affected data from database                     │
│   ├─ V1 Signups (Human, Organization)                     │
│   ├─ V1 Transfers → Update affected balances              │
│   ├─ V2 Register (Human, Organization, Group)             │
│   ├─ V2 Transfers → Update affected balances              │
│   ├─ V2 Trust → Update group memberships                  │
│   └─ V2 ERC20 Wrappers → Update wrapper addresses         │
│         ↓                                                   │
│ Update RollbackCaches with new/modified entries            │
│         ↓                                                   │
│ Update Secondary Indexes for balance changes               │
│         ↓                                                   │
│ Update lastProcessedBlock                                  │
│         ↓                                                   │
│ Service continues serving requests with updated data       │
└─────────────────────────────────────────────────────────────┘
```

### 3. Rollback Handling (Reorg)

When a blockchain reorganization is detected via block hash mismatch:

```csharp
// NotificationListenerService.cs - HandleNotificationAsync
// Query recent blocks and update ring buffer
var reorgPoint = _state.BlockRingBuffer.UpdateFromBlocks(recentBlocks);

if (reorgPoint.HasValue)
{
    logger.LogWarning("Detected reorg at block {ReorgBlock}! Rolling back caches...", reorgPoint.Value);

    // 1. Rollback all caches (delete entries >= reorgPoint)
    caches.RollbackAll(reorgPoint.Value);

    // 2. Rebuild secondary indexes from remaining data
    caches.RebuildSecondaryIndexes();

    // 3. Update state
    state.LastProcessedBlock = Math.Min(state.LastProcessedBlock, reorgPoint.Value - 1);
}
```

The `BlockRingBuffer` detects reorgs by comparing stored block hashes:

```csharp
// BlockRingBuffer.cs - DetectReorg
public long? DetectReorg(long blockNumber, string blockHash)
{
    // Find block in buffer
    var existingBlock = _blocks.FirstOrDefault(b => b.BlockNumber == blockNumber);
    if (existingBlock != default)
    {
        // Compare hashes (case-insensitive)
        if (!string.Equals(existingBlock.BlockHash, blockHash, StringComparison.OrdinalIgnoreCase))
        {
            return blockNumber; // Reorg detected at this block
        }
    }
    return null; // No reorg
}
```

Each `RollbackCache` maintains a versioned history:

```csharp
// Entry: (value, blockNumber)
Add(blockNumber, key, value);      // Adds/updates with block number
Remove(key);                        // Removes from cache
TryGetValue(key, out value);        // Gets current value
DeleteAllGreaterOrEqualBlock(block); // Rollback operation
```

### 4. Balance Queries (O(1) Lookups)

The cache uses secondary indexes for efficient balance queries:

```csharp
// Primary cache: "address:tokenId" → balance
V1BalancesByAccountAndToken["0xabc:0xtoken1"] = 100.5m;
V2BalancesByAccountAndToken["0xabc:0xtoken2"] = 50.25m;

// Secondary index: address → set of tokenIds
_v1BalancesByAddress["0xabc"] = {"0xtoken1", "0xtoken3"};
_v2BalancesByAddress["0xabc"] = {"0xtoken2", "0xtoken5"};

// Query: Get all tokens for address (O(1) set lookup)
var tokens = GetTokenIdsForAddress("0xabc", isV1: true);
// Returns: {"0xtoken1", "0xtoken3"}

// Then lookup each balance (O(1) dictionary lookup per token)
foreach (var token in tokens) {
    var balance = V1BalancesByAccountAndToken[$"0xabc:{token}"];
}
```

## Cache Data Model

### V1 Caches

```csharp
// Avatars: address → (type, tokenAddress)
V1Avatars["0xabc..."] = ("Human", "0xtoken...");

// Token Owners: tokenAddress → ownerAddress
V1TokenOwnerByToken["0xtoken..."] = "0xowner...";

// Profile CIDs: address → cidV0
V1AvatarToCidMap["0xabc..."] = "QmX...";

// Balances: "address:tokenAddress" → balance (Circles)
V1BalancesByAccountAndToken["0xabc:0xtoken"] = 123.45m;
```

### V2 Caches

```csharp
// Avatars: address → (type, registeredAt)
V2Avatars["0xabc..."] = ("CrcV2_RegisterHuman", 1638360000);

// ERC20 Wrappers: avatarAddress → wrapperAddress
Erc20WrapperAddresses["0xavatar..."] = "0xwrapper...";

// Groups: groupAddress → (name, mint)
Groups["0xgroup..."] = ("Berlin Circle", "0xmint...");

// Group Memberships: "group:member" → (member, expiryTime)
GroupMemberships["0xgroup:0xmember"] = ("0xmember...", 1738360000);

// Profile CIDs: address → cidV0
V2AvatarToCidMap["0xabc..."] = "QmY...";

// Short Names: address → shortName
V2AvatarToShortNameMap["0xabc..."] = "alice.crc";

// Balances: "address:tokenId" → balance (Circles)
V2BalancesByAccountAndToken["0xabc:123"] = 456.78m;
```

## Performance Characteristics

### Query Performance

- **Balance Lookup (Single Address):** ~0.1-0.5ms (O(1) + O(n) where n = number of tokens held)
- **Total Balance (Single Address):** ~0.1-0.5ms
- **Avatar Lookup:** ~0.01ms (O(1) dictionary lookup)
- **Profile CID Lookup:** ~0.01ms (O(1) dictionary lookup)
- **Batch Queries (100 addresses):** ~10-50ms

### Memory Usage

Estimated memory per 10,000 entities:

- **Avatars (V1/V2):** ~2-3 MB
- **Balances:** ~5-10 MB (depends on token distribution)
- **Profile CIDs:** ~1-2 MB
- **Total (100k avatars, 500k balances):** ~500 MB - 1 GB

### Rollback Capacity

With `ROLLBACK_CAPACITY=12` (default):

- **History Depth:** 12 blocks (~60 seconds on Gnosis Chain)
- **Memory Overhead:** ~10-20% per cache (stores up to 12 versions of changed entries)

## Configuration & Tuning

### Rollback Capacity

```bash
# Default: 12 blocks (~60 seconds on Gnosis Chain)
export ROLLBACK_CAPACITY=12

# Higher values = more memory but better reorg protection
export ROLLBACK_CAPACITY=50
```

**Considerations:**

- Gnosis Chain: ~5 second block time → 12 blocks = 60 seconds
- Deep reorgs are rare (< 12 blocks in most cases)
- Higher capacity = more memory overhead

### Max Catchup Lag

```bash
# Default: 10 blocks
export MAX_CATCHUP_LAG=10
```

Service returns "not ready" (503) if cache lag exceeds this value.

### PostgreSQL Connection Strings

```bash
# Write connection (for LISTEN/NOTIFY)
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres;Pooling=true;MinPoolSize=1;MaxPoolSize=10"

# Read-only connection (for queries during warmup/updates)
export POSTGRES_READONLY_CONNECTION_STRING="Host=localhost;Port=5432;Database=postgres;Username=readonly;Password=readonly;Pooling=true;MinPoolSize=5;MaxPoolSize=20"
```

## Development

### Prerequisites

- .NET 10.0 SDK
- PostgreSQL 15+ (with indexed Circles data)
- Running Circles Index plugin

### Building

```bash
cd src/Cache/Circles.Cache.Service
dotnet build
```

### Testing

```bash
# Run service
dotnet run

# Test endpoints
curl http://localhost:3001/live
curl http://localhost:3001/ready
curl http://localhost:3001/cache/stats
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7/total
```

### Project Structure

```
src/Cache/
├── Circles.Cache.Service/
│   ├── Program.cs                         # ASP.NET Core app setup
│   ├── CacheServiceSettings.cs            # Configuration
│   ├── CacheServiceState.cs               # Service state tracking
│   ├── BlockRingBuffer.cs                 # Block tracking and reorg detection
│   ├── Caches/
│   │   └── CacheContainer.cs              # All cache instances
│   ├── Controllers/
│   │   ├── BalancesController.cs          # Balance API
│   │   ├── AvatarsController.cs           # Avatar API
│   │   └── ProfilesController.cs          # Profile CID API
│   ├── Services/
│   │   ├── CacheWarmupService.cs          # Initial cache loading
│   │   ├── NotificationListenerService.cs # Real-time updates
│   │   └── MetricsUpdateService.cs        # Prometheus metrics
│   ├── Metrics/
│   │   └── CacheMetrics.cs                # Prometheus metrics definitions
│   └── Models/
│       └── ApiResponses.cs                # Response DTOs
└── [Shared with Index project]
    └── Circles.Index.Common/
        └── RollbackCache.cs               # Core cache implementation
```

## Monitoring

### Prometheus Metrics

Available at `GET /metrics`:

```
# Cache operations
cache_hits_total{cache="V1BalancesByAccountAndToken"}
cache_misses_total{cache="V1BalancesByAccountAndToken"}
cache_entries{cache="V1Avatars"}

# Listener operations
cache_notifications_received_total
cache_blocks_processed_total
cache_reorgs_detected_total

# HTTP metrics (via prometheus-net)
http_requests_received_total
http_request_duration_seconds
```

### Health Monitoring

```bash
# Check if service is up
curl http://localhost:3001/live

# Check if service is ready to serve traffic
curl http://localhost:3001/ready

# Get cache statistics
curl http://localhost:3001/cache/stats
```

### Logs

The service logs important events:

```
[2024-01-15 10:30:00] info: === Circles Cache Service Starting ===
[2024-01-15 10:30:00] info: PostgreSQL Connection: Host=localhost;Port=5432;...
[2024-01-15 10:30:00] info: Rollback Capacity: 12 blocks
[2024-01-15 10:30:05] info: Cache warmup starting...
[2024-01-15 10:31:00] info: Cache warmup completed in 55.3s
[2024-01-15 10:31:00] info: Loaded 50,000 V1 balances, 75,000 V2 balances
[2024-01-15 10:31:00] info: Starting PostgreSQL LISTEN/NOTIFY listener on channel: circles_index_events
[2024-01-15 10:31:00] info: Successfully connected to PostgreSQL LISTEN channel
[2024-01-15 10:31:05] info: Processing block range 31234567 to 31234567
[2024-01-15 10:31:05] info: Successfully processed blocks 31234567 to 31234567
```

## Troubleshooting

### Cache Not Updating

**Symptoms:** `lastProcessedBlock` stuck, cache data stale

**Diagnosis:**

```bash
# Check if listener is connected
curl http://localhost:3001/cache/stats | jq '.listenerConnected'

# Check PostgreSQL notifications are being sent
# (from Index plugin logs)
```

**Solutions:**

1. Check PostgreSQL connection
2. Verify `CIRCLES_PG_NOTIFY_CHANNEL` matches Index plugin setting
3. Restart cache service
4. Check Index plugin is running and processing blocks

### High Memory Usage

**Symptoms:** Service using more memory than expected

**Diagnosis:**

```bash
# Check cache sizes
curl http://localhost:3001/cache/stats | jq '.total_entries'

# Monitor memory
docker stats circles-cache
```

**Solutions:**

1. Reduce `ROLLBACK_CAPACITY` (reduces history overhead)
2. Check for memory leaks (unusual growth over time)
3. Verify database queries are not loading too much data

### Slow Queries

**Symptoms:** API responses taking longer than expected

**Diagnosis:**

```bash
# Check warmup completed
curl http://localhost:3001/cache/stats | jq '.warmupComplete'

# Check for large number of tokens per address
```

**Solutions:**

1. Wait for warmup to complete
2. Check if address has excessive number of tokens
3. Verify secondary indexes are built (RebuildSecondaryIndexes called after warmup)

### Reorg Issues

**Symptoms:** Inconsistent data after blockchain reorganization

**Diagnosis:**

```bash
# Check reorg metrics
curl http://localhost:3001/metrics | grep reorgs_detected

# Check logs for reorg warnings
```

**Solutions:**

1. Increase `ROLLBACK_CAPACITY` if deep reorgs occur
2. Verify rollback logic is working (check logs)
3. Ensure secondary indexes are rebuilt after rollback

### Port Already in Use

```bash
# Use different port
PORT=3002 dotnet run

# Or set in environment
export PORT=3002
```

## Deployment

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY bin/Release/net10.0/publish/ .
ENTRYPOINT ["dotnet", "Circles.Cache.Service.dll"]
```

### Docker Compose

```yaml
version: "3.8"
services:
  cache:
    build: ./src/Cache/Circles.Cache.Service
    ports:
      - "3001:3001"
    environment:
      - POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=postgres;Username=postgres;Password=postgres
      - POSTGRES_READONLY_CONNECTION_STRING=${POSTGRES_CONNECTION_STRING}
      - CIRCLES_PG_NOTIFY_CHANNEL=circles_index_events
      - ROLLBACK_CAPACITY=12
      - MAX_CATCHUP_LAG=10
      - PORT=3001
    depends_on:
      - postgres
    restart: unless-stopped
```

## Integration

### Using with RPC Service

The Cache Service is designed to complement the RPC service by providing fast balance queries:

```bash
# RPC Service (database mode - slower but always current)
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0",
  "method": "circles_getTotalBalance",
  "params": ["0xaddr..."],
  "id": 1
}'

# Cache Service (in-memory - faster, near real-time)
curl http://localhost:3001/api/balances/0xaddr.../total
```

**Use Cases:**

- **Cache Service:** User-facing apps requiring fast, repeated balance queries
- **RPC Service:** Administrative queries, complex filters, guaranteed current data

### Using with Pathfinder

The Cache Service can provide fast balance lookups for pathfinding:

```bash
# 1. Get source address balances from cache
curl http://localhost:3001/api/balances/0xsource.../

# 2. Use pathfinder to find path
curl -X POST http://localhost:8080/flow -d '{
  "source": "0xsource...",
  "sink": "0xsink...",
  "targetFlow": "1000000000000000000"
}'
```

## Related Documentation

- [Main README](../../README.md) - Complete protocol documentation
- [DEVELOPMENT.md](../../DEVELOPMENT.md) - Build and deployment guide
- [Circles.Rpc.Host](../Rpc/Circles.Rpc.Host/README.md) - RPC service documentation
- [Circles.Index](../Index/README.md) - Index plugin documentation
- [Circles.Pathfinder](../Pathfinder/Circles.Pathfinder/README.md) - Pathfinding service

## API Summary

| Endpoint                           | Method | Description                      |
| ---------------------------------- | ------ | -------------------------------- |
| `/live`                            | GET    | Liveness probe                   |
| `/ready`                           | GET    | Readiness probe                  |
| `/cache/stats`                     | GET    | Cache statistics                 |
| `/metrics`                         | GET    | Prometheus metrics               |
| `/`                                | GET    | Service info                     |
| `/api/balances/{address}`          | GET    | Get all token balances           |
| `/api/balances/{address}/total`    | GET    | Get total balance (all versions) |
| `/api/balances/{address}/total/v1` | GET    | Get total V1 balance             |
| `/api/balances/{address}/total/v2` | GET    | Get total V2 balance             |
| `/api/avatars/{address}`           | GET    | Get avatar info                  |
| `/api/avatars/batch`               | POST   | Get avatar info (batch)          |
| `/api/profiles/{address}/cid`      | GET    | Get profile CID                  |
| `/api/profiles/cid/batch`          | POST   | Get profile CIDs (batch)         |
| `/api/profiles/content/{cid}`      | GET    | Get profile content by CID       |
| `/api/profiles/content/batch`      | POST   | Get profile content (batch)      |
