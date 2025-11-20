# Circles Index

A Nethermind plugin that indexes Circles protocol events from the blockchain into a PostgreSQL database, enabling fast queries and historical data access.

## Overview

The Index plugin monitors the blockchain in real-time, extracts Circles-related events from transaction logs, and stores them in a structured PostgreSQL database. This indexed data powers the RPC service and enables efficient querying of:

- Token transfers and balances
- Trust relationships
- Avatar registrations (humans, groups, organizations)
- Profile metadata (IPFS CIDs)
- Safe deployments
- Group operations and treasury events
- Token offers and swaps

## Architecture

### Core Components

```
┌─────────────────────────────────────────────────────────────┐
│                    Nethermind Plugin                        │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │ StateMachine │→ │  LogParsers  │→ │     Sink     │     │
│  │  (Indexer)   │  │  (V1/V2/...)│  │   (Batch)    │     │
│  └──────────────┘  └──────────────┘  └──────────────┘     │
│         ↓                  ↓                   ↓            │
│  ┌──────────────────────────────────────────────────────┐  │
│  │              PostgreSQL Database                     │  │
│  │  (Indexed Circles Events & State)                   │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

#### 1. StateMachine (`StateMachine.cs`)

The indexer state machine that orchestrates the indexing process:

- **States:** New → Initial → Syncing → WaitForNewBlock → NotifySubscribers
- **Reorg Handling:** Detects and handles blockchain reorganizations
- **Block Processing:** Reads blocks, extracts receipts, parses logs
- **Cache Management:** Maintains in-memory caches for performance

#### 2. LogParsers

Protocol-specific parsers that extract events from transaction logs:

- **CirclesV1** - Hub, Trust, Transfer, Signup events
- **CirclesV2** - Hub, Trust, PersonalMint, RegisterHuman/Group/Organization
- **CirclesV2.NameRegistry** - RegisterShortName, UpdateMetadataDigest, CidV0
- **CirclesV2.StandardTreasury** - Vault operations and collateral
- **Safe** - Safe proxy deployments
- **CirclesV2.TokenOffers** - Token offer factory events
- **CirclesV2.LBP** - Liquidity bootstrapping pool events
- **CirclesV2.CMGroupDeployer** - Conditional money group deployments
- **And more...**

Each parser implements `ILogParser`:

```csharp
public interface ILogParser
{
    IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex);
}
```

#### 3. Sink (`Circles.Index.Common/Sink.cs`)

Batches and writes events to the database:

- **Batching:** Accumulates events until batch size is reached
- **Type Grouping:** Groups events by type for efficient bulk inserts
- **Async Writing:** Writes batches asynchronously to PostgreSQL
- **Error Handling:** Logs and handles write failures

#### 4. Database Layer (`Circles.Index.Postgres`)

PostgreSQL integration:

- **Schema Management:** Auto-migration on startup
- **Bulk Inserts:** Efficient batch writes using `COPY`
- **Query Interface:** Structured query API for RPC layer
- **Connection Pooling:** Reuses database connections

### Project Structure

```
src/Index/
├── Circles.Index/                              # Main plugin (Gnosis.Circles.Nethermind.Plugin)
│   ├── Plugin.cs                               # Nethermind plugin entry point
│   ├── StateMachine.cs                         # Indexer state machine
│   ├── Context.cs                              # Shared context for indexing
│   └── Settings.cs                             # Configuration
├── Circles.Index.Common/                       # Shared utilities
│   ├── Sink.cs                                 # Event batching and writing
│   ├── IDatabase.cs                            # Database abstraction
│   ├── ILogParser.cs                           # Log parser interface
│   └── Settings.cs                             # Base settings
├── Circles.Index.Postgres/                     # PostgreSQL implementation
│   ├── PostgresDb.cs                           # Database client
│   ├── Migrations/                             # Schema migrations
│   └── QueryBuilder.cs                         # SQL query generation
├── Circles.Index.Query/                        # Query abstractions
│   ├── SelectDto.cs                            # Query DTO
│   ├── FilterPredicate.cs                      # Filter expressions
│   └── QueryEngine.cs                          # Query execution
├── Circles.Index.DatabaseSchemaProvider/       # Schema registry
│   └── DatabaseSchemaProvider.cs               # All protocol schemas
├── Circles.Index.CirclesV1/                    # V1 protocol indexing
│   ├── DatabaseSchema.cs                       # V1 table definitions
│   ├── Events.cs                               # V1 event DTOs
│   └── LogParser.cs                            # V1 log parser
├── Circles.Index.CirclesV2/                    # V2 protocol indexing
│   ├── DatabaseSchema.cs                       # V2 table definitions
│   ├── Events.cs                               # V2 event DTOs
│   └── LogParser.cs                            # V2 log parser
├── Circles.Index.CirclesV2.NameRegistry/       # V2 name registry
├── Circles.Index.CirclesV2.StandardTreasury/   # V2 treasury
├── Circles.Index.CirclesViews/                 # Database views (V1, V2, combined)
├── Circles.Index.Profiles/                     # IPFS profile management
├── Circles.Index.Safe/                         # Safe deployments
├── Circles.Index.ContractClient/               # Contract interaction
└── [Other protocol-specific indexers...]
```

## How It Works

### 1. Initialization

```csharp
// Plugin.cs - Init()
1. Load settings from environment variables
2. Connect to PostgreSQL database
3. Run database migrations (create tables/indexes)
4. Initialize all log parsers (V1, V2, Safe, etc.)
5. Create Sink for batch writing
6. Start StateMachine
7. Start IPFS profile downloader
```

### 2. Block Processing Loop

```csharp
// StateMachine.cs
while (syncing) {
    1. Get next block from Nethermind
    2. Get transaction receipts for block
    3. For each receipt:
       a. Extract log entries
       b. Pass to all LogParsers
       c. Collect parsed events
    4. Send events to Sink
    5. Sink batches events
    6. When batch full, write to database
    7. Update block cursor
}
```

### 3. Event Parsing

```csharp
// Example: CirclesV2.LogParser
public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
{
    // Check if log topic matches our events
    if (log.Topics[0] == _trustTopic)
    {
        // Decode log data
        var truster = new Address(log.Topics[1]);
        var trustee = new Address(log.Topics[2]);
        var expiryTime = new UInt256(log.Data);
        
        // Return typed event DTO
        yield return new Trust(
            block.Number,
            block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            truster.ToString(),
            trustee.ToString(),
            expiryTime
        );
    }
}
```

### 4. Database Schema

Each protocol defines its schema:

```csharp
// CirclesV2.DatabaseSchema.cs
public class DatabaseSchema : IDatabaseSchema
{
    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }

    // Define table: CrcV2_Trust
    var trust = new EventSchema(
        "CrcV2",                    // Namespace
        "Trust",                    // Table name
        trustTopic,                 // Event topic hash
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("truster", ValueTypes.Address, true),
            new("trustee", ValueTypes.Address, true),
            new("expiryTime", ValueTypes.BigInt, false)
        ]
    );
    
    Tables.Add(("CrcV2", "Trust"), trust);
}
```

### 5. Reorg Handling

```csharp
// StateMachine.cs - HandleReorg
1. Detect reorg (block hash mismatch)
2. Find common ancestor block
3. Delete all events from reorg point onwards
4. Invalidate caches
5. Resume syncing from common ancestor
```

## Running the Index Plugin

### With Nethermind (Production)

The plugin runs inside Nethermind:

```bash
# Using Docker Compose (recommended)
docker compose -f docker/docker-compose.gnosis.yml up -d

# Or use script
./scripts/docker-run.sh gnosis
```

### Local Development

Fastest iteration for plugin development:

```bash
# 1. Start PostgreSQL
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis

# 2. Build and run Nethermind with plugin
./scripts/run-index.sh

# Optional: Set custom Nethermind source
NETHERMIND_SOURCE=~/path/to/nethermind ./scripts/run-index.sh
```

**Prerequisites:**
- Nethermind source in `src/nethermind/` (git submodule)
- PostgreSQL 15+ running
- .NET 9.0 SDK
- ~700GB+ disk space for blockchain data

## Configuration

All configuration via environment variables (see `Settings.cs`):

### Database

```bash
# Required
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres"
export POSTGRES_READONLY_CONNECTION_STRING="${POSTGRES_CONNECTION_STRING}"

# Optional
export EVENT_BUFFER_SIZE=1000  # Batch size for event writes
```

### Contract Addresses (Gnosis Chain)

```bash
# Circles V1
export CIRCLES_V1_HUB_ADDRESS="0x29b9a7fbb8995b2423a71cc17cf9810798f6c543"
export CIRCLES_V1_NAME_REGISTRY="0x..."

# Circles V2
export CIRCLES_V2_HUB_ADDRESS="0x..."
export CIRCLES_NAME_REGISTRY_ADDRESS="0x..."
export CIRCLES_STANDARD_TREASURY_ADDRESS="0x..."
export CIRCLES_ERC20_LIFT_ADDRESS="0x..."

# Additional contracts
export SAFE_PROXY_FACTORY_ADDRESSES="0x...,0x..."
export CIRCLES_TOKEN_OFFER_FACTORY_ADDRESS="0x..."
export CIRCLES_LBP_FACTORY_ADDRESS="0x..."
# ... (see Settings.cs for full list)
```

### IPFS

```bash
export IPFS_GATEWAY_URL="https://gateway.aboutcircles.com"
```

## Database Schema

The plugin creates tables for all indexed events:

### Example Tables

- **`CrcV1_Signup`** - V1 user registrations
- **`CrcV1_Trust`** - V1 trust relationships
- **`CrcV1_Transfer`** - V1 token transfers
- **`CrcV2_RegisterHuman`** - V2 human registrations
- **`CrcV2_RegisterGroup`** - V2 group registrations
- **`CrcV2_Trust`** - V2 trust relationships
- **`CrcV2_TransferSingle`** - V2 ERC1155 transfers
- **`CrcV2_TransferBatch`** - V2 batch transfers
- **`V_Crc_Avatars`** - View combining V1 and V2 avatars
- **`V_Crc_TrustRelations`** - View of current trust graph
- **`V_Crc_Transfers`** - View combining all transfers

### Database Views

Views provide unified access to V1 and V2 data:

- **`V_CrcV1_*`** - V1-specific views
- **`V_CrcV2_*`** - V2-specific views
- **`V_Crc_*`** - Combined V1+V2 views

## Adding New Protocol Support

To index a new contract/protocol:

### 1. Create Protocol Project

```bash
cd src/Index
mkdir Circles.Index.MyProtocol
```

### 2. Define Events (`Events.cs`)

```csharp
public record MyEvent(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string MyField1,
    string MyField2
) : IIndexEvent;
```

### 3. Define Schema (`DatabaseSchema.cs`)

```csharp
public class DatabaseSchema : IDatabaseSchema
{
    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }
    public IEventDtoTableMap EventDtoTableMap { get; }
    public ISchemaPropertyMap SchemaPropertyMap { get; }

    public DatabaseSchema()
    {
        // Define table
        var myEvent = EventSchema.FromSolidity("MyProtocol",
            "event MyEvent(address indexed field1, uint256 field2)");
        Tables.Add(("MyProtocol", "MyEvent"), myEvent);

        // Map DTO to table
        EventDtoTableMap.Add<MyEvent>(("MyProtocol", "MyEvent"));

        // Map DTO properties to columns
        SchemaPropertyMap.Add(("MyProtocol", "MyEvent"),
            new Dictionary<string, Func<MyEvent, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "myField1", e => e.MyField1 },
                { "myField2", e => e.MyField2 }
            });
    }
}
```

### 4. Implement LogParser (`LogParser.cs`)

```csharp
public class LogParser(Address contractAddress) : ILogParser
{
    private readonly Hash256 _myEventTopic = new(Keccak.Compute("MyEvent(address,uint256)").BytesToArray());

    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.LoggersAddress != contractAddress)
            yield break;

        if (log.Topics[0] == _myEventTopic)
        {
            yield return new MyEvent(
                block.Number,
                block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                new Address(log.Topics[1]).ToString(),
                new UInt256(log.Data).ToString()
            );
        }
    }
}
```

### 5. Register in Plugin (`Plugin.cs`)

```csharp
// Add schema to DatabaseSchemaProvider
DatabaseSchemaProvider.Schemas.AllSchemas.Add(new MyProtocol.DatabaseSchema());

// Add log parser
var logParsers = new List<ILogParser>
{
    // ... existing parsers
    new MyProtocol.LogParser(new Address("0x..."))
};
```

## Testing

```bash
# Run all Index tests
./scripts/test.sh

# Run specific test project
./scripts/test.sh common
./scripts/test.sh query

# With coverage
./scripts/test.sh --coverage
```

## Monitoring

### Database Queries

Check indexer progress:

```sql
-- Latest indexed block
SELECT MAX("blockNumber") FROM "System_Block";

-- Event counts by type
SELECT 'CrcV1_Transfer' as table, COUNT(*) FROM "CrcV1_Transfer"
UNION ALL
SELECT 'CrcV2_Trust', COUNT(*) FROM "CrcV2_Trust";

-- Recent events
SELECT * FROM "V_Crc_Avatars" ORDER BY "blockNumber" DESC LIMIT 10;
```

### Health Check

```bash
# Check if indexer is running and synced
curl http://localhost:8545 -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"circles_health","params":[],"id":1}'
```

## Performance Tuning

### Batch Size

```bash
# Increase batch size for faster indexing (uses more memory)
export EVENT_BUFFER_SIZE=5000
```

### Database

```postgresql
-- Add indexes for common queries
CREATE INDEX idx_transfers_from ON "CrcV1_Transfer"("from");
CREATE INDEX idx_transfers_to ON "CrcV1_Transfer"("to");
CREATE INDEX idx_trust_user ON "CrcV1_Trust"("user");
```

### PostgreSQL Config

```ini
# postgresql.conf
shared_buffers = 4GB
work_mem = 256MB
maintenance_work_mem = 1GB
effective_cache_size = 12GB
max_connections = 100
```

## Troubleshooting

### Indexer Stuck

```sql
-- Check for gaps in indexed blocks
SELECT * FROM "System_Block" 
WHERE "blockNumber" NOT IN (
    SELECT "blockNumber" + 1 FROM "System_Block"
);

-- Reset to specific block (WARNING: deletes data)
DELETE FROM "System_Block" WHERE "blockNumber" >= 30000000;
```

### Database Connection Issues

```bash
# Test connection
psql -h localhost -U postgres -d postgres -c "SELECT 1"

# Check connection string
echo $POSTGRES_CONNECTION_STRING
```

### Memory Issues

```bash
# Reduce batch size
export EVENT_BUFFER_SIZE=500

# Monitor memory usage
docker stats circles-index
```

## Related Documentation

- [Main README](../../README.md) - Complete protocol documentation
- [DEVELOPMENT.md](../../DEVELOPMENT.md) - Build and deployment guide
- [Circles.Rpc.Host](../Rpc/Circles.Rpc.Host/README.md) - RPC service documentation
- [Circles.Pathfinder](../Pathfinder/Circles.Pathfinder/README.md) - Pathfinding service

## NuGet Package

Published as: **Gnosis.Circles.Nethermind.Plugin**

```bash
# Install
dotnet add package Gnosis.Circles.Nethermind.Plugin

# Build from source
./scripts/nuget-pack.sh
# Output: nupkgs/Gnosis.Circles.Nethermind.Plugin.*.nupkg
```
