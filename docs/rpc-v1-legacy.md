# Circles V1 Legacy RPC Methods

This document covers V1-specific methods for legacy support. For the complete RPC reference including V2 methods, see [rpc-reference.md](rpc-reference.md).

## Table of Contents

- [circles\_getTotalBalance (V1)](#circles_gettotalbalance-v1)
- [circles\_getTrustRelations (V1)](#circles_gettrustrelations-v1)
- [Query V1 Hub Transfers](#query-v1-hub-transfers)
- [Query V1 Trust Events](#query-v1-trust-events)

---

## circles_getTotalBalance (V1)

Query the total Circles V1 holdings of an address.

**Request:**

```bash
curl -X POST https://rpc.aboutcircles.com/ \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTotalBalance",
    "params": ["0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c"],
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

## circles_getTrustRelations (V1)

Query all V1 trust relations of an address.

**Request:**

```bash
curl -X POST https://rpc.aboutcircles.com/ \
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
      "0x5fd8f7464c050ec0fb34223aab544e13510812fa": 50,
      "0x83d296691be2c9d7be14378ecbf2d95c3ddb0200": 100,
      "0x70551a5862ef2c9baf81596f67be723283b6ccd0": 100
    },
    "trustedBy": {
      "0x965090908dcd0b134802f35c9138a7e987b5182f": 100,
      "0x5ce3d708a2b1e8371530754c930fc9b5bad27ab7": 100,
      "0x6fae976eb90127b895ceddf8311864cda42ac6ac": 100,
      "0x3e93a305d5cd96202c12084414a6622fa7a36c3d": 100
    }
  },
  "id": 1
}
```

**Note:** Trust values are percentages (0-100). A value of 100 means full trust, 0 means no trust.

---

## Query V1 Hub Transfers

Query the 10 most recent Circles V1 transfers from or to an address using `circles_query`.

**Request:**

```bash
curl -X POST https://rpc.aboutcircles.com/ \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_query",
    "params": [
      {
        "Namespace": "CrcV1",
        "Table": "HubTransfer",
        "Limit": 10,
        "Columns": [],
        "Filter": [
          {
            "Type": "Conjunction",
            "ConjunctionType": "Or",
            "Predicates": [
              {
                "Type": "FilterPredicate",
                "FilterType": "Equals",
                "Column": "from",
                "Value": "0xc5d6c75087780e0c18820883cf5a580bb3a4d834"
              },
              {
                "Type": "FilterPredicate",
                "FilterType": "Equals",
                "Column": "to",
                "Value": "0xc5d6c75087780e0c18820883cf5a580bb3a4d834"
              }
            ]
          }
        ],
        "Order": [
          {"Column": "blockNumber", "SortOrder": "DESC"},
          {"Column": "transactionIndex", "SortOrder": "DESC"},
          {"Column": "logIndex", "SortOrder": "DESC"}
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
    "Columns": [
      "blockNumber",
      "timestamp",
      "transactionIndex",
      "logIndex",
      "transactionHash",
      "from",
      "to",
      "amount"
    ],
    "Rows": [
      [
        6690264,
        1698156365,
        0,
        2,
        "0x09531454a49c742fcc8940c570e0884345b5811e002e31e512d450e201204676",
        "0xec21a5c94343cc26485916daab84a02328104271",
        "0xc5d6c75087780e0c18820883cf5a580bb3a4d834",
        "409039276061704540"
      ]
    ]
  },
  "id": 1
}
```

---

## Query V1 Trust Events

Query V1 trust relations using the legacy `V_CrcV1` namespace.

**Request:**

```bash
curl -X POST https://rpc.aboutcircles.com/ \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "circles_query",
    "params": [
      {
        "Namespace": "V_CrcV1",
        "Table": "TrustRelations",
        "Limit": 10,
        "Columns": [],
        "Filter": [
          {
            "Type": "Conjunction",
            "ConjunctionType": "Or",
            "Predicates": [
              {
                "Type": "FilterPredicate",
                "FilterType": "LessThan",
                "Column": "blockNumber",
                "Value": 9819862
              },
              {
                "Type": "Conjunction",
                "ConjunctionType": "And",
                "Predicates": [
                  {
                    "Type": "FilterPredicate",
                    "FilterType": "Equal",
                    "Column": "blockNumber",
                    "Value": 9819862
                  },
                  {
                    "Type": "FilterPredicate",
                    "FilterType": "LessThan",
                    "Column": "transactionIndex",
                    "Value": 0
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  }'
```

---

## V1 vs V2 Differences

| Aspect | V1 | V2 |
|--------|----|----|
| Token Standard | ERC-20 | ERC-1155 |
| Trust Limit | 0-100 percentage | Binary (trust/no trust) with expiry |
| Token ID | Contract address | Avatar address |
| Profile Storage | External | On-chain CID reference |

---

## Migration Notes

V1 users migrating to V2 should use the unified methods in [rpc-reference.md](rpc-reference.md):

- `circles_getAvatarInfo` - Returns both V1 and V2 data with `hasV1` and `v1Token` fields
- `circles_getTokenBalances` - Returns V1 and V2 balances together
- `circles_getProfileView` - Returns `v1Balance` and `v2Balance` separately
