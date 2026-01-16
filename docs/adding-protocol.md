# Adding another protocol (indexing more contracts)

The indexer works by parsing log entries of transaction receipts, filtering by event topics / emitters, and writing the decoded event DTOs into Postgres.

At a high level, each protocol / contract-family lives in its own assembly with three main parts:

- `DatabaseSchema.cs` — defines which events are indexed, their topics, and the table schemas.
- `Events.cs` — DTOs (usually records) that represent indexed events.
- `LogParser.cs` — converts `LogEntry` + receipt context into those DTOs.

Once those exist, you register the schema + mappings + log parser in the main plugin composition.

---

## 1) `DatabaseSchema.cs`

The schema pulls together:

- Tables (namespace + table name + topic + columns)
- Mapping: event DTO type → (namespace, table)
- Mapping: DTO property → column value extractor

Minimal skeleton:

```csharp
public class DatabaseSchema : IDatabaseSchema
{
    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }
        = new Dictionary<(string Namespace, string Table), EventSchema>();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();
}
```

### Tables

Tables are defined as an `EventSchema` (namespace, table, topic, columns):

```csharp
var transfer = new EventSchema(
    "MyProtocol",                                                           // Namespace
    "Transfer",                                                             // Table
    Keccak.Compute("Transfer(address,address,uint256)").BytesToArray(),     // Topic
    [
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

Column descriptors:

```csharp
public record EventFieldSchema(
    string Column,
    ValueTypes Type,
    bool IsIndexed,
    bool IncludeInPrimaryKey = false);
```

Alternative: create from Solidity signature:

```csharp
var signup = EventSchema.FromSolidity(
    "MyProtocol",
    "event Signup(address indexed user, address indexed token)");
```

### `EventDtoTableMap`

Maps DTO types to the table they should be written into:

```csharp
EventDtoTableMap.Add<Signup>(("MyProtocol", "Signup"));
```

### `SchemaPropertyMap`

Maps table columns to a value extractor (can be a computed value):

```csharp
SchemaPropertyMap.Add(("MyProtocol", "Signup"),
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

---

## 2) `Events.cs`

DTOs are usually plain records. They must implement `IIndexEvent` (the core pagination fields).

Example:

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

The interface:

```csharp
public interface IIndexEvent
{
    long BlockNumber { get; }
    long Timestamp { get; }
    int TransactionIndex { get; }
    int LogIndex { get; }
}
```

---

## 3) `LogParser.cs`

Log parsers implement `ILogParser` and emit zero or more `IIndexEvent` DTOs for a log entry.

```csharp
public class LogParser(Address emitterAddress) : ILogParser
{
    private readonly Hash256 _transferTopic = new(DatabaseSchema.Transfer.Topic);

    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var events = new List<IIndexEvent>();

        if (log.Topics.Length == 0)
        {
            return events;
        }

        var topic0 = log.Topics[0];
        var isTransfer = topic0 == _transferTopic;
        if (isTransfer)
        {
            events.Add(ParseTransfer(block, receipt, log, logIndex));
        }

        return events;
    }
}
```

---

## 4) Register everything

Schemas, property maps and DTO table maps are composed into composites; log parsers are collected into a single list.

### Register the schema

```csharp
IDatabaseSchema common = new Common.DatabaseSchema();
IDatabaseSchema v1 = new CirclesV1.DatabaseSchema();
IDatabaseSchema v2 = new CirclesV2.DatabaseSchema();
IDatabaseSchema myProtocol = new MyProtocol.DatabaseSchema();

IDatabaseSchema databaseSchema = new CompositeDatabaseSchema([
    common,
    v1,
    v2,
    myProtocol
]);
```

### Register the property map + DTO table map

```csharp
Sink sink = new Sink(
    database,
    new CompositeSchemaPropertyMap([
        v1.SchemaPropertyMap,
        v2.SchemaPropertyMap,
        myProtocol.SchemaPropertyMap
    ]),
    new CompositeEventDtoTableMap([
        v1.EventDtoTableMap,
        v2.EventDtoTableMap,
        myProtocol.EventDtoTableMap
    ]),
    settings.EventBufferSize);
```

### Register the log parser

```csharp
ILogParser[] logParsers =
[
    new CirclesV1.LogParser(settings.CirclesV1HubAddress),
    new CirclesV2.LogParser(settings.CirclesV2HubAddress),
    new MyProtocol.LogParser(settings.MyProtocolEmitterAddress)
];
```

---

## Migration note (important)

On first execution, the plugin will create missing tables in the database.

It will **not** auto-migrate existing tables if you change a schema later.
You’ll need to update Postgres manually (or add a migration step) when schemas evolve.
