# Partial Table Reindexing

This document describes how to backfill specific tables without a full reindex.

## Overview

The `Circles.Index.Backfill` CLI tool allows you to populate specific tables from historical blocks without affecting existing data or requiring a full reindex. This is useful when:

- Adding new event types to the schema
- Fixing data in specific tables
- Recovering from partial data issues

## Quick Start (Docker - Recommended)

The recommended way to run backfill is using the Docker script, which handles networking and connection strings automatically:

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

## Supported Tables

Run `./scripts/docker-backfill.sh list-tables` to see all backfillable tables:

| Table | Description |
|-------|-------------|
| `CrcV2_FlowEdgesScopeSingleStarted` | Flow edge scope start events |
| `CrcV2_FlowEdgesScopeLastEnded` | Flow edge scope end events |
| `CrcV2_SetAdvancedUsageFlag` | Advanced usage flag events |
| `CrcV2_PaymentGateway_GatewayCreated` | Payment gateway creation |
| `CrcV2_PaymentGateway_PaymentReceived` | Payments through gateways |
| `CrcV2_PaymentGateway_TrustUpdated` | Gateway trust updates |

## CLI Commands

### List Tables

```bash
./scripts/docker-backfill.sh list-tables
```

### Check Status

```bash
./scripts/docker-backfill.sh status
```

### Run Backfill

```bash
./scripts/docker-backfill.sh backfill \
  --tables CrcV2_PaymentGateway_GatewayCreated \
  --from-block 43610000
```

## Command Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--tables` | `-t` | Tables to backfill (required, space-separated) | - |
| `--from-block` | `-f` | Start block number | 37534026 (V2 deployment) |
| `--to-block` | `-e` | End block number | System_Block max |
| `--batch-size` | `-b` | Blocks per batch | 1000 |
| `--dry-run` | - | Parse only, don't write | false |
| `--force` | - | Bypass safety check (dangerous) | false |

## Safety Check

Before running, the tool verifies the indexer is not running:

1. Checks if `CIRCLES_PLUGIN_DISABLED=true` is set
2. Monitors `System_Block` for 3 seconds to ensure it's not advancing

If the indexer is detected as running, the tool will refuse to start.

## Adding New Events to the Backfill Tool

When new event types are added to the indexer, register them in `src/Index/Circles.Index.Backfill/EventRegistry.cs`:

```csharp
// Find the contract address in Settings.cs, then add:
RegisterEvent("CrcV2_MyProtocol_MyEvent",
    "event MyEvent(address indexed user, uint256 amount)",
    "0x1234..."); // factory address

// For non-standard types (e.g., uint96):
RegisterEventManual("CrcV2_MyProtocol_SpecialEvent",
    "SpecialEvent(address,uint96)",
    new[] {
        ("user", FieldType.Address, true),
        ("value", FieldType.BigInt, false)
    },
    "0x1234...");
```

Then rebuild: `docker build -t circles-backfill:local -f docker/backfill.Dockerfile .`

See [Detailed Backfill Documentation](_resources/partial-table-reindexing.md) for full extensibility guide.

## Re-indexing Existing Tables

If you need to re-index tables that already have data:

```bash
# 1. Connect to postgres and delete existing data
source .env
docker exec -it postgres-gnosis psql -U "$POSTGRES_USER" -d postgres \
  -c 'DELETE FROM "CrcV2_PaymentGateway_GatewayCreated" WHERE "blockNumber" >= 43610000;'

# 2. Run backfill
./scripts/docker-backfill.sh backfill -t CrcV2_PaymentGateway_GatewayCreated -f 43610000
```

## Progress Tracking

The tool tracks progress in `System_BackfillProgress`:

```sql
SELECT * FROM "System_BackfillProgress";
```

If interrupted, run the same command again to resume.

## Safety Guarantees

| Risk | Mitigation |
|------|------------|
| Backfill corrupts System_Block | Tool never writes to System_Block |
| Existing tables modified | Only writes to explicitly specified tables |
| Incomplete backfill | Progress tracked, can resume |
| Wrong data written | Upsert mode allows safe re-runs |
| Indexer starts during backfill | Safety check + CIRCLES_PLUGIN_DISABLED |

## Troubleshooting

### Connection refused

- Ensure Docker stack is running: `./scripts/docker-run.sh gnosis ps`

### Table not found

- Use `list-tables` to see available tables
- Table names are case-sensitive

### No events found

- Verify contract address in EventRegistry.cs matches Settings.cs
- Verify from-block is before the first event

## Related Documentation

- [Detailed Backfill Documentation](_resources/partial-table-reindexing.md) - Full guide with extensibility
- [Reindexing Guide](_resources/reindexing-guide.md) - Full reindex via REINDEX_FROM_BLOCK
- [Indexer State Machine Flow](indexer-flow.md) - How the indexer processes blocks
