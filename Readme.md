# Circles Nethermind Plug-in

A [Nethermind](https://www.nethermind.io/nethermind-client) plugin to index and
query [Circles](https://www.aboutcircles.com/) protocol events.

## Quickstart

The repository contains a docker-compose file to start a Nethermind node with the Circles plugin installed. There are
configurations for Gnosis Chain, Chiado and Spaceneth (a local testnet).

The quickstart configurations use [lighthouse](https://github.com/sigp/lighthouse) as consensus engine and spin up a
postgres database to store the indexed data. The spaceneth configuration comes with a local blockscout instance.

### 1. Clone the repository

```bash
git clone https://github.com/CirclesUBI/circles-nethermind-plugin.git
cd circles-nethermind-plugin
```

### 2. Create a jwtsecret

For the use with Gnosis Chain and Chiado, a shared secret is required to authenticate requests between the execution and
consensus engine.

```bash
# Gnosis Chain
mkdir -p ./.state/jwtsecret-gnosis
openssl rand -hex 32 > ./.state/jwtsecret-gnosis/jwt.hex
```

```bash
# Chiado
mkdir -p ./.state/jwtsecret-chiado
openssl rand -hex 32 > ./.state/jwtsecret-chiado/jwt.hex
```

### 3. Set up the .env file

Copy the `.env.example` file to `.env` and adjust the values to your needs.

```bash
cp .env.example .env
```

### 4. Run node

Choose if your want to run your node on Gnosis Chain or Chiado.

```bash
# Gnosis chain
docker compose -f docker-compose.gnosis.yml up -d
```

```bash
# Chiado
docker compose -f docker-compose.chiado.yml up -d
```

That's it! The node must be fully synced before you can start querying the Circles events.
Once synced you can use the node like any other RPC node, but with the added benefit of querying Circles events directly
at the same RPC endpoint.

#### Ports:

* `30303/tcp` (nethermind p2p)
* `30303/udp` (nethermind p2p)
* `8545/tcp` (nethermind rpc)
* `5432/tcp` (postgres)
* `9000/tcp` (consensus p2p)
* `9000/udp` (consensus p2p)
* `5054/tcp` (consensus metrics)

#### Volumes

* `./.state` - Directory containing all host mapped docker volumes
    * `./.state/consensus-chiado|consensus-chiado` - Lighthouse consensus engine data
    * `./.state/nethermind-chiado|nethermind-gnosis` - Nethermind data
    * `./.state/postgres-chiado|postgres-gnosis` - Postgres data
    * `./.state/jwtsecret-chiado|jwtsecret-gnosis` - Shared secret between execution and consensus engine

### Spaceneth

The process of setting up a local only node is a bit more involved. However, by using this approach, you gain
the possibility to manipulate the node's time and don't need any xDai, which is useful for testing purposes.

```bash
docker compose -f docker-compose.spaceneth.yml up -d
```

#### Deploying the Circles contracts

Since a new spaceneth node is empty except for the genesis block, you need to deploy the Circles contracts yourself.

```bash
# Clone the Circles contracts submodules
git submodule update --init --recursive
```

```bash
# Deploy the contracts
npm install && ./deploy.sh
```

## Circles RPC methods

### circles_getTotalBalance

This method allows you to query the total Circles (v1) holdings of an address.

#### Request:

```shell
curl -X POST --data '{
"jsonrpc":"2.0",
"method":"circles_getTotalBalance",
"params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
"id":1
}' -H "Content-Type: application/json" https://circles-rpc.aboutcircles.com/
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
"params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
"id":1
}' -H "Content-Type: application/json" httpS://circles-rpc.aboutcircles.com/
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
"params":["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
"id":1
}' -H "Content-Type: application/json" https://circles-rpc.aboutcircles.com/
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

This method allows you to query Circles events. The method takes a single parameter, which is a JSON object with the
following properties:

* `namespace` - The protocol namespace to query (System, CrcV1 or CrcV2).
* `table` - The table to query (e.g. `Signup`, `Trust`, etc.).
* `columns` - An array of column names to return or `[]` to return all columns of the table.
* `filter` - Filters that can be used e.g. for pagination or to search for specific values.
* `order` - A list of columns to order the results by.
* `distinct` - If set to `true`, only distinct rows are returned.
* `limit` - The maximum number of rows to return (defaults to max. 1000).

*There is no default order, so make sure to always add sensible order columns.*

#### Available namespaces, tables and columns

Every table has at least the following columns:

* `blockNumber` - The block number the event was emitted in.
* `timestamp` - The unix timestamp of the event.
* `transactionIndex` - The index of the transaction in the block.
* `logIndex` - The index of the log in the transaction.

Tables for batch events have an additional `batchIndex` column.
The items of a batch are treated like individual events that can only be distinguished by the `batchIndex`.

Namespaces and tables:

* `System`
    * `Block`
* `CrcV1`
    * `HubTransfer`
    * `OrganizationSignup`
    * `Signup`
    * `Transfer`
    * `Trust`
* `CrcV2`
    * `ApprovalForAll`
    * `DiscountCost`
    * `InviteHuman`
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

#### Available filter types

* `Equals`
* `NotEquals`
* `GreaterThan`
* `GreaterThanOrEquals`
* `LessThan`
* `LessThanOrEquals`
* `Like`
* `NotLike`
* `In`
* `NotIn`

#### Pagination

You can use the combination of `blockNumber`, `transactionIndex` and `logIndex` 
(+ `batchIndex` in the case of batch events) together with a `limit` to paginate through the results.

#### Example

Query the last two Circles signups:

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "CrcV1",
      "Table": "Signup",
      "Limit": 2,
      "Columns": [],
      "Filter": [],
      "Order": [
        {
          "Column": "blockNumber",
          "SortOrder": "ASC"
        },
        {
          "Column": "transactionIndex",
          "SortOrder": "ASC"
        },
        {
          "Column": "logIndex",
          "SortOrder": "ASC"
        }
      ]
    }
  ]
}' -H "Content-Type: application/json" https://localhost:8545/
```

##### Response:

```json
{
  "jsonrpc": "2.0",
  "result": {
    "columns": [
      "blockNumber",
      "timestamp",
      "transactionIndex",
      "logIndex",
      "transactionHash",
      "user",
      "token"
    ],
    "rows": [
      [
        "0x597343",
        "0x64f5aa5a",
        "0x0",
        "0x3",
        "0xb41462160f73af912b550b27a7ed31e091d5da6c59a6325b367048ea42eef47f",
        "0x4bc38a9f15508d19299a45b063556ec4bee853ff",
        "0xcc724001786fcf8414747dd598e8e9383882b6d7"
      ],
      [
        "0x597343",
        "0x64f5aa5a",
        "0x0",
        "0x3",
        "0xb41462160f73af912b550b27a7ed31e091d5da6c59a6325b367048ea42eef47f",
        "0x4bc38a9f15508d19299a45b063556ec4bee853ff",
        "0xcc724001786fcf8414747dd598e8e9383882b6d7"
      ]
    ]
  },
  "id": 1
}

```