# Circles Nethermind Plug-in

A [Nethermind](https://www.nethermind.io/nethermind-client) plugin to index and
query [Circles](https://www.aboutcircles.com/) protocol events.

* [Quickstart](#quickstart)
    * [Query a node](#query-a-node)
    * [Run a node](#run-a-node)
        * [Clone the repository](#1-clone-the-repository)
        * [Create a jwtsecret](#2-create-a-jwtsecret)
        * [Set up the .env file](#3-set-up-the-env-file)
        * [Run node](#4-run-node)
            * [Ports](#ports)
            * [Volumes](#volumes)
    * [Run a spaceneth node](#run-a-spaceneth-node)
        * [Deploying the Circles contracts](#deploying-the-circles-contracts)
        * [Blockscout](#blockscout)
        * [Get a funded account](#get-a-funded-account)
        * [Manipulate time](#manipulate-time)
        * [Reset the spaceneth node](#reset-the-spaceneth-node)
* [Circles RPC methods](#circles-rpc-methods)
    * [circles_getTotalBalance / circlesV2_getTotalBalance](#circles_gettotalbalance--circlesv2_gettotalbalance)
    * [circles_getTokenBalance / circlesV2_getTokenBalances](#circles_gettokenbalance--circlesv2_gettokenbalances)
    * [circles_query](#circles_query)
    * [circles_events](#circles_events)
    * [eth_subscribe("circles")](#eth_subscribecircles)
* [Add a custom protocol](#add-a-custom-protocol)
    * [DatabaseSchema.cs](#databaseschemacs)
        * [Tables](#tables)
        * [EventDtoTableMap](#eventdtotablemap)
        * [SchemaPropertyMap](#schemapropertymap)
    * [Events.cs](#eventscs)
    * [LogParser.cs](#logparsercs)
    * [Register the protocol](#register-the-protocol)
        * [Register the schema](#register-the-schema)
        * [Register the SchemaPropertyMap and EventDtoTableMap](#register-the-schemapropertymap-and-eventdtotablemap)
        * [Register the LogParser](#register-the-logparser)

## Quickstart

### Query a node

If you're just looking for a way to query Circles events, you can check out the query examples:

* [General examples](general-example-requests.md)
* [Circles v1 examples](v1-example-requests.md)
* [Circles v2 examples](v2-example-requests.md)

For a detailed description of the available RPC methods, see the [Circles RPC methods](#circles-rpc-methods) section.

### Run a node

The repository contains a docker-compose file to start a Nethermind node with the Circles plugin installed. There are
configurations for Gnosis Chain, Chiado and Spaceneth (a local testnet).

The quickstart configurations use [lighthouse](https://github.com/sigp/lighthouse) as consensus engine and spin up a
postgres database to store the indexed data. The spaceneth configuration comes with a local blockscout instance.

#### 1. Clone the repository

```bash
git clone https://github.com/CirclesUBI/circles-nethermind-plugin.git
cd circles-nethermind-plugin
```

#### 2. Create a jwtsecret

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

#### 3. Set up the .env file

Copy the `.env.example` file to `.env` and adjust the values to your needs.

```bash
cp .env.example .env
```

#### 4. Run node

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

##### Ports:

* `30303/tcp` (nethermind p2p)
* `30303/udp` (nethermind p2p)
* `8545/tcp` (nethermind rpc)
* `5432/tcp` (postgres)
* `9000/tcp` (consensus p2p)
* `9000/udp` (consensus p2p)
* `5054/tcp` (consensus metrics)

##### Volumes

* `./.state` - Directory containing all host mapped docker volumes
    * `./.state/consensus-chiado|consensus-chiado` - Lighthouse consensus engine data
    * `./.state/nethermind-chiado|nethermind-gnosis` - Nethermind data
    * `./.state/postgres-chiado|postgres-gnosis` - Postgres data
    * `./.state/jwtsecret-chiado|jwtsecret-gnosis` - Shared secret between execution and consensus engine

### Run a spaceneth node

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

To deploy the contracts, we add a script to the Circles contracts repository that deploys the contracts to the spaceneth
node.

```bash
# Add the deploy script to the Circles contracts repository
./add-deploy-script-tp-v2-repo.sh
```

As a last step, we need to replace the `tload` and `tstore` based reentrancy guards with a more classic approach.
Spaceneth does not support these instructions.

1. Open `circles-contracts-v2/src/hub/Hub.sol`
2. Replace this modifier:
   ```solidity
   modifier nonReentrant(uint8 _code) {
       assembly {
           if tload(0) { revert(0, 0) }
           tstore(0, 1)
       }
       _;
       assembly {
           tstore(0, 0)
       }
   }
   ```

   with this modifier:

   ```solidity
   bool private _reentrancyGuard;
   modifier nonReentrant(uint8 _code) {
       if (_reentrancyGuard) {
           revert CirclesReentrancyGuard(_code);
       }
       _reentrancyGuard = true;
       _;
       _reentrancyGuard = false;
   }
   ```
3. Open `circles-contracts-v2/foundry.toml`
4. Remove this line:
   ```toml
   evm_version = 'cancun'
   ```

Now you can deploy the contracts to the spaceneth node.

```bash
# Deploy the contracts
npm install && ./deploy.sh
```

#### Blockscout

You can access the blockscout instance at `http://localhost:4000`.

#### Get a funded account

You can get a funded account private key by running:

```bash
npm install
node createFundedAccount.js
```

#### Manipulate time

You can fast-forward the time by running:

```bash
curl -X POST -H "Content-Type: application/json" -d '{"fake_time": "+1d x1"}' http://localhost:5000/set_time
```

**Explanation:**

```json
{
  "fake_time": "+1d x1"
}
```

`+1d` means to offset the current time by 1 day. `x1` means that the time will pass as real time. If you want to
fast-forward the time, you can increase the number of `x` (e.g. `x10`).

_NOTE: This will restart the nethermind node._

#### Reset the spaceneth node

If you want to start over, you can reset the spaceneth node by running:

```bash
# Stop the stack
docker compose -f docker-compose.spaceneth.yml down
``` 

```bash
# Delete all persisted data
sudo rm -rf .state/nethermind-spaceneth
sudo rm -rf .state/postgres-spaceneth
sudo rm -rf .state/postgres2-spaceneth
sudo rm -rf .state/redis-spaceneth
```

```bash
# Start the stack again
docker compose -f docker-compose.spaceneth.yml up
```

## Circles RPC methods

The plugin extends the Nethermind JSON-RPC API with additional methods to query Circles events and aggregate values.

You can find concrete examples for all rpc-methods in the [v1-example-requests.md](v1-example-requests.md)
and [v2-example-requests.md](v2-example-requests.md) files.

### circles_getTotalBalance / circlesV2_getTotalBalance

These methods allow you to query the total Circles (v1/v2) holdings of an address.

**Signature**:

* `circles_getTotalBalance(address: string, asTimeCircles: bool = false)`.
* `circlesV2_getTotalBalance(address: string, asTimeCircles: bool = false)`.

#### Example

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_getTotalBalance",
  "params": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    true
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

#### Response

This method returns a string formatted BigInteger value. The value is the sum of all Circles holdings of the address.

If `asTimeCircles` is set to `true`, the value is formatted
as [TimeCircles](https://github.com/CirclesUBI/timecircles) floating point number instead of the raw BigInteger value.

### circles_getTokenBalance / circlesV2_getTokenBalances

These methods allow you to query all individual Circles (v1/v2) holdings of an address.

**Signature**:

* `circles_getTokenBalances(address: string, asTimeCircles: bool = false)`.
* `circlesV2_getTokenBalances(address: string, asTimeCircles: bool = false)`.

#### Example

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_getTokenBalances",
  "params": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    true
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

#### Response

This method returns an array of objects with the following properties:

* `tokenId` - The address of the token.
* `balance` - The balance of the token.
* `tokenOwner` - The address of the token owner.

If `asTimeCircles` is set to `true`, the value is formatted
as [TimeCircles](https://github.com/CirclesUBI/timecircles) floating point number instead of the raw BigInteger value.

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

_NOTE: There is no default order, so make sure to always add sensible order columns._

#### Example

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [
    {
      "Namespace": "V_Crc",
      "Table": "Avatars",
      "Limit": 100,
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

#### Response

The result is a JSON object that resembles a table with rows and columns:

* `Columns` - An array of column names.
* `Rows` - An array of rows, where each row is an array of values.

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
    * `CidV0` (predecessor of `URI` and `UpdateMetadataDigest`)
* `V_CrcV1`
    * `Avatars` (view combining `Signup` and `OrganizationSignup`)
    * `TrustRelations` (view filtered to represent all current `Trust` relations)
* `V_CrcV2`
    * `Avatars` (view combining `RegisterHuman`, `InviteHuman`, `RegisterGroup` and `RegisterOrganization`)
    * `TrustRelations` (view filtered to represent all current `Trust` relations)
    * `Transfers` (view combining `TransferBatch` and `TransferSingle`)
* `V_Crc`
    * `Avatars` (view combining `V_CrcV1_Avatars` and `V_CrcV2_Avatars`)
    * `TrustRelations` (view combining `V_CrcV1_TrustRelations` and `V_CrcV2_TrustRelations`)

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
(+ `batchIndex` in the case of batch events) together with a `limit` and order to paginate through the results.

### circles_events

Queries all events that involve a specific address. Can be used to e.g. easily populate a user's transaction history.

**Signature**: `circles_events(address: string, fromBlock: number, toBlock?: number)`.

The `fromBlock` and `toBlock` parameters can be used to filter the events by block number.
The `toBlock` parameter can be set to `null` to query all events from `fromBlock` to the latest block.

#### Example

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [
    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    30282299,
    null
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

#### Response

The response generally contains the following fields:

* `event` - The name of the event (See
  [Available namespaces, tables and columns](#available-namespaces-tables-and-columns) for available event types).
* `values` - The values of the event.

The values contain at least the following fields:

* `blockNumber` - The block number the event was emitted in.
* `timestamp` - The unix timestamp of the event.
* `transactionIndex` - The index of the transaction in the block.
* `logIndex` - The index of the log in the transaction.
* `transactionHash` - The hash of the transaction.

### eth_subscribe("circles")

Subscribes to all Circles events. The subscription is a stream of events that are emitted as soon as they've been
indexed. Can be filtered to just a specific address.

**Signature**: `eth_subscribe("circles", { address?: string })`.

#### Example

Copy the following code into an HTML file and open it in a browser to subscribe to Circles events.

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <title>WebSocket Circles Event Subscription</title>
</head>
<body>
<h1>WebSocket Circles Event Subscription</h1>
<div id="log"></div>

<script>
    class WebsocketConnection {
        constructor(url) {
            this.url = url;
            this.websocket = null;
            this.messageId = 0;
            this.pendingResponses = {};
            this.subscriptionListeners = {};
        }

        connect() {
            return new Promise((resolve, reject) => {
                this.websocket = new WebSocket(this.url);
                this.websocket.onopen = () => {
                    console.log('Connected');
                    resolve();
                };
                this.websocket.onmessage = (event) => {
                    const message = JSON.parse(event.data);
                    const {id, method, params} = message;
                    if (id !== undefined && this.pendingResponses[id]) {
                        this.pendingResponses[id].resolve(message);
                        delete this.pendingResponses[id];
                    }
                    if (method === 'eth_subscription' && params) {
                        const {subscription, result} = params;
                        if (this.subscriptionListeners[subscription]) {
                            this.subscriptionListeners[subscription].forEach(listener => listener(result));
                        }
                    }
                };
                this.websocket.onclose = () => console.log('Disconnected');
                this.websocket.onerror = (error) => {
                    console.error('WebSocket error:', error);
                    reject(error);
                };
            });
        }

        sendMessage(method, params, timeout = 5000) {
            if (!this.websocket || this.websocket.readyState !== WebSocket.OPEN) {
                return Promise.reject('WebSocket is not connected');
            }
            const id = this.messageId++;
            const message = {jsonrpc: "2.0", method, params, id};
            return new Promise((resolve, reject) => {
                this.pendingResponses[id] = {resolve, reject};
                this.websocket.send(JSON.stringify(message));
                setTimeout(() => {
                    if (this.pendingResponses[id]) {
                        this.pendingResponses[id].reject('Request timed out');
                        delete this.pendingResponses[id];
                    }
                }, timeout);
            });
        }

        async subscribe(method, params, listener) {
            const response = await this.sendMessage('eth_subscribe', [method, params]);
            const subscriptionId = response.result;
            if (!this.subscriptionListeners[subscriptionId]) {
                this.subscriptionListeners[subscriptionId] = [];
            }
            this.subscriptionListeners[subscriptionId].push(listener);
            return subscriptionId;
        }
    }

    const wsConnection = new WebsocketConnection('ws://localhost:8545');
    const logElement = document.getElementById('log');

    function log(message) {
        const messageElement = document.createElement('div');
        messageElement.textContent = message;
        logElement.appendChild(messageElement);
    }

    (async () => {
        try {
            await wsConnection.connect();
            log('Connected to websocket...');

            // Subscribe to all Circles events:
            const subscriptionArgs = JSON.stringify({});

            // Subscribe to events for a specific address:
            // const subscriptionArgs = JSON.stringify({"address": "0xde374ece6fa50e781e81aac78e811b33d16912c7"});

            const subscriptionId = await wsConnection.subscribe('circles', subscriptionArgs), (
            event
        ) =>
            {
                log(`Circles event: ${JSON.stringify(event)}`);
            }
        )
            ;
            log(`Subscribed with ID: ${subscriptionId}`);
        } catch (error) {
        }
    })();
</script>
</body>
</html>
```

#### Response

The emitted events are the same as the objects returned by the `circles_events` ([circles_events Response](#response-1))
method.

## Add a custom protocol

The plugin parses the log entries of all transaction receipts, filters them and stores them in a database.
To do so it needs the following information:

* The event topic
* The address of the contract that emits the event
* A table schema for the database

All the above information are packaged into an own assembly per protocol.
It's structured like this:

* [your-protocol].csproj
    * `DatabaseSchema.cs` - Pulls together all information about the indexed events of a protocol.
    * `Events.cs` - Contains the DTOs for the events (usually just Records).
    * `LogParser.cs` - Extracts events from the transaction receipt logs.

### DatabaseSchema.cs

The schema pulls together all information about the indexed events of a protocol. Each event type must have a
corresponding table in the database. Tables are grouped into namespaces. In practice, a namespace is just a prefix
in front of the table name. Additionally, to the tables the schema contains a mapping of the event DTOs to the tables
and a mapping of the event properties to the table columns.

```csharp
public class DatabaseSchema : IDatabaseSchema
{
    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } 
        = new Dictionary<(string Namespace, string Table), EventSchema>();
    
    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();
    
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();
}
```

#### Tables

The tables are defined as a dictionary with a tuple of the namespace and table name as key and an `EventSchema` as
value:

```csharp
var transfer = new EventSchema(
    "CrcV1",                                                                // Namespace
    "Transfer",                                                             // Table
    Keccak.Compute("Transfer(address,address,uint256)").BytesToArray(),     // Event topic
    [                                                                       // Columns ..
        new ("blockNumber", ValueTypes.Int, true),
        new ("timestamp", ValueTypes.Int, true),
        new ("transactionIndex", ValueTypes.Int, true),
        new ("logIndex", ValueTypes.Int, true),
        new ("transactionHash", ValueTypes.String, true),
        new ("tokenAddress", ValueTypes.Address, true),
        new ("from", ValueTypes.Address, true),
        new ("to", ValueTypes.Address, true),
        new ("amount", ValueTypes.BigInt, false)
    ]);

```

The single fields/columns are defined as follows:

```csharp
 public record EventFieldSchema(string Column, ValueTypes Type, bool IsIndexed, bool IncludeInPrimaryKey = false);
```

Alternatively, you can create an EventSchema from a solidity event signature:

```csharp
var signup = EventSchema.FromSolidity("CrcV1",
        "event Signup(address indexed user, address indexed token)")
```

#### EventDtoTableMap

Every protocol implementation has a set of DTOs that represent the events. The `EventDtoTableMap` maps these DTOs to
the tables defined in the schema. The mapping is established between the generic type and the namespace and table name.

```csharp
EventDtoTableMap.Add<Signup>(("CrcV1", "Signup"));
```

#### SchemaPropertyMap

The `SchemaPropertyMap` maps the properties of the DTOs to the columns of the tables.
Each column is mapped to a function that extracts the value from the DTO. The function can also return a calculated
value.

```csharp
SchemaPropertyMap.Add(("CrcV1", "Signup"),
    new Dictionary<string, Func<Signup, object?>>
    {
        { "blockNumber", e => e.BlockNumber },
        { "timestamp", e => e.Timestamp },
        { "transactionIndex", e => e.TransactionIndex },
        { "logIndex", e => e.LogIndex },
        { "transactionHash", e => e.TransactionHash },
        { "user", e => e.User },
        { "token", e => e.Token }
    });
```

### Events.cs

The events file contains the DTOs for the events. Usually, these are just records with the properties of the event.

```csharp
public record Signup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string User,
    string Token) : IIndexEvent;
```

All DTOs must derive from the `IIndexEvent` interface that specifies the basic properties necessary for pagination:

```csharp
public interface IIndexEvent
{
    long BlockNumber { get; }
    long Timestamp { get; }
    int TransactionIndex { get; }
    int LogIndex { get; }
}
```

### LogParser.cs

The log parser is responsible for extracting the events from the transaction receipt logs. It must implement the
`ILogParser` interface.

```csharp
public class LogParser(Address emitterAddress) : ILogParser {
    // Use the topics previously defined in the schema
    Hash256 _transferTopic = new(DatabaseSchema.Transfer.Topic)
    
    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        List<IIndexEvent> events = new();
        if (log.Topics.Length == 0)
        {
            return events;
        }
        
        // Parse the log entry and add the resulting event DTOs to the list
        var topic = log.Topics[0];
        if (topic == _transferTopic))
        {
            events.Add(Erc20Transfer(block, receipt, log, logIndex));
        }
        
        return events;
    }
}
```

### Register the protocol

The schema, property map and log parser must be registered in the main plugin file.

On first execution, the plugin will create the necessary tables in the database.

___Note:___ _The plugin will not create new tables if the schema changes. You have to manually update the database
schema._

#### Register the schema

```csharp
// Add your schema to the composite schema:
IDatabaseSchema common = new Common.DatabaseSchema();
IDatabaseSchema v1 = new CirclesV1.DatabaseSchema();
IDatabaseSchema v2 = new CirclesV2.DatabaseSchema();
IDatabaseSchema customprotocol = new CustomProtocol.DatabaseSchema();
// ...
IDatabaseSchema databaseSchema = new CompositeDatabaseSchema([common, v1, v2, customprotocol /*, ...*/]);
```

#### Register the SchemaPropertyMap and EventDtoTableMap

```csharp
// Add your SchemaPropertyMap and EventDtoTableMap to the composite maps to initialize the sink:
Sink sink = new Sink(
    database,
    new CompositeSchemaPropertyMap([
        v1.SchemaPropertyMap, v2.SchemaPropertyMap, v2NameRegistry.SchemaPropertyMap /*, ...*/
    ]),
    new CompositeEventDtoTableMap([
        v1.EventDtoTableMap, v2.EventDtoTableMap, v2NameRegistry.EventDtoTableMap /*, ...*/
    ]),
    settings.EventBufferSize);
```

#### Register the LogParser

```csharp
// Add your log parser to the list of log parsers:
ILogParser[] logParsers =
[
    new CirclesV1.LogParser(settings.CirclesV1HubAddress),
    new CirclesV2.LogParser(settings.CirclesV2HubAddress),
    new CirclesV2.NameRegistry.LogParser(settings.CirclesNameRegistryAddress) //,
    // ...
];
```