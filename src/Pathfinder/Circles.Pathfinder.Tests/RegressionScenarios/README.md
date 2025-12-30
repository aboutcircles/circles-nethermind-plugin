# Transfer Scenario Tests

This directory contains JSON fixtures for snapshot-based transfer scenario testing.

## Purpose

Each JSON file defines a transfer scenario that is:
1. Reproducible at a specific block number (frozen blockchain state)
2. Validated by the pathfinder (graph construction, max flow, edge ordering)
3. Executed on Anvil fork for E2E contract verification

## Categories

| Category | Description |
|----------|-------------|
| `direct-transfer` | User-to-user transfers without group minting |
| `group-minting` | Transfers involving Router and group token minting |
| `self-conversion` | Token conversion where source equals sink |
| `wrapped-tokens` | Transfers using wrapped ERC20 tokens |
| `consented-flow` | Transfers with consented flow validation |

## Usage

The `ScenarioTests.cs` test class loads all JSON files and:
1. Creates a test environment session at the specified block
2. Computes the transfer path
3. Validates expected outcomes (path found, min flow, edge ordering)
4. Executes on Anvil fork if `runOnAnvil: true`

```bash
# Run all scenario tests
dotnet test --filter "Category=Scenarios"

# Run E2E Anvil tests
dotnet test --filter "Category=E2E"
```

## Schema

```json
{
  "id": "unique-scenario-id",
  "name": "Human-readable name",
  "category": "direct-transfer|group-minting|self-conversion|wrapped-tokens|consented-flow",
  "block": 43193632,
  "source": "0x...",
  "sink": "0x...",
  "description": "Detailed description of what this scenario tests",

  "shouldFindPath": true,
  "minFlow": "1000000000000000000",
  "expectedRevertReason": null,
  "runOnAnvil": true,

  "fromTokens": ["0x..."],
  "toTokens": ["0x..."],
  "excludedTokens": ["0x..."],
  "maxTransfers": 10,
  "withWrap": false,

  "discoveredAt": "2025-11-17T23:18:00+01:00",
  "fixedIn": "commit or method name",
  "tags": ["regression", "edge-ordering"]
}
```

## Adding New Scenarios

1. **Find a suitable block** - Use discovery queries or note the block when a bug occurs
2. **Verify the scenario** - Ensure pathfinding works at that block state
3. **Create JSON file** - Follow the naming convention: `{category}-{number}.json`
4. **Run tests** - Verify the scenario passes locally before committing

## Existing Scenarios

### Regression Scenarios
- **mint-path-bug-001.json** - November 2025 edge ordering bug (group minting)

### Direct Transfer Scenarios
- **direct-transfer-001.json** - Basic user-to-user transfer
- **direct-transfer-with-filter-001.json** - Transfer with fromTokens filter
- **direct-transfer-combined-filter-001.json** - Transfer with both filters
- **max-flow-001.json** - Maximum flow calculation

### Self-Conversion Scenarios
- **self-conversion-001.json** - Token conversion with toTokens filter
- **self-conversion-no-path-001.json** - Negative test (no path expected)
- **self-conversion-wrapped-001.json** - Self-conversion with wrapped tokens

### Group Minting Scenarios
- **group-minting-002.json** - Edge ordering validation

### Wrapped Token Scenarios
- **wrapped-token-001.json** - Transfer with wrapped tokens enabled
