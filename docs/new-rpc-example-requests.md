# New RPC Methods - Request/Response Documentation

Complete documentation for all newly implemented RPC methods with examples.

---

## Table of Contents

- [New RPC Methods - Request/Response Documentation](#new-rpc-methods---requestresponse-documentation)
  - [Table of Contents](#table-of-contents)
  - [SDK Enablement Methods](#sdk-enablement-methods)
    - [circles\_getProfileView](#circles_getprofileview)
    - [circles\_getTrustNetworkSummary](#circles_gettrustnetworksummary)
    - [circles\_getAggregatedTrustRelationsEnriched](#circles_getaggregatedtrustrelationsenriched)
    - [circles\_getValidInviters](#circles_getvalidinviters)
    - [circles\_getTransactionHistoryEnriched](#circles_gettransactionhistoryenriched)
    - [circles\_searchProfileByAddressOrName](#circles_searchprofilebyaddressorname)
  - [Paginated Query Methods](#paginated-query-methods)
    - [circles\_findGroups](#circles_findgroups)
    - [circles\_getGroupMembers](#circles_getgroupmembers)
    - [circles\_getGroupMemberships](#circles_getgroupmemberships)
    - [circles\_getTransactionHistory](#circles_gettransactionhistory)
    - [circles\_getTokenHolders](#circles_gettokenholders)
  - [Aggregated Query Methods](#aggregated-query-methods)
    - [circles\_getAggregatedTrustRelations](#circles_getaggregatedtrustrelations)
  - [Performance Comparison](#performance-comparison)
    - [Before (Multiple Calls)](#before-multiple-calls)
    - [After (Single Call)](#after-single-call)
  - [Error Responses](#error-responses)
  - [SDK Usage Examples](#sdk-usage-examples)
    - [TypeScript/JavaScript](#typescriptjavascript)
    - [Python](#python)
  - [Migration Guide](#migration-guide)
    - [For SDK Developers](#for-sdk-developers)
    - [For dApp Developers](#for-dapp-developers)
  - [Support](#support)

---

## Cache Service Integration

Several RPC methods can leverage the Cache Service for faster responses. Enable with:

```bash
export USE_CACHE_SERVICE=true
export CACHE_SERVICE_URL=http://localhost:3001
```

**Methods with Cache Support:**

| Method | Cache Used For | Fallback |
|--------|---------------|----------|
| `circles_getTrustRelations` | Trust relation lookup | Database |
| `circles_getGroupMembers` | First page of results | Database for pagination |
| `circles_getGroupMemberships` | First page of results | Database for pagination |
| `circles_getValidInviters` | Trust relation lookup | Database |
| `circles_getTotalBalance` | Balance lookup | Database |
| `circles_getAvatarInfo` | Avatar info lookup | Database |
| `circles_getProfileCid` | CID lookup | Database |

All cache-enabled methods gracefully fall back to database queries if the cache service is unavailable.

---

## SDK Enablement Methods

### circles_getProfileView

**Description**: Get complete profile view consolidating avatar info, profile data, trust stats, and balances in a single call. Replaces 6-7 separate RPC calls. Profile metadata is normalized to the fields that are available on production (`name` and `previewImageUrl`).

**Parameters**:

- `address` (string): Ethereum address

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getProfileView",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "avatarInfo": {
      "version": 2,
      "type": "CrcV2_RegisterHuman",
      "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "tokenId": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "hasV1": true,
      "v1Token": "0x42cedde51198d1773590311e2a340dc06b24cb37",
      "cidV0Digest": "0x9b...",
      "cidV0": "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
      "isHuman": true,
      "name": "Alice",
      "symbol": "ALICE"
    },
    "profile": {
      "name": "Alice",
      "previewImageUrl": "ipfs://Qm..."
    },
    "trustStats": {
      "trustsCount": 42,
      "trustedByCount": 38
    },
    "v1Balance": "150.25",
    "v2Balance": "1250.75"
  }
}
```

**Use Case**: Dashboard loading, profile pages

---

### circles_getTrustNetworkSummary

**Description**: Get aggregated trust network metrics for the direct (depth‑1) neighborhood. The optional `maxDepth` parameter is reserved for future graph exploration but is currently limited to depth 1.

**Parameters**:

- `address` (string): Ethereum address
- `maxDepth` (number, optional): Maximum traversal depth (default: 2)

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getTrustNetworkSummary",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 2]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "directTrustsCount": 42,
    "directTrustedByCount": 38,
    "mutualTrustsCount": 35,
    "mutualTrusts": [
      "0x1234567890abcdef1234567890abcdef12345678",
      "0xabcdef1234567890abcdef1234567890abcdef12"
    ],
    "networkReach": 45
  }
}
```

**Use Case**: Network visualization, trust graph analysis

---

### circles_getAggregatedTrustRelationsEnriched

**Description**: Get categorized trust relations (mutual/trusts/trustedBy) with avatar metadata pre-loaded. Use `circles_getAggregatedTrustRelations` if you also need timestamps/expiry fields.

**Parameters**:

- `address` (string): Ethereum address

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getAggregatedTrustRelationsEnriched",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "mutual": [
      {
        "address": "0x1234567890abcdef1234567890abcdef12345678",
        "avatarInfo": {
          "version": 2,
          "type": "CrcV2_RegisterHuman",
          "avatar": "0x1234567890abcdef1234567890abcdef12345678",
          "tokenId": "0x1234567890abcdef1234567890abcdef12345678",
          "hasV1": false,
          "cidV0Digest": "0xab...",
          "cidV0": "QmABC...",
          "isHuman": true,
          "name": "Bob",
          "symbol": "BOB"
        },
        "relationType": "mutual"
      }
    ],
    "trusts": [
      {
        "address": "0xabcdef1234567890abcdef1234567890abcdef12",
        "avatarInfo": null,
        "relationType": "trusts"
      }
    ],
    "trustedBy": [
      {
        "address": "0x9876543210fedcba9876543210fedcba98765432",
        "avatarInfo": null,
        "relationType": "trustedBy"
      }
    ]
  }
}
```

**Use Case**: Trust list UI with avatars/names

---

### circles_getValidInviters

**Description**: Find addresses that trust you AND have sufficient balance to send an invitation. Balance-filtered inviter discovery.

**Cache Integration**: When cache service is enabled (`USE_CACHE_SERVICE=true`), trust relations are fetched from cache for faster response.

**Parameters**:

- `address` (string): Ethereum address to find inviters for
- `minimumBalance` (string, optional): Minimum CRC balance required (stringified decimal). When omitted, all inviters are returned.

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getValidInviters",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", "10.0"]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "validInviters": [
      {
        "address": "0x1234567890abcdef1234567890abcdef12345678",
        "balance": "150.25",
        "avatarInfo": {
          "version": 2,
          "type": "CrcV2_RegisterHuman",
          "avatar": "0x1234567890abcdef1234567890abcdef12345678",
          "tokenId": "0x1234567890abcdef1234567890abcdef12345678",
          "hasV1": false,
          "cidV0Digest": "0xab...",
          "cidV0": "QmABC...",
          "isHuman": true,
          "name": "Bob",
          "symbol": "BOB"
        }
      }
    ]
  }
}
```

**Use Case**: Invitation flows, sponsor discovery

---

### circles_getTransactionHistoryEnriched

**Description**: Transaction history with participant profiles pre-loaded. Replaces `circles_events` + multiple `getProfileByAddress` calls.

**Parameters**:

- `address` (string): Ethereum address
- `fromBlock` (number): Starting block number (inclusive)
- `toBlock` (number, optional): Ending block number (default: latest)
- `limit` (number, optional): Maximum results (default: 20)
- `cursor` (string, optional): Base64 encoded `"blockNumber:transactionIndex:logIndex"` pointer for pagination

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getTransactionHistoryEnriched",
    "params": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      36000000,
      null,
      10,
      null
    ]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "results": [
      {
        "blockNumber": 36500000,
        "transactionHash": "0xabc123...",
        "transactionIndex": 10,
        "logIndex": 3,
        "event": {
          "from": "0x1234567890abcdef1234567890abcdef12345678",
          "to": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          "value": "50000000000000000000",
          "id": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          "blockNumber": 36500000,
          "timestamp": 1704240000
        },
        "participants": {
          "0x1234567890abcdef1234567890abcdef12345678": {
            "avatarInfo": {
              "version": 2,
              "type": "CrcV2_RegisterHuman",
              "avatar": "0x1234567890abcdef1234567890abcdef12345678",
              "tokenId": "0x1234567890abcdef1234567890abcdef12345678",
              "hasV1": false,
              "cidV0Digest": "0xab...",
              "cidV0": "QmABC...",
              "isHuman": true,
              "name": "Bob",
              "symbol": "BOB"
            },
            "profile": {
              "name": "Bob",
              "previewImageUrl": "ipfs://Qm..."
            }
          },
          "0xde374ece6fa50e781e81aac78e811b33d16912c7": {
            "avatarInfo": {
              "version": 2,
              "type": "CrcV2_RegisterHuman",
              "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
              "tokenId": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
              "hasV1": true,
              "v1Token": "0x42cedde51198d1773590311e2a340dc06b24cb37",
              "cidV0Digest": "0x9b...",
              "cidV0": "QmYxivS5D...",
              "isHuman": true,
              "name": "Alice",
              "symbol": "ALICE"
            },
            "profile": {
              "name": "Alice",
              "previewImageUrl": "ipfs://Qm..."
            }
          }
        }
      }
    ],
    "hasMore": true,
    "nextCursor": "MzY1MDAwMDA6MTA6Mw=="
  }
}
```

**Use Case**: Transaction history UI with sender/receiver info

---

### circles_searchProfileByAddressOrName

**Description**: Unified search endpoint. If the query starts with `0x`, the server performs an address-prefix lookup (including partial matches). Otherwise it falls back to the full-text profile index used by `circles_searchProfiles`.

**Parameters**:

- `query` (string): Address prefix or free-form text
- `limit` (number, optional): Maximum number of profiles (default: 10)
- `offset` (number, optional): Offset for text search (default: 0)
- `types` (string[], optional): Restrict to avatar types such as `CrcV2_RegisterHuman`

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_searchProfileByAddressOrName",
    "params": ["alice", 10, 0, ["CrcV2_RegisterHuman"]]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "query": "alice",
    "searchType": "text",
    "results": [
      {
        "name": "Alice",
        "previewImageUrl": "ipfs://Qm..."
      },
      {
        "name": "Alice Bakery",
        "previewImageUrl": "ipfs://Qn..."
      }
    ],
    "totalCount": 12
  }
}
```

**Use Case**: Autosuggest for address inputs and global profile search bars

---

## Paginated Query Methods

### circles_findGroups

**Description**: Discover registered Circles groups with optional filters and cursor-based pagination.

**Parameters**:

- `limit` (number, optional): Results per page (default: 50)
- `queryParams` (object, optional): Filters
  - `nameStartsWith` (string, optional)
  - `symbolStartsWith` (string, optional)
  - `ownerIn` (string[], optional): Match by `mint` address
- `cursor` (string, optional): Base64 encoded `"blockNumber:transactionIndex:logIndex"`

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_findGroups",
    "params": [
      25,
      {"nameStartsWith": "Com"},
      null
    ]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "results": [
      {
        "group": "0x1234567890abcdef1234567890abcdef12345678",
        "name": "Community DAO",
        "symbol": "CDAO",
        "mint": "0x42cedde51198d1773590311e2a340dc06b24cb37",
        "treasury": "0xabcd...",
        "blockNumber": 36000000,
        "timestamp": 1704067200
      }
    ],
    "hasMore": true,
    "nextCursor": "MzYwMDAwMDA6MDow"
  }
}
```

**Use Case**: Group discovery lists and admin dashboards

### circles_getGroupMembers

**Description**: Get paginated list of group members with cursor-based navigation.

**Cache Integration**: When cache service is enabled and no cursor is provided (first page), results are fetched from cache for faster response. Subsequent pages fall back to database.

**Parameters**:

- `groupAddress` (string): Group contract address
- `limit` (number, optional): Results per page (default: 100, max: 1000)
- `cursor` (string, optional): Base64 encoded `"blockNumber:transactionIndex:logIndex"` cursor returned by this method

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getGroupMembers",
    "params": ["0x1234567890abcdef1234567890abcdef12345678", 100, null]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "results": [
      {
        "blockNumber": 36000000,
        "timestamp": 1704067200,
        "transactionIndex": 0,
        "logIndex": 1,
        "transactionHash": "0xabc123...",
        "group": "0x1234567890abcdef1234567890abcdef12345678",
        "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "expiryTime": 1735689600
      }
    ],
    "hasMore": true,
    "nextCursor": "MzYwNTAwMDA6NTox"
  }
}
```

**Cursor Format**: Base64 encoded string of `blockNumber:transactionIndex:logIndex` (e.g., `MzYwNTAwMDA6NTox` represents `36050000:5:1`).

**Use Case**: Group member lists, pagination

---

### circles_getGroupMemberships

**Description**: Get paginated list of groups a member belongs to (inverse of getGroupMembers).

**Cache Integration**: When cache service is enabled and no cursor is provided (first page), results are fetched from cache for faster response. Subsequent pages fall back to database.

**Parameters**:

- `memberAddress` (string): Member's Ethereum address
- `limit` (number, optional): Results per page (default: 50, max: 1000)
- `cursor` (string, optional): Base64 encoded `"blockNumber:transactionIndex:logIndex"`

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getGroupMemberships",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 50, null]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "results": [
      {
        "blockNumber": 36050000,
        "timestamp": 1704153600,
        "transactionIndex": 5,
        "logIndex": 1,
        "transactionHash": "0xdef456...",
        "group": "0x1234567890abcdef1234567890abcdef12345678",
        "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "expiryTime": 1735689600
      }
    ],
    "hasMore": false,
    "nextCursor": null
  }
}
```

**Use Case**: "My Groups" UI

---

### circles_getTransactionHistory

**Description**: Get paginated transaction history with cursor-based navigation. More efficient than `getTransactionHistoryEnriched` when profile data not needed.

**Parameters**:

- `avatarAddress` (string): Ethereum address
- `limit` (number, optional): Results per page (default: 50, max: 1000)
- `cursor` (string, optional): Base64 encoded `"blockNumber:transactionIndex:logIndex:batchIndex"`

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getTransactionHistory",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 50, null]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "results": [
      {
        "blockNumber": 36500000,
        "timestamp": 1704240000,
        "transactionIndex": 10,
        "logIndex": 3,
        "transactionHash": "0xabc123...",
        "version": 2,
        "from": "0x1234567890abcdef1234567890abcdef12345678",
        "to": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "operator": null,
        "id": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "value": "50000000000000000000",
        "circles": "50",
        "attoCircles": "50000000000000000000",
        "crc": "50",
        "attoCrc": "50000000000000000000",
        "staticCircles": "60",
        "staticAttoCircles": "60000000000000000000"
      }
    ],
    "hasMore": true,
    "nextCursor": "MzY1MDAwMDA6MTA6Mzow"
  }
}
```

**Cursor Format**: Base64 encoded string representing `blockNumber:transactionIndex:logIndex:batchIndex` (e.g., `MzY1MDAwMDA6MTA6Mzow`).

**Use Case**: Transaction lists, infinite scroll

---

### circles_getTokenHolders

**Description**: Get paginated list of token holders for a specific token.

**Parameters**:

- `tokenAddress` (string): Token contract address or token ID
- `limit` (number, optional): Results per page (default: 100, max: 1000)
- `cursor` (string, optional): Last account address returned (opaque string, not base64)

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getTokenHolders",
    "params": ["0x42cedde51198d1773590311e2a340dc06b24cb37", 100, null]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "results": [
      {
        "account": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "tokenId": "0x42cedde51198d1773590311e2a340dc06b24cb37",
        "balance": "150000000000000000000",
        "version": 1
      },
      {
        "account": "0x1234567890abcdef1234567890abcdef12345678",
        "tokenId": "0x42cedde51198d1773590311e2a340dc06b24cb37",
        "balance": "75.50",
        "version": 1
      }
    ],
    "hasMore": true,
    "nextCursor": "0x1234567890abcdef1234567890abcdef12345678"
  }
}
```

**Cursor Format**: `nextCursor` returns the last `account` string. Pass it back verbatim to continue scanning in ascending order.

**Use Case**: Token holder analytics, distribution charts

---

## Aggregated Query Methods

### circles_getAggregatedTrustRelations

**Description**: Get trust relations categorized by type (mutual, outgoing, incoming). More efficient than querying raw trust events.

**Parameters**:

- `avatar` (string): Ethereum address

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getAggregatedTrustRelations",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": [
    {
      "subjectAvatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "relation": "mutuallyTrusts",
      "objectAvatar": "0x1234567890abcdef1234567890abcdef12345678",
      "timestamp": 1704067200,
      "expiryTime": 1735689600,
      "objectAvatarType": "Human"
    },
    {
      "subjectAvatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "relation": "trusts",
      "objectAvatar": "0xabcdef1234567890abcdef1234567890abcdef12",
      "timestamp": 1704153600,
      "expiryTime": 1735689600,
      "objectAvatarType": "Organization"
    },
    {
      "subjectAvatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "relation": "trustedBy",
      "objectAvatar": "0x9876543210fedcba9876543210fedcba98765432",
      "timestamp": 1704240000,
      "expiryTime": 1735689600,
      "objectAvatarType": "Human"
    }
  ]
}
```

**Relation Types**:

- `mutuallyTrusts`: Bidirectional trust
- `trusts`: Subject trusts object (outgoing)
- `trustedBy`: Object trusts subject (incoming)

**Use Case**: Trust graph visualization, social connections

---

## Performance Comparison

### Before (Multiple Calls)

**Get Profile View** - 7 separate calls:

```javascript
const avatar = await rpc.call("circles_getAvatarInfo", [address]) // 50ms
const profile = await rpc.call("circles_getProfileByAddress", [address]) // 100ms
const v1Balance = await rpc.call("circles_getTotalBalance", [address, 1]) // 200ms
const v2Balance = await rpc.call("circles_getTotalBalance", [address, 2]) // 200ms
const trusts = await rpc.call("circles_getTrustRelations", [address]) // 150ms
// Process trust stats...
// Total: ~700ms + network latency
```

### After (Single Call)

```javascript
const profileView = await rpc.call("circles_getProfileView", [address]) // 150ms
// Total: 150ms
```

**Improvement**: 78% faster (4.7x speedup)

---

## Error Responses

All methods return standard JSON-RPC error format:

**Invalid Parameters**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": "Invalid Ethereum address format"
  }
}
```

**Internal Error**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32603,
    "message": "Internal error",
    "data": "Database query failed"
  }
}
```

---

## SDK Usage Examples

### TypeScript/JavaScript

```typescript
import { CirclesRpc } from "@circles/rpc"

const rpc = new CirclesRpc("http://localhost:8081")

// Profile view
const profile = await rpc.getProfileView("0xde37...")
console.log(`${profile.profile.name}: ${profile.totalBalance} CRC`)

// Trust network
const network = await rpc.getTrustNetworkSummary("0xde37...", 2)
console.log(`Network reach: ${network.networkReach} people`)

// Paginated groups
let cursor = null
do {
  const page = await rpc.getGroupMemberships("0xde37...", 50, cursor)
  page.results.forEach((membership) => {
    console.log(`Member of: ${membership.groupInfo.name}`)
  })
  cursor = page.nextCursor
} while (page.hasMore)
```

### Python

```python
import requests

def rpc_call(method, params):
    response = requests.post('http://localhost:8081', json={
        'jsonrpc': '2.0',
        'id': 1,
        'method': method,
        'params': params
    })
    return response.json()['result']

# Profile view
profile = rpc_call('circles_getProfileView', ['0xde37...'])
print(f"{profile['profile']['name']}: {profile['totalBalance']} CRC")

# Valid inviters
inviters = rpc_call('circles_getValidInviters', ['0xde37...', '10.0'])
print(f"Found {inviters['count']} valid inviters")
```

---

## Migration Guide

### For SDK Developers

**Old Pattern** (Multiple calls):

```typescript
// OLD: 7 calls
const avatar = await getAvatarInfo(address)
const profile = await getProfileByAddress(address)
const v1Bal = await getTotalBalance(address, 1)
const v2Bal = await getTotalBalance(address, 2)
const trusts = await getTrustRelations(address)
const trustStats = calculateStats(trusts)
```

**New Pattern** (Single call):

```typescript
// NEW: 1 call
const view = await getProfileView(address)
// All data in view.avatarInfo, view.profile, view.trustStats, view.v1Balance, view.v2Balance
```

### For dApp Developers

**Update imports**:

```typescript
// Add new methods
import {
  getProfileView,
  getTrustNetworkSummary,
  getValidInviters,
  // ... etc
} from "@circles/sdk"
```

**Benefits**:

- Fewer network round-trips
- Lower latency
- Reduced server load
- Simpler code

---

## Support

- **Documentation**: `/docs/`
- **Issues**: [github.com/aboutcircles/circles-nethermind-plugin/issues](https://github.com/aboutcircles/circles-nethermind-plugin/issues)
- **SDK Repo**: `sdk-v2/`
