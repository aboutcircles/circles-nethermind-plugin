# Testing Guide

This guide covers the testing infrastructure for the Circles Nethermind Plugin.

## Overview

### Test Types

| Type                  | Description                                        | Runs On               |
| --------------------- | -------------------------------------------------- | --------------------- |
| **Unit Tests**        | Fast, isolated tests with no external dependencies | All PRs + pushes      |
| **Integration Tests** | Tests against real blockchain state via test-env   | Push to dev/main only |
| **E2E Tests**         | Full contract execution on Anvil forks             | Push to dev/main only |

All tests run by default. When `TEST_ENV_URL` is not set, integration/E2E tests skip gracefully with a message.

### CI Security Model

**Why don't integration tests run on PRs?**

Integration tests require access to `staging.circlesubi.network/test-env`, which provides block-filtered PostgreSQL, Anvil forks, and RPC proxy. If these ran on PRs from forks, malicious actors could exfiltrate credentials or access internal staging infrastructure.

By running only on push to dev/main (trusted code), credentials stay protected while still catching regressions before release.

---

## Test Environment (circles-test-environment)

The test environment is a service that provides isolated, reproducible test environments at specific blockchain states.

### Why Sessions?

Each test session provides:

- **State Isolation** - Each Anvil fork is independent; tests don't interfere with each other
- **Block-Specific Views** - PostgreSQL queries only see data up to the session's block number
- **Resource Management** - Sessions auto-expire (default 5m, max 10m), preventing leaks
- **Concurrency Limit** - Max 10 concurrent sessions to prevent resource exhaustion

**Important:** Always close sessions when done. Abandoned sessions block slots until TTL expires.

### Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              STAGING HOST                                    │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │                  TEST ENVIRONMENT SERVICE (:8080)                       │ │
│  │                                                                         │ │
│  │  /api/v1/session              Create/manage sessions                    │ │
│  │  /api/v1/session/{id}/query   Execute SQL (SELECT only)     ──────┐     │ │
│  │  /api/v1/session/{id}/anvil   Proxy to Anvil JSON-RPC       ───┐  │     │ │
│  │  /api/v1/session/{id}/rpc     Proxy to Circles RPC Host   ──┐  │  │     │ │
│  │  /api/v1/blocks               Block availability info       │  │  │     │ │
│  └─────────────────────────────────────────────────────────────│──│──│     │ │
│                                                                │  │  │     │ │
│            ┌───────────────────────────────────────────────────┘  │  │     │ │
│            │              ┌───────────────────────────────────────┘  │     │ │
│            │              │              ┌───────────────────────────┘     │ │
│            ▼              ▼              ▼                                 │ │
│  ┌──────────────────┐  ┌─────────────┐  ┌──────────────────┐               │ │
│  │ Circles RPC Host │  │ Anvil       │  │ PostgreSQL       │               │ │
│  │                  │  │ (per-session│  │                  │               │ │
│  │ Receives header: │  │  on dynamic │  │ Block-filtered   │               │ │
│  │X-Max-Block-Number│  │  ports)     │  │ via session var  │               │ │
│  │ to filter queries│  │             │  │                  │               │ │
│  └────────┬─────────┘  └─────────────┘  └────────▲─────────┘               │ │
│           │                                      │                         │ │
│           └──────────────────────────────────────┘                         │ │
│                    (RPC Host also queries PostgreSQL)                      │ │
└──────────────────────────────────────────────────────────────────────────────┘
          ▲
          │ HTTPS (publicly accessible via Caddy reverse proxy)
          │
    External Clients (SDK, CI, Local Dev)
```

### Session Workflow

```
1. POST /api/v1/session { blockNumber: N, features: ["db", "anvil", "rpc"], ttl: "5m" }
   → Creates: Anvil fork at block N, DB connection with block filter, RPC proxy
   → Returns: sessionId

2. Use session:
   - POST /session/{id}/query  → SQL against block-filtered DB
   - POST /session/{id}/anvil  → JSON-RPC to Anvil fork
   - POST /session/{id}/rpc    → Circles RPC (also block-filtered)

3. DELETE /session/{id} (or wait for TTL to auto-expire)
```

### Endpoints

| Endpoint | Service          | Description                                        |
| -------- | ---------------- | -------------------------------------------------- |
| `/query` | PostgreSQL       | Raw SQL queries (SELECT only) against indexed data |
| `/rpc`   | Circles RPC Host | `circles_*` methods with block filtering           |
| `/anvil` | Anvil fork       | Execute transactions, impersonate accounts         |

---

## Test Projects

| Project                         | Location          | Coverage Focus                           |
| ------------------------------- | ----------------- | ---------------------------------------- |
| **Circles.Pathfinder.Tests**    | `src/Pathfinder/` | Path algorithm, edge ordering, scenarios |
| **Circles.Common.Tests**  | `src/Common/`      | Demurrage math, numeric precision        |
| **Circles.Index.Query.Tests**   | `src/Index/`      | Query building, SQL generation           |
| **Circles.Cache.Service.Tests** | `src/Cache/`      | Cache invalidation, state management     |
| **Circles.Rpc.Host.Tests**      | `src/Rpc/`        | RPC handlers, ABI encoding               |

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

### Running with Test Environment

```bash
# All tests with test environment (integration + E2E)
TEST_ENV_URL=https://staging.circlesubi.network/test-env \
dotnet test src/Pathfinder/Circles.Pathfinder.Tests

# Run specific scenario by ID
dotnet test --filter "Name~mint-path-bug-001"
```

### Environment Variables

| Variable              | Purpose                   | Default                 |
| --------------------- | ------------------------- | ----------------------- |
| `TEST_ENV_URL`        | Test environment base URL | (none - tests skip)     |
| `PATHFINDER_URL`      | Pathfinder service URL    | `http://localhost:8080` |
| `BUILD_CONFIGURATION` | Build config for test.sh  | `Debug`                 |
| `TEST_VERBOSITY`      | Test output verbosity     | `normal`                |

---

## API Reference

### Create Session

```bash
POST https://staging.circlesubi.network/test-env/api/v1/session
Content-Type: application/json

{
  "blockNumber": 43193632,
  "features": ["db", "anvil", "rpc"],
  "ttl": "5m"
}
```

**Response:**

```json
{
  "sessionId": "abc123def456",
  "blockNumber": 43193632,
  "status": "Active",
  "expiresAt": "2025-12-24T13:00:00Z"
}
```

### Execute SQL Query

```bash
POST /api/v1/session/{sessionId}/query

{
  "sql": "SELECT * FROM \"CrcV2_Trust\" WHERE truster = @addr LIMIT 10",
  "parameters": { "addr": "0x..." },
  "maxRows": 1000
}
```

**Safety constraints:**

- Only SELECT/WITH queries allowed
- Blocked: DROP, DELETE, UPDATE, INSERT, ALTER, etc.
- Max rows: 10,000
- Query timeout: 30s

### Anvil RPC Proxy

```bash
POST /api/v1/session/{sessionId}/anvil

{
  "jsonrpc": "2.0",
  "method": "anvil_impersonateAccount",
  "params": ["0x..."],
  "id": 1
}
```

Supports all Ethereum JSON-RPC methods plus Anvil-specific: `anvil_impersonateAccount`, `anvil_setBalance`, etc.

### Circles RPC Proxy

```bash
POST /api/v1/session/{sessionId}/rpc

{
  "jsonrpc": "2.0",
  "method": "circles_getAvatarInfo",
  "params": ["0x..."],
  "id": 1
}
```

---

## Usage Examples

### TypeScript (with SDK)

Using the `@aboutcircles/test-env` SDK package:

```typescript
import { TestEnvironmentClient } from "@aboutcircles/test-env"

const client = new TestEnvironmentClient("https://staging.circlesubi.network/test-env")

const session = await client.createSession({
  blockNumber: 43193632,
  features: ["db", "anvil", "rpc"],
  ttl: "5m", // Keep it short!
})

try {
  // 1. Query PostgreSQL (block-filtered)
  const trustData = await session.query(
    `SELECT * FROM "CrcV2_Trust" WHERE truster = @addr LIMIT 100`,
    { addr: "0xde374ece6fa50e781e81aac78e811b33d16912c7" }
  )

  // 2. Query Circles RPC (block-filtered)
  const avatarInfo = await session.getAvatarInfo("0xde374ece6fa50e781e81aac78e811b33d16912c7")

  // 3. Execute on Anvil fork
  await session.impersonateAccount("0xde374ece6fa50e781e81aac78e811b33d16912c7")
  await session.setBalance("0xde374ece6fa50e781e81aac78e811b33d16912c7", 10n ** 18n)

  const txHash = await session.sendTransaction({
    from: "0xde374ece6fa50e781e81aac78e811b33d16912c7",
    to: "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8",
    data: "0x...",
  })

  const receipt = await session.getTransactionReceipt(txHash)
  console.log("Success:", receipt.status === "0x1")
} finally {
  await session.close() // Always cleanup!
}
```

### TypeScript (raw fetch)

```typescript
const TEST_ENV = "https://staging.circlesubi.network/test-env"

// 1. Create session
const session = await fetch(`${TEST_ENV}/api/v1/session`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({
    blockNumber: 43193632,
    features: ["db", "anvil", "rpc"],
    ttl: "5m",
  }),
}).then((r) => r.json())

const { sessionId } = session

try {
  // 2. Query PostgreSQL (block-filtered)
  const trustData = await fetch(
    `${TEST_ENV}/api/v1/session/${sessionId}/query`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        sql: `SELECT * FROM "CrcV2_Trust" WHERE truster = @addr LIMIT 100`,
        parameters: { addr: "0xde374ece6fa50e781e81aac78e811b33d16912c7" },
      }),
    }
  ).then((r) => r.json())

  // 3. Query Circles RPC (block-filtered)
  const avatarInfo = await fetch(
    `${TEST_ENV}/api/v1/session/${sessionId}/rpc`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        jsonrpc: "2.0",
        method: "circles_getAvatarInfo",
        params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
        id: 1,
      }),
    }
  ).then((r) => r.json())

  // 4. Execute on Anvil fork
  await fetch(`${TEST_ENV}/api/v1/session/${sessionId}/anvil`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      jsonrpc: "2.0",
      method: "anvil_impersonateAccount",
      params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
      id: 2,
    }),
  })

  const txHash = await fetch(`${TEST_ENV}/api/v1/session/${sessionId}/anvil`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      jsonrpc: "2.0",
      method: "eth_sendTransaction",
      params: [
        {
          from: "0xde374ece6fa50e781e81aac78e811b33d16912c7",
          to: "0x...",
          data: "0x...",
        },
      ],
      id: 3,
    }),
  }).then((r) => r.json())
} finally {
  // 5. Always cleanup!
  await fetch(`${TEST_ENV}/api/v1/session/${sessionId}`, {
    method: "DELETE",
  })
}
```

### C# (NUnit)

```csharp
[Test]
public async Task ExecuteTransferOnAnvil()
{
    await using var session = await TestEnvironmentClient.CreateSessionAsync(
        43193632,
        features: ["db", "anvil"],
        ttl: "30m");

    using var anvil = new AnvilExecutionHelper(session);

    // Verify block number
    var block = await anvil.GetBlockNumberAsync();
    Assert.That(block, Is.GreaterThanOrEqualTo(43193632));

    // Impersonate and execute transactions
    await anvil.ImpersonateAccountAsync("0x...");
    var result = await anvil.ExecuteTransactionAsync(
        from: "0x...",
        to: "0x...",
        data: "0x...");

    Assert.That(result.Success, Is.True);
}
```

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

### Regression Scenario JSON

Create files in `src/Pathfinder/Circles.Pathfinder.Tests/RegressionScenarios/`:

```json
{
  "id": "category-description-001",
  "name": "Human readable description",
  "category": "direct-transfer|group-minting|self-conversion|wrapped-tokens|consented-flow|no-path",
  "block": 43193632,
  "source": "0x...",
  "sink": "0x...",
  "description": "Detailed explanation",

  "shouldFindPath": true,
  "minFlow": "1000000000000000000",
  "expectedRevertReason": null,

  "runOnAnvil": false,
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
- `performance` - High-load scenarios (large graphs, many edges)
- `boundary` - Edge cases and boundary condition tests
- `negative-test` - Tests that verify correct error handling
- `production-discrepancy` - Cases where prod differs from staging (tracking issues)

### Key Regression Scenarios (Jan 2026)

| Scenario | Category | Tests |
|----------|----------|-------|
| `high-trust-avatar-001` | performance | Group with 1500+ members, graph loading at scale |
| `high-trust-consented-001` | consented-flow | Large group with consented flow enabled |
| `deep-nesting-5hop-001` | direct-transfer | 5+ intermediary hops, deep graph traversal |
| `deep-nesting-consented-001` | consented-flow | Deep path with consented source |
| `group-to-group-001` | group-minting | Group treasury to group transfer |
| `group-to-group-consented-001` | consented-flow | Group-to-group with consented intermediary |
| `multi-group-collateral-001` | group-minting | Path through multiple groups |
| `multi-group-consented-001` | consented-flow | **CRITICAL** - Multi-group + consented (isPermittedFlow bug scenario) |
| `competing-paths-001` | direct-transfer | Multiple valid routes, path selection |
| `consented-wrapped-001` | consented-flow | Consented flow with wrapped token output |
| `filter-zero-overlap-001` | negative-test | Token filter with no valid intersection |
| `max-transfers-boundary-001` | boundary | High maxTransfers (100) boundary test |
| `low-balance-partial-001` | direct-transfer | Partial flow with simulated low balance |
| `prod-discrepancy-*` | production-discrepancy | Tracking prod vs staging graph differences |

---

## E2E Tests: Contract Execution

E2E tests **actually execute transfers on-chain** via Anvil fork:

1. Test requests session with `features: ["db", "anvil"]`
2. Test-env spawns Anvil fork at specified block
3. Test computes path using block-filtered DB
4. Test builds `operateFlowMatrix` calldata from pathfinder output
5. Test executes transaction on Anvil via proxy, impersonating the source address
6. Test verifies transaction succeeds (no `ERC1155InsufficientBalance` revert)

---

## Troubleshooting

### Tests Skip with "TEST_ENV_URL not set"

Expected behavior for local development. Set the env var to run integration tests:

```bash
export TEST_ENV_URL=https://staging.circlesubi.network/test-env
```

### "Session creation failed" or "Max sessions reached"

1. Check test-env health: `curl $TEST_ENV_URL/health`
2. Wait for existing sessions to expire (TTL default: 5m)
3. Staging may be under maintenance

### Anvil Timeout Errors

Anvil forking requires an archive node with historical state. The test environment uses `rpc.gnosischain.com` as the fork RPC by default.

### Edge Ordering Tests Fail

These tests validate the mint-along-path fix. If failing:

1. Check `SortEdgesForMintDependencies()` in `TransferPathService.cs`
2. Ensure collateral edges precede mint edges in sorted output
3. Run specific scenario: `dotnet test --filter "Name~mint-path"`

---

## Deployment

The test environment is a **git submodule** of circles-nethermind-plugin.

```bash
# Initialize submodule
git submodule update --init circles-test-environment

# Deploy via Ansible (from aboutcircles-infrastructure repo)
make deploy-test-environment HOST=indexer-staging2

# Or with full stack
make all HOST=indexer-staging2 -e deploy_test_environment=true
```

---

## Discovery Queries

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

## Further Documentation

- Test Environment: `circles-test-environment/README.md`
- Pathfinder Tests: `src/Pathfinder/Circles.Pathfinder.Tests/README.md`
- Regression Scenarios: `src/Pathfinder/Circles.Pathfinder.Tests/RegressionScenarios/README.md`
