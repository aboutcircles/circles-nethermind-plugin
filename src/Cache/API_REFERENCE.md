# Cache Service API Reference

Complete reference for all Circles Cache Service HTTP endpoints with request/response examples.

---

## Table of Contents

- [Cache Service API Reference](#cache-service-api-reference)
  - [Table of Contents](#table-of-contents)
  - [Base URL](#base-url)
  - [Health \& Status Endpoints](#health--status-endpoints)
    - [GET /live](#get-live)
    - [GET /ready](#get-ready)
    - [GET /cache/stats](#get-cachestats)
  - [Balance Endpoints](#balance-endpoints)
    - [GET /api/balances/{address}](#get-apibalancesaddress)
    - [GET /api/balances/{address}/total/v1](#get-apibalancesaddresstotalv1)
    - [GET /api/balances/{address}/total/v2](#get-apibalancesaddresstotalv2)
    - [GET /api/balances/{address}/total](#get-apibalancesaddresstotal)
  - [Avatar Endpoints](#avatar-endpoints)
    - [GET /api/avatars/{address}](#get-apiavatarsaddress)
    - [POST /api/avatars/batch](#post-apiavatarsbatch)
  - [Profile Endpoints](#profile-endpoints)
    - [GET /api/profiles/{address}/cid](#get-apiprofilesaddresscid)
    - [POST /api/profiles/cid/batch](#post-apiprofilescidbatch)
  - [Trust Relation Endpoints](#trust-relation-endpoints)
    - [GET /api/trust/{address}](#get-apitrustaddress)
  - [Group Membership Endpoints](#group-membership-endpoints)
    - [GET /api/groups/{groupAddress}/members](#get-apigroupsgroupaddressmembers)
    - [GET /api/groups/memberships/{memberAddress}](#get-apigroupsmembershipsmemberaddress)
  - [Error Responses](#error-responses)
    - [400 Bad Request](#400-bad-request)
    - [404 Not Found](#404-not-found)
    - [500 Internal Server Error](#500-internal-server-error)
    - [503 Service Unavailable](#503-service-unavailable)
  - [Performance Characteristics](#performance-characteristics)
    - [Cache Hit Performance](#cache-hit-performance)
    - [Cache Size](#cache-size)
  - [Integration Examples](#integration-examples)
    - [Using with RPC Service](#using-with-rpc-service)
    - [Using Directly from Frontend](#using-directly-from-frontend)
  - [Health Check Integration](#health-check-integration)
    - [Docker Compose](#docker-compose)
    - [Kubernetes](#kubernetes)
  - [Rate Limiting](#rate-limiting)
  - [Monitoring](#monitoring)
    - [Key Metrics to Track](#key-metrics-to-track)
    - [Prometheus Metrics (Future)](#prometheus-metrics-future)
  - [Rate Limiting](#rate-limiting)
  - [Monitoring](#monitoring)
    - [Key Metrics to Track](#key-metrics-to-track)
    - [Prometheus Metrics (Future)](#prometheus-metrics-future)
  - [Phase 3 RPC Integration](#phase-3-rpc-integration)
    - [SDK Enablement Methods](#sdk-enablement-methods)
    - [Performance Benefits](#performance-benefits)
    - [Cache Integration](#cache-integration)
    - [Method Examples](#method-examples)

---

## Base URL

```
http://localhost:3001
```

Or in Docker Compose:

```
http://cache-service:3001
```

---

## Health & Status Endpoints

### GET /live

**Description**: Liveness probe - returns 200 if service process is running.

**Use Case**: Kubernetes/Docker liveness check

**Request**:

```bash
curl http://localhost:3001/live
```

**Response** (200 OK):

```
Healthy
```

---

### GET /ready

**Description**: Readiness probe - returns 200 only if warmup is complete and listener is connected.

**Use Case**: Kubernetes/Docker readiness check, health monitoring

**Request**:

```bash
curl http://localhost:3001/ready
```

**Response** (200 OK - Service Ready):

```json
{
  "status": "ready",
  "lastProcessedBlock": 36789012,
  "dbHead": 36789012,
  "lag": 0,
  "warmupComplete": true,
  "listenerConnected": true
}
```

**Response** (503 Service Unavailable - Warmup In Progress):

```json
{
  "status": "not_ready",
  "lastProcessedBlock": 20000000,
  "dbHead": 36789012,
  "lag": 16789012,
  "warmupComplete": false,
  "listenerConnected": false
}
```

**Response** (503 Service Unavailable - Listener Disconnected):

```json
{
  "status": "not_ready",
  "lastProcessedBlock": 36789012,
  "dbHead": 36790000,
  "lag": 988,
  "warmupComplete": true,
  "listenerConnected": false
}
```

---

### GET /cache/stats

**Description**: Returns cache statistics including entry counts for all caches.

**Use Case**: Monitoring, debugging, capacity planning

**Request**:

```bash
curl http://localhost:3001/cache/stats
```

**Response** (200 OK):

```json
{
  "v1_avatars": 130010,
  "v1_token_owners": 130010,
  "v1_avatar_cids": 30010,
  "v2_avatars": 30010,
  "erc20_wrappers": 3001,
  "groups": 1000,
  "group_memberships": 10000,
  "v2_avatar_cids": 30000,
  "v2_avatar_short_names": 1000,
  "v1_balances": 350782,
  "v2_balances": 33783,
  "v1_trust_relations": 1082430,
  "v2_trust_relations": 273507,
  "v1_trust_by_truster_index": 95000,
  "v1_trust_by_trustee_index": 95000,
  "v2_trust_by_truster_index": 12000,
  "v2_trust_by_trustee_index": 12000,
  "group_membership_by_group_index": 528,
  "group_membership_by_member_index": 7500,
  "erc20_wrapper_reverse_index": 6772,
  "total_entries": 1936046,
  "lastProcessedBlock": 36789012,
  "warmupComplete": true,
  "listenerConnected": true
}
```

---

## Balance Endpoints

### GET /api/balances/{address}

**Description**: Get all token balances (V1 and V2) for an Ethereum address.

**Parameters**:

- `address` (path, required): Ethereum address (0x + 40 hex characters)

**Request**:

```bash
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7
```

**Response** (200 OK):

```json
[
  {
    "tokenId": "0x42cedde51198d1773590311e2a340dc06b24cb37",
    "balance": "150.25",
    "tokenOwner": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "version": 1,
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  },
  {
    "tokenId": "0x86533d1aDA8Ffbe7b6F7244F9A1b707f7f3e239b",
    "balance": "75.50",
    "tokenOwner": "0x7cadf434b692ca029d950607a4b3f139c30d4e98",
    "version": 1,
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  },
  {
    "tokenId": "123456789012345678901234567890",
    "balance": "1250.75",
    "tokenOwner": null,
    "version": 2,
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  }
]
```

**Response** (400 Bad Request - Invalid Address):

```json
{
  "error": "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters"
}
```

**Response** (500 Internal Server Error):

```json
{
  "error": "Internal server error"
}
```

**Notes**:

- Balances are returned in Circles (not attoCircles)
- V1 tokens include `tokenOwner` field (the avatar that minted the token)
- V2 tokens use numeric `tokenId` instead of address
- Empty array `[]` returned if address has no balances

---

### GET /api/balances/{address}/total/v1

**Description**: Get total V1 balance for an address (sum of all V1 tokens).

**Parameters**:

- `address` (path, required): Ethereum address

**Request**:

```bash
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7/total/v1
```

**Response** (200 OK):

```json
{
  "balance": "225.75",
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Response** (400 Bad Request):

```json
{
  "error": "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters"
}
```

---

### GET /api/balances/{address}/total/v2

**Description**: Get total V2 balance for an address (sum of all V2 tokens).

**Parameters**:

- `address` (path, required): Ethereum address

**Request**:

```bash
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7/total/v2
```

**Response** (200 OK):

```json
{
  "balance": "1250.75",
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

---

### GET /api/balances/{address}/total

**Description**: Get total balance for an address (sum of all V1 + V2 tokens).

**Parameters**:

- `address` (path, required): Ethereum address

**Request**:

```bash
curl http://localhost:3001/api/balances/0xde374ece6fa50e781e81aac78e811b33d16912c7/total
```

**Response** (200 OK):

```json
{
  "balance": "1476.50",
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Notes**:

- This is the sum of V1 + V2 total balances
- Most commonly used endpoint for displaying user's total CRC balance

---

## Avatar Endpoints

### GET /api/avatars/{address}

**Description**: Get avatar information for a single Ethereum address.

**Parameters**:

- `address` (path, required): Ethereum address

**Request**:

```bash
curl http://localhost:3001/api/avatars/0xde374ece6fa50e781e81aac78e811b33d16912c7
```

**Response** (200 OK - V2 Human):

```json
{
  "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
  "version": 2,
  "type": "Human",
  "tokenId": "123456789012345678901234567890",
  "hasV1": true,
  "v1Token": "0x42cedde51198d1773590311e2a340dc06b24cb37",
  "cidV0": "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
  "isHuman": true,
  "name": "Community Pool",
  "symbol": null,
  "shortName": "zAlice",
  "registeredAt": 1704067200,
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Response** (200 OK - V2 Group):

```json
{
  "avatar": "0x1234567890abcdef1234567890abcdef12345678",
  "version": 2,
  "type": "Group",
  "tokenId": "987654321098765432109876543210",
  "hasV1": false,
  "v1Token": null,
  "cidV0": null,
  "isHuman": false,
  "name": "Community DAO",
  "symbol": "CDAO",
  "shortName": null,
  "registeredAt": 1704153600,
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Response** (200 OK - Not Found):

```json
null
```

**Notes**:

- Returns `null` if avatar not found
- `type` can be: `"Human"`, `"Organization"`, or `"Group"`
- `registeredAt` is Unix timestamp (V2 only, null for V1-only avatars)
- `cidV0` is IPFS CID for avatar's profile metadata
- `shortName` is the V2 registered short name in Base58Btc format (e.g., "zAlice")
- `lastProcessedBlock` indicates the cache's latest processed block
- `timestamp` is the Unix timestamp when the response was generated

---

### POST /api/avatars/batch

**Description**: Get avatar information for multiple addresses in a single request.

**Request Body**:

```json
{
  "addresses": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "0x7cadf434b692ca029d950607a4b3f139c30d4e98",
    "0x1234567890abcdef1234567890abcdef12345678"
  ]
}
```

**Request**:

```bash
curl -X POST http://localhost:3001/api/avatars/batch \
  -H "Content-Type: application/json" \
  -d '{
    "addresses": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "0x7cadf434b692ca029d950607a4b3f139c30d4e98"
    ]
  }'
```

**Response** (200 OK):

```json
[
  {
    "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "version": 2,
    "type": "Human",
    "tokenId": "123456789012345678901234567890",
    "hasV1": true,
    "v1Token": "0x42cedde51198d1773590311e2a340dc06b24cb37",
    "cidV0": "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
    "isHuman": true,
    "name": null,
    "symbol": null,
    "shortName": "zAlice",
    "registeredAt": 1704067200,
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  },
  null,
  {
    "avatar": "0x1234567890abcdef1234567890abcdef12345678",
    "version": 2,
    "type": "Group",
    "tokenId": "987654321098765432109876543210",
    "hasV1": false,
    "v1Token": null,
    "cidV0": null,
    "isHuman": false,
    "name": "Community DAO",
    "symbol": "CDAO",
    "shortName": null,
    "registeredAt": 1704153600,
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  }
]
```

**Notes**:

- Returns array in same order as input addresses
- `null` entries for addresses not found
- Maximum batch size is 100 addresses

---

## Profile Endpoints

### GET /api/profiles/{address}/cid

**Description**: Get IPFS CID (Content Identifier) for an avatar's profile.

**Parameters**:

- `address` (path, required): Ethereum address

**Request**:

```bash
curl http://localhost:3001/api/profiles/0xde374ece6fa50e781e81aac78e811b33d16912c7/cid
```

**Response** (200 OK):

```json
{
  "cid": "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Response** (200 OK - No Profile):

```json
{
  "cid": null,
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Notes**:

- CID can be used to fetch profile JSON from IPFS
- Returns `null` for `cid` if avatar has no profile metadata

---

### POST /api/profiles/cid/batch

**Description**: Get profile CIDs for multiple addresses in a single request.

**Request Body**:

```json
{
  "addresses": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "0x7cadf434b692ca029d950607a4b3f139c30d4e98"
  ]
}
```

**Request**:

```bash
curl -X POST http://localhost:3001/api/profiles/cid/batch \
  -H "Content-Type: application/json" \
  -d '{
    "addresses": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "0x7cadf434b692ca029d950607a4b3f139c30d4e98"
    ]
  }'
```

**Response** (200 OK):

```json
[
  {
    "cid": "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  },
  {
    "cid": null,
    "lastProcessedBlock": 36789012,
    "timestamp": 1704240000
  }
]
```

**Notes**:

- Returns array in same order as input addresses
- `cid` is `null` for avatars without profiles
- Maximum batch size is 100 addresses

---

## Trust Relation Endpoints

### GET /api/trust/{address}

**Description**: Get all trust relations for an address (both who they trust and who trusts them).

**Parameters**:

- `address` (path, required): Ethereum address
- `version` (query, optional): Filter by Circles version (1 or 2). Omit for both.

**Request**:

```bash
curl http://localhost:3001/api/trust/0xde374ece6fa50e781e81aac78e811b33d16912c7
```

**Request with version filter**:

```bash
curl "http://localhost:3001/api/trust/0xde374ece6fa50e781e81aac78e811b33d16912c7?version=2"
```

**Response** (200 OK):

```json
{
  "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
  "trusts": [
    {
      "truster": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "trustee": "0x1234567890abcdef1234567890abcdef12345678",
      "expiryTime": 1735689600,
      "version": 2,
      "lastProcessedBlock": 36789012,
      "timestamp": 1704240000
    }
  ],
  "trustedBy": [
    {
      "truster": "0xabcdef1234567890abcdef1234567890abcdef12",
      "trustee": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "expiryTime": 100,
      "version": 1,
      "lastProcessedBlock": 36789012,
      "timestamp": 1704240000
    }
  ],
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Notes**:

- V1 trust uses `expiryTime` to store the trust limit (0-100 percentage)
- V2 trust uses `expiryTime` as actual Unix timestamp expiry
- `trusts` = addresses this user trusts (outgoing)
- `trustedBy` = addresses that trust this user (incoming)

---

## Group Membership Endpoints

### GET /api/groups/{groupAddress}/members

**Description**: Get all members of a specific group.

**Parameters**:

- `groupAddress` (path, required): Group contract address

**Request**:

```bash
curl http://localhost:3001/api/groups/0x1234567890abcdef1234567890abcdef12345678/members
```

**Response** (200 OK):

```json
{
  "group": "0x1234567890abcdef1234567890abcdef12345678",
  "members": [
    {
      "group": "0x1234567890abcdef1234567890abcdef12345678",
      "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "expiryTime": 1735689600,
      "lastProcessedBlock": 36789012,
      "timestamp": 1704240000
    },
    {
      "group": "0x1234567890abcdef1234567890abcdef12345678",
      "member": "0xabcdef1234567890abcdef1234567890abcdef12",
      "expiryTime": 1735689600,
      "lastProcessedBlock": 36789012,
      "timestamp": 1704240000
    }
  ],
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

---

### GET /api/groups/memberships/{memberAddress}

**Description**: Get all groups that a member belongs to.

**Parameters**:

- `memberAddress` (path, required): Member's Ethereum address

**Request**:

```bash
curl http://localhost:3001/api/groups/memberships/0xde374ece6fa50e781e81aac78e811b33d16912c7
```

**Response** (200 OK):

```json
{
  "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
  "groups": [
    {
      "group": "0x1234567890abcdef1234567890abcdef12345678",
      "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "expiryTime": 1735689600,
      "lastProcessedBlock": 36789012,
      "timestamp": 1704240000
    }
  ],
  "lastProcessedBlock": 36789012,
  "timestamp": 1704240000
}
```

**Notes**:

- Returns empty `groups` array if member has no group memberships
- `expiryTime` is the membership expiration Unix timestamp

---

## Error Responses

### 400 Bad Request

Returned when request validation fails.

**Example**: Invalid Ethereum address format

```json
{
  "error": "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters"
}
```

### 404 Not Found

Returned when resource is not found (single avatar lookup only).

**Example**: Avatar not found

```json
null
```

### 500 Internal Server Error

Returned when server encounters an unexpected error.

```json
{
  "error": "Internal server error"
}
```

**Note**: Check server logs for details

### 503 Service Unavailable

Returned by `/ready` endpoint when cache is not ready.

```json
{
  "status": "not_ready",
  "warmupComplete": false,
  "listenerConnected": false
}
```

---

## Performance Characteristics

### Cache Hit Performance

| Endpoint                          | Typical Response Time | Data Source                                           |
| --------------------------------- | --------------------- | ----------------------------------------------------- |
| GET /api/balances/{address}       | 5-20ms                | In-memory cache (O(n) where n = # tokens for address) |
| GET /api/balances/{address}/total | 5-15ms                | In-memory cache (aggregation)                         |
| GET /api/avatars/{address}        | 2-10ms                | In-memory cache (O(1) lookup)                         |
| POST /api/avatars/batch           | 10-50ms               | In-memory cache (O(n) lookups)                        |
| GET /api/profiles/{address}/cid   | 2-10ms                | In-memory cache (O(1) lookup)                         |

### Cache Size

**Typical Production (Gnosis Mainnet)**:

- Total entries: ~1.1M
- Memory usage: ~500 MB
- V1 balances: ~350K entries
- V2 balances: ~35K entries
- Avatars: ~160K entries
- Groups: ~1K entries

---

## Integration Examples

### Using with RPC Service

The RPC service automatically uses cache when `USE_CACHE_SERVICE=true`:

```bash
# Set environment variables
export USE_CACHE_SERVICE=true
export CACHE_SERVICE_URL=http://localhost:3001

# Start RPC service
./scripts/run-rpc.sh

# RPC calls will automatically use cache
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getTotalBalance",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 0]
  }'
```

### Using Directly from Frontend

```typescript
// TypeScript example
const CACHE_SERVICE_URL = "http://localhost:3001"

async function getTotalBalance(address: string): Promise<string> {
  const response = await fetch(
    `${CACHE_SERVICE_URL}/api/balances/${address}/total`
  )
  const data = await response.json()
  return data.balance
}

async function getAvatarsBatch(addresses: string[]): Promise<AvatarInfo[]> {
  const response = await fetch(`${CACHE_SERVICE_URL}/api/avatars/batch`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ addresses }),
  })
  return response.json()
}
```

---

## Health Check Integration

### Docker Compose

```yaml
cache-service:
  image: circles-cache-service
  ports:
    - "3001:3001"
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:3001/ready"]
    interval: 10s
    timeout: 5s
    retries: 10
    start_period: 60s # Allow time for warmup
```

### Kubernetes

```yaml
apiVersion: v1
kind: Pod
metadata:
  name: cache-service
spec:
  containers:
    - name: cache-service
      image: circles-cache-service
      ports:
        - containerPort: 3001
      livenessProbe:
        httpGet:
          path: /live
          port: 3001
        initialDelaySeconds: 10
        periodSeconds: 10
      readinessProbe:
        httpGet:
          path: /ready
          port: 3001
        initialDelaySeconds: 60 # Warmup time
        periodSeconds: 10
```

---

## Rate Limiting

**Current Implementation**: None

**Recommendations for Production**:

- Add rate limiting middleware (e.g., AspNetCoreRateLimit)
- Suggested limits:
  - Single address queries: 100 req/min per IP
  - Batch queries: 20 req/min per IP
  - Health checks: No limit

---

## Monitoring

### Key Metrics to Track

1. **Cache Hit Rate**: Should be >95%
2. **Response Time**: P50 <10ms, P99 <50ms
3. **Warmup Time**: Track on startup
4. **Listener Uptime**: Should stay connected
5. **Memory Usage**: Monitor for leaks
6. **Block Lag**: `dbHead - lastProcessedBlock` should be <10

### Prometheus Metrics (Future)

```
# HELP circles_cache_entries_total Total number of cached entries
# HELP circles_cache_response_time_seconds Response time histogram
circles_cache_response_time_seconds_bucket{endpoint="/api/balances",le="0.01"} 40000
```

---

## Phase 3 RPC Integration

**Overview**: The Circles RPC service now includes 6 new SDK enablement methods that consolidate multiple RPC calls into single optimized endpoints. These methods can work with or without the cache service.

### SDK Enablement Methods

| Method | Purpose | Replaces | Cache Support |
|--------|---------|----------|---------------|
| `circles_getProfileView` | Complete profile (avatar + profile + balances + trust stats) | 6-7 calls | ✅ Partial |
| `circles_getTrustNetworkSummary` | Aggregated trust network statistics | 3-4 calls | ❌ No cache |
| `circles_getAggregatedTrustRelationsEnriched` | Trust relations categorized by type + avatar info | 2-3 calls | ❌ No cache |
| `circles_getValidInviters` | Inviters with sufficient balance | 3-4 calls | ✅ Yes |
| `circles_getTransactionHistoryEnriched` | Transactions with participant profiles | 2-3 calls | ❌ No cache |
| `circles_searchProfileByAddressOrName` | Unified search (address or text) | 2 calls | ❌ No cache |

### Performance Benefits

- **60-80% reduction** in network round-trips for common operations
- **Massive latency improvement** for profile views and trust network queries
- **Reduced server load** through server-side aggregation
- **Better developer experience** with fewer API calls required

### Cache Integration

Some Phase 3 methods leverage the cache service when available:

#### `circles_getProfileView` (Cache-Enhanced)
- **Cache Hit**: Avatar info + profile CID lookups (5-10ms)
- **Database Fallback**: Trust relations + balance calculations (50-100ms)
- **Performance**: 2-3x faster with cache enabled

#### `circles_getValidInviters` (Cache-Enhanced)
- **Cache Hit**: Avatar info for trusted addresses (10-20ms)
- **Database Fallback**: Trust relations + balance checks (100-200ms)
- **Performance**: 3-4x faster with cache enabled

### Method Examples

#### circles_getProfileView

**Replaces 6-7 individual RPC calls:**

**Before (7 calls)**:
```bash
# Call 1: Get avatar info
curl -X POST http://localhost:8081 -d '{"method":"circles_getAvatarInfo","params":["0xaddr"]}'

# Call 2: Get profile
curl -X POST http://localhost:8081 -d '{"method":"circles_getProfileByAddress","params":["0xaddr"]}'

# Call 3: Get V1 balance
curl -X POST http://localhost:8081 -d '{"method":"circles_getTotalBalance","params":["0xaddr",1]}'

# Call 4: Get V2 balance
curl -X POST http://localhost:8081 -d '{"method":"circles_getTotalBalance","params":["0xaddr",2]}'

# Call 5: Get trust relations
curl -X POST http://localhost:8081 -d '{"method":"circles_getTrustRelations","params":["0xaddr"]}'

# Call 6: Get mutual trusts
curl -X POST http://localhost:8081 -d '{"method":"circles_getAggregatedTrustRelations","params":["0xaddr"]}'

# Call 7: Count trusted by
curl -X POST http://localhost:8081 -d '{"method":"circles_getTrustRelations","params":["0xaddr"]}'
```

**After (1 call)**:
```bash
curl -X POST http://localhost:8081 -d '{
  "jsonrpc": "2.0",
  "method": "circles_getProfileView", 
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
  "id": 1
}'
```

**Response**:
```json
{
  "jsonrpc": "2.0",
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "avatarInfo": {
      "version": 2,
      "type": "Human", 
      "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "isHuman": true,
      "name": "Alice",
      "symbol": null
    },
    "profile": {
      "name": "Alice's Profile",
      "description": "Community builder and Circles enthusiast"
    },
    "trustStats": {
      "trustsCount": 25,
      "trustedByCount": 18
    },
    "v1Balance": "150.25",
    "v2Balance": "75.50"
  },
  "id": 1
}
```

#### circles_getValidInviters

**Replaces 3-4 individual calls for invitation flows:**

**Before**:
```bash
# Get trust relations
curl -X POST http://localhost:8081 -d '{"method":"circles_getTrustRelations","params":["0xaddr"]}'

# Get balances for each trusted address (loop)
curl -X POST http://localhost:8081 -d '{"method":"circles_getTotalBalance","params":["0xtrusted_addr",2]}'
# ... repeat for each trusted address

# Filter by minimum balance (client-side)
```

**After**:
```bash
curl -X POST http://localhost:8081 -d '{
  "jsonrpc": "2.0", 
  "method": "circles_getValidInviters",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", "96"],
  "id": 1
}'
```

**Response**:
```json
{
  "jsonrpc": "2.0",
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "validInviters": [
      {
        "address": "0xtrusted1...",
        "balance": "150.25",
        "avatarInfo": {
          "version": 2,
          "type": "Human",
          "name": "Bob"
        }
      },
      {
        "address": "0xtrusted2...", 
        "balance": "200.75",
        "avatarInfo": {
          "version": 2,
          "type": "Human", 
          "name": "Carol"
        }
      }
    ]
  },
  "id": 1
}
```

#### circles_getTrustNetworkSummary

**Aggregates trust network analysis:**

```bash
curl -X POST http://localhost:8081 -d '{
  "jsonrpc": "2.0",
  "method": "circles_getTrustNetworkSummary",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 2],
  "id": 1
}'
```

**Response**:
```json
{
  "jsonrpc": "2.0",
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "directTrustsCount": 25,
    "directTrustedByCount": 18,
    "mutualTrustsCount": 12,
    "mutualTrusts": ["0xaddr1...", "0xaddr2..."],
    "networkReach": 31
  },
  "id": 1
}
```

### Testing Phase 3 Methods

Test all Phase 3 methods with the RPC test script:

```bash
# Test all Phase 3 SDK methods
./scripts/test-rpc.sh --json --json-dir ./phase3-results
```

The test script includes comprehensive tests for all 6 methods with validation that responses match expected schemas.

### Integration Notes

1. **Cache Dependency**: Methods that support cache will automatically use it when `USE_CACHE_SERVICE=true`
2. **Fallback Behavior**: All cache-enabled methods gracefully fall back to database queries if cache is unavailable
3. **Response Consistency**: Methods return consistent data formats regardless of cache usage
4. **Error Handling**: Proper error codes and messages for invalid parameters or missing data

**Performance with Cache**:
- Cache-enabled methods: 5-20ms typical response
- Database-only methods: 50-200ms typical response
- Cache hit rate should be >95% for optimal performance
circles_cache_entries_total{cache="v1_balances"} 350782

# HELP circles_cache_requests_total Total number of cache requests
circles_cache_requests_total{endpoint="/api/balances",status="200"} 45123

# HELP circles_cache_response_time_seconds Response time histogram
circles_cache_response_time_seconds_bucket{endpoint="/api/balances",le="0.01"} 40000
```

---
