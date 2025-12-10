# Circles RPC Reference

Complete reference for all Circles JSON-RPC methods.

## Table of Contents

- [Circles RPC Reference](#circles-rpc-reference)
  - [Table of Contents](#table-of-contents)
  - [Overview](#overview)
  - [Cache Service Integration](#cache-service-integration)
  - [SDK Methods](#sdk-methods)
    - [circles\_getProfileView](#circles_getprofileview)
    - [circles\_getTrustNetworkSummary](#circles_gettrustnetworksummary)
    - [circles\_getAggregatedTrustRelationsEnriched](#circles_getaggregatedtrustrelationsenriched)
    - [circles\_getValidInviters](#circles_getvalidinviters)
    - [circles\_getTransactionHistoryEnriched](#circles_gettransactionhistoryenriched)
    - [circles\_searchProfileByAddressOrName](#circles_searchprofilebyaddressorname)
    - [circles\_getInvitationOrigin](#circles_getinvitationorigin)
  - [Avatar \& Profile Methods](#avatar--profile-methods)
    - [circles\_getAvatarInfo](#circles_getavatarinfo)
    - [circles\_getAvatarInfoBatch](#circles_getavatarinfobatch)
    - [circles\_getProfileByAddress](#circles_getprofilebyaddress)
    - [circles\_getProfileByAddressBatch](#circles_getprofilebyaddressbatch)
    - [circles\_getProfileByCid](#circles_getprofilebycid)
    - [circles\_getProfileByCidBatch](#circles_getprofilebycidbatch)
    - [circles\_searchProfiles](#circles_searchprofiles)
  - [Token \& Balance Methods](#token--balance-methods)
    - [circles\_getTotalBalance](#circles_gettotalbalance)
    - [circles\_getTokenBalances](#circles_gettokenbalances)
    - [circles\_getTokenInfo](#circles_gettokeninfo)
    - [circles\_getTokenInfoBatch](#circles_gettokeninfobatch)
    - [circles\_getTokenHolders](#circles_gettokenholders)
  - [Trust Relation Methods](#trust-relation-methods)
    - [circles\_getTrustRelations](#circles_gettrustrelations)
    - [circles\_getAggregatedTrustRelations](#circles_getaggregatedtrustrelations)
    - [circles\_getCommonTrust](#circles_getcommontrust)
  - [Group Methods](#group-methods)
    - [circles\_findGroups](#circles_findgroups)
    - [circles\_getGroupMembers](#circles_getgroupmembers)
    - [circles\_getGroupMemberships](#circles_getgroupmemberships)
  - [Event \& Query Methods](#event--query-methods)
    - [circles\_events](#circles_events)
    - [circles\_query](#circles_query)
    - [circles\_tables](#circles_tables)
  - [Transaction History](#transaction-history)
    - [circles\_getTransactionHistory](#circles_gettransactionhistory)
  - [Network Methods](#network-methods)
    - [circles\_getNetworkSnapshot](#circles_getnetworksnapshot)
  - [Subscriptions](#subscriptions)
    - [circles subscription](#circles-subscription)
  - [Pagination](#pagination)
    - [Cursor-Based Pagination](#cursor-based-pagination)
  - [Error Responses](#error-responses)

---

## Overview

The Circles RPC provides JSON-RPC 2.0 endpoints for querying Circles V1 and V2 protocol data. All methods use the standard JSON-RPC format:

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "METHOD_NAME",
    "params": [...]
  }'
```

**Public Endpoints:**
- Production: `https://rpc.aboutcircles.com/`
- Gnosis: `https://rpc.circlesubi.network/`

---

## Cache Service Integration

Several RPC methods leverage the Cache Service for faster responses. Enable with:

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

## SDK Methods

High-level methods optimized for common SDK use cases. These consolidate multiple queries into single calls.

### circles_getProfileView

Get complete profile view consolidating avatar info, profile data, trust stats, and balances in a single call. Replaces 6-7 separate RPC calls.

**Parameters:**
- `address` (string): Ethereum address

**Request:**

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

**Response:**

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

---

### circles_getTrustNetworkSummary

Get aggregated trust network metrics for the direct (depth-1) neighborhood.

**Parameters:**
- `address` (string): Ethereum address
- `maxDepth` (number, optional): Maximum traversal depth (default: 2, currently limited to 1)

**Request:**

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

**Response:**

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

---

### circles_getAggregatedTrustRelationsEnriched

Get categorized trust relations (mutual/trusts/trustedBy) with avatar metadata pre-loaded.

**Parameters:**
- `address` (string): Ethereum address

**Request:**

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

**Response:**

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

---

### circles_getValidInviters

Find addresses that trust you AND have sufficient balance to send an invitation.

**Parameters:**
- `address` (string): Ethereum address to find inviters for
- `minimumBalance` (string, optional): Minimum CRC balance required (stringified decimal)

**Request:**

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

**Response:**

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

---

### circles_getTransactionHistoryEnriched

Transaction history with participant profiles pre-loaded. Replaces `circles_events` + multiple `getProfileByAddress` calls.

**Parameters:**
- `address` (string): Ethereum address
- `fromBlock` (number): Starting block number (inclusive)
- `toBlock` (number, optional): Ending block number (default: latest)
- `limit` (number, optional): Maximum results (default: 20)
- `cursor` (string, optional): Base64 encoded pagination cursor

**Request:**

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

**Response:**

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
            "avatarInfo": { ... },
            "profile": {
              "name": "Bob",
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

---

### circles_searchProfileByAddressOrName

Unified search endpoint. If the query starts with `0x`, performs address-prefix lookup. Otherwise falls back to full-text profile search.

**Parameters:**
- `query` (string): Address prefix or free-form text
- `limit` (number, optional): Maximum results (default: 10)
- `offset` (number, optional): Offset for text search (default: 0)
- `types` (string[], optional): Restrict to avatar types

**Request:**

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

**Response:**

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
      }
    ],
    "totalCount": 12
  }
}
```

---

### circles_getInvitationOrigin

Reconstructs how a user was invited to Circles by checking multiple invitation mechanisms.

**Parameters:**
- `address` (string): Ethereum address to query

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getInvitationOrigin",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"]
  }'
```

**Response (V2 Escrow Invitation):**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "invitationType": "v2_escrow",
    "inviter": "0x1234567890abcdef1234567890abcdef12345678",
    "proxyInviter": null,
    "escrowAmount": "100000000000000000000",
    "blockNumber": 36500000,
    "timestamp": 1704240000,
    "transactionHash": "0xabc123...",
    "version": 2
  }
}
```

**Response (V2 At Scale Invitation):**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "invitationType": "v2_at_scale",
    "inviter": "0xoriginInviter...",
    "proxyInviter": "0xproxyInviter...",
    "escrowAmount": null,
    "blockNumber": 36500000,
    "timestamp": 1704240000,
    "transactionHash": "0xabc123...",
    "version": 2
  }
}
```

**Response (V1 Self-Signup):**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "invitationType": "v1_signup",
    "inviter": null,
    "proxyInviter": null,
    "escrowAmount": null,
    "blockNumber": 25000000,
    "timestamp": 1624240000,
    "transactionHash": "0xdef456...",
    "version": 1
  }
}
```

**Response (Not Registered):**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": null
}
```

**Invitation Types:**

| Type | Description | Version |
|------|-------------|---------|
| `v1_signup` | V1 self-signup (no inviter, legacy) | 1 |
| `v2_standard` | V2 direct invitation via human trust | 2 |
| `v2_escrow` | V2 invitation with CRC token escrow | 2 |
| `v2_at_scale` | V2 scalable invitation system with origin + proxy inviters | 2 |

**Field Descriptions:**
- `address`: The queried avatar address
- `invitationType`: Type of invitation mechanism used (see table above)
- `inviter`: Address of the inviting avatar (null for v1_signup)
- `proxyInviter`: Proxy inviter address (only set for v2_at_scale)
- `escrowAmount`: Escrowed CRC amount in atto-circles (only set for v2_escrow)
- `blockNumber`: Block number when the invitation was recorded
- `timestamp`: Unix timestamp of the invitation
- `transactionHash`: Transaction hash of the invitation event
- `version`: Circles version (1 or 2)

---

## Avatar & Profile Methods

### circles_getAvatarInfo

Get avatar information for a single address. Returns both V1 and V2 avatar data.

**Parameters:**
- `address` (string): Ethereum address

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getAvatarInfo",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id": 1
  }'
```

**Response (V2 Avatar):**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "version": 2,
    "type": "CrcV2_RegisterHuman",
    "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "tokenId": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "hasV1": true,
    "v1Token": "0x1234567890abcdef1234567890abcdef12345678",
    "cidV0Digest": "",
    "cidV0": "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG",
    "isHuman": true,
    "name": "Alice",
    "symbol": "",
    "shortName": "alice"
  },
  "id": 1
}
```

**Response (V1-Only Avatar):**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "version": 1,
    "type": "CrcV1_Signup",
    "avatar": "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
    "tokenId": "0xabcdef1234567890abcdef1234567890abcdef12",
    "hasV1": true,
    "v1Token": "0xabcdef1234567890abcdef1234567890abcdef12",
    "cidV0Digest": "",
    "cidV0": "QmXwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG",
    "isHuman": true,
    "name": null,
    "symbol": "",
    "shortName": null
  },
  "id": 1
}
```

**Field Descriptions:**
- `version`: 1 for V1 avatars, 2 for V2 avatars
- `type`: Event type (`CrcV1_Signup`, `CrcV2_RegisterHuman`, `CrcV2_RegisterGroup`)
- `avatar`: Avatar address
- `tokenId`: For V1, the token address; for V2, the avatar address (ERC-1155)
- `hasV1`: Whether this address has a V1 signup
- `v1Token`: V1 token address (null if no V1 signup)
- `cidV0`: IPFS CID for profile metadata
- `isHuman`: Whether avatar is human (true) or group/organization (false)
- `name`: Avatar name (V2 only)
- `shortName`: Short name for V2 avatars

---

### circles_getAvatarInfoBatch

Get avatar information for multiple addresses.

**Parameters:**
- `addresses` (string[]): Array of Ethereum addresses (max 1000)

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getAvatarInfoBatch",
    "params": [
      ["0xde374ece6fa50e781e81aac78e811b33d16912c7", "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"]
    ],
    "id": 1
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": [
    {
      "version": 2,
      "type": "CrcV2_RegisterHuman",
      "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      ...
    },
    {
      "version": 1,
      "type": "CrcV1_Signup",
      "avatar": "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
      ...
    }
  ],
  "id": 1
}
```

---

### circles_getProfileByAddress

Get enriched profile information for an address. Combines IPFS profile data with on-chain metadata.

**Parameters:**
- `address` (string): Ethereum address

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getProfileByAddress",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id": 1
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "name": "Alice",
    "description": "Circles user from Berlin",
    "imageUrl": "https://example.com/avatar.jpg",
    "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "shortName": "alice",
    "avatarType": "CrcV2_RegisterHuman",
    "CID": "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG"
  },
  "id": 1
}
```

---

### circles_getProfileByAddressBatch

Get profiles for multiple addresses in batch.

**Parameters:**
- `addresses` (string[]): Array of Ethereum addresses (max 1000)

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getProfileByAddressBatch",
    "params": [
      ["0xde374ece6fa50e781e81aac78e811b33d16912c7", "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"]
    ],
    "id": 1
  }'
```

---

### circles_getProfileByCid

Get a profile by its IPFS CID.

**Parameters:**
- `cid` (string): IPFS CID (e.g., `Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W`)

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getProfileByCid",
    "params": ["Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W"],
    "id": 1
  }'
```

---

### circles_getProfileByCidBatch

Get multiple profiles by CID.

**Parameters:**
- `cids` (string[]): Array of IPFS CIDs (nulls allowed for sparse lookups)

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getProfileByCidBatch",
    "params": [["Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W", null, "QmZuR1Jkhs9RLXVY28eTTRSnqbxLTBSoggp18Yde858xCM"]],
    "id": 1
  }'
```

---

### circles_searchProfiles

Search profiles by name, description, or address.

**Parameters:**
- `query` (string): Search query
- `limit` (number, optional): Maximum results (default: 10)
- `offset` (number, optional): Pagination offset (default: 0)

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_searchProfiles",
    "params": ["alice", 10, 0]
  }'
```

---

## Token & Balance Methods

### circles_getTotalBalance

Get the total Circles balance of an address.

**Parameters:**
- `address` (string): Ethereum address
- `version` (number, optional): 1 for V1 only, 2 for V2 only, omit for combined

**Request (V1):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTotalBalance",
    "params": ["0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c"],
    "id": 1
  }'
```

**Request (V2):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circlesV2_getTotalBalance",
    "params": ["0xcadd4ea3bcc361fc4af2387937d7417be8d7dfc2"],
    "id": 1
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": "5444258229585459544466",
  "id": 1
}
```

---

### circles_getTokenBalances

Get all individual token balances for an address.

**Parameters:**
- `address` (string): Ethereum address

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTokenBalances",
    "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id": 1
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": [
    {
      "tokenAddress": "0x1234567890abcdef1234567890abcdef12345678",
      "tokenId": "0x1234567890abcdef1234567890abcdef12345678",
      "tokenOwner": "0xabcdef1234567890abcdef1234567890abcdef12",
      "tokenType": "CrcV1_Signup",
      "version": 1,
      "attoCircles": "1000000000000000000",
      "circles": 1.0,
      "staticAttoCircles": "1000000000000000000",
      "staticCircles": 1.0,
      "attoCrc": "1000000000000000000",
      "crc": 1.0,
      "isErc20": true,
      "isErc1155": false,
      "isWrapped": false,
      "isInflationary": true,
      "isGroup": false
    },
    {
      "tokenAddress": "0x9876543210abcdef9876543210abcdef98765432",
      "tokenId": "123456789012345678901234567890",
      "tokenOwner": "0x9876543210abcdef9876543210abcdef98765432",
      "tokenType": "CrcV2_RegisterHuman",
      "version": 2,
      "attoCircles": "500000000000000000",
      "circles": 0.5,
      "staticAttoCircles": "500000000000000000",
      "staticCircles": 0.5,
      "attoCrc": "500000000000000000",
      "crc": 0.5,
      "isErc20": false,
      "isErc1155": true,
      "isWrapped": false,
      "isInflationary": false,
      "isGroup": false
    }
  ],
  "id": 1
}
```

**Field Descriptions:**
- `tokenAddress`: Address of the token contract
- `tokenId`: For V1, same as tokenAddress; for V2 ERC-1155, uint256 token ID
- `tokenOwner`: Avatar that owns the token
- `tokenType`: Type (`CrcV1_Signup`, `CrcV2_RegisterHuman`, `CrcV2_RegisterGroup`, `CrcV2_ERC20WrapperDeployed_Inflationary`, `CrcV2_ERC20WrapperDeployed_Demurraged`)
- `version`: 1 for V1, 2 for V2
- `attoCircles`: Balance in atto-circles (10^-18 circles)
- `circles`: Balance in circles (decimal)
- `isErc20`: Whether token is ERC-20
- `isErc1155`: Whether token is ERC-1155
- `isWrapped`: Whether token is a V2 wrapped token
- `isInflationary`: Whether token is inflationary
- `isGroup`: Whether token belongs to a group

---

### circles_getTokenInfo

Get information about a specific token.

**Parameters:**
- `tokenAddress` (string): Token contract address

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTokenInfo",
    "params": ["0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e"],
    "id": 1
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "token": "0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e",
    "tokenOwner": "0x1f689e9a6f075dac566d5c93419a3fd372dee154",
    "version": 2,
    "type": "Avatar",
    "isErc20": true,
    "isErc1155": false,
    "isWrapped": false,
    "isInflationary": false,
    "isGroup": false
  },
  "id": 1
}
```

---

### circles_getTokenInfoBatch

Get information about multiple tokens.

**Parameters:**
- `tokenAddresses` (string[]): Array of token addresses

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTokenInfoBatch",
    "params": [
      ["0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e", "0x42cedde51198d1773590311e340dc06b24cb37"]
    ],
    "id": 1
  }'
```

---

### circles_getTokenHolders

Get paginated list of token holders for a specific token.

**Parameters:**
- `tokenAddress` (string): Token contract address or token ID
- `limit` (number, optional): Results per page (default: 100, max: 1000)
- `cursor` (string, optional): Last account address returned

**Request:**

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

**Response:**

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
      }
    ],
    "hasMore": true,
    "nextCursor": "0x1234567890abcdef1234567890abcdef12345678"
  }
}
```

---

## Trust Relation Methods

### circles_getTrustRelations

Get all trust relations for an address.

**Parameters:**
- `address` (string): Ethereum address

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTrustRelations",
    "params": ["0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c"],
    "id": 1
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "user": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "trusts": {
      "0xb7c83b840e146f9768a1fdc4ce46c8ad17594720": 100,
      "0x5fd8f7464c050ec0fb34223aab544e13510812fa": 50
    },
    "trustedBy": {
      "0x965090908dcd0b134802f35c9138a7e987b5182f": 100,
      "0x5ce3d708a2b1e8371530754c930fc9b5bad27ab7": 100
    }
  },
  "id": 1
}
```

---

### circles_getAggregatedTrustRelations

Get trust relations categorized by type (mutual, outgoing, incoming).

**Parameters:**
- `avatar` (string): Ethereum address

**Request:**

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

**Response:**

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

**Relation Types:**
- `mutuallyTrusts`: Bidirectional trust
- `trusts`: Subject trusts object (outgoing)
- `trustedBy`: Object trusts subject (incoming)

---

### circles_getCommonTrust

Query the common trust relations of two addresses.

**Parameters:**
- `address1` (string): First Ethereum address
- `address2` (string): Second Ethereum address

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_getCommonTrust",
    "params": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"
    ]
  }'
```

---

## Group Methods

### circles_findGroups

Discover registered Circles groups with optional filters and cursor-based pagination.

**Parameters:**
- `limit` (number, optional): Results per page (default: 50)
- `queryParams` (object, optional): Filters
  - `nameStartsWith` (string, optional)
  - `symbolStartsWith` (string, optional)
  - `ownerIn` (string[], optional): Match by `mint` address
- `cursor` (string, optional): Base64 encoded cursor

**Request:**

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

**Response:**

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

---

### circles_getGroupMembers

Get paginated list of group members.

**Parameters:**
- `groupAddress` (string): Group contract address
- `limit` (number, optional): Results per page (default: 100, max: 1000)
- `cursor` (string, optional): Base64 encoded cursor

**Request:**

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

**Response:**

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

---

### circles_getGroupMemberships

Get paginated list of groups a member belongs to.

**Parameters:**
- `memberAddress` (string): Member's Ethereum address
- `limit` (number, optional): Results per page (default: 50, max: 1000)
- `cursor` (string, optional): Base64 encoded cursor

**Request:**

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

**Response:**

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

---

## Event & Query Methods

### circles_events

Query all events that involve a specific address. Returns paginated results with cursor-based navigation.

**Signature:** `circles_events(address, fromBlock, toBlock?, eventTypes?, filterPredicates?, sortAscending?, limit?, cursor?)`

**Parameters:**

1. `address` (string, optional): Filter by address (null for all addresses)
2. `fromBlock` (number, optional): Starting block number (inclusive)
3. `toBlock` (number, optional): Ending block number (null for latest)
4. `eventTypes` (string[], optional): Filter by event types
5. `filterPredicates` (FilterPredicate[], optional): Advanced filters
6. `sortAscending` (boolean, optional): Sort order (default: false = descending)
7. `limit` (number, optional): Maximum events to return (default: 100, max: 1000)
8. `cursor` (string, optional): Base64 encoded pagination cursor from previous response

**Request (Basic):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_events",
    "params": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      30282299,
      null
    ]
  }'
```

**Request (With Event Type Filter):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_events",
    "params": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      38000000,
      null,
      ["CrcV1_Trust"],
      null,
      false
    ]
  }'
```

**Request (With Pagination):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_events",
    "params": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      30282299,
      null,
      null,
      null,
      false,
      50,
      null
    ]
  }'
```

**Request (Next Page):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_events",
    "params": [
      "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      30282299,
      null,
      null,
      null,
      false,
      50,
      "MzAyODIyOTk6MDoy"
    ]
  }'
```

**Request (With Filter Predicates):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_events",
    "params": [
      null,
      38000000,
      39000000,
      ["CrcV1_Transfer"],
      [
        {
          "column": "amount",
          "filterType": "GreaterThan",
          "value": "1000000000000000000"
        }
      ],
      false,
      100,
      null
    ]
  }'
```

**Supported Event Types:**
- V1: `CrcV1_HubTransfer`, `CrcV1_OrganizationSignup`, `CrcV1_Signup`, `CrcV1_Transfer`, `CrcV1_Trust`
- V2: `CrcV2_ApprovalForAll`, `CrcV2_PersonalMint`, `CrcV2_RegisterGroup`, `CrcV2_RegisterHuman`, `CrcV2_RegisterOrganization`, `CrcV2_RegisterShortName`, `CrcV2_Stopped`, `CrcV2_TransferBatch`, `CrcV2_TransferSingle`, `CrcV2_Trust`, `CrcV2_UpdateMetadataDigest`, `CrcV2_URI`

**Supported FilterTypes:**
- `Equals`, `NotEquals`
- `GreaterThan`, `GreaterThanOrEquals`, `LessThan`, `LessThanOrEquals`
- `Like`, `ILike`, `NotLike`
- `In`, `NotIn`
- `IsNull`, `IsNotNull`

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "events": [
      {
        "event": "CrcV1_Trust",
        "values": {
          "blockNumber": 30282299,
          "timestamp": 1715978910,
          "transactionIndex": 0,
          "logIndex": 2,
          "transactionHash": "0x9d5e2ac..."
        }
      }
    ],
    "hasMore": true,
    "nextCursor": "MzAyODIyOTk6MDoy"
  },
  "id": 1
}
```

**Response Fields:**

- `events`: Array of event objects
- `hasMore`: Boolean indicating if more results are available
- `nextCursor`: Base64 encoded cursor for fetching the next page (null if no more results)

**Cursor Format:**
The cursor is a Base64 encoded string of `blockNumber:transactionIndex:logIndex`. Treat it as opaque and pass it unchanged to fetch subsequent pages.

---

### circles_query

Low-level query interface for direct table access.

**Parameters:**
- Query object with:
  - `Namespace` (string): Table namespace (`V_Crc`, `V_CrcV1`, `V_CrcV2`, `CrcV1`, `CrcV2`)
  - `Table` (string): Table name
  - `Columns` (string[]): Columns to return (empty for all)
  - `Filter` (array): Filter predicates
  - `Order` (array, optional): Sort order
  - `Limit` (number, optional): Max rows

**Request (Get Avatars):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_query",
    "params": [
      {
        "Namespace": "V_Crc",
        "Table": "Avatars",
        "Limit": 10,
        "Columns": [],
        "Filter": [],
        "Order": [
          {"Column": "blockNumber", "SortOrder": "DESC"},
          {"Column": "transactionIndex", "SortOrder": "DESC"},
          {"Column": "logIndex", "SortOrder": "DESC"}
        ]
      }
    ]
  }'
```

**Request (Trust Relations with Filter):**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_query",
    "params": [
      {
        "Namespace": "V_Crc",
        "Table": "TrustRelations",
        "Columns": [],
        "Filter": [
          {
            "Type": "Conjunction",
            "ConjunctionType": "Or",
            "Predicates": [
              {
                "Type": "FilterPredicate",
                "FilterType": "Equals",
                "Column": "truster",
                "Value": "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565"
              },
              {
                "Type": "FilterPredicate",
                "FilterType": "Equals",
                "Column": "trustee",
                "Value": "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565"
              }
            ]
          }
        ],
        "Order": [
          {"Column": "blockNumber", "SortOrder": "DESC"}
        ]
      }
    ]
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "result": {
    "Columns": ["blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash", "..."],
    "Rows": [
      [9833016, 1715978910, 0, 2, "0x9d5e2ac...", "..."]
    ]
  },
  "id": 1
}
```

---

### circles_tables

Return all available namespaces and tables queryable by `circles_query`.

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 0,
    "method": "circles_tables",
    "params": []
  }'
```

---

## Transaction History

### circles_getTransactionHistory

Get paginated transaction history with cursor-based navigation.

**Parameters:**
- `avatarAddress` (string): Ethereum address
- `limit` (number, optional): Results per page (default: 50, max: 1000)
- `cursor` (string, optional): Base64 encoded cursor

**Request:**

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

**Response:**

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

---

## Network Methods

### circles_getNetworkSnapshot

Download a full snapshot of the Circles network state (current trust relations and balances).

**Request:**

```bash
curl -X POST http://localhost:8081 \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getNetworkSnapshot",
    "params": [],
    "id": 1
  }'
```

---

## Subscriptions

### circles subscription

WebSocket subscription for real-time event updates.

```javascript
// Example WebSocket subscription
const ws = new WebSocket('wss://rpc.aboutcircles.com/');
ws.send(JSON.stringify({
  jsonrpc: '2.0',
  id: 1,
  method: 'circles_subscribe',
  params: ['events', { address: '0xde374ece6fa50e781e81aac78e811b33d16912c7' }]
}));
```

---

## Pagination

### Cursor-Based Pagination

Most list endpoints use cursor-based pagination for efficient traversal of large datasets.

**Pattern:**

```bash
# First page
curl -X POST ... -d '{"params": ["0x...", 50, null]}'

# Response includes nextCursor
# {"result": {"results": [...], "hasMore": true, "nextCursor": "MzY1MDAwMDA6MTA6Mw=="}}

# Next page - pass nextCursor
curl -X POST ... -d '{"params": ["0x...", 50, "MzY1MDAwMDA6MTA6Mw=="]}'
```

**Cursor Formats:**

- Most methods: Base64 encoded `blockNumber:transactionIndex:logIndex`
- `circles_events`: Base64 encoded `blockNumber:transactionIndex:logIndex`
- `circles_getTransactionHistory`: Base64 encoded `blockNumber:transactionIndex:logIndex:batchIndex`
- `circles_getTokenHolders`: Raw account address string

---

## Error Responses

All methods return standard JSON-RPC error format:

**Invalid Parameters:**

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

**Internal Error:**

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
