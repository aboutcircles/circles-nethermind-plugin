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
| `payment-gateway` | Payment gateway transfers with group trust (tests router + edge ordering) |
| `production-discrepancy` | Cases where production returns different results than staging (graph completeness issues) |
| `performance` | High-load scenarios testing graph loading and pathfinding at scale |
| `boundary` | Edge case and boundary condition tests |
| `negative-test` | Tests that should NOT find a path (validation of error handling) |

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

### Payment Gateway Scenarios
- **payment-gateway-group-mint-001.json** - On-chain verified routed transfer to payment gateway with group trust (block 44288768)
- **payment-gateway-consented-001.json** - Routed transfer with CONSENTED FLOW ENABLED, tests isPermittedFlow fix (block 44289365)
- To create new fixtures, run: `cd scripts && node payment-gateway-test.mjs`

### Production Discrepancy Scenarios
These test cases document confirmed differences between production and staging pathfinder results.
Intended to track graph completeness issues where production has incomplete trust data.

- **prod-discrepancy-v1-user-001.json** - V1 user to gateway: prod returns 0, staging finds path (Jan 2026)
- **prod-discrepancy-nonv1-user-001.json** - Non-V1 user proving V1 filtering is not the cause (Jan 2026)
- **prod-discrepancy-partial-flow-001.json** - Partial flow on prod vs full flow on staging (Jan 2026)

### Performance & Stress Test Scenarios
Test scenarios designed to validate pathfinder behavior under high load conditions.

- **high-trust-avatar-001.json** - Avatar with 5,620+ trust edges (graph loading stress test)
- **high-trust-consented-001.json** - High-trust avatar with consented flow enabled
- **deep-nesting-5hop-001.json** - Deep multi-hop path (5+ intermediaries)
- **deep-nesting-consented-001.json** - Deep path with consented source
- **max-transfers-boundary-001.json** - High maxTransfers boundary test (100 transfers)

### Group-to-Group Scenarios
Transfers between group treasuries - rare but important for organizational payments.

- **group-to-group-001.json** - Direct group treasury to group transfer
- **group-to-group-consented-001.json** - Group-to-group with consented intermediary
- **multi-group-collateral-001.json** - Path routing through multiple groups
- **multi-group-consented-001.json** - **CRITICAL**: Multi-group + consented flow (isPermittedFlow bug scenario)

### Edge Case Scenarios
Boundary conditions and negative tests.

- **filter-zero-overlap-001.json** - Token filter with no valid intersection (negative test)
- **low-balance-partial-001.json** - Partial flow with simulated low balance
- **competing-paths-001.json** - Multiple valid routes to sink (path selection)
- **consented-wrapped-001.json** - Consented flow + wrapped token output (pipeline ordering)
