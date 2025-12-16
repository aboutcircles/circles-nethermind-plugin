# Partial Table Reindexing

This document describes how to backfill specific tables without a full reindex.

## Overview

The `Circles.Index.Backfill` CLI tool allows you to populate specific tables from historical blocks without affecting existing data or requiring a full reindex. This is useful when:

- Adding new event types to the schema
- Fixing data in specific tables
- Recovering from partial data issues

## Tables Requiring Backfill

The following CrcV2 tables were added after initial deployment and may need backfilling:

| Table | Description |
|-------|-------------|
| `CrcV2_FlowEdgesScopeSingleStarted` | Flow edge scope start events |
| `CrcV2_FlowEdgesScopeLastEnded` | Flow edge scope end events |
| `CrcV2_SetAdvancedUsageFlag` | Advanced usage flag events |

## CLI Commands

### List Tables

Show all available tables with their row counts:

```bash
dotnet run --project src/Index/Circles.Index.Backfill -- list-tables
```

Tables with 0 rows are candidates for backfilling.

### Check Status

View progress of ongoing or completed backfills:

```bash
dotnet run --project src/Index/Circles.Index.Backfill -- status
```

### Run Backfill

```bash
dotnet run --project src/Index/Circles.Index.Backfill -- backfill \
  --tables CrcV2_FlowEdgesScopeSingleStarted CrcV2_FlowEdgesScopeLastEnded CrcV2_SetAdvancedUsageFlag \
  --from-block 37534026 \
  --rpc-url http://localhost:8545
```

## Command Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--tables` | `-t` | Tables to backfill (required, space-separated) | - |
| `--from-block` | `-f` | Start block number | 37534026 (V2 deployment) |
| `--to-block` | `-e` | End block number | System_Block max |
| `--connection-string` | `-c` | PostgreSQL connection string | See below |
| `--rpc-url` | `-r` | Nethermind RPC endpoint | `http://localhost:8545` |
| `--batch-size` | `-b` | Blocks per batch | 1000 |
| `--dry-run` | - | Parse only, don't write | false |
| `--force` | - | Bypass safety check (dangerous) | false |

### Safety Check

Before running, the tool verifies the indexer is not running:

1. Checks if `CIRCLES_PLUGIN_DISABLED=true` is set
2. Monitors `System_Block` for 3 seconds to ensure it's not advancing

If the indexer is detected as running, the tool will refuse to start. Use `--force` to bypass (not recommended).

### Connection String

The tool automatically constructs the connection string from environment variables:

1. `--connection-string` option (if provided)
2. `POSTGRES_CONNECTION_STRING` env var
3. Constructed from `POSTGRES_USER` + `POSTGRES_PASSWORD` (+ optional `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`)

## Step-by-Step Guide

### 1. Stop Indexer and Restart Nethermind in RPC-only Mode

```bash
# Stop the indexer (Nethermind with Circles plugin)
docker compose -f docker/docker-compose.gnosis.yml stop nethermind-gnosis

# Restart Nethermind with the Circles plugin disabled (RPC-only mode)
CIRCLES_PLUGIN_DISABLED=true docker compose -f docker/docker-compose.gnosis.yml up -d nethermind-gnosis
```

This runs Nethermind without the Circles indexing plugin, so it only serves as an RPC endpoint for the backfill tool.

### 2. Source Environment

```bash
source docker/.env
```

### 3. Get Current Block Height

```bash
PGPASSWORD=$POSTGRES_PASSWORD psql -h localhost -U $POSTGRES_USER -d postgres \
  -c "SELECT MAX(\"blockNumber\") as current_block FROM \"System_Block\";"
```

### 4. Run Backfill

```bash
dotnet run --project src/Index/Circles.Index.Backfill -- backfill \
  -t CrcV2_FlowEdgesScopeSingleStarted CrcV2_FlowEdgesScopeLastEnded CrcV2_SetAdvancedUsageFlag \
  -f 37534026 \
  -r "http://localhost:8545"
```

### 5. Verify Data

```bash
PGPASSWORD=$POSTGRES_PASSWORD psql -h localhost -U $POSTGRES_USER -d postgres <<EOF
SELECT 'CrcV2_FlowEdgesScopeSingleStarted' as table_name,
       COUNT(*) as count,
       MIN("blockNumber") as min_block,
       MAX("blockNumber") as max_block
FROM "CrcV2_FlowEdgesScopeSingleStarted"
UNION ALL
SELECT 'CrcV2_FlowEdgesScopeLastEnded', COUNT(*), MIN("blockNumber"), MAX("blockNumber")
FROM "CrcV2_FlowEdgesScopeLastEnded"
UNION ALL
SELECT 'CrcV2_SetAdvancedUsageFlag', COUNT(*), MIN("blockNumber"), MAX("blockNumber")
FROM "CrcV2_SetAdvancedUsageFlag";
EOF
```

### 6. Restart Indexer

```bash
docker compose -f docker/docker-compose.gnosis.yml start circles-indexer
```

## Re-indexing Existing Tables

If you need to re-index tables that already have data:

```bash
# 1. Stop indexer
docker compose -f docker/docker-compose.gnosis.yml stop circles-indexer

# 2. Delete existing data
PGPASSWORD=$POSTGRES_PASSWORD psql -h localhost -U $POSTGRES_USER -d postgres \
  -c "DELETE FROM \"CrcV2_FlowEdgesScopeSingleStarted\" WHERE \"blockNumber\" >= 37534026;"

# 3. Run backfill
dotnet run --project src/Index/Circles.Index.Backfill -- backfill \
  -t CrcV2_FlowEdgesScopeSingleStarted \
  -f 37534026

# 4. Restart indexer
docker compose -f docker/docker-compose.gnosis.yml start circles-indexer
```

## Progress Tracking

The tool creates a `System_BackfillProgress` table to track progress:

```sql
SELECT * FROM "System_BackfillProgress";
```

If backfill is interrupted, run the same command again to resume from the last completed batch.

## Safety Guarantees

| Risk | Mitigation |
|------|------------|
| Backfill corrupts System_Block | Tool never writes to System_Block |
| Existing tables modified | Only writes to explicitly specified tables |
| Incomplete backfill | Progress tracked, can resume |
| Wrong data written | Upsert mode allows safe re-runs |
| Indexer starts during backfill | Stop indexer before backfilling |

## Troubleshooting

### Connection refused to RPC

- Ensure Nethermind is running
- Check RPC URL is correct

### Table not found in schema

- Use `list-tables` to see available tables
- Format: `Namespace_Table` (e.g., `CrcV2_FlowEdgesScopeSingleStarted`)

### Slow backfill

- Increase batch size: `--batch-size 5000`
- Use local RPC endpoint
- Direct database connection (not via tunnel)

## Related Documentation

- [Indexer State Machine Flow](indexer-flow.md) - How the indexer processes blocks
- [CLAUDE.md](../CLAUDE.md) - Project overview and commands
