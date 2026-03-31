# Circles RPC Host

JSON-RPC 2.0 service exposing Circles protocol data: balances, avatars, profiles, trust networks, events, groups, invitations, and transitive transfer paths.

**45 Circles methods** + transparent proxy for standard Ethereum RPC (`eth_*`, `net_*`, `web3_*`).

| Endpoint | Purpose |
|----------|---------|
| `POST /` | JSON-RPC 2.0 (single & batch) |
| `GET /ws` or `GET /ws/subscribe` | WebSocket event subscriptions |
| `GET /docs` | API documentation portal |
| `GET /openrpc.json` | Machine-readable OpenRPC spec |
| `GET /openrpc` | Interactive playground (CirclesTools) |
| `GET /live` | Liveness probe |
| `GET /ready` | Readiness probe (DB + Nethermind sync + Pathfinder + Indexer lag) |
| `GET /health` | Nethermind connectivity check |
| `GET /metrics` | Prometheus metrics |

## Quick Start

```bash
# Using script (recommended)
./scripts/run-rpc.sh

# Or directly
cd src/Rpc/Circles.Rpc.Host && dotnet run
```

Default: `http://localhost:8081`

## Configuration

### Required Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `POSTGRES_READONLY_CONNECTION_STRING` | — | PostgreSQL connection string for indexed Circles data |
| `NETHERMIND_RPC_URL` | `http://localhost:8545` | Nethermind JSON-RPC endpoint |

### Optional Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `BALANCE_MODE` | `live` | `"live"` (eth_call, accurate) or `"database"` (fast, may be stale) |
| `DATABASE_QUERY_TIMEOUT_SECONDS` | `30` | Timeout for general DB queries |
| `PROFILE_SEARCH_TIMEOUT_SECONDS` | `30` | Timeout for profile search queries |
| `EXTERNAL_PATHFINDER_URL` | — | Pathfinder REST API URL (for `circlesV2_findPath`, `circles_getNetworkSnapshot`) |
| `USE_CACHE_SERVICE` | `false` | Enable Cache Service for balance/avatar/profile queries |
| `CACHE_SERVICE_URL` | — | Cache Service endpoint (required if `USE_CACHE_SERVICE=true`) |
| `PROFILE_PINNING_SERVICE_URL` | — | Profile pinning service (fast profile search proxy, falls back to SQL) |
| `CORS_ALLOWED_ORIGINS` | `*` | Comma-separated origins, or `*` for all |
| `RPC_MAX_CONCURRENT_REQUESTS` | `max(CPU×4, 32)` | Concurrency semaphore limit; excess returns 503 |
| `RPC_RATE_LIMIT_PER_SECOND` | `100` | Per-IP sustained rate (0 = disabled); batch items count individually |
| `RPC_RATE_LIMIT_BURST` | `200` | Per-IP burst allowance before throttling kicks in |
| `INDEXER_MAX_LAG_BLOCKS` | `100` | Max block lag before `/ready` returns 503 (0 = disabled) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | OpenTelemetry OTLP endpoint (enables distributed tracing) |

### Balance Modes

| Mode | Accuracy | Speed | Requires |
|------|----------|-------|----------|
| `live` (default) | Current block state | Slower | Nethermind RPC |
| `database` | Indexed (may lag) | Fast | PostgreSQL only |

## JSON-RPC 2.0

### Request Format

```json
{
  "jsonrpc": "2.0",
  "method": "circles_getTotalBalance",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", true],
  "id": 1
}
```

### Response Format

**Success:**

```json
{
  "jsonrpc": "2.0",
  "result": "142.87",
  "id": 1
}
```

**Error:**

```json
{
  "jsonrpc": "2.0",
  "error": { "code": -32602, "message": "Invalid params: Address parameter is required" },
  "id": 1
}
```

### Error Codes

| Code | Meaning | HTTP Status |
|------|---------|-------------|
| `-32700` | Parse error (malformed JSON) | 400 |
| `-32600` | Invalid Request (missing jsonrpc/method, batch too large/empty) | 400 |
| `-32601` | Method not found | 200 |
| `-32602` | Invalid params (wrong types, missing required) | 200 |
| `-32603` | Internal server error / proxy error | 200/500 |
| `-32000` | Server busy (concurrency limit) | 503 |
| `-32000` | Rate limit exceeded | 429 |

### Batch Requests

Send a JSON array of requests. All responses are returned in a single JSON array.

**Limits:**
- Max **50 items** per batch
- Max **1 MB** request body
- Empty arrays are rejected (JSON-RPC 2.0 spec)
- Each item costs 1 rate-limit token (50-item batch = 50 tokens)

**Routing:** Circles methods (`circles_*`, `circlesV2_*`) are handled locally; Ethereum methods (`eth_*`, `net_*`, `web3_*`) are batched and proxied to Nethermind in a single call. Unknown methods return `-32601`.

```bash
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '[
  {"jsonrpc":"2.0","method":"circles_getTotalBalance","params":["0xde374ece6fa50e781e81aac78e811b33d16912c7",true],"id":1},
  {"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":2}
]'
```

### Rate Limiting & Concurrency

**Rate limiting** — per-IP token bucket:
- Sustained: `RPC_RATE_LIMIT_PER_SECOND` tokens/sec (default 100)
- Burst: `RPC_RATE_LIMIT_BURST` (default 200)
- Batch items count individually
- Returns HTTP 429 + `{"code": -32000, "message": "Rate limit exceeded"}`
- Stale per-IP buckets evicted after 5 minutes of inactivity
- Set `RPC_RATE_LIMIT_PER_SECOND=0` to disable

**Concurrency** — semaphore guard:
- Max `RPC_MAX_CONCURRENT_REQUESTS` in-flight Circles requests
- Returns HTTP 503 + `{"code": -32000, "message": "Server busy"}`
- Non-blocking: rejects immediately, no queuing

### Ethereum Proxy

Standard Ethereum JSON-RPC methods are transparently proxied to Nethermind:

| Prefix | Example Methods |
|--------|----------------|
| `eth_*` | `eth_blockNumber`, `eth_getBalance`, `eth_call`, `eth_sendRawTransaction` |
| `net_*` | `net_version`, `net_peerCount` |
| `web3_*` | `web3_clientVersion` |

Blocked: `admin_*`, `debug_*`, `personal_*`, `miner_*` (security).

---

## WebSocket Subscriptions

Real-time event streaming via WebSocket. The server listens on PostgreSQL `circles_events` NOTIFY channel and pushes matching events to connected clients.

### Endpoints

- `ws://localhost:8081/ws`
- `ws://localhost:8081/ws/subscribe`

### Subscribe

Send a JSON-RPC 2.0 message after connecting:

```json
{
  "jsonrpc": "2.0",
  "method": "circles_subscribe",
  "params": { "address": "0xde374ece6fa50e781e81aac78e811b33d16912c7" },
  "id": 1
}
```

- `params.address` (optional): filter events containing this address. Omit or `null` for all events.
- Also accepts `"method": "eth_subscribe"` for compatibility (mapped internally to `circles_subscribe`).

### Acknowledgement

```json
{
  "jsonrpc": "2.0",
  "result": { "subscriptionId": "a1b2c3..." },
  "id": 1
}
```

### Event Push

```json
{
  "jsonrpc": "2.0",
  "method": "circles_subscription",
  "params": { "result": [ ...events... ] }
}
```

Events are the same format as `circles_events` responses. Pushes occur per block range as the indexer processes new blocks.

### JavaScript Example

```javascript
const ws = new WebSocket("ws://localhost:8081/ws");
ws.onopen = () => {
  ws.send(JSON.stringify({
    jsonrpc: "2.0",
    method: "circles_subscribe",
    params: { address: "0xde374ece6fa50e781e81aac78e811b33d16912c7" },
    id: 1
  }));
};
ws.onmessage = (event) => {
  const data = JSON.parse(event.data);
  if (data.method === "circles_subscription") {
    console.log("New events:", data.params.result);
  }
};
```

---

## RPC Methods Reference

### Discovery

#### `rpc.discover`

Returns the OpenRPC 1.3.2 specification document describing all available methods, parameters, and schemas.

**Parameters:** None

**Returns:** OpenRPC document (JSON object)

---

### Balance & Token Methods

#### `circles_getTotalBalance`

Get total V1 Circles balance for an address.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | address | string | yes | — | Ethereum address |
| 1 | asTimeCircles | boolean | no | true | Format as TimeCircles (adjusts for inflation) |

**Returns:** `string` — total balance

```bash
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circles_getTotalBalance",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", true]
}'
```

#### `circlesV2_getTotalBalance`

Get total V2 Circles balance for an address. V2 uses ERC-1155 tokens with ~7%/year demurrage.

**Parameters:** Same as `circles_getTotalBalance`.

**Returns:** `string` — total V2 balance

#### `circles_getTokenBalances`

Get all individual token balances for an address (V1 + V2, personal + group + wrapped).

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | address | string | yes |

**Returns:** `CirclesTokenBalance[]`

```typescript
interface CirclesTokenBalance {
  tokenAddress: string;
  tokenId: string;
  tokenOwner: string;
  tokenType: string;     // "CrcV1_Signup" | "CrcV2_RegisterHuman" | "CrcV2_RegisterGroup" | ...
  version: number;       // 1 or 2
  attoCircles: string;   // Demurraged balance in atto (10^-18)
  circles: number;       // Demurraged balance in Circles
  staticAttoCircles: string; // Static (non-demurraged) atto
  staticCircles: number;
  attoCrc: string;       // CRC units (legacy)
  crc: number;
  isErc20: boolean;
  isErc1155: boolean;
  isWrapped: boolean;    // ERC-20 wrapper around ERC-1155
  isInflationary: boolean;
  isGroup: boolean;
}
```

#### `circles_getTokenInfo`

Get metadata for a specific token.

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | tokenAddress | string | yes |

**Returns:** `TokenInfo | null`

```typescript
interface TokenInfo {
  tokenAddress: string;
  tokenOwner: string;
  tokenType: string;
  version: number;
  isErc20: boolean;
  isErc1155: boolean;
  isWrapped: boolean;
  isInflationary: boolean;
  isGroup: boolean;
}
```

#### `circles_getTokenInfoBatch`

Get metadata for multiple tokens.

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | tokenAddresses | string[] | yes |

**Returns:** `(TokenInfo | null)[]` — same length as input, null for unknown tokens

#### `circles_getTokenHolders`

Get all holders of a specific token with cursor-based pagination.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | tokenAddress | string | yes | — |
| 1 | limit | number | no | 100 |
| 2 | cursor | string | no | null |

**Returns:** `PagedResponse<TokenHolderRow>`

```typescript
interface TokenHolderRow {
  account: string;
  balance: string;
  tokenAddress: string;
  version: number;
}
```

---

### Avatar & Profile Methods

#### `circles_getAvatarInfo`

Get avatar information (V1 + V2 merged).

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | address | string | yes |

**Returns:** `AvatarInfo`

```typescript
interface AvatarInfo {
  version: number;
  type: string;        // "CrcV2_RegisterHuman" | "CrcV2_RegisterGroup" | "CrcV2_RegisterOrganization" | "CrcV1_Signup" | ...
  avatar: string;
  tokenId: string;
  hasV1: boolean;
  v1Token: string | null;
  cidV0Digest: string;
  cidV0: string | null;
  isHuman: boolean;
  name: string | null;
  symbol: string;
}
```

#### `circles_getAvatarInfoBatch`

**Parameters:** `[string[]]` — array of addresses

**Returns:** `AvatarInfo[]`

#### `circles_getProfileCid`

Get IPFS CID for an avatar's profile.

**Parameters:** `[string]` — address

**Returns:** `string | null` — CIDv0 string

#### `circles_getProfileCidBatch`

**Parameters:** `[string[]]` — addresses

**Returns:** `{ [address: string]: string | null }`

#### `circles_getProfileByCid`

Retrieve profile data from IPFS by CID.

**Parameters:** `[string]` — CIDv0

**Returns:** `object | null` — profile JSON from IPFS

#### `circles_getProfileByCidBatch`

**Parameters:** `[string[]]` — CIDs

**Returns:** `{ [cid: string]: object | null }`

#### `circles_getProfileByAddress`

Get profile for an address (CID lookup → IPFS fetch, enriched with avatar type).

**Parameters:** `[string]` — address

**Returns:** `object | null` — profile JSON enriched with avatar type and short name

#### `circles_getProfileByAddressBatch`

**Parameters:** `[string[]]` — addresses

**Returns:** `{ [address: string]: object | null }`

#### `circles_searchProfiles`

Full-text search across profiles.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | text | string | yes | — | Search query (max 3 tokens, each > 1 char) |
| 1 | limit | number | no | 20 | Max results (max 100) |
| 2 | offset | number | no | 0 | Pagination offset |
| 3 | types | string[] | no | null | Filter by avatar types |

**Returns:**

```typescript
{
  total: number;
  results: Array<{
    avatar: string;
    avatarInfo: AvatarInfo;
    profile: object | null;
  }>;
}
```

```bash
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circles_searchProfiles",
  "params": ["berlin", 10, 0, ["CrcV2_RegisterHuman"]]
}'
```

---

### Trust & Network Methods

#### `circles_getTrustRelations`

Get V1 trust relationships for an address.

> **Note:** V1 only. For V2 trust, use `circles_getAggregatedTrustRelations` or query `V_CrcV2_TrustRelations` via `circles_query`.

**Parameters:** `[string]` — address

**Returns:**

```typescript
{
  user: string;
  trusts: Array<{ user: string; limit: number }>;
  trustedBy: Array<{ user: string; limit: number }>;
}
```

#### `circles_getAggregatedTrustRelations`

Get V2 trust relations in SDK-compatible format with relation types.

**Parameters:** `[string]` — avatar address

**Returns:** `AggregatedTrustRelation[]`

```typescript
interface AggregatedTrustRelation {
  subjectAvatar: string;
  relation: "mutuallyTrusts" | "trusts" | "trustedBy";
  objectAvatar: string;
  timestamp: number;
  expiryTime: number;
  objectAvatarType: string | null;  // "CrcV2_RegisterHuman" | "CrcV2_RegisterGroup" | ...
}
```

#### `circles_getCommonTrust`

Find addresses that two users both trust.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | address1 | string | yes | — |
| 1 | address2 | string | yes | — |
| 2 | version | number | no | null (both V1+V2) |

**Returns:** `{ address1: string; address2: string; commonTrusts: string[] }`

```bash
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circles_getCommonTrust",
  "params": ["0xaddr1...", "0xaddr2...", 2]
}'
```

#### `circles_getNetworkSnapshot`

Get a complete snapshot of the trust network. Proxied to Pathfinder service.

**Parameters:** None

**Returns:** Network snapshot (raw Pathfinder response)

---

### Group Methods

#### `circles_findGroups`

Find groups with optional filters and cursor-based pagination.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | limit | number | no | 50 |
| 1 | queryParams | object | no | null |
| 2 | cursor | string | no | null |

**queryParams:**

```typescript
{
  nameStartsWith?: string;
  symbolStartsWith?: string;
  ownerIn?: string[];  // Filter by group owner addresses
}
```

**Returns:** `PagedResponse<GroupRow>`

```typescript
interface GroupRow {
  group: string;
  name: string;
  symbol: string;
  mint: string;       // Mint policy address
  treasury: string;   // Treasury address
  blockNumber: number;
  timestamp: number;
}
```

#### `circles_getGroupMembers`

Get members of a group with cursor-based pagination.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | groupAddress | string | yes | — |
| 1 | limit | number | no | 100 |
| 2 | cursor | string | no | null |

**Returns:** `PagedResponse<GroupMembershipRow>`

```typescript
interface GroupMembershipRow {
  blockNumber: number;
  timestamp: number;
  transactionIndex: number;
  logIndex: number;
  transactionHash: string;
  group: string;
  member: string;
  expiryTime: number;
}
```

#### `circles_getGroupMemberships`

Get groups that an address is a member of (inverse of `circles_getGroupMembers`).

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | memberAddress | string | yes | — |
| 1 | limit | number | no | 50 |
| 2 | cursor | string | no | null |

**Returns:** `PagedResponse<GroupMembershipRow>`

---

### Transaction & Transfer Methods

#### `circles_getTransactionHistory`

Get transaction history for an avatar with all circle amount formats.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | avatarAddress | string | yes | — | |
| 1 | limit | number | no | 50 | |
| 2 | cursor | string | no | null | base64-encoded `block:tx:log` |
| 3 | version | number | no | null | null=both, 1=V1, 2=V2 |
| 4 | excludeIntermediary | boolean | no | true | If true, uses TransferSummary (excludes hop transfers) |

**Returns:** `PagedResponse<TransactionHistoryRow>`

```typescript
interface TransactionHistoryRow {
  blockNumber: number;
  timestamp: number;
  transactionIndex: number;
  logIndex: number;
  transactionHash: string;
  version: number;
  from: string;
  to: string;
  operator: string | null;
  id: string | null;           // Token ID
  value: string;               // Raw demurraged attoCircles
  circles: string;             // Demurraged Circles
  attoCircles: string;
  crc: string;                 // CRC units
  attoCrc: string;
  staticCircles: string;       // Non-demurraged
  staticAttoCircles: string;
}
```

#### `circles_getTransferData`

Get ERC-1155 transfer calldata bytes for an address. Convenience method without raw `filterPredicates`.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | address | string | yes | — | Primary address |
| 1 | direction | string | no | null | `"sent"` (from=addr), `"received"` (to=addr), null=both |
| 2 | counterparty | string | no | null | AND with specific counterparty |
| 3 | fromBlock | number | no | null | Start block (inclusive) |
| 4 | toBlock | number | no | null | End block (inclusive) |
| 5 | limit | number | no | 50 | Max results (max 1000) |
| 6 | cursor | string | no | null | base64-encoded `block:tx:log` |

**Returns:** `PagedResponse<TransferDataRow>`

```typescript
interface TransferDataRow {
  blockNumber: number;
  timestamp: number;
  transactionIndex: number;
  logIndex: number;
  transactionHash: string;
  from: string;
  to: string;
  data: string;   // Hex-encoded bytes
}
```

---

### Event & Query Methods

#### `circles_events`

Query indexed blockchain events with advanced filtering. Returns a **flat array** (backwards compatible).

**Parameters (positional):**

| Index | Name | Type | Default | Description |
|-------|------|------|---------|-------------|
| 0 | address | string\|null | null | Filter by address (null = all) |
| 1 | fromBlock | number\|null | null | Start block (inclusive) |
| 2 | toBlock | number\|null | null | End block (inclusive) |
| 3 | eventTypes | string[]\|null | null | Filter by event types (null = all) |
| 4 | filterPredicates | object[]\|null | null | Advanced filters (see [Filter Predicates](#filter-predicates)) |
| 5 | sortAscending | boolean\|null | false | Sort order |
| 6 | limit | number\|null | 100 | Max events (max 1000) |
| 7 | cursor | string\|null | null | Pagination cursor |

> Parameters are positional. To set `limit` (index 6), pass `null` for unused preceding slots.

**Returns:** Flat event array (for paginated results with `hasMore`/`nextCursor`, use `circles_events_paginated`)

```bash
# All events for a specific transaction
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circles_events",
  "params": [null, 0, null, null,
    [{"Type": "FilterPredicate", "FilterType": "In",
      "Column": "transactionHash",
      "Value": ["0xfc98cb9fbb96c19043c73214570660a04de9bbd78eb0ea127ce4371f4bbf0c5a"]}],
    false, 1000]
}'

# V2 transfer events for an address
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circles_events",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7", 30282299, null, ["CrcV2_Transfer"]]
}'
```

#### `circles_events_paginated`

Same parameters as `circles_events`. Returns `{ events, hasMore, nextCursor }` for cursor-based pagination.

**Returns:**

```typescript
{
  events: object[];
  hasMore: boolean;
  nextCursor: string | null;
}
```

Pass `nextCursor` from the previous response as param index 7 to get the next page.

#### `circles_getBlockByTimestamp`

Convert a Unix timestamp to the nearest block number. Useful for building block ranges for event queries.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | timestamp | number | yes | — | Unix timestamp (seconds since epoch) |
| 1 | direction | string | no | `"before"` | `"before"` = at or before, `"after"` = at or after |

**Returns:**

```typescript
{ blockNumber: number; timestamp: number }
```

#### `circles_query`

Generic database query interface. Query any indexed table with structured filters.

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | query | SelectDto | yes |

**SelectDto:**

```typescript
{
  namespace: string;      // e.g., "V_Crc", "CrcV2", "CrcV1"
  table: string;          // e.g., "Avatars", "TrustRelations"
  columns?: string[];     // Empty = all columns
  filter?: FilterPredicate[];
  order?: Array<{ column: string; sortOrder: "ASC" | "DESC" }>;
  limit?: number;
  distinct?: boolean;
}
```

**Returns:**

```typescript
{ columns: string[]; rows: object[][] }
```

Use `circles_tables` to discover available namespaces, tables, and columns.

```bash
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circles_query",
  "params": [{
    "namespace": "V_Crc",
    "table": "Avatars",
    "columns": [],
    "limit": 10,
    "order": [{"column": "blockNumber", "sortOrder": "DESC"}]
  }]
}'
```

#### `circles_paginated_query`

Same as `circles_query` but returns cursor-based pagination for tables with event columns.

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | query | SelectDto | yes |
| 1 | cursor | string | no |

**Returns:**

```typescript
{
  columns: string[];
  rows: object[][];
  hasMore: boolean;
  nextCursor: string | null;
}
```

### Filter Predicates

Used by `circles_events`, `circles_events_paginated`, `circles_query`, and `circles_paginated_query`.

```typescript
{
  Type: "FilterPredicate";
  FilterType: string;   // Operator (see table)
  Column: string;       // Column name
  Value: any;           // Type depends on FilterType
}
```

**Operators:**

| FilterType | Value Type | Description |
|------------|-----------|-------------|
| `Equals` | scalar (string\|number) | Exact match |
| `NotEquals` | scalar (string\|number) | Not equal |
| `GreaterThan` | scalar (string\|number) | Greater than |
| `GreaterThanOrEquals` | scalar (string\|number) | ≥ |
| `LessThan` | scalar (string\|number) | Less than |
| `LessThanOrEquals` | scalar (string\|number) | ≤ |
| `Like` | string (with `%` wildcards) | SQL LIKE |
| `ILike` | string (with `%` wildcards) | Case-insensitive LIKE |
| `NotLike` | string (with `%` wildcards) | SQL NOT LIKE |
| `In` | **array** (string[]\|number[]) | Match any in array (max 1000 elements) |
| `NotIn` | **array** (string[]\|number[]) | Exclude values in array (max 1000 elements) |
| `IsNull` | *(ignored)* | Column IS NULL |
| `IsNotNull` | *(ignored)* | Column IS NOT NULL |

> **Important:** `In` and `NotIn` require `Value` to be an **array**, not a single string.

**Conjunction (AND/OR nesting):**

Multiple predicates in the `filterPredicates` array are AND-ed together. For OR logic, use Conjunction:

```json
{
  "Type": "Conjunction",
  "ConjunctionType": "Or",
  "Predicates": [
    {"Type": "FilterPredicate", "FilterType": "Equals", "Column": "from", "Value": "0xabc..."},
    {"Type": "FilterPredicate", "FilterType": "Equals", "Column": "to", "Value": "0xabc..."}
  ]
}
```

---

### System Methods

#### `circles_health`

Check service health.

**Parameters:** None

**Returns:** `{ status: string; timestamp: number; database: string; index: string }`

#### `circles_tables`

List available database tables and their schemas. Use this to discover what's queryable via `circles_query`.

**Parameters:** None

**Returns:**

```typescript
Array<{
  namespace: string;
  tables: Array<{
    table: string;
    topic: string;
    columns: Array<{ column: string; type: string }>;
  }>;
}>
```

---

### Pathfinder Methods

#### `circlesV2_findPath`

Calculate a transitive payment path through the V2 trust network. Proxied to Pathfinder service.

**Parameters:**

| Index | Name | Type | Required |
|-------|------|------|----------|
| 0 | flowRequest | FlowRequest | yes |

**FlowRequest:**

```typescript
{
  source: string;         // Sender address
  sink: string;           // Receiver address
  targetFlow: string;     // Amount in atto-circles (wei)
  fromTokens?: string[];  // Source token filter
  toTokens?: string[];    // Destination token filter
  withWrap?: boolean;     // Enable ERC-20 wrapping
}
```

**Returns:** Pathfinder response (JSON object with transfer steps, max flow, etc.)

```bash
curl -X POST http://localhost:8081 -H 'Content-Type: application/json' -d '{
  "jsonrpc": "2.0", "id": 1,
  "method": "circlesV2_findPath",
  "params": [{
    "source": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    "sink": "0x42cEDde51198D1773590311E2A340DC06B24cB37",
    "targetFlow": "1000000000000000000"
  }]
}'
```

---

### SDK Enablement Methods

Composite methods that replace multiple RPC round-trips. Reduce SDK calls by 60-80%.

#### `circles_getProfileView`

Complete profile view: avatar + profile + trust stats + balances in one call.

**Parameters:** `[string]` — address

**Returns:**

```typescript
{
  address: string;
  avatarInfo: AvatarInfo | null;
  profile: object | null;
  trustStats: { trustsCount: number; trustedByCount: number };
  v1Balance: string | null;
  v2Balance: string | null;
}
```

#### `circles_getTrustNetworkSummary`

Aggregated trust network statistics.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | address | string | yes | — |
| 1 | maxDepth | number | no | null |

**Returns:**

```typescript
{
  address: string;
  directTrustsCount: number;
  directTrustedByCount: number;
  mutualTrustsCount: number;
  mutualTrusts: string[];
  networkReach: number;
}
```

#### `circles_getAggregatedTrustRelationsEnriched`

Trust relations categorized by type with enriched avatar info. Paginated.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | address | string | yes | — |
| 1 | limit | number | no | 50 (max 200) |
| 2 | cursor | string | no | null |

**Returns:**

```typescript
{
  address: string;
  results: TrustRelationInfo[];  // { address, avatarInfo, relationType }
  counts: { mutual: number; trusts: number; trustedBy: number; total: number };
  hasMore: boolean;
  nextCursor: string | null;
}
```

#### `circles_getValidInviters`

Addresses that trust the given address AND have sufficient balance to invite.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | address | string | yes | — |
| 1 | minimumBalance | string | no | null |
| 2 | limit | number | no | 50 (max 200) |
| 3 | cursor | string | no | null |

**Returns:**

```typescript
{
  address: string;
  results: InviterInfo[];  // { address, balance, avatarInfo }
  hasMore: boolean;
  nextCursor: string | null;
}
```

#### `circles_getTransactionHistoryEnriched`

Transaction history with participant profiles attached.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | address | string | yes | — | |
| 1 | fromBlock | number | yes | — | |
| 2 | toBlock | number | no | null | |
| 3 | limit | number | no | 20 | |
| 4 | cursor | string | no | null | |
| 5 | version | number | no | null | null=V2, 1=V1, 2=V2 |
| 6 | excludeIntermediary | boolean | no | true | Exclude hop transfers |

**Returns:** `PagedResponse<EnrichedTransaction>`

```typescript
interface EnrichedTransaction {
  blockNumber: number;
  timestamp: number;
  transactionIndex: number;
  logIndex: number;
  transactionHash: string;
  version: number;
  from: string;
  to: string;
  operator: string | null;
  id: string | null;
  value: string;
  circles: string;
  attoCircles: string;
  crc: string;
  attoCrc: string;
  staticCircles: string;
  staticAttoCircles: string;
  fromProfile: object | null;
  toProfile: object | null;
}
```

#### `circles_searchProfileByAddressOrName`

Unified search: auto-detects address prefix (`0x`) vs text search.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | query | string | yes | — |
| 1 | limit | number | no | 20 (max 100) |
| 2 | cursor | string | no | null |
| 3 | types | string[] | no | null |

**Returns:**

```typescript
{
  query: string;
  searchType: "address" | "text";
  results: object[];
  hasMore: boolean;
  nextCursor: string | null;
}
```

---

### Invitation Methods

#### `circles_getInvitationOrigin`

Reconstruct how a user was invited to Circles. Supports V1 Signup, V2 Standard, V2 Escrow, and V2 At Scale.

**Parameters:** `[string]` — address

**Returns:** `InvitationOriginResponse | null`

```typescript
{
  address: string;
  invitationType: "v1_signup" | "v2_standard" | "v2_escrow" | "v2_at_scale";
  inviter: string | null;        // null for v1_signup
  proxyInviter: string | null;   // Only for v2_at_scale
  escrowAmount: string | null;   // Only for v2_escrow (atto-circles)
  blockNumber: number;
  timestamp: number;
  transactionHash: string;
  version: number;               // 1 or 2
}
```

#### `circles_getAllInvitations`

Get all available invitations from all sources (trust, escrow, at-scale) in one call.

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | address | string | yes | — |
| 1 | minimumBalance | string | no | null |

**Returns:**

```typescript
{
  address: string;
  trustInvitations: TrustInvitation[];
  escrowInvitations: EscrowInvitation[];
  atScaleInvitations: AtScaleInvitation[];
}
```

#### `circles_getTrustInvitations`

Get trust-based invitations only (subset of `circles_getAllInvitations`).

**Parameters:**

| Index | Name | Type | Required | Default |
|-------|------|------|----------|---------|
| 0 | address | string | yes | — |
| 1 | minimumBalance | string | no | null |

**Returns:** `TrustInvitation[]`

```typescript
interface TrustInvitation {
  address: string;
  source: "trust";
  balance: string;
  avatarInfo: AvatarInfo | null;
}
```

#### `circles_getEscrowInvitations`

Get escrow-based invitations (CRC escrowed for the address). Filters out redeemed/revoked/refunded.

**Parameters:** `[string]` — address

**Returns:** `EscrowInvitation[]`

```typescript
interface EscrowInvitation {
  address: string;
  source: "escrow";
  escrowedAmount: string;
  escrowDays: number;
  blockNumber: number;
  timestamp: number;
  avatarInfo: AvatarInfo | null;
}
```

#### `circles_getAtScaleInvitations`

Get at-scale invitations (pre-created accounts not yet claimed).

**Parameters:** `[string]` — address

**Returns:** `AtScaleInvitation[]`

```typescript
interface AtScaleInvitation {
  address: string;
  source: "atScale";
  blockNumber: number;
  timestamp: number;
  originInviter: string | null;
}
```

#### `circles_getInvitationsFrom`

Get accounts invited by a specific avatar.

**Parameters:**

| Index | Name | Type | Required | Default | Description |
|-------|------|------|----------|---------|-------------|
| 0 | address | string | yes | — | Inviter address |
| 1 | accepted | boolean | no | false | `true`=registered, `false`=pending (trusted but not registered) |

**Returns:**

```typescript
{
  address: string;
  accepted: boolean;
  results: Array<{
    address: string;
    status: string;
    blockNumber: number;
    timestamp: number;
    avatarInfo: AvatarInfo | null;
  }>;
}
```

---

## Cursor-Based Pagination

Methods returning `PagedResponse<T>` use cursor-based pagination:

```typescript
{
  results: T[];
  hasMore: boolean;
  nextCursor: string | null;  // Pass as cursor param to get next page
}
```

Cursors are opaque base64 strings (typically encoding `block:transactionIndex:logIndex`). Do not parse or construct them manually.

---

## Method Summary

| # | Method | Category | Paginated | Cache |
|---|--------|----------|-----------|-------|
| 1 | `rpc.discover` | Discovery | — | — |
| 2 | `circles_getTotalBalance` | Balance | — | Yes |
| 3 | `circlesV2_getTotalBalance` | Balance | — | Yes |
| 4 | `circles_getTokenBalances` | Balance | — | Yes |
| 5 | `circles_getTokenInfo` | Token | — | — |
| 6 | `circles_getTokenInfoBatch` | Token | — | — |
| 7 | `circles_getTokenHolders` | Token | Cursor | — |
| 8 | `circles_getAvatarInfo` | Avatar | — | Yes |
| 9 | `circles_getAvatarInfoBatch` | Avatar | — | Yes |
| 10 | `circles_getProfileCid` | Profile | — | — |
| 11 | `circles_getProfileCidBatch` | Profile | — | — |
| 12 | `circles_getProfileByCid` | Profile | — | — |
| 13 | `circles_getProfileByCidBatch` | Profile | — | — |
| 14 | `circles_getProfileByAddress` | Profile | — | Yes |
| 15 | `circles_getProfileByAddressBatch` | Profile | — | Yes |
| 16 | `circles_searchProfiles` | Profile | Offset | — |
| 17 | `circles_getTrustRelations` | Trust | — | — |
| 18 | `circles_getAggregatedTrustRelations` | Trust | — | — |
| 19 | `circles_getCommonTrust` | Trust | — | — |
| 20 | `circles_getNetworkSnapshot` | Network | — | — |
| 21 | `circles_findGroups` | Group | Cursor | — |
| 22 | `circles_getGroupMembers` | Group | Cursor | — |
| 23 | `circles_getGroupMemberships` | Group | Cursor | — |
| 24 | `circles_getTransactionHistory` | Transaction | Cursor | — |
| 25 | `circles_getTransferData` | Transaction | Cursor | — |
| 26 | `circles_getBlockByTimestamp` | Block | — | — |
| 27 | `circles_events` | Events | — | — |
| 28 | `circles_events_paginated` | Events | Cursor | — |
| 29 | `circles_query` | Query | — | — |
| 30 | `circles_paginated_query` | Query | Cursor | — |
| 31 | `circles_health` | System | — | — |
| 32 | `circles_tables` | System | — | — |
| 33 | `circlesV2_findPath` | Pathfinder | — | — |
| 34 | `circles_getProfileView` | SDK | — | — |
| 35 | `circles_getTrustNetworkSummary` | SDK | — | — |
| 36 | `circles_getAggregatedTrustRelationsEnriched` | SDK | Cursor | — |
| 37 | `circles_getValidInviters` | SDK | Cursor | — |
| 38 | `circles_getTransactionHistoryEnriched` | SDK | Cursor | — |
| 39 | `circles_searchProfileByAddressOrName` | SDK | Cursor | — |
| 40 | `circles_getInvitationOrigin` | Invitation | — | — |
| 41 | `circles_getAllInvitations` | Invitation | — | — |
| 42 | `circles_getTrustInvitations` | Invitation | — | — |
| 43 | `circles_getEscrowInvitations` | Invitation | — | — |
| 44 | `circles_getAtScaleInvitations` | Invitation | — | — |
| 45 | `circles_getInvitationsFrom` | Invitation | — | — |

---

## Architecture

```
Client Request
    │
    ▼
┌─────────────────────────────────────────────┐
│  ASP.NET Core (Kestrel)                     │
│  ├─ Rate Limiter (per-IP token bucket)      │
│  ├─ Batch Middleware (array detect, split)   │
│  └─ Concurrency Semaphore                   │
└─────────────┬───────────────────────────────┘
              │
    ┌─────────┴─────────┐
    ▼                   ▼
circles_*           eth_*/net_*/web3_*
    │                   │
    ▼                   ▼
CirclesRpcModule    NethermindRpcClient
    │                   │
    ▼                   ▼
┌──────────┬────────┬──────────────┐
│ Postgres │ IPFS   │ Pathfinder   │
│ (Index)  │(Profiles)│(Max Flow)  │
└──────────┴────────┴──────────────┘
    │
    ▼ (optional)
CacheServiceClient
```

### Key Components

| File | Purpose |
|------|---------|
| `Program.cs` | App setup, batch middleware, WebSocket handler, method dispatch switch |
| `CirclesRpcModule.cs` + `RpcModule/*.cs` | Business logic (partial classes by domain) |
| `ICirclesRpcModule.cs` | Interface + all DTO record definitions |
| `RpcRateLimiter.cs` | Per-IP token bucket rate limiter |
| `RpcMethodClassifier.cs` | Method routing (circles vs proxy vs reject) |
| `RpcMetrics.cs` | Prometheus counters/histograms/gauges |
| `CirclesSubscriptionService.cs` | WebSocket subscription via PostgreSQL LISTEN/NOTIFY |
| `NethermindRpcClient.cs` | HTTP client for Nethermind proxy + balance eth_calls |
| `Settings.cs` | Environment variable configuration |
| `BuilderSetup.cs` | DI container, health checks, OpenTelemetry |
| `OpenRpc/OpenRpcGenerator.cs` | Auto-generated OpenRPC 1.3.2 spec from reflection |

## Prometheus Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `circles_rpc_requests_total` | Counter | method | Total requests |
| `circles_rpc_request_duration_seconds` | Histogram | method | Request duration |
| `circles_rpc_errors_total` | Counter | method, error_type | Errors by type |
| `circles_rpc_inflight_requests` | Gauge | method | Currently in-flight |
| `circles_rpc_active_subscriptions` | Gauge | — | Active WebSocket subscriptions |
| `circles_rpc_rejected_total` | Counter | — | Rejected by concurrency limit |
| `circles_rpc_proxied_total` | Counter | method | Proxied to Nethermind |
| `circles_rpc_proxy_duration_seconds` | Histogram | method | Proxy request duration |
| `circles_rpc_rate_limited_total` | Counter | — | Rejected by rate limiter |
| `circles_rpc_batch_total` | Counter | — | Batch requests received |
| `circles_rpc_batch_size` | Histogram | — | Items per batch |

## Development

### Prerequisites

- .NET 10.0 SDK
- PostgreSQL 15+ (with indexed Circles data)
- Nethermind RPC endpoint (optional, for live balance mode)
- Pathfinder service (optional, for `circlesV2_findPath` and `circles_getNetworkSnapshot`)

### Testing

```bash
# Run unit tests
./scripts/test.sh Circles.Rpc.Host.Tests

# Test a live endpoint
./scripts/test-rpc.sh                               # localhost:8081
./scripts/test-rpc.sh https://rpc.aboutcircles.com  # production
make test-rpc-regression                             # compare local vs production
```

### Adding New RPC Methods

1. Add method signature to `ICirclesRpcModule.cs`
2. Implement in the appropriate `RpcModule/CirclesRpcModule.*.cs` partial class
3. Add handler function in `Program.cs` (or use `ReflectionHandler` for convention-based routing)
4. Add case to the dispatch switch in `Program.cs`
5. Add method name to `RpcMethodClassifier.KnownCirclesMethods`
6. Update this README

## Troubleshooting

**Port in use:** `ASPNETCORE_URLS="http://localhost:8082" ./scripts/run-rpc.sh`

**DB connection errors:** Check `docker ps | grep postgres` and test with `psql`.

**Pathfinder fails:** Ensure `curl http://localhost:8080/health` returns 200 and `EXTERNAL_PATHFINDER_URL` is set.

**Balance timeouts:** Increase `DATABASE_QUERY_TIMEOUT_SECONDS` or switch to `BALANCE_MODE=database`.

**429 Too Many Requests:** Reduce request rate or increase `RPC_RATE_LIMIT_PER_SECOND` / `RPC_RATE_LIMIT_BURST`.

**503 Server Busy:** Increase `RPC_MAX_CONCURRENT_REQUESTS` or investigate slow queries.
