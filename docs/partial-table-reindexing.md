# Reindexing Guide

This guide covers how to reindex Circles event tables - both partial table backfills and full reindexing.

## When to Reindex

- New event types were added but historical events weren't captured
- Data corruption or inconsistency detected
- Contract address changes require re-scanning blocks
- Fixing data in specific tables

## Option A: Partial Table Backfill (Recommended)

Use the `docker-backfill.sh` script for surgical table-specific backfills without affecting other data.

### Quick Start

```bash
# 1. Ensure indexer is stopped (set CIRCLES_PLUGIN_DISABLED=true in .env)
./scripts/docker-run.sh gnosis up -d nethermind-gnosis

# 2. List available tables
./scripts/docker-backfill.sh list-tables

# 3. Run backfill for specific tables
./scripts/docker-backfill.sh backfill \
  -t CrcV2_PaymentGateway_GatewayCreated \
     CrcV2_PaymentGateway_PaymentReceived \
     CrcV2_PaymentGateway_TrustUpdated \
  -f 43610000

# 4. Re-enable indexer (set CIRCLES_PLUGIN_DISABLED=false in .env)
./scripts/docker-run.sh gnosis up -d nethermind-gnosis
```

### CLI Commands

#### List Tables

Show all available tables with their row counts:

```bash
./scripts/docker-backfill.sh list-tables
```

Tables with 0 rows are candidates for backfilling.

#### Check Status

View progress of ongoing or completed backfills:

```bash
./scripts/docker-backfill.sh status
```

#### Run Backfill

```bash
./scripts/docker-backfill.sh backfill \
  --tables CrcV2_PaymentGateway_GatewayCreated \
  --from-block 43610000
```

### Command Options

| Option         | Short | Description                                    | Default                  |
| -------------- | ----- | ---------------------------------------------- | ------------------------ |
| `--tables`     | `-t`  | Tables to backfill (required, space-separated) | -                        |
| `--from-block` | `-f`  | Start block number                             | 37534026 (V2 deployment) |
| `--to-block`   | `-e`  | End block number                               | System_Block max         |
| `--batch-size` | `-b`  | Blocks per batch                               | 1000                     |
| `--dry-run`    | -     | Parse only, don't write                        | false                    |
| `--force`      | -     | Bypass safety check (dangerous)                | false                    |

### Safety Check

Before running, the tool verifies the indexer is not running:

1. Checks if `CIRCLES_PLUGIN_DISABLED=true` is set
2. Monitors `System_Block` for 15 seconds to ensure it's not advancing (Gnosis chain has ~5s block time)

If the indexer is detected as running, the tool will refuse to start. Use `--force` to bypass (not recommended).

### Re-indexing Existing Tables

If you need to re-index tables that already have data, first clear the existing data:

```bash
# 1. Connect to postgres and delete existing data
source .env
docker exec -it postgres-gnosis psql -U "$POSTGRES_USER" -d postgres \
  -c 'DELETE FROM "CrcV2_PaymentGateway_GatewayCreated" WHERE "blockNumber" >= 43610000;'

# 2. Run backfill
./scripts/docker-backfill.sh backfill -t CrcV2_PaymentGateway_GatewayCreated -f 43610000
```

### Progress Tracking

The tool tracks progress in `System_BackfillProgress`:

```sql
SELECT * FROM "System_BackfillProgress";
```

If interrupted, run the same command again to resume from the last completed batch.

## Option B: Full Reindex via `REINDEX_FROM_BLOCK`

For major changes affecting many tables, set an environment variable and restart. The plugin handles everything automatically.

### How It Works

1. On startup, the StateMachine checks for `REINDEX_FROM_BLOCK` env var
2. Executes `DELETE FROM table WHERE blockNumber >= X` on **ALL event tables**
3. Sets internal `_reindexCleanupDone` flag (prevents re-deletion on retry/error)
4. Reinitializes all caches from the cleaned database
5. Resyncs from the specified block onwards

**Safety guarantees:**

- Deletion runs only **once per process lifetime** (flag-protected)
- Each table deleted separately with 10-minute timeout
- If crash occurs mid-deletion, restart will continue from where it left off
- Idempotent: re-running deletion on already-cleaned tables is safe

### Steps

```bash
# 1. Source your environment
source .env

# 2. Check current state (optional)
./scripts/docker-backfill.sh list-tables

# 3. Stop the indexer service
./scripts/docker-run.sh gnosis stop nethermind-gnosis

# 4. Set reindex block in .env
# For payment gateway: use 43610000 (before first gateway at 43,610,829)
# For full V2 reindex: use 36501311 (V2 launch block)
echo "REINDEX_FROM_BLOCK=43610000" >> .env

# 5. Start the indexer - it will:
#    - Delete ALL tables from block 43,610,000 onwards
#    - Reinitialize caches
#    - Resync from that block
./scripts/docker-run.sh gnosis up -d nethermind-gnosis

# 6. Monitor progress
./scripts/docker-run.sh gnosis logs -f nethermind-gnosis

# 7. IMPORTANT: After reindex completes, REMOVE the env var!
#    Otherwise it will re-delete data on next restart.
# Edit .env and remove the REINDEX_FROM_BLOCK line
```

### What Gets Deleted

When `REINDEX_FROM_BLOCK=43610000` is set:

| Affected          | Action                                  |
| ----------------- | --------------------------------------- |
| `CrcV1_*` tables  | DELETE WHERE blockNumber >= 43610000    |
| `CrcV2_*` tables  | DELETE WHERE blockNumber >= 43610000    |
| `Safe_*` tables   | DELETE WHERE blockNumber >= 43610000    |
| `System_Block`    | DELETE WHERE blockNumber >= 43610000    |
| Views (`V_*`)     | Skipped (auto-update from base tables)  |
| Caches            | Reinitialized from database             |

**No manual table deletion needed** - the plugin manages all tables atomically.

### Expected Log Output

```text
[REINDEX] Reindexing ALL tables from block 43,610,000
Deleted 1234 rows from CrcV2_PaymentGateway_GatewayCreated
...
[REINDEX] Data deletion complete. Will resync from specified block.
```

## Adding New Events to the Backfill Tool

When new event types are added to the indexer, register them in `src/Index/Circles.Index.Backfill/EventRegistry.cs`.

### Step 1: Find the Event Schema

Look at the existing `DatabaseSchema.cs` in the protocol directory:

```csharp
public static readonly EventSchema MyNewEvent = EventSchema.FromSolidity(
    Namespace,
    "event MyNewEvent(address indexed user, uint256 amount)"
);
```

### Step 2: Find the Contract Address

Check `src/Common/Circles.Common/Settings.cs` for the authoritative contract address:

```csharp
public readonly string[] MyProtocolFactoryAddresses =
    Environment.GetEnvironmentVariable("V2_MY_PROTOCOL_ADDRESS")?.Split(',')
    ?? ["0x1234567890abcdef1234567890abcdef12345678"];
```

### Step 3: Register in EventRegistry.cs

```csharp
// Find the contract address in Settings.cs, then add:
RegisterEvent("CrcV2_MyProtocol_MyNewEvent",
    "event MyNewEvent(address indexed user, uint256 amount)",
    "0x1234..."); // factory address

// For non-standard types (e.g., uint96) or arrays:
RegisterEventManual("CrcV2_MyProtocol_SpecialEvent",
    "SpecialEvent(address,uint96)",  // Canonical signature for topic hash
    new[] {
        ("user", FieldType.Address, true),     // indexed
        ("value", FieldType.BigInt, false)     // non-indexed
    },
    "0x1234...");
```

### Step 4: Rebuild and Test

```bash
# Rebuild the Docker image
docker build -t circles-backfill:local -f docker/backfill.Dockerfile .

# Verify the new event appears
./scripts/docker-backfill.sh list-tables

# Test with dry-run
./scripts/docker-backfill.sh backfill -t CrcV2_MyProtocol_MyNewEvent -f 43000000 --dry-run
```

### Supported Field Types

| Solidity Type     | FieldType      | Notes                 |
| ----------------- | -------------- | --------------------- |
| `address`         | `Address`      | 20-byte addresses     |
| `address[]`       | `AddressArray` | Array of addresses    |
| `uint8..uint64`   | `Int`          | Small integers        |
| `uint96..uint256` | `BigInt`       | Large integers        |
| `uint256[]`       | `BigIntArray`  | Array of big integers |
| `bytes32`         | `Bytes32`      | Fixed 32-byte arrays  |
| `bytes`           | `Bytes`        | Dynamic byte arrays   |
| `string`          | `String`       | UTF-8 strings         |
| `bool`            | `Boolean`      | True/false            |

## Key Environment Variables

| Variable                     | Description                                     | Example         |
| ---------------------------- | ----------------------------------------------- | --------------- |
| `REINDEX_FROM_BLOCK`         | Delete all data >= this block and resync        | `43610000`      |
| `CIRCLES_PLUGIN_DISABLED`    | Run Nethermind without indexing (RPC-only)      | `true`          |
| `POSTGRES_CONNECTION_STRING` | Database connection                             | `Server=...`    |
| `START_BLOCK`                | Initial sync start block (fresh DB only)        | `36501311`      |

## Reindex Time Estimates

When Nethermind is already synced (no network sync needed):

| Scope                              | Blocks | Estimated Time    |
| ---------------------------------- | ------ | ----------------- |
| Full reindex (from V2 launch)      | ~7.4M  | 1-3 days          |
| Payment gateway (from first event) | ~340K  | Few hours         |
| Specific table backfill            | Varies | Minutes to hours  |

*Times depend on hardware, database performance, and event density.*

## Safety Guarantees

| Risk                           | Mitigation                                 |
| ------------------------------ | ------------------------------------------ |
| Backfill corrupts System_Block | Tool never writes to System_Block          |
| Existing tables modified       | Only writes to explicitly specified tables |
| Incomplete backfill            | Progress tracked, can resume               |
| Wrong data written             | Upsert mode allows safe re-runs            |
| Indexer starts during backfill | Safety check + CIRCLES_PLUGIN_DISABLED     |

## Verification Queries

```sql
-- Check payment gateway counts
SELECT
  (SELECT COUNT(*) FROM "CrcV2_PaymentGateway_GatewayCreated") as gateways,
  (SELECT COUNT(*) FROM "CrcV2_PaymentGateway_PaymentReceived") as payments;

-- Check System_Block progress
SELECT MAX("blockNumber") as latest_block FROM "System_Block";

-- Check backfill progress
SELECT * FROM "System_BackfillProgress";
```

## Troubleshooting

### Connection refused

- Ensure Docker stack is running: `./scripts/docker-run.sh gnosis ps`
- Check network exists: `docker network ls | grep circles-gnosis`

### Table not found

- Use `list-tables` to see available tables
- Table names are case-sensitive
- Format: `Namespace_EventName` (e.g., `CrcV2_PaymentGateway_GatewayCreated`)

### No events found

- Verify contract address in EventRegistry.cs matches Settings.cs
- Verify from-block is before the first event
- Use `--dry-run` to test without writing

### Slow backfill

- Increase batch size: `--batch-size 5000`
- Check Nethermind RPC is responsive
- Ensure postgres has adequate resources

### Reindex runs on every restart

**Cause:** `REINDEX_FROM_BLOCK` env var still set.
**Fix:** Remove the env var after reindex completes.

### Backfill tool refuses to run

**Cause:** Indexer is still running (safety check).
**Fix:** Set `CIRCLES_PLUGIN_DISABLED=true` in `.env` and restart.

### Crash during reindex

**Cause:** Process killed or crashed mid-reindex.
**Fix:** Just restart with `REINDEX_FROM_BLOCK` still set. The deletion is idempotent.

## Related Documentation

- [Indexer State Machine Flow](indexer-flow.md) - How the indexer processes blocks
- [CLAUDE.md](../CLAUDE.md) - Project overview and commands
