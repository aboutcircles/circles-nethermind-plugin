## Circles V1 RPC examples

1. [circles_getTotalBalance](#circles_getTotalBalance)
2. [circles_getTokenBalances](#circles_getTokenBalances)
3. [circles_getTrustRelations](#circles_getTrustRelations) (deprecated)
4. circles_query  
   4.1. [Get the trust relations between avatars](#get-the-trust-relations-between-avatars)  
   4.2. [Get the transaction history of a wallet](#get-the-transaction-history-of-a-wallet)

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

#### Get the trust relations between avatars

Find all incoming and outgoing trust relations of a Circles V2 avatar:

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_CrcV1",
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
                "Column": "user",
                "Value": "0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37"
              },
              {
                "Type": "FilterPredicate",
                "FilterType": "Equals",
                "Column": "canSendTo",
                "Value": "0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37"
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
      "user",
      "canSendTo",
      "limit"
    ],
    "Rows": [
      [
        5025124,
        1689590970,
        0,
        1,
        "0xc897be855814de0bde352aa2e6bc0d6caa8c8277b7eb08efae51db9ccc541c3c",
        "0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37",
        "0x965090908dcd0b134802f35c9138a7e987b5182f",
        "100"
      ],
      [
        5024850,
        1689589595,
        1,
        1,
        "0x9ebd06dcadd51c03344550172436496adbe456a83a21e952cc2af1a2809febfb",
        "0x965090908dcd0b134802f35c9138a7e987b5182f",
        "0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37",
        "100"
      ],
      [
        5023748,
        1689584085,
        1,
        2,
        "0xd38b98210c54b3f4b8596fffc69f5868c5625d2fb956e2266eafcedb2f4528c1",
        "0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37",
        "0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37",
        "100"
      ]
    ]
  },
  "id": 1
}
```

#### Get the transaction history of a wallet

Query the 10 most recent Circles V1 transfers from or to an address:

```shell
curl -X POST --data '{
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
      ],
      [
        6690257,
        1698156305,
        0,
        2,
        "0x3670bdf7e01328acab57afbf3b7d65382e27b2629ad338ca0761eb2566735330",
        "0xec21a5c94343cc26485916daab84a02328104271",
        "0xc5d6c75087780e0c18820883cf5a580bb3a4d834",
        "818078449864134800"
      ]
    ]
  },
  "id": 1
}
```