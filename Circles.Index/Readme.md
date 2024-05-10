# Circles Nethermind Plug-in
Indexes all Circles transactions to a SQLite database and provides a JSON-RPC module to query the data.

The plugin provides a JSON-RPC module to query the data. The following RPC methods are supported:

* `circles_getTotalBalance(Address address)`
    ```shell
    curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getTotalBalance",
    "params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id":1
    }' -H "Content-Type: application/json" http://localhost:8545/
    ```

* `circles_getTokenBalances(Address address)`
    ```shell
    curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getTokenBalances",
    "params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id":1
    }' -H "Content-Type: application/json" http://localhost:8545/
    ```

* `circles_getTrustRelations(Address address)`
    ```shell
    curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getTrustRelations",
    "params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id":1
    }' -H "Content-Type: application/json" http://localhost:8545/
    ```

* `circles_query(Select select)` 
    ```shell
  curl -X POST --data '{
      "jsonrpc":"2.0",
      "method":"circles_query",
      "id":1,
      "params": [{
        "Namespace": "CrcV1",
        "Table": "Signup",
        "Columns": [
          "blockNumber",
          "timestamp",
          "transactionIndex",
          "logIndex",
          "transactionHash",
          "user",
          "token"
        ]
  }' -H "Content-Type: application/json" http://localhost:8545/
  ```
* `circles_computeTransfer(string query)`
    ```shell
  curl -X POST --data '{
    "jsonrpc": "2.0",
        "method": "circles_computeTransfer",
        "params": [
          "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          "0xAEf690AD9Da7d54B880E3edC8E936518023086dD",
          "99999999999999999999999999999999999999",
        ],
        "id": 1
  }' -H "Content-Type: application/json" https://circles-rpc.circlesubi.id/
  ```
