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
      "invitedBy",
      "avatar",
      "tokenId",
      "name",
      "cidV0Digest"
    ],
    "Rows": [
      [
        9819751,
        1715909570,
        0,
        1,
        "0x408796aed78e7743b0c4851a003dfb343987a206ddb9bab9ee8d87f6f34c4224",
        "group",
        null,
        "0xabab7fccac344519639449f843d966b24730836d",
        "0xabab7fccac344519639449f843d966b24730836d",
        "Hans Peter Meier Wurstwaren GmbH",
        "0x0e7071c59df3b9454d1d18a15270aa36d54f89606a576dc621757afd44ad1d2e"
      ],
      [
        9814715,
        1715884250,
        0,
        1,
        "0x562dc2ba8edadecdffccecd4510a75135399af05878b258e367ec2bd1150a4c8",
        "organization",
        null,
        "0xfe7b2837dac1848248cbfb0f683d8e178050ba1b",
        null,
        "Peter",
        "0x0e7071c59df3b9454d1d18a15270aa36d54f89606a576dc621757afd44ad1d2e"
      ],
      [
        9814708,
        1715884215,
        0,
        2,
        "0x1ebcefbea5db55a1156a42bad494af2baebd4943c8dd73f943333f2cee39b3aa",
        "human",
        null,
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

##### Get the trust relations between avatars

Find all incoming and outgoing trust relations of a Circles V2 avatar:

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_CrcV2",
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