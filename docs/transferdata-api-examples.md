# TransferData RPC Examples

The `CrcV2_TransferData` event captures the `data` bytes parameter from ERC-1155 transfer calls that is NOT emitted in `TransferSingle`/`TransferBatch` events. This data is extracted from transaction calldata.

## 1. Query TransferData events in block range

```bash
curl -s 'https://staging.circlesubi.network/' -X POST \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"circles_events","params":[null,44370000,44380000,["CrcV2_TransferData"],null,false,5,null],"id":1}'
```

Response:
```json
{
  "jsonrpc": "2.0",
  "result": {
    "events": [
      {
        "event": "CrcV2_TransferData",
        "values": {
          "blockNumber": "0x2a519a7",
          "timestamp": "0x6978ca26",
          "transactionIndex": "0x3",
          "logIndex": "0x0",
          "transactionHash": "0xc70079b0fe7805579f1cac32d91416d7efbc74f126fe4a90dfa625ce6c502916",
          "data": "\\x00000000000000000000000093ed294e8093c7ce413dd96d4fca8c1c01e95267",
          "from": "0x20ecd8bdeb2f48d8a7c94e542aa4fec5790d9676",
          "to": "0x00738aca013b7b2e6cfe1690f0021c3182fa40b5"
        }
      }
    ],
    "hasMore": false
  },
  "id": 1
}
```

## 2. Query TransferData by specific address (sender)

```bash
curl -s 'https://staging.circlesubi.network/' -X POST \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"circles_events","params":["0x20ecd8bdeb2f48d8a7c94e542aa4fec5790d9676",null,null,["CrcV2_TransferData"],null,false,3,null],"id":1}'
```

Response:
```json
{
  "jsonrpc": "2.0",
  "result": {
    "events": [
      {
        "event": "CrcV2_TransferData",
        "values": {
          "blockNumber": "0x2a519a7",
          "timestamp": "0x6978ca26",
          "transactionIndex": "0x3",
          "logIndex": "0x0",
          "transactionHash": "0xc70079b0fe7805579f1cac32d91416d7efbc74f126fe4a90dfa625ce6c502916",
          "data": "\\x00000000000000000000000093ed294e8093c7ce413dd96d4fca8c1c01e95267",
          "from": "0x20ecd8bdeb2f48d8a7c94e542aa4fec5790d9676",
          "to": "0x00738aca013b7b2e6cfe1690f0021c3182fa40b5"
        }
      }
    ],
    "hasMore": true,
    "nextCursor": "NDQzNzEyMTU6MTM6MA=="
  },
  "id": 1
}
```

## Parameters

The `circles_events` method accepts:
```
circles_events(address, fromBlock, toBlock, eventTypes, emitter, sortAscending, limit, cursor)
```

- `address`: Filter by participant address (can be null)
- `fromBlock`: Start block number (can be null)
- `toBlock`: End block number (can be null)
- `eventTypes`: Array of event type names (e.g., `["CrcV2_TransferData"]`)
- `emitter`: Filter by emitter address (can be null)
- `sortAscending`: Boolean for sort order (false = newest first)
- `limit`: Maximum results to return
- `cursor`: Pagination cursor from previous response

## TransferData Event Schema

| Field | Type | Description |
|-------|------|-------------|
| blockNumber | hex | Block number |
| timestamp | hex | Unix timestamp |
| transactionIndex | hex | Index of transaction in block |
| logIndex | hex | Synthetic log index (negative for calldata-derived) |
| transactionHash | hex | Transaction hash |
| from | address | Sender address |
| to | address | Recipient address |
| data | bytes | The `data` parameter from the transfer call |

## Summary (Staging)

- **Total records**: 3,695
- **Block range**: 37,506,572 - 44,374,439
- **Supported call types**:
  - `safeTransferFrom(address,address,uint256,uint256,bytes)`
  - `safeBatchTransferFrom(address,address,uint256[],uint256[],bytes)`
  - `operateFlowMatrix(address[],FlowEdge[],Stream[],bytes)` - extracts data from Stream structs
- **Wrapper support**: ERC-4337 handleOps, Safe execTransaction, Safe multiSend
