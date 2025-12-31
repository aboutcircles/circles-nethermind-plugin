# Testing Guide

This guide covers the testing infrastructure for the Circles Nethermind Plugin.

## Overview

### Test Types

| Type | Category Attribute | When Runs | Description |
|------|-------------------|-----------|-------------|
| **Unit** | (none) | All PRs + pushes | Fast, isolated tests with no external dependencies |
| **Snapshot** | `[Category("Snapshot")]` | Push to dev/main only | Tests against real blockchain state via test-env |
| **Regression** | `[Category("Regression")]` | Push to dev/main only | Validates known bug fixes don't regress |
| **E2E** | `[Category("E2E")]` | Push to dev/main only | Full contract execution on Anvil forks |

### CI Security Model

**Why don't snapshot/regression tests run on PRs?**

Snapshot tests require access to `staging.circlesubi.network/test-env`, which provides:
- Block-filtered PostgreSQL sessions (time-travel queries)
- Anvil forks for contract execution
- Query proxy for pathfinder testing

If these tests ran on PRs from forks, malicious actors could:
1. Modify test code to exfiltrate the `TEST_ENV_URL` credential
2. Access internal staging infrastructure
3. Discover network topology and internal APIs

By running only on push to dev/main (trusted code), credentials stay protected while still catching regressions before release.

---

## Test Projects

| Project | Location | Coverage Focus |
|---------|----------|----------------|
| **Circles.Pathfinder.Tests** | `src/Pathfinder/` | Path algorithm, edge ordering, scenarios |
| **Circles.Index.Common.Tests** | `src/Index/` | Demurrage math, numeric precision |
| **Circles.Index.Query.Tests** | `src/Index/` | Query building, SQL generation |
| **Circles.Cache.Service.Tests** | `src/Cache/` | Cache invalidation, state management |
| **Circles.Rpc.Host.Tests** | `src/Rpc/` | RPC handlers, ABI encoding |

### Test Files by Project

<details>
<summary>Pathfinder Tests (most comprehensive)</summary>

- `EdgeOrderingTests.cs` - Mint-along-path edge ordering validation
- `V2PathfinderTests.cs` - Core algorithm tests
- `RegressionTests.cs` - Category=Regression, loads JSON scenarios
- `ScenarioTests.cs` - Category=Snapshot, comprehensive E2E scenarios
- `RegressionScenarios/*.json` - 14+ scenario definitions

</details>

<details>
<summary>Cache Service Tests</summary>

- `CacheStateServiceTests.cs` - Cache state machine
- `TokenOwnerByIdServiceTests.cs` - Token ownership lookup
- `TrustGraphCacheTests.cs` - Trust graph caching

</details>

---

## Running Tests

### Quick Reference

```bash
# All unit tests (fast, no external deps)
./scripts/test.sh

# Specific project
./scripts/test.sh pathfinder
./scripts/test.sh cache
./scripts/test.sh rpc
./scripts/test.sh index

# With coverage report
./scripts/test.sh --coverage

# Filter by test name
./scripts/test.sh --filter=EdgeOrdering
```

### Running Categorized Tests

```bash
# Snapshot tests (requires TEST_ENV_URL)
TEST_ENV_URL=https://staging.circlesubi.network/test-env \
dotnet test --filter "Category=Snapshot"

# Regression tests only
TEST_ENV_URL=https://staging.circlesubi.network/test-env \
dotnet test --filter "Category=Regression"

# Run specific scenario by ID
dotnet test --filter "Name~mint-path-bug-001"
```

### Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `TEST_ENV_URL` | Test environment base URL | (none - tests skip) |
| `PATHFINDER_URL` | Pathfinder service URL | `http://localhost:8080` |
| `POSTGRES_READONLY_CONNECTION_STRING` | Direct DB access for validation | (none) |
| `BUILD_CONFIGURATION` | Build config for test.sh | `Debug` |
| `TEST_VERBOSITY` | Test output verbosity | `normal` |

---

## Writing New Tests

### Unit Test Template (AAA Pattern)

```csharp
[Test]
public void MethodName_Scenario_ExpectedResult()
{
    // Arrange
    var input = CreateTestInput();
    var sut = new SystemUnderTest();

    // Act
    var result = sut.Method(input);

    // Assert
    Assert.That(result.Property, Is.EqualTo(expected));
}
```

### Regression Scenario JSON Schema

Create files in `src/Pathfinder/Circles.Pathfinder.Tests/RegressionScenarios/`:

```json
{
  "id": "category-description-001",
  "name": "Human readable description",
  "category": "direct-transfer|group-minting|self-conversion|wrapped-tokens|consented-flow|no-path",
  "block": 43193632,
  "source": "0x...",
  "sink": "0x...",
  "description": "Detailed explanation of what this tests",

  "shouldFindPath": true,
  "minFlow": "1000000000000000000",
  "expectedRevertReason": null,

  "runOnAnvil": false,
  "fromTokens": [],
  "toTokens": [],
  "excludedTokens": [],
  "maxTransfers": null,
  "withWrap": false,

  "tags": ["regression", "discovered"]
}
```

**Categories:**
- `direct-transfer` - Simple A→B token transfer
- `group-minting` - Transfers involving group token minting
- `self-conversion` - source==sink, converting between tokens
- `wrapped-tokens` - Involves wrapped/demurraged tokens
- `consented-flow` - Uses operator approvals (ApprovalForAll)
- `no-path` - Expected to return no valid path (negative test)

### Negative Test Patterns

**No path expected:**
```json
{
  "id": "no-path-disconnected-001",
  "shouldFindPath": false,
  "minFlow": null
}
```

**Contract revert expected:**
```json
{
  "id": "contract-revert-bad-order-001",
  "shouldFindPath": true,
  "runOnAnvil": true,
  "expectedRevertReason": "CirclesHubFlowEdgeIsNotPermitted"
}
```

---

## Test Environment

The test environment (`TEST_ENV_URL`) provides:

### Session API

```bash
# Create a session at specific block
curl -X POST "$TEST_ENV_URL/session" \
  -H "Content-Type: application/json" \
  -d '{"block": 43193632}'
# Returns: {"sessionId": "uuid", "proxyUrl": "...", "anvilUrl": "..."}

# Health check
curl "$TEST_ENV_URL/health"
```

### Query Proxy

Routes requests to block-filtered database:
- `POST /rpc` - JSON-RPC to Circles RPC with session context
- `GET /pathfinder/flow` - Pathfinder queries at session block

### Anvil Forks

Fork mainnet state at specific block for contract execution testing.

### Discovery Queries

Find interesting scenarios in staging database:

```sql
-- Users with multiple collateral tokens (group minting scenarios)
SELECT b.account, COUNT(DISTINCT b."tokenId") as tokens
FROM "V_CrcV2_BalancesByAccountAndToken" b
WHERE b."balance" > 1000000000000000000
GROUP BY b.account
HAVING COUNT(DISTINCT b."tokenId") >= 3
LIMIT 10;

-- Find users with consented flow (ApprovalForAll)
SELECT "truster", "trustee"
FROM "CrcV2_ApprovalForAll"
WHERE "approved" = true
LIMIT 10;

-- Large balance holders (for testing max flow)
SELECT "account", SUM("balance") as total
FROM "V_CrcV2_BalancesByAccountAndToken"
GROUP BY "account"
ORDER BY total DESC
LIMIT 10;
```

---

## Troubleshooting

### Tests Skip with "TEST_ENV_URL not set"

Expected behavior for local development. Set the env var to run snapshot tests:
```bash
export TEST_ENV_URL=https://staging.circlesubi.network/test-env
```

### "Session creation failed" Errors

1. Check test-env health: `curl $TEST_ENV_URL/health`
2. Staging may be under maintenance
3. Block number may be too old (pruned state)

### Pathfinder Tests Timeout

The test.sh script auto-starts docker services for pathfinder tests:
```bash
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis pathfinder rpc
```

If this fails, manually verify services:
```bash
curl http://localhost:8081/rpc -X POST \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"circles_health","params":[]}'
```

### Edge Ordering Tests Fail

These tests validate the mint-along-path fix. If failing:
1. Check `SortEdgesForMintDependencies()` in `TransferPathService.cs`
2. Ensure collateral edges precede mint edges in sorted output
3. Run specific scenario: `dotnet test --filter "Name~mint-path"`

### Coverage Reports Empty

Ensure coverlet is installed:
```bash
dotnet tool install --global coverlet.console
./scripts/test.sh --coverage
# Results in TestResults/coverage/
```

---

## CI/CD Integration

### GitHub Actions Workflow

```yaml
# .github/workflows/ci-build-test.yml
jobs:
  build-and-test:    # All PRs + pushes - unit tests only
  snapshot-tests:    # Push only - requires TEST_ENV_URL secret
  regression-tests:  # Push only - requires TEST_ENV_URL secret
```

### Local Pre-Push Validation

Run before pushing:
```bash
# Quick validation
./scripts/test.sh

# Full validation (if you have test-env access)
TEST_ENV_URL=https://staging.circlesubi.network/test-env \
dotnet test --filter "Category!=E2E"
```

---

## Adding Test Coverage

### Recent Additions

- **NumericPrecisionTests.cs** - Tests for demurrage edge cases (day 0, underflow, large values)
- **PathExtractionTests.cs** - Unit tests for path extraction algorithm (empty/cyclic graphs)
- **LogDataParsingHelperTests.cs** - ABI decoding tests (addresses, UInt256, arrays, strings)
- **Negative scenario JSON files** - `no-path-*.json` for expected failure cases

### Remaining Gaps

1. **Full LogParser tests** - `src/Index/Circles.Index.CirclesV2/LogParser.cs` integration tests
   - `LogDataParsingHelper` is now tested but the full `LogParser` needs Nethermind mocks
   - Consider using real log fixtures from blockchain
2. **RPC integration** - CirclesRpcModule methods marked `[Ignore]`

### Test Naming Convention

```
MethodName_Scenario_ExpectedResult

Examples:
- SortEdges_CollateralBeforeMint_ReturnsOrderedList
- DemurrageCalculation_Day0_ReturnsOriginalAmount
- FindPath_DisconnectedGraph_ReturnsEmpty
```
