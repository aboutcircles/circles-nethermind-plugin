## General circles RPC examples

The examples in this file are general Circles RPC methods that can be used to query Circles V1 and V2 data.

1. [circles subscription](#circles-subscription)
2. [circles_events](#circles_events)
3. [circles_query](#circles_query)      
   3.1. [Get a list of Circles avatars](#get-a-list-of-circles-users)  
   3.2. [Get the trust relations between avatars](#get-the-trust-relations-between-avatars)

### circles subscription

#### Example

```js
```

### circles_events

Queries all events that involve a specific address. This is especially useful to update a client once it's address is
involved in an event (see [circles subscription](#circles-subscription))
or can be used to populate a history view for a specific address.

**Signature**: `circles_events(address: string, fromBlock: number, toBlock?: number)`.

The `fromBlock` and `toBlock` parameters can be used to filter the events by block number.
The `toBlock` parameter can be set to `null` to query all events from `fromBlock` to the latest block.

#### Example

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    30282299,
    null
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

#### Response

The response generally contains the following fields:

* `event` - The name of the event.
    * `CrcV1_...`
        * `HubTransfer`
        * `OrganizationSignup`
        * `Signup`
        * `Transfer`
        * `Trust`
    * `CrcV2_...`
        * `ApprovalForAll`
        * `DiscountCost`
        * `InviteHuman`
        * `PersonalMint`
        * `RegisterGroup`
        * `RegisterHuman`
        * `RegisterOrganization`
        * `RegisterShortName`
        * `Stopped`
        * `TransferBatch`
        * `TransferSingle`
        * `Trust`
        * `UpdateMetadataDigest`
        * `URI`
        * `CidV0` (predecessor of `URI` and `UpdateMetadataDigest`)
* `values` - The values of the event.

The values contain at least the following fields:

* `blockNumber` - The block number the event was emitted in.
* `timestamp` - The unix timestamp of the event.
* `transactionIndex` - The index of the transaction in the block.
* `logIndex` - The index of the log in the transaction.
* `transactionHash` - The hash of the transaction.

### circles_query

##### Get a paginated list of trust relations

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_CrcV1",
      "Table": "TrustRelations",
      "Limit": 10,
      "Columns": [],
      "Filter": [{
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
                "FilterType": "Equal",
                "Column": "transactionIndex",
                "Value": 0
              },
              {
                "Type": "FilterPredicate",
                "FilterType": "LessThan",
                "Column": "logIndex",
                "Value": 1
              }
            ]
          }
        ]
      }]
  }]
}' -H "Content-Type: application/json" http://localhost:8545/

```

##### Get a list of Circles avatars

This query returns v1 as well as v2 Circles users. The version of the user can be determined by the `version` column.

The following columns are only valid for v2 users:

* `invitedBy` - The address of the user who invited the user.
* `name` - The name of the group or organization.
* `cidV0Digest` - The token metadata CID of the avatar.

```shell
curl -X POST --data '{
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
      ]
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

##### Response:

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
      "version",
      "type",
      "invitedBy",
      "avatar",
      "tokenId",
      "name",
      "cidV0Digest"
    ],
    "Rows": [
      [
        9833016,
        1715978910,
        0,
        2,
        "0x9d5e2ac311eed1c258f9f0885b464baa72e2a36936314723060a03ea59790d72",
        2,
        "human",
        "0xc661fe4ce147c209ea6ca66a2a2323b69791a463",
        "0x52098d2cae70c5f1cda44305c10bf39b98dde4cc",
        "0x52098d2cae70c5f1cda44305c10bf39b98dde4cc",
        null,
        null
      ],
      [
        9819862,
        1715910125,
        0,
        2,
        "0xbd4c3cdf7f0e075f14099e7e5263cce6d0617bc3fc18c92635654b8496d51a77",
        1,
        "human",
        null,
        "0xa315ae910694d7d94406c07962ed56400491cfd4",
        "0x31807cb064a3688bd1cdac56a4a55ee5d78665cd",
        null,
        null
      ],
      [
        9819751,
        1715909570,
        0,
        1,
        "0x408796aed78e7743b0c4851a003dfb343987a206ddb9bab9ee8d87f6f34c4224",
        2,
        "group",
        null,
        "0xabab7fccac344519639449f843d966b24730836d",
        "0xabab7fccac344519639449f843d966b24730836d",
        "Hans Peter Meier Wurstwaren GmbH",
        "0x0e7071c59df3b9454d1d18a15270aa36d54f89606a576dc621757afd44ad1d2e"
      ]
    ]
  },
  "id": 1
}
```

##### Get the trust relations between avatars

This query returns the trust relations between avatars.

The following columns are only valid for v1 trust relations:

* `limit` - The trust limit (0 is no trust, 100 full trust).

The following columns are only valid for v2 trust relations:

* `expiryTime` - The expiry time of the trust relation.

```shell
curl -X POST --data '{
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
      ]
    }
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

##### Response:

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
      "version",
      "trustee",
      "truster",
      "expiryTime",
      "limit"
    ],
    "Rows": [
      [
        9819804,
        1715909835,
        0,
        0,
        "0x41670ceb0bd544f69a6c41ab5390df4ea3ae782cf89b693ac0d7908999bd2f47",
        2,
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "0x25548e3e36c2d1862e4f7aa99a490bf71ed087ca",
        "79228162514264337593543950335",
        null
      ],
      [
        9814663,
        1715883990,
        0,
        1,
        "0xb60737ba5a6f5da7dcde863a36008e9199ddd0b85a76e51b17293c8cc50d7379",
        1,
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        null,
        "100"
      ]
    ]
  },
  "id": 1
}
```