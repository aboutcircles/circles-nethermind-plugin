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
* [Circles RPC methods](#circles-rpc-methods)
    * [circles_getTotalBalance / circlesV2_getTotalBalance](#circles_gettotalbalance--circlesv2_gettotalbalance)
    * [circles_getTokenBalances](#circles_gettokenbalances)
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
configurations for Gnosis Chain and Chiado.

The quickstart configurations use [lighthouse](https://github.com/sigp/lighthouse) as consensus engine and spin up a
postgres database to store the indexed data.

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
    "0xde374ece6fa50e781e81aac78e811b33d16912c7"
  ]
}' -H "Content-Type: application/json" http://localhost:8545/
```

#### Response

This method returns a string formatted BigInteger value. The value is the sum of all Circles holdings of the address.

If `asTimeCircles` is set to `true`, the value is formatted
as [TimeCircles](https://github.com/CirclesUBI/timecircles) floating point number instead of the raw BigInteger value.

### circles_getTokenBalances

These methods allow you to query all individual Circles (v1/v2) holdings of an address.

**Signature**:

* `circles_getTokenBalances(address: string, asTimeCircles: bool = false)`.

#### Example

```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_getTokenBalances",
  "params": [
    "0xd68193591d47740e51dfbc410da607a351b56586"
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

## Available Namespaces, Tables, and Columns

Every table has at least the following columns:
* `blockNumber` - The block number the event was emitted in.
* `timestamp` - The unix timestamp of the event.
* `transactionIndex` - The index of the transaction in the block.
* `logIndex` - The index of the log in the transaction.
* `transactionHash` - The hash of the transaction.

Tables for batch events have an additional `batchIndex` column.

### Namespaces and Tables:

#### CrcV1
* `HubTransfer`
    * `from` (Address)
    * `to` (Address)
    * `amount` (BigInteger)
* `Signup`
    * `user` (Address)
    * `token` (Address)
* `OrganizationSignup`
    * `organization` (Address)
* `Trust`
    * `canSendTo` (Address)
    * `user` (Address)
    * `limit` (BigInteger)
* `Transfer`
    * `tokenAddress` (Address)
    * `from` (Address)
    * `to` (Address)
    * `amount` (BigInteger)

#### CrcV2
* `PersonalMint`
    * `human` (Address)
    * `amount` (BigInteger)
    * `startPeriod` (BigInteger)
    * `endPeriod` (BigInteger)
* `RegisterGroup`
    * `group` (Address)
    * `mint` (Address)
    * `treasury` (Address)
    * `name` (String)
    * `symbol` (String)
* `RegisterHuman`
    * `avatar` (Address)
* `RegisterOrganization`
    * `organization` (Address)
    * `name` (String)
* `Stopped`
    * `avatar` (Address)
* `Trust`
    * `truster` (Address)
    * `trustee` (Address)
    * `expiryTime` (BigInteger)
* `TransferSingle`
    * `operator` (Address)
    * `from` (Address)
    * `to` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)
* `TransferBatch`
    * `operator` (Address)
    * `from` (Address)
    * `to` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)
* `URI`
    * `value` (String)
    * `id` (BigInteger)
* `ApprovalForAll`
    * `account` (Address)
    * `operator` (Address)
    * `approved` (Boolean)
* `Erc20WrapperDeployed`
    * `avatar` (Address)
    * `erc20Wrapper` (Address)
    * `circlesType` (Int64)
* `Erc20WrapperTransfer`
    * `tokenAddress` (Address)
    * `from` (Address)
    * `to` (Address)
    * `amount` (BigInteger)
* `DepositInflationary`
    * `account` (Address)
    * `amount` (BigInteger)
    * `demurragedAmount` (BigInteger)
* `WithdrawInflationary`
    * `account` (Address)
    * `amount` (BigInteger)
    * `demurragedAmount` (BigInteger)
* `DepositDemurraged`
    * `account` (Address)
    * `amount` (BigInteger)
    * `inflationaryAmount` (BigInteger)
* `WithdrawDemurraged`
    * `account` (Address)
    * `amount` (BigInteger)
    * `inflationaryAmount` (BigInteger)
* `StreamCompleted`
    * `operator` (Address)
    * `from` (Address)
    * `to` (Address)
    * `id` (BigInteger)
    * `amount` (BigInteger)

#### CrcV2 (NameRegistry)
* `RegisterShortName`
    * `avatar` (Address)
    * `shortName` (UInt72)
    * `nonce` (BigInteger)
* `UpdateMetadataDigest`
    * `avatar` (Address)
    * `metadataDigest` (Bytes32)
* `CidV0`
    * `avatar` (Address)
    * `cidV0Digest` (Bytes32)

#### CrcV2 (StandardTreasury)
* `CreateVault`
    * `group` (Address)
    * `vault` (Address)
* `CollateralLockedSingle`
    * `group` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)
    * `userData` (Bytes)
* `CollateralLockedBatch`
    * `group` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)
    * `userData` (Bytes)
* `GroupRedeem`
    * `group` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)
    * `data` (Bytes)
* `GroupRedeemCollateralReturn`
    * `group` (Address)
    * `to` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)
* `GroupRedeemCollateralBurn`
    * `group` (Address)
    * `id` (BigInteger)
    * `value` (BigInteger)

#### V_CrcV1 (Circles v1 views)
    * `Avatars` (view combining `Signup` and `OrganizationSignup`)
    * `TrustRelations` (view filtered to represent all current `Trust` relations)
#### V_CrcV2 (Circles v2 views)
    * `V_CrcV2_InviteHuman` (view combining CrcV2_Trust and CrcV2_RegisterHuman)
    * `Avatars` (view combining `CrcV2_RegisterHuman`, `V_CrcV2_InviteHuman`, `CrcV2_RegisterGroup` and
      `CrcV2_RegisterOrganization`)
    * `TrustRelations` (view filtered to represent all current `Trust` relations)
    * `Transfers` (view combining `CrcV2_TransferBatch`, `CrcV2_TransferSingle` and `CrcV2_Erc20WrapperTransfer`)
    * `GroupMemberships` (view combining `CrcV2_RegisterGroup`, `V_CrcV2_TrustRelations` and `V_CrcV2_Avatars`)
#### V_Crc (Views combining v1 and v2 data)
    * `Avatars` (view combining `V_CrcV1_Avatars` and `V_CrcV2_Avatars`)
    * `TrustRelations` (view combining `V_CrcV1_TrustRelations` and `V_CrcV2_TrustRelations`)
    * `Transfers` (view combining `V_CrcV1_Transfer` and `V_CrcV2_Transfers`)

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
}' -H "Content-Type: application/json" https://rpc.helsinki.aboutcircles.com/
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

#### Filtering

The `circles_events` method can be filtered by specifying a filter as the last parameter:
```shell
curl -X POST --data '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [
    null,
    0,
    null,
    [{
       "Type": "FilterPredicate",
       "FilterType": "In",
       "Column": "transactionHash",
       "Value": ["0xd1380076b6ad2d1872951da1852c20ed3161e10f237aca27dc531795fa6867e0"]
    }]
  ]
}' -H "Content-Type: application/json" https://rpc.helsinki.aboutcircles.com/
```

### eth_subscribe("circles")

Subscribes to all Circles events. The subscription is a stream of events that are emitted as soon as they've been
indexed. Can be filtered to just a specific address.

**Signature**: `eth_subscribe("circles", { address?: string })`.

#### Example

This call subscribes to all Circles events (firehose):

```shell
npx wscat -c wss://rpc.helsinki.aboutcircles.com/ws -x '{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["circles",{}]}' -w 3600
```

This call subscribes to all Circles events that involve the address `0xde374ece6fa50e781e81aac78e811b33d16912c7`:

```shell
npx wscat -c wss://rpc.helsinki.aboutcircles.com/ws -x '{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["circles",{"address":"0xde374ece6fa50e781e81aac78e811b33d16912c7"}]}' -w 3600
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