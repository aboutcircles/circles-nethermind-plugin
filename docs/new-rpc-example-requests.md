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
  - [Paginated Query Methods](#paginated-query-methods)
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

## SDK Enablement Methods

### circles_getProfileView

**Description**: Get complete profile view consolidating avatar info, profile data, trust stats, and balances in a single call. Replaces 6-7 separate RPC calls.

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
      "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "version": 2,
      "type": "Human",
      "tokenId": "123456789012345678901234567890",
      "hasV1": true,
      "v1Token": "0x42cedde51198d1773590311e2a340dc06b24cb37",
      "cidV0": "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
      "isHuman": true,
      "registeredAt": 1704067200
    },
    "profile": {
      "name": "Alice",
      "description": "Community organizer",
      "avatarUrl": "ipfs://Qm...",
      "previewImageUrl": "ipfs://Qm..."
    },
    "trustStats": {
      "trustsCount": 42,
      "trustedByCount": 38,
      "mutualTrustsCount": 35
    },
    "v1Balance": "150.25",
    "v2Balance": "1250.75",
    "totalBalance": "1401.00"
  }
}
```

**Use Case**: Dashboard loading, profile pages

---

### circles_getTrustNetworkSummary

**Description**: Get aggregated trust network metrics with configurable depth traversal. Server-side graph analysis.

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
    "networkReach": 156,
    "maxDepth": 2,
    "trustDistribution": {
      "depth1": 42,
      "depth2": 114
    }
  }
}
```

**Use Case**: Network visualization, trust graph analysis

---

### circles_getAggregatedTrustRelationsEnriched

**Description**: Get categorized trust relations (mutual/trusts/trustedBy) with full avatar info pre-loaded. Enhanced version of `circles_getAggregatedTrustRelations`.

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
          "avatar": "0x1234567890abcdef1234567890abcdef12345678",
          "type": "Human",
          "cidV0": "QmABC..."
        },
        "profile": {
          "name": "Bob",
          "avatarUrl": "ipfs://..."
        },
        "timestamp": 1704067200,
        "expiryTime": 1735689600
      }
    ],
    "trusts": [
      {
        "address": "0xabcdef1234567890abcdef1234567890abcdef12",
        "avatarInfo": {...},
        "profile": {...},
        "timestamp": 1704153600
      }
    ],
    "trustedBy": [
      {
        "address": "0x9876543210fedcba9876543210fedcba98765432",
        "avatarInfo": {...},
        "profile": {...},
        "timestamp": 1704240000
      }
    ]
  }
}
```

**Use Case**: Trust list UI with avatars/names

---

### circles_getValidInviters

**Description**: Find addresses that trust you AND have sufficient balance to send an invitation. Balance-filtered inviter discovery.

**Parameters**:

- `address` (string): Ethereum address to find inviters for
- `minimumBalance` (string, optional): Minimum CRC balance required (default: "1.0")

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
    "validInviters": [
      {
        "address": "0x1234567890abcdef1234567890abcdef12345678",
        "totalBalance": "150.25",
        "v1Balance": "50.25",
        "v2Balance": "100.00",
        "avatarInfo": {
          "type": "Human",
          "cidV0": "QmABC..."
        },
        "profile": {
          "name": "Bob"
        }
      },
      {
        "address": "0xabcdef1234567890abcdef1234567890abcdef12",
        "totalBalance": "75.50",
        "v1Balance": "75.50",
        "v2Balance": "0.00",
        "avatarInfo": {...},
        "profile": {...}
      }
    ],
    "count": 2,
    "minimumBalance": "10.0"
  }
}
```

**Use Case**: Invitation flows, sponsor discovery

---

### circles_getTransactionHistoryEnriched

**Description**: Transaction history with participant profiles pre-loaded. Replaces `circles_events` + multiple `getProfileByAddress` calls.

**Parameters**:

- `address` (string): Ethereum address
- `fromBlock` (number, optional): Starting block number
- `toBlock` (number, optional): Ending block number (default: latest)
- `limit` (number, optional): Maximum results (default: 50)

**Request**:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getTransactionHistoryEnriched",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 36000000, null, 10]
  }'
```

**Response**:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "transactions": [
      {
        "blockNumber": 36500000,
        "timestamp": 1704240000,
        "transactionHash": "0xabc123...",
        "from": "0x1234567890abcdef1234567890abcdef12345678",
        "to": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "value": "50.0",
        "tokenId": "123456789012345678901234567890",
        "fromProfile": {
          "address": "0x1234567890abcdef1234567890abcdef12345678",
          "name": "Bob",
          "avatarUrl": "ipfs://...",
          "type": "Human"
        },
        "toProfile": {
          "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          "name": "Alice",
          "avatarUrl": "ipfs://...",
          "type": "Human"
        },
        "direction": "incoming"
      }
    ],
    "hasMore": true,
    "fromBlock": 36000000,
    "toBlock": 36789012
  }
}
```

**Use Case**: Transaction history UI with sender/receiver info

---

## Paginated Query Methods

### circles_getGroupMembers

**Description**: Get paginated list of group members with cursor-based navigation.

**Parameters**:

- `groupAddress` (string): Group contract address
- `limit` (number, optional): Results per page (default: 100, max: 1000)
- `cursor` (string, optional): Pagination cursor from previous response

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
        "group": "0x1234567890abcdef1234567890abcdef12345678",
        "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "memberType": "Human",
        "expiryTime": 1735689600,
        "timestamp": 1704067200,
        "blockNumber": 36000000
      },
      {
        "group": "0x1234567890abcdef1234567890abcdef12345678",
        "member": "0xabcdef1234567890abcdef1234567890abcdef12",
        "memberType": "Human",
        "expiryTime": 1735689600,
        "timestamp": 1704153600,
        "blockNumber": 36050000
      }
    ],
    "hasMore": true,
    "nextCursor": "eyJibG9ja051bWJlciI6MzYwNTAwMDAsInR4SW5kZXgiOjUsImxvZ0luZGV4IjoxfQ=="
  }
}
```

**Cursor Format**: Base64-encoded JSON: `{"blockNumber":36050000,"txIndex":5,"logIndex":1}`

**Use Case**: Group member lists, pagination

---

### circles_getGroupMemberships

**Description**: Get paginated list of groups a member belongs to (inverse of getGroupMembers).

**Parameters**:

- `memberAddress` (string): Member's Ethereum address
- `limit` (number, optional): Results per page (default: 50, max: 1000)
- `cursor` (string, optional): Pagination cursor

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
        "group": "0x1234567890abcdef1234567890abcdef12345678",
        "member": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "expiryTime": 1735689600,
        "timestamp": 1704067200,
        "groupInfo": {
          "name": "Community DAO",
          "symbol": "CDAO"
        }
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
- `cursor` (string, optional): Pagination cursor

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
        "transactionHash": "0xabc123...",
        "from": "0x1234567890abcdef1234567890abcdef12345678",
        "to": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "value": "50000000000000000000",
        "circles": 50.0,
        "attoCircles": "50000000000000000000",
        "crc": 50.0,
        "attoCrc": "50000000000000000000",
        "staticCircles": 60.0,
        "staticAttoCircles": "60000000000000000000"
      }
    ],
    "hasMore": true,
    "nextCursor": "eyJibG9ja051bWJlciI6MzY1MDAwMDAsInR4SW5kZXgiOjEwLCJsb2dJbmRleCI6M30="
  }
}
```

**Use Case**: Transaction lists, infinite scroll

---

### circles_getTokenHolders

**Description**: Get paginated list of token holders for a specific token.

**Parameters**:

- `tokenAddress` (string): Token contract address or token ID
- `limit` (number, optional): Results per page (default: 100, max: 1000)
- `cursor` (string, optional): Pagination cursor

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
        "balance": "150.25",
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
    "nextCursor": "eyJhY2NvdW50IjoiMHgxMjM0NTY3ODkwYWJjZGVmIn0="
  }
}
```

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
- **Issues**: https://github.com/aboutcircles/circles-nethermind-plugin/issues
- **SDK Repo**: `sdk-v2/`
