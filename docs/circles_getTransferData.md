# circles_getTransferData

Dedicated RPC method for querying `CrcV2_TransferData` — the calldata bytes from ERC-1155 transfers that are NOT emitted in `TransferSingle`/`TransferBatch` events.

Replaces the `circles_events` + `filterPredicates` approach documented in [transferdata-api-examples.md](transferdata-api-examples.md).

## Parameters

```
circles_getTransferData(address, fromBlock?, toBlock?, direction?, counterparty?, limit?, cursor?)
```

| # | Param | Type | Default | Description |
|---|-------|------|---------|-------------|
| 0 | `address` | string | required | Primary address to filter |
| 1 | `fromBlock` | long? | null | Start block (inclusive) |
| 2 | `toBlock` | long? | null | End block (inclusive) |
| 3 | `direction` | string? | null | `"sent"` = from, `"received"` = to, null = both |
| 4 | `counterparty` | string? | null | If set, AND with specific counterparty |
| 5 | `limit` | int | 50 | Max results (capped at 1000) |
| 6 | `cursor` | string? | null | Pagination cursor from previous response |

## Query Logic

| Params | SQL equivalent |
|--------|----------------|
| `["0xA"]` | `from=A OR to=A` |
| `["0xA", 43000000, 44000000]` | `(from=A OR to=A) AND block BETWEEN 43M..44M` |
| `["0xA", null, null, "sent"]` | `from=A` |
| `["0xA", null, null, "received"]` | `to=A` |
| `["0xA", null, null, "sent", "0xB"]` | `from=A AND to=B` |
| `["0xA", null, null, "received", "0xB"]` | `from=B AND to=A` |
| `["0xA", null, null, null, "0xB"]` | `(from=A AND to=B) OR (from=B AND to=A)` |

## Curl Examples

### All transfers involving an address (no filters)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0"],
    "id": 1
  }'
```

### Block range filter

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", 43000000, 44000000],
    "id": 1
  }'
```

### From block only (no upper bound)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", 44000000],
    "id": 1
  }'
```

### Sent only (from filter)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", null, null, "sent"],
    "id": 1
  }'
```

### Received only (to filter)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", null, null, "received"],
    "id": 1
  }'
```

### Block range + direction

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", 43000000, 44000000, "sent"],
    "id": 1
  }'
```

### Sent to specific counterparty (from + to filter)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": [
      "0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0",
      null, null,
      "sent",
      "0x0dfa95fdad98d1b44b2db4fc513657e5426b1006"
    ],
    "id": 1
  }'
```

### Received from specific counterparty (to + from filter)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": [
      "0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0",
      null, null,
      "received",
      "0x0dfa95fdad98d1b44b2db4fc513657e5426b1006"
    ],
    "id": 1
  }'
```

### Both directions with counterparty (bidirectional pair)

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": [
      "0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0",
      null, null,
      null,
      "0x0dfa95fdad98d1b44b2db4fc513657e5426b1006"
    ],
    "id": 1
  }'
```

### With limit and cursor pagination

```bash
# First page (limit 2)
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", null, null, null, null, 2],
    "id": 1
  }'

# Next page — pass nextCursor from previous response
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": [
      "0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0",
      null, null, null, null,
      2,
      "NDMxMzQ0Mjk6OTow"
    ],
    "id": 1
  }'
```

## Response Format

```json
{
  "jsonrpc": "2.0",
  "result": {
    "results": [
      {
        "blockNumber": 44143207,
        "timestamp": 1768302445,
        "transactionIndex": 10,
        "logIndex": 0,
        "transactionHash": "0xa80c64f6f050c121ab1fef69d4cfad5981113af8...",
        "from": "0x227642ebd3a801e7b44a5bb956c02c2d97ca71f0",
        "to": "0x00738aca013b7b2e6cfe1690f0021c3182fa40b5",
        "data": "0x00000000000000000000000012105a9b291af2abb059..."
      }
    ],
    "hasMore": true,
    "nextCursor": "NDQxNDMyMDc6MTA6MA=="
  },
  "id": 1
}
```

| Field | Type | Description |
|-------|------|-------------|
| `blockNumber` | number | Block number |
| `timestamp` | number | Unix timestamp |
| `transactionIndex` | number | Index within block |
| `logIndex` | number | Synthetic log index (0 or negative) |
| `transactionHash` | string | Transaction hash |
| `from` | string | Sender address (lowercase) |
| `to` | string | Recipient address (lowercase) |
| `data` | string | Hex-encoded calldata bytes (`0x`-prefixed) |
| `hasMore` | boolean | More pages available |
| `nextCursor` | string? | Opaque cursor for next page |

## Error Handling

Invalid direction returns `-32602`:

```bash
curl -s -X POST https://staging.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTransferData",
    "params": ["0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0", null, null, "invalid"],
    "id": 1
  }'
```

```json
{
  "jsonrpc": "2.0",
  "error": { "code": -32602, "message": "direction must be 'sent', 'received', or null" },
  "id": 1
}
```
