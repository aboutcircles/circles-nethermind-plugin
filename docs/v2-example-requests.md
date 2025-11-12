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
# Query PaymentReceived events from the CrcV2 PaymentGateway
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV2_PaymentGateway",
      "Table": "PaymentReceived",
      "Columns": [],
      "Filter": [],
      "Order": []
    }
  ]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/
```

```shell
# Calculate a path between two addresses with a target flow.
curl 'http://localhost:5000/' \
 -H 'Content-Type: application/json' \
 --data-raw '{"jsonrpc":"2.0","id":0,"method":"circlesV2_findPath","params":[{"Source":"0x749c930256b47049cb65adcd7c25e72d5de44b3b","Sink":"0xde374ece6fa50e781e81aac78e811b33d16912c7","TargetFlow":"99999999999999999999999999999999999","EnableGroupMinting":true}]}'
```

```shell
# Check the health status of the Circles service.
curl 'http://localhost:8545/' \
 -H 'Content-Type: application/json' \
 --data-raw '{"jsonrpc":"2.0","id":0,"method":"circles_health","params":[]}'
```

```shell
# Calculate a path that swaps one token into another (circular path)
curl 'http://localhost:5000/' \
  -H 'Content-Type: application/json' \
  --data-raw '{
  "jsonrpc": "2.0",
  "id": 0,
  "method": "circlesV2_findPath",
  "params": [
    {
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
    }
  ]
}' | jq > path_response.json

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
  "method": "circles_getTokenBalances",
  "params": ["0x7cadf434b692ca029d950607a4b3f139c30d4e98"]
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq > balance_from_mem_dev.json
```

```shell
# A slower method to query the balance breakdown of a specific avatar address (but with values directly taken from the rpc instead of the index).
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getTokenBalances",
    "params":["0x7cadf434b692ca029d950607a4b3f139c30d4e98"],
    "id":1
}' -H "Content-Type: application/json" https://rpc.aboutcircles.com/ | jq > balance_from_rpc_live.json
```

```shell
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getAvatarInfo",
    "params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
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
    "params":["0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7"],
    "id":1
}' -H "Content-Type: application/json" https://rpc.circlesubi.network/ | jq
```

```shell
# Query profiles by address in batch.
curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getProfileByAddressBatch",
    "params":[["0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7", "0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7", "0xf712d3b31de494b5c0ea51a6a407460ca66b12e8", null, "0xde374ece6fa50e781e81aac78e811b33D16912C7", "0xde374ece6fa50e781e81aac78e811b33D16912C7"]],
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
        "params":  ["0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7", 10, 0]
      }' | jq
```

```shell
curl -X POST --data '{
"jsonrpc":"2.0",
	"method":"circles_getProfileByAddressBatch",
	"params":[[
	"0x15ef9fb2a81140655192eb83e2ac88f44a99a9b6",
	"0x1f689e9a6f075dac566d5c93419a3fd372dee154",
	"0x6d0f0bf40c9a81d9272ed4317ecf456bfa1dc8c0",
	"0xc8f362fe25c73e6662aaaf490dea9b50b4fe397f",
	"0x19674f4be5b0321160391d573acf329cec016498",
	"0xd38740de1e02041bcf40c247e18ef465ea9e6e3e",
	"0x2630783d26ede6eb5ae7f80a34c27c7a7bf78858",
	"0x936b34e7f08d8e3fdadda4a71fc1f4f4107fb46d",
	"0xbb1c4afbeba583619323275ce9ed31ce4e91bd7e",
	"0xa573f367fbec4e8f98a225c7c07cf6d9b53b9ea2",
	"0xceb31a705f189940f09e986a4c51a48544ac126a",
	"0xcc016d0cda9b0545ab889455a64d038265d9ff52",
	"0x4d9145def1647eff0136205ab3034f5297b524ac",
	"0x89d6e227a3fdb9ee2ca0b51255fa18e9abac41a4",
	"0x008c3a081e7991e4d4280f56dc7af615356f13b0",
	"0x132182b8f9fcc54f8a38194557613e170a70a50e",
	"0x63c343decadce952c9948d07e3144a9450da3ab7",
	"0x08eb6b76d27f0e6ff90a8f114f2ee1ae59527f21",
	"0x08a8ea1a1c76819727be7b4a508effc35836d966",
	"0x415e144f6c21bae8dd05a7a76c15ea417aa0230c",
	"0x80976a2394461daaf5a480f2d1d9ee04fa64c2b5",
	"0xc7988cf4d3eca6676aa65848fe6a9d660bbd839e",
	"0xd3a5e2e06f36fda6ee4b09d87620f89bdf7b20f3",
	"0xb59f7ad40c58d25aaf1e82f32ba4e6610420558a",
	"0x9314da847b8ae3721bb51504483fa96847265684",
	"0x4f7c8ffd11b0087c1f473b28b005dbf0a418591b",
	"0x298d58b75671db36a8ef04c06a8e2e9adb0f6138",
	"0x37417ad34728fd8c255be73264609e66078094dd",
	"0x8dd07bde1f1a4a03fb7930c26ab223124f9964ac",
	"0xccdba6d1a403c1864305e2618f836aaad082b9ad",
	"0xcd5417232221ac16b557cc648760076a665182be",
	"0x0b0c3d8bd2276938bad1e450202cc39cb5797c12",
	"0x62d50e80722003c2580032dcd731a2642fb4a0c8",
	"0x1f92251e3de5578116f24ba31a97060870878ad6",
	"0xfa1955d6414dafd6dc394c9001db6a80c85fe49f",
	"0xce5270a8f129cbf9d5bc21ec4532c58e17bb1538",
	"0xc4057109e385a756c098787b22b0675a9a62ef54",
	"0x17b1163bcafdf9b0231244f10293f42af0c780e6",
	"0xbb5c084b9e20400e0261569014de3db46cf6ddf0",
	"0xa6cb0b6f65bb608cc57cf046ce4795663be44aac",
	"0x31924fc6824631181202feca9ea5841250bb4d79",
	"0x0c2af634e825a260467ddb4f68ecd15d539bf609",
	"0x042486aa3cb46ae01330441703e58a526ca5dff7",
	"0xc049240aa5cd7a5441e29e37d33f7f9c6bc3183e",
	"0x48b7d080e9487968b2a03ff9c600250d99415a1f",
	"0x1648610b8d538a3ce6f74697e4eabb000bc1cf7a",
	"0x96de9a5d2ab3d915adddb94f0b8b924e7d9fca2e",
	"0x34163ea5409f2b54b09362a4a4a82aecd3cfdb95",
	"0xca03f9afb199d9fb7b1db387d51ecdc858a058e4",
	"0xd1d9a50651cdb1a9a0788a3a572d79d8481fd72d",
	"0x0e8c9e403f19090970bd1f5d03d99d1f09fc88b1",
	"0x5be4b7ada9cf4f1d58c61001607cbc09c9c55669",
	"0x4ad02f9dde8033310e2b38e9ccfc52887fdde36b",
	"0x22bc57817901cfed69d67d49d9bdd92147f207c6",
	"0x7cd8c7a036c18cf14c4c2eb1bb4316478097b71f",
	"0x12d00219ab1d36bc8811cc016dcd4815681b2c30",
	"0x3e2f4172b595217d1d5e29b266da2eda25cc9bb8",
	"0xdb7061c34b3606fe3272a7317119f04c6fa745b2",
	"0xcd6713f51c3f751a99e8469776f7313468a70bb2",
	"0x88c6c2ad4d415466d74269fce555f96e3b6d7305",
	"0xd9b44948a3fd05108beb6cd9484369e842a621e2",
	"0xf4c2f5a4360daeea8fe69cf677fe654111e4824a",
	"0xb8c7273a41d3a1aad38cd69991956a9a7d9ab47b",
	"0x0180f81014d10543a6002e796d1f0ee5787e84cf",
	"0x6622ba0df7bdb7b80b66c1d6281104a8f43c6e5e",
	"0xe1104e5c287ee7934cee68b0dab141b8c9e67ace",
	"0x31ad9e0b98a73612f2b57a4104b209699c09a46a",
	"0xecb3b3bfb637454c7c2d81b3c1f641418c17ccb7",
	"0x604eea464cdcaa612bc8b43a3646a8816809fea7",
	"0x7ce3b7bee44a23b28e21bb43afaa284c5563cb89",
	"0xd7aeb8ef6bc73effde18fd0d2a152b523ab549af",
	"0x05a2f664e734442d85fae39e7e1a26a507230d5d",
	"0x04a02581a5f032ce8b850e74acee7ed28ee351a3",
	"0x4477520b7f8023535eb891f45218f9ecbd0cfcfa",
	"0x330d89c9bbb07cd63c13d18583da1c6e71e52527",
	"0xc0aa0b6a86f1880b6a76096fa2483ac49d4f9619",
	"0xbcaaab068caf7da7764fa280590d5f5b2fc75d73",
	"0xa3a8aaa3ddf1ab3eb2aa06a05a787aa0f866807d",
	"0xc1f6916bb4a98fbba4248fe86d7fdc0922fc5a7c",
	"0x2dc3f60e8e920359b4145c222edf634428a449db",
	"0x6f7b7c2f7046f579a4a91c1c4a5eb41d0bcd90a3",
	"0x6deebedcf083d2d0aef30c31c21ec1aebaaa130a",
	"0x670f3e14e205e97ed63ca759c018ce6936d7ff37",
	"0xca9727ff50ea68af1e3a6ba14e654eff7e75939a",
	"0x7debae26a727278a3ad82c00c8f7b7484fa34496",
	"0x7e6bffb11bb73d7cef3c2c2c14474f606fd91da4",
	"0x44aa753bebfda719abac73ba619493a8c3ad8dfd",
	"0x19fffb7fd446d99c85ee5dcb89959a1b5c68114b",
	"0x3dc90067dadc23770770fc8f7f0d76ad51978bc4",
	"0xd47a6bfa995f3dc166b27576e8bd60a96b7b9ea5",
	"0xa3685110968e39ab538c2f5cd976f6e0aed6031e",
	"0x59c9c9e3c64de3db2614ec13abb404bebaa00001",
	"0xc3cc95a48be295bdcd62bef8533f56ee8dd96542",
	"0xd95c643b44d8a21f5c306af4cdd868b4248b1106",
	"0x7f288b4b4fe35c82bc431d1d9b2727c220dd561b",
	"0x32d7dd5f935980cadedb2b92660057de203e70dd",
	"0x33c7ad77ff087b97213c8ee4be5e4565c717a496",
	"0x6dbd32bc9a058311b59d89cb50cd0801312a84ff",
	"0xf908b9e395ed3f2eb923b7f74b6d74580f5b70d9",
	"0xf908b9e395ed3f2eb923b7f74b6d74580f5b70d9"
]],
	"id":1
}' -H "Content-Type: application/json" http://localhost:5000/
```