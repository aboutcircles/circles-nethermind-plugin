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
    }' -H "Content-Type: application/json" https://circles-rpc.circlesubi.id/
    ```

* `circles_getTokenBalances(Address address)`
    ```shell
    curl -X POST --data '{
    "jsonrpc":"2.0",
    "method":"circles_getTokenBalances",
    "params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
    "id":1
    }' -H "Content-Type: application/json" https://circles-rpc.circlesubi.id/
    ```

  * `circles_getTrustRelations(Address address)`
      ```shell
      curl -X POST --data '{
      "jsonrpc":"2.0",
      "method":"circles_getTrustRelations",
      "params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
      "id":1
      }' -H "Content-Type: application/json" https://circles-rpc.circlesubi.id/
      ```

    * `circles_queryTrustEvents(CirclesTrustQuery query)`
        ```shell
      curl -X POST --data '{
          "jsonrpc":"2.0",
          "method":"circles_query",
          "id":1,
          "params": [{
            "Table": 12,
            "Columns": [
              1,
              36,
              30,
              6,
              11,
              34,
              24
            ],
            "Conditions": [
              {
                "Type": "LessThan",
                "Column": 24,
                "Value": "500000000000000000000",
                "Elements": null
              },
              {
                "Type": "Equals",
                "Column": 11,
                "Value": "0x0000000000000000000000000000000000000000",
                "Elements": null
              }
            ]
        }]
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
