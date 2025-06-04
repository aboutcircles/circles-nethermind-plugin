```shell
# Get the total v2 Circles balance of an account
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circlesV2_getTotalBalance",
    "params":["0xcadd4ea3bcc361fc4af2387937d7417be8d7dfc2"],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/
````

```shell
# Find all incoming and outgoing trust relations of a Circles V2 avatar:
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
      "Order": []
    }
  ]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/
```

```shell
# Calculate a path between two addresses with a target flow.
curl 'https://rpc.circlesubi.network/' \
 -H 'Content-Type: application/json' \
 --data-raw '{"jsonrpc":"2.0","id":0,"method":"circlesV2_findPath","params":[{"Source":"0x749c930256b47049cb65adcd7c25e72d5de44b3b","Sink":"0xde374ece6fa50e781e81aac78e811b33d16912c7","TargetFlow":"99999999999999999999999999999999999"}]}'
```

```shell
# Return all available namespaces and tables which can be queried by `circles_query`.
curl 'https://rpc.circlesubi.network/' -H 'Content-Type: application/json' --data-raw '{"jsonrpc":"2.0","id":0,"method":"circles_tables","params":[]}' | jq
```

```shell
# Query all events of type CrcV1_Trust from block 38000000 to the latest block.
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [
    null,
    38000000,
    null,
    ["CrcV1_Trust"],
    null,
    false
  ]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/
```

```shell
# Query the common trust relations of two addresses (only common outgoing trust relations are considered).
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_getCommonTrust",
  "params": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7", "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"
  ]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/
```

```shell
# A fast method to query the balance breakdown of a specific avatar address.
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_getBalanceBreakdown",
  "params": ["0x14b31a052964143b4c455e3650164ff6c91f81be"]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# A slower method to query the balance breakdown of a specific avatar address (but with values directly taken from the rpc instead of the index).
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getTokenBalances",
    "params":["0xc6d075112b96b75460e543e3bf70be9ab45b62d9"],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getAvatarInfoBatch",
    "params":[["0xde374ece6fa50e781e81aac78e811b33d16912c7"]],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Get information about a specific token (0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e)
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_Crc",
      "Table": "Tokens",
      "Columns": [
         "blockNumber",
         "timestamp",
         "transactionIndex",
         "logIndex",
         "transactionHash",
         "version",
         "type",
         "token",
         "tokenOwner"
      ],
      "Filter": [{
         "Type": "FilterPredicate",
         "FilterType": "Equals",
         "Column": "token",
         "Value": "0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e"
      }],
      "Order": [
      ]
    }
  ]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Get a profile by it's CID.
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getProfileByCid",
    "params":["Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W"],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Get many profiles by CID.
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getProfileByCidBatch",
    "params":[["Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W",null , "QmZuR1Jkhs9RLXVY28eTTRSnqbxLTBSoggp18Yde858xCM", "QmanRNbDjbiSFdxcYT9S9wpk3gaCVnM81MVAHkmJj6AqE5"]],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Query the profile for an avatar address.
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getProfileByAddress",
    "params":["0x5d033356cf431207ac72f3b48a86db0ebbcd6fdf"],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Query profiles by address in batch.
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getProfileByAddressBatch",
    "params":[["0x5d033356cf431207ac72f3b48a86db0ebbcd6fdf", "0x0e50fc4e7d629bc5edd69b6dddb3c22c6e60704b", "0xf712d3b31de494b5c0ea51a6a407460ca66b12e8", null, "0xde374ece6fa50e781e81aac78e811b33D16912C7", "0xde374ece6fa50e781e81aac78e811b33D16912C7"]],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Who holds '0x42cedde51198d1773590311e2a340dc06b24cb37' token?
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_CrcV2",
      "Table": "BalancesByAccountAndToken",
      "Columns": [],
      "Filter": [{
         "Type": "FilterPredicate",
         "FilterType": "Equals",
         "Column": "tokenAddress",
         "Value": "0x42cedde51198d1773590311e2a340dc06b24cb37"
      }],
      "Order": [{
         "Column": "demurragedTotalBalance",
         "SortOrder": "DESC"
      }]
    }
  ]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Who backed their Circles with a Balancer LBP?
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_query",
    "params":[{
      "Namespace": "CrcV2",
      "Table": "CirclesBackingDeployed",
      "Columns": [
      ],            
      "Filter": [{
         "Type": "FilterPredicate",
         "FilterType": "Equals",
         "Column": "emitter",
         "Value": "0xeced91232c609a42f6016860e8223b8aecaa7bd0"
      }],        
      "Order": [],
      "Limit": 1000      
    }],            
    "id":1         
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/
```

```shell
# Download a full snapshot of the Circles network state (current trust relations and balances)
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getNetworkSnapshot",
    "params":[],            
    "id":1         
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Search profiles by name, description or address
curl -s -X POST https://rpc.circlesubi.network \
  -H 'Content-Type: application/json' \
  -d '{
        "jsonrpc": "2.0",
        "id":      1,
        "method":  "circles_searchProfiles",
        "params":  ["Ben", 10, 0]
      }' | jq
```