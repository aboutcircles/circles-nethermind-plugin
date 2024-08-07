## Circles V2 RPC examples

1. [circlesV2_getTotalBalance](#circlesV2_getTotalBalance) 
2. [circlesV2_getTokenBalances](#circlesV2_getTokenBalances)
3. [circles_query](#circles_query)  
3.1. [Get the transaction history of a wallet](#get-the-transaction-history-of-a-wallet)    
3.2. [Get a list of Circles avatars](#get-a-list-of-circles-users)  
3.3. [Get the trust relations between avatars](#get-the-trust-relations-between-avatars)  

### circlesV2_getTotalBalance

This method allows you to query the total Circles (v2) holdings of an address.

#### Request:

```shell
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circlesV2_getTotalBalance",
    "params":["0xc661fe4ce147c209ea6ca66a2a2323b69791a463"],
    "id":1
}' -H "Content-Type: application/json" https://chiado-rpc.aboutcircles.com/
````

##### Response:

```json
{
  "jsonrpc": "2.0",
  "result": "5444258229585459544466",
  "id": 1
}
```

### circlesV2_getTokenBalances

This method allows you to query all individual Circles (v2) holdings of an address.

#### Request:

```shell
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circlesV2_getTokenBalances",
    "params":["0xc661fe4ce147c209ea6ca66a2a2323b69791a463"],
    "id":1
}' -H "Content-Type: application/json" https://chiado-rpc.aboutcircles.com/
```

##### Response:

```json
{
  "jsonrpc": "2.0",
  "result": [
    {
      "tokenId": "0x25548e3e36c2d1862e4f7aa99a490bf71ed087ca",
      "balance": "999602703486168722"
    },
    {
      "tokenId": "0xc661fe4ce147c209ea6ca66a2a2323b69791a463",
      "balance": "5998013477961872802"
    }
  ],
  "id": 1
}
```

### circles_query

#### Get the transaction history of a wallet

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
}' -H "Content-Type: application/json" https://chiado-rpc.aboutcircles.com/
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

#### Get a list of Circles avatars

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
}' -H "Content-Type: application/json" https://chiado-rpc.aboutcircles.com/
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

#### Get the trust relations between avatars

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
}' -H "Content-Type: application/json" https://chiado-rpc.aboutcircles.com/
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
      "trustee",
      "truster",
      "expiryTime"
    ],
    "Rows": [
      [
        9819804,
        1715909835,
        0,
        0,
        "0x41670ceb0bd544f69a6c41ab5390df4ea3ae782cf89b693ac0d7908999bd2f47",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "0x25548e3e36c2d1862e4f7aa99a490bf71ed087ca",
        "79228162514264337593543950335"
      ],
      [
        9819786,
        1715909745,
        0,
        0,
        "0x627cb6702e4d357431f7faf0f627416285ee81c84f2175d3cf9d9866c5ad880c",
        "0xae3a29a9ff24d0e936a5579bae5c4179c4dff565",
        "0xabab7fccac344519639449f843d966b24730836d",
        "79228162514264337593543950335"
      ]
    ]
  },
  "id": 1
}
```