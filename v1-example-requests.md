## Circles V1 RPC methods

1. [circles_getTotalBalance](#circles_getTotalBalance)
2. [circles_getTokenBalances](#circles_getTokenBalances)
3. [circles_getTrustRelations](#circles_getTrustRelations)

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