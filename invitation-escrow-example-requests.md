# Circles V2 Invitation Escrow Event Examples

This document contains example queries for the Circles V2 Invitation Escrow contract events.

## Available Tables

The invitation escrow events are indexed under the `CrcV2_InvitationEscrow` namespace with the following tables:

### InvitationEscrowed
Emitted when an inviter locks tokens for a specific invitee.

**Columns:**
- `blockNumber` (Int64) - The block number
- `timestamp` (Int64) - Unix timestamp
- `transactionIndex` (Int32) - Transaction index in block
- `logIndex` (Int32) - Log index in transaction
- `transactionHash` (String) - Transaction hash
- `emitter` (Address) - Contract address that emitted the event
- `inviter` (Address) - Address that locked tokens
- `invitee` (Address) - Address designated to receive the tokens
- `amount` (BigInteger) - Amount of tokens escrowed

### InvitationRedeemed
Emitted when the invitee accepts the invitation and the tokens are used/transferred.

**Columns:** Same as InvitationEscrowed

### InvitationRevoked
Emitted when the inviter cancels the invitation and retrieves their tokens.

**Columns:** Same as InvitationEscrowed

### InvitationRefunded
Emitted when tokens are returned to the inviter (e.g., during a sweep or specific refund action).

**Columns:** Same as InvitationEscrowed

## Example Queries

### Query all escrowed invitations

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationEscrowed",
      "Columns": [],
      "Filter": [],
      "Order": [
        {
          "Column": "blockNumber",
          "SortOrder": "DESC"
        },
        {
          "Column": "transactionIndex",
          "SortOrder": "DESC"
        },
        {
          "Column": "logIndex",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query all invitations for a specific inviter

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationEscrowed",
      "Columns": [],
      "Filter": [
        {
          "Type": "FilterPredicate",
          "FilterType": "Equals",
          "Column": "inviter",
          "Value": "0xYourInviterAddressHere"
        }
      ],
      "Order": [
        {
          "Column": "timestamp",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query all invitations for a specific invitee

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationEscrowed",
      "Columns": [],
      "Filter": [
        {
          "Type": "FilterPredicate",
          "FilterType": "Equals",
          "Column": "invitee",
          "Value": "0xYourInviteeAddressHere"
        }
      ],
      "Order": [
        {
          "Column": "timestamp",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query all redeemed invitations

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationRedeemed",
      "Columns": [],
      "Filter": [],
      "Order": [
        {
          "Column": "blockNumber",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query all revoked invitations

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationRevoked",
      "Columns": [],
      "Filter": [],
      "Order": [
        {
          "Column": "blockNumber",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query all refunded invitations

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationRefunded",
      "Columns": [],
      "Filter": [],
      "Order": [
        {
          "Column": "blockNumber",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query invitation history for a specific inviter-invitee pair

This query combines all four event types to get the complete history for a specific invitation relationship:

```shell
# Query escrowed events
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationEscrowed",
      "Columns": [],
      "Filter": [
        {
          "Type": "Conjunction",
          "ConjunctionType": "And",
          "Predicates": [
            {
              "Type": "FilterPredicate",
              "FilterType": "Equals",
              "Column": "inviter",
              "Value": "0xInviterAddress"
            },
            {
              "Type": "FilterPredicate",
              "FilterType": "Equals",
              "Column": "invitee",
              "Value": "0xInviteeAddress"
            }
          ]
        }
      ],
      "Order": [
        {
          "Column": "timestamp",
          "SortOrder": "ASC"
        }
      ]
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query invitations by amount range

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationEscrowed",
      "Columns": [],
      "Filter": [
        {
          "Type": "Conjunction",
          "ConjunctionType": "And",
          "Predicates": [
            {
              "Type": "FilterPredicate",
              "FilterType": "GreaterThanOrEquals",
              "Column": "amount",
              "Value": "97000000000000000000"
            },
            {
              "Type": "FilterPredicate",
              "FilterType": "LessThanOrEquals",
              "Column": "amount",
              "Value": "100000000000000000000"
            }
          ]
        }
      ],
      "Order": [
        {
          "Column": "amount",
          "SortOrder": "DESC"
        }
      ],
      "Limit": 100
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

### Query invitations within a block range

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_InvitationEscrow",
      "Table": "InvitationEscrowed",
      "Columns": [],
      "Filter": [
        {
          "Type": "Conjunction",
          "ConjunctionType": "And",
          "Predicates": [
            {
              "Type": "FilterPredicate",
              "FilterType": "GreaterThanOrEquals",
              "Column": "blockNumber",
              "Value": 1000000
            },
            {
              "Type": "FilterPredicate",
              "FilterType": "LessThanOrEquals",
              "Column": "blockNumber",
              "Value": 2000000
            }
          ]
        }
      ],
      "Order": [
        {
          "Column": "blockNumber",
          "SortOrder": "ASC"
        }
      ],
      "Limit": 1000
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

## Using circles_events Method

You can also use the `circles_events` method to get all invitation escrow events for a specific address:

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [
    "0xYourAddressHere",
    0,
    null
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

This will return all Circles events (including invitation escrow events) that involve the specified address.

## Configuration

The invitation escrow contract address can be configured via the `V2_INVITATION_ESCROW_ADDRESS` environment variable:

```bash
V2_INVITATION_ESCROW_ADDRESS=0x0956c08ad2dcc6f4a1e0cc5ffa3a08d2a6d85f29
```

Default address (Gnosis Chain): `0x0956c08ad2dcc6f4a1e0cc5ffa3a08d2a6d85f29`

## Notes

- All amounts are stored as BigInteger values (wei-like precision)
- The contract enforces minimum (97 ether) and maximum (100 ether) escrow amounts
- All addresses are stored in lowercase with 0x prefix
- All indexed columns (inviter, invitee, amount) can be efficiently filtered
