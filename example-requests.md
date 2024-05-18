## Circles RPC methods

### circles_getTotalBalance

This method allows you to query the total Circles (v1) holdings of an address.

#### Request:

```shell
curl -X POST --data '{
"jsonrpc":"2.0",
"method":"circles_getTotalBalance",
"params":["0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c"],
"id":1
}' -H "Content-Type: application/json" http://localhost:8545/
````

##### Response:

```json
{
  "jsonrpc": "2.0",
  "result": "5444258229585459544466",
  "id": 1
}
```

### circles_getTokenBalance

This method allows you to query all individual Circles (v1) holdings of an address.

#### Request:

```shell
curl -X POST --data '{
"jsonrpc":"2.0",
"method":"circles_getTokenBalances",
"params":["0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c"],
"id":1
}' -H "Content-Type: application/json" http://localhost:8545/
```

##### Response:

```json
{
  "jsonrpc": "2.0",
  "result": [
    {
      "token": "0x057f55e194b94073d2dfa4e86163c2e897086dc7",
      "balance": "51429863673363442586"
    },
    {
      "token": "0x09c85ee337f6f7bdd3e5e17213b703c26d9c907d",
      "balance": "56223473812572527629"
    }
  ],
  "id": 1
}
```

### circles_getTrustRelations

This method allows you to query all (v1) trust relations of an address.

#### Request:

```shell
curl -X POST --data '{
"jsonrpc":"2.0",
"method":"circles_getTrustRelations",
"params":["0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c"],
"id":1
}' -H "Content-Type: application/json" http://localhost:8545/
````

##### Response:

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

### circles_query

##### Get the transaction history of a wallet

Query the 10 most recent Circles V2 transfers from or to an address:

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_CrcV2",
      "Table": "Transfers",
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
                "Value": "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565"
              },
              {
                "Type": "FilterPredicate",
                "FilterType": "Equals",
                "Column": "to",
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
        },
        {
          "Column": "batchIndex",
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
      "batchIndex",
      "transactionHash",
      "operator",
      "from",
      "to",
      "id",
      "value"
    ],
    "Rows": [
      [
        9817761,
        1715899520,
        0,
        0,
        0,
        "0x3b3f9cfebdd164bf53cfcb1fe4c163f388712f566576edf6ac1f2c55da95929a",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "0x0000000000000000000000000000000000000000",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "994661466795450363997821247051269595921846891877",
        "4000000000000000000"
      ],
      [
        9817761,
        1715899520,
        0,
        0,
        0,
        "0x3b3f9cfebdd164bf53cfcb1fe4c163f388712f566576edf6ac1f2c55da95929a",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "0x0000000000000000000000000000000000000000",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "994661466795450363997821247051269595921846891877",
        "4000000000000000000"
      ]
    ]
  },
  "id": 1
}
```


##### Get a list of Circles users

Query latest 10 Circles V2 registrations:

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_CrcV2",
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
      "type",
      "avatar",
      "tokenId",
      "name",
      "cidV0Digest"
    ],
    "Rows": [
      [
        9817945,
        1715900445,
        0,
        1,
        "0x4a72ef04c38d4f4759534fc4666e1cd5e3338ed7dbfb938a58155efe676e895d",
        "group",
        "0xd1911ecd477a08df036da356006cf75dbded32b3",
        "0xd1911ecd477a08df036da356006cf75dbded32b3",
        "Peter Group",
        "0x0e7071c59df3b9454d1d18a15270aa36d54f89606a576dc621757afd44ad1d2e"
      ],
      [
        9814708,
        1715884215,
        0,
        2,
        "0x1ebcefbea5db55a1156a42bad494af2baebd4943c8dd73f943333f2cee39b3aa",
        "human",
        "0xb3f0882a345dfbdd2b98c833b9d603d42e18fe21",
        "0xb3f0882a345dfbdd2b98c833b9d603d42e18fe21",
        null,
        "0x0e7071c59df3b9454d1d18a15270aa36d54f89606a576dc621757afd44ad1d2e"
      ]
    ]
  },
  "id": 1
}
```