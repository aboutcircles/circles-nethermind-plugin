# Circles Pathfinder RPC Methods

Documentation for transitive transfer path calculation methods.

## Table of Contents

- [Overview](#overview)
- [circlesV2\_findPath](#circlesv2_findpath)
  - [Basic Transfer Path](#basic-transfer-path)
  - [Token Swap (Circular Path)](#token-swap-circular-path)
- [Response Format](#response-format)
- [Simulated Balances](#simulated-balances)

---

## Overview

The Pathfinder service calculates optimal paths for transitive Circles transfers. It uses Google OR-Tools to find routes through the trust network that maximize flow while respecting trust relationships and token balances.

**Endpoint:** The pathfinder typically runs on port 8080 (REST API), but can also be accessed via the RPC host.

---

## circlesV2_findPath

Calculate a transfer path between two addresses.

### Basic Transfer Path

Find the maximum possible transfer from source to sink.

**Parameters:**
- `Source` (string): Sender's Ethereum address
- `Sink` (string): Recipient's Ethereum address
- `TargetFlow` (string): Desired transfer amount in atto-CRC (use large value for max flow)

**Request:**

```bash
curl -X POST http://localhost:5000/ \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 0,
    "method": "circlesV2_findPath",
    "params": [{
      "Source": "0x749c930256b47049cb65adcd7c25e72d5de44b3b",
      "Sink": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "TargetFlow": "99999999999999999999999999999999999"
    }]
  }'
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 0,
  "result": {
    "maxFlow": "1000000000000000000000",
    "transfers": [
      {
        "from": "0x749c930256b47049cb65adcd7c25e72d5de44b3b",
        "to": "0xabc123...",
        "tokenId": "0x749c930256b47049cb65adcd7c25e72d5de44b3b",
        "value": "500000000000000000000"
      },
      {
        "from": "0xabc123...",
        "to": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
        "tokenId": "0xabc123...",
        "value": "500000000000000000000"
      }
    ]
  }
}
```

---

### Token Swap (Circular Path)

Calculate a path that swaps one token for another. This creates a circular path where the source and sink are the same address.

**Parameters:**
- `Source` (string): Your Ethereum address
- `Sink` (string): Same as source (circular)
- `TargetFlow` (string): Amount to swap in atto-CRC
- `FromTokens` (string[]): Token(s) you want to spend
- `ToTokens` (string[]): Token(s) you want to receive
- `SimulatedBalances` (array, optional): Override balances for simulation

**Request:**

```bash
curl -X POST http://localhost:5000/ \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 0,
    "method": "circlesV2_findPath",
    "params": [{
      "Source": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "Sink": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "TargetFlow": "100000000000000000000",
      "FromTokens": [
        "0x86533d1aDA8Ffbe7b6F7244F9A1b707f7f3e239b"
      ],
      "ToTokens": [
        "0x6B69683C8897e3d18e74B1Ba117b49f80423Da5d"
      ],
      "SimulatedBalances": [
        {
          "Holder": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          "Token": "0x86533d1aDA8Ffbe7b6F7244F9A1b707f7f3e239b",
          "Amount": "100000000000000000000",
          "IsWrapped": false
        }
      ]
    }]
  }'
```

---

## Response Format

**Successful Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 0,
  "result": {
    "maxFlow": "1000000000000000000000",
    "transfers": [
      {
        "from": "0x...",
        "to": "0x...",
        "tokenId": "0x...",
        "value": "..."
      }
    ]
  }
}
```

**Fields:**
- `maxFlow`: Maximum achievable flow in atto-CRC
- `transfers`: Ordered list of individual transfers to execute

**No Path Found:**

```json
{
  "jsonrpc": "2.0",
  "id": 0,
  "result": {
    "maxFlow": "0",
    "transfers": []
  }
}
```

---

## Simulated Balances

Use `SimulatedBalances` to test transfers with hypothetical token holdings without affecting real state.

**SimulatedBalance Object:**

```json
{
  "Holder": "0x...",      // Address holding the token
  "Token": "0x...",       // Token address/ID
  "Amount": "...",        // Balance in atto-CRC
  "IsWrapped": false      // Whether this is a wrapped ERC-20 token
}
```

**Use Cases:**
- Test if a transfer would succeed before executing
- Calculate paths for tokens you're about to receive
- Simulate multi-step transaction sequences

---

## Integration with Hub Contract

The transfer array returned by `circlesV2_findPath` can be passed directly to the Circles Hub contract's `operateFlowMatrix` function for execution.

```solidity
// Pseudo-code for executing the path
hub.operateFlowMatrix(
    flowVertices,    // Derived from transfers
    flow,            // Flow amounts
    streams,         // Stream definitions
    packedCoordinates
);
```

See the [Circles SDK](https://github.com/aboutcircles/circles-sdk) for helper functions that convert pathfinder output to contract call parameters.
