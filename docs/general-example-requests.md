## General circles RPC examples

The examples in this file are general Circles RPC methods that can be used to query Circles V1 and V2 data.

- [General circles RPC examples](#general-circles-rpc-examples)
  - [circles subscription](#circles-subscription)
    - [Example](#example)
  - [circles\_events](#circles_events)
    - [Example](#example-1)
    - [Response](#response)
  - [circles\_query](#circles_query)
      - [Get a paginated list of trust relations](#get-a-paginated-list-of-trust-relations)
      - [Get a list of Circles avatars](#get-a-list-of-circles-avatars)
      - [Response:](#response-1)
      - [Get the trust relations between avatars](#get-the-trust-relations-between-avatars)
      - [Response:](#response-2)
  - [circles\_events](#circles_events-1)

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
}' -H "Content-Type: application/json" https://rpc.aboutcircles.com/
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
}' -H "Content-Type: application/json" https://rpc.aboutcircles.com/

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
}' -H "Content-Type: application/json" https://rpc.aboutcircles.com/
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
}' -H "Content-Type: application/json" https://rpc.aboutcircles.com/
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

### circles_events
Queries all events that involve a specific address between two block numbers. 
```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": ["0x389522f8f44cd5cd835d510a17b5f65f74a46468", 9000000]
}' -H "Content-Type: application/json" https://rpc.aboutcircles.com/
```

### circles_getTokenInfo

Get information about a specific token.

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getTokenInfo",
  "params": ["0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e"],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Response:

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

### circles_getTokenInfoBatch

Get information about multiple tokens in batch.

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getTokenInfoBatch",
  "params": [
    ["0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e", "0x42cedde51198d1773590311e340dc06b24cb37"]
  ],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Response:

```json
{
  "jsonrpc": "2.0",
  "result": [
    {
      "token": "0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e",
      "version": 2,
      "type": "Avatar"
    },
    {
      "token": "0x42cedde51198d1773590311e340dc06b24cb37",
      "version": 1,
      "type": "Avatar"
    }
  ],
  "id": 1
}
```

### circles_getAvatarInfo

Get avatar information for a single address. Returns both V1 and V2 avatar data.

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getAvatarInfo",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Response (Phase 1):

**V2 Avatar with V1 Compatibility:**
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

**V1-Only Avatar:**
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

#### Field Descriptions:

- `version`: 1 for V1 avatars, 2 for V2 avatars
- `type`: Event type (`CrcV1_Signup`, `CrcV2_RegisterHuman`, `CrcV2_RegisterGroup`)
- `avatar`: Avatar address
- `tokenId`: For V1, the token address; for V2, the avatar address (ERC-1155)
- `hasV1`: Whether this address has a V1 signup
- `v1Token`: V1 token address (null if no V1 signup)
- `cidV0`: IPFS CID for profile metadata (V2 takes priority, V1 fallback)
- `isHuman`: Whether avatar is human (true) or group/organization (false)
- `name`: Avatar name (V2 only)
- `shortName`: Short name for V2 avatars (V2 only)

### circles_getAvatarInfoBatch

Get avatar information for multiple addresses (batch version).

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getAvatarInfoBatch",
  "params": [
    ["0xde374ece6fa50e781e81aac78e811b33d16912c7", "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"]
  ],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Response (Phase 1):

```json
{
  "jsonrpc": "2.0",
  "result": [
    {
      "version": 2,
      "type": "CrcV2_RegisterHuman",
      "avatar": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "tokenId": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
      "hasV1": true,
      "v1Token": "0x1234567890abcdef1234567890abcdef12345678",
      "cidV0": "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG",
      "isHuman": true,
      "name": "Alice",
      "shortName": "alice"
    },
    {
      "version": 1,
      "type": "CrcV1_Signup",
      "avatar": "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
      "tokenId": "0xabcdef1234567890abcdef1234567890abcdef12",
      "hasV1": true,
      "v1Token": "0xabcdef1234567890abcdef1234567890abcdef12",
      "cidV0": "QmXwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG",
      "isHuman": true,
      "name": null,
      "shortName": null
    }
  ],
  "id": 1
}
```

**Note**: Maximum 1000 addresses per batch request.

### circles_getTokenBalances

Get all token balances for a specific address. Returns detailed balance information for V1, V2 ERC-1155, and V2 ERC-20 wrapped tokens.

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getTokenBalances",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Response (Phase 1):

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

#### Field Descriptions:

- `tokenAddress`: Address of the token contract
- `tokenId`: For V1, same as tokenAddress; for V2 ERC-1155, uint256 token ID
- `tokenOwner`: Avatar that owns the token
- `tokenType`: Type of token (`CrcV1_Signup`, `CrcV2_RegisterHuman`, `CrcV2_RegisterGroup`, `CrcV2_ERC20WrapperDeployed_Inflationary`, `CrcV2_ERC20WrapperDeployed_Demurraged`)
- `version`: 1 for V1, 2 for V2
- `attoCircles`: Balance in atto-circles (10^-18 circles)
- `circles`: Balance in circles (decimal)
- `staticAttoCircles`: Static balance (for inflationary tokens)
- `staticCircles`: Static balance in circles (decimal)
- `attoCrc`: Balance in atto-CRC
- `crc`: Balance in CRC (decimal)
- `isErc20`: Whether token is ERC-20
- `isErc1155`: Whether token is ERC-1155
- `isWrapped`: Whether token is a V2 wrapped token
- `isInflationary`: Whether token is inflationary
- `isGroup`: Whether token belongs to a group

#### Important Phase 1 Limitations:

**Database-Only Calculations**: The returned balances are raw sums of historical transfers from the database. They do **NOT** account for time-based adjustments:

- **V1 tokens**: No inflation adjustment applied (actual balance may be higher)
- **V2 demurraged tokens**: No demurrage decay applied (actual balance may be lower)
- **V2 inflationary tokens**: No inflation adjustment applied (actual balance may differ)

**In Phase 1**:
- `attoCircles` = `staticAttoCircles` = `attoCrc` (no conversion)
- All decimal values are based on the same underlying raw database sum

**In Phase 3** (with blockchain connector):
- These values will differ based on current timestamp and token type
- Accurate inflation/demurrage calculations will be applied
- Real-time balance queries via `eth_call` will be available

For accurate balances with time-based adjustments, wait for Phase 3 or query the blockchain directly.

### circles_getProfileByAddress

Get enriched profile information for an address. Combines IPFS profile data with on-chain metadata.

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getProfileByAddress",
  "params": ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Response (Phase 1 - Enriched Profile):

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

#### Enrichment Features (Phase 1):

- **IPFS Profile Data**: `name`, `description`, `imageUrl` from IPFS
- **On-Chain Metadata** (Phase 1 additions):
  - `address`: Avatar address
  - `shortName`: V2 short name from `CrcV2_RegisterShortName`
  - `avatarType`: Type from `V_CrcV2_Avatars` or `CrcV1_Signup`
  - `CID`: IPFS CID (V2 takes priority, V1 fallback)

### circles_getProfileByAddressBatch

Get enriched profiles for multiple addresses in batch.

#### Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_getProfileByAddressBatch",
  "params": [
    ["0xde374ece6fa50e781e81aac78e811b33d16912c7", "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"]
  ],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

**Note**: Maximum 1000 addresses per batch request.

### circles_events (with filtering)

Query events with basic and advanced filtering. Phase 1 adds support for advanced filter predicates.

#### Parameters:

1. `address` (string, optional): Filter by address
2. `fromBlock` (number, optional): Starting block number (inclusive)
3. `toBlock` (number, optional): Ending block number (inclusive)
4. `eventTypes` (string[], optional): Filter by event types
5. `filterPredicates` (FilterPredicate[], optional): **Phase 1 Addition** - Advanced filters
6. `sortAscending` (boolean, optional): Sort order (default: false/descending)

#### Basic Filtering Request:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_events",
  "params": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    38000000,
    null,
    ["CrcV1_Trust"],
    null,
    false
  ],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Advanced Filtering Request (Phase 1):

Query events with value greater than 1 CRC:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
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
    false
  ],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Multiple Predicates Example:

Query transfers to specific addresses with amount range:

```bash
curl -X POST --data '{
  "jsonrpc": "2.0",
  "method": "circles_events",
  "params": [
    null,
    38000000,
    null,
    ["CrcV2_TransferSingle"],
    [
      {
        "column": "to",
        "filterType": "In",
        "value": ["0xabc...", "0xdef...", "0x123..."]
      },
      {
        "column": "value",
        "filterType": "GreaterThanOrEquals",
        "value": "100000000000000000"
      }
    ],
    false
  ],
  "id": 1
}' -H "Content-Type: application/json" http://localhost:5000/
```

#### Supported FilterTypes (Phase 1):

- `Equals`: Exact match
- `NotEquals`: Not equal to
- `GreaterThan`: Greater than (numeric)
- `GreaterThanOrEquals`: Greater than or equal (numeric)
- `LessThan`: Less than (numeric)
- `LessThanOrEquals`: Less than or equal (numeric)
- `Like`: SQL LIKE pattern match (case-sensitive)
- `ILike`: SQL ILIKE pattern match (case-insensitive)
- `NotLike`: SQL NOT LIKE
- `In`: Value in array
- `NotIn`: Value not in array
- `IsNull`: Column is NULL
- `IsNotNull`: Column is NOT NULL

#### Response:

```json
{
  "jsonrpc": "2.0",
  "result": {
    "events": [
      {
        "blockNumber": 38000001,
        "transactionHash": "0x1234567890abcdef",
        "logIndex": 0,
        "event": "CrcV1_Transfer",
        "payload": {
          "blockNumber": 38000001,
          "transactionIndex": 0,
          "logIndex": 0,
          "transactionHash": "0x1234567890abcdef",
          "from": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          "to": "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
          "amount": "1000000000000000000",
          "tokenAddress": "0xabc..."
        }
      }
    ]
  },
  "id": 1
}
```

#### Notes:

- All predicates are combined with AND logic
- Maximum 1000 events returned
- Predicates use parameterized queries (SQL injection safe)
- Backward compatible with existing simple filtering