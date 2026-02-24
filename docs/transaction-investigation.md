# Transaction Investigation Guide

This document explains how to investigate Circles V2 transactions on Gnosis mainnet, particularly for debugging routed transfers and group minting operations.

## Prerequisites

```bash
# Install Foundry (includes cast)
curl -L https://foundry.paradigm.xyz | bash
foundryup

# Alternative: use the built-in script
cd scripts && npm install
```

## Quick Reference

| Address | Description |
|---------|-------------|
| `0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8` | Hub V2 |
| `0xdc287474114cc0551a81ddc2eb51783fbf34802f` | Router |
| `0x186725D8fe10a573DC73144F7a317fCae5314F19` | Payment Gateway Factory |

## Using the Built-in Script

The fastest way to analyze a transaction:

```bash
cd scripts
source ../.env  # Load TX_PRIVATE_KEY and SAFE_ADDRESS

# Analyze any transaction
node payment-gateway-test.mjs analyze-tx 0x2920e223b72d6121ae0cb310b733019c58f09343d1d57f34e24f6eb472964c9a

# Check consented flow status
node payment-gateway-test.mjs check-consented 0x4b6F72008e7ACa33De36B6565eF30264626B21dB

# Enable consented flow (trust router from Safe)
node payment-gateway-test.mjs enable-consented
```

## Using Cast (Foundry)

### Get Transaction Receipt

```bash
# Full receipt with all logs
cast receipt 0x2920e223... --rpc-url https://rpc.gnosischain.com

# Get just the logs
cast receipt 0x2920e223... --rpc-url https://rpc.gnosischain.com | jq '.logs'
```

### Decode ERC1155 Events

```bash
# Event: TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)
# Topic0: 0xc3d58168c5ae7397731d063d5bbf3d657854427343f4c083240f7aacaa2d0f62

# Query TransferSingle events from Hub V2 at a specific block
cast logs --from-block 44288768 --to-block 44288768 \
  --address 0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8 \
  'TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)' \
  --rpc-url https://rpc.gnosischain.com
```

### Find Router Involvement (Burns to 0x0)

Transfers TO `0x0` indicate collateral being burned for group token minting:

```bash
cast logs --from-block 44288768 --to-block 44288768 \
  --address 0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8 \
  'TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)' \
  --topic2 0x0000000000000000000000000000000000000000000000000000000000000000 \
  --rpc-url https://rpc.gnosischain.com
```

### Check Avatar Type

```bash
# Is it a group?
cast call 0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8 \
  "isGroup(address)(bool)" 0xaa9081197e02f2fdacfc65e7606743fa2d005208 \
  --rpc-url https://rpc.gnosischain.com

# Is it a human?
cast call 0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8 \
  "isHuman(address)(bool)" 0x4b6F72008e7ACa33De36B6565eF30264626B21dB \
  --rpc-url https://rpc.gnosischain.com

# Is it an organization?
cast call 0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8 \
  "isOrganization(address)(bool)" 0x4b6F72008e7ACa33De36B6565eF30264626B21dB \
  --rpc-url https://rpc.gnosischain.com
```

### Check Consented Flow Status

```bash
ROUTER=0xdc287474114cc0551a81ddc2eb51783fbf34802f
AVATAR=0x4b6F72008e7ACa33De36B6565eF30264626B21dB

# Check if avatar trusts router (= consented flow enabled)
cast call 0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8 \
  "isTrusted(address,address)(bool)" $AVATAR $ROUTER \
  --rpc-url https://rpc.gnosischain.com
```

### Decode Amounts

```bash
# Convert hex to decimal (in wei)
cast --to-dec 0x0de0b6b3a7640000
# Result: 1000000000000000000 (= 1 CRC)

# Format as ether
cast --from-wei 1000000000000000000
# Result: 1.0
```

## Understanding Transfer Flows

### Regular Transfer (No Router)

```
Source → Intermediary1 → Intermediary2 → Sink
        (personal token transfers)
```

- All transfers are between non-zero addresses
- No burns to `0x0`, no mints from `0x0`

### Routed Transfer (Mint-Along-Path)

```
Source → Intermediary1 → Router (burn) → Group (mint) → Sink
```

When the pathfinder finds a path through a group:
1. Personal tokens are transferred to intermediaries
2. Collateral tokens are **burned** (transferred to `0x0`)
3. Group tokens are **minted** (transferred from `0x0`)
4. Group tokens are delivered to the sink

Evidence of router involvement:
- **Burns to 0x0**: Collateral deposited for minting
- **Mints from 0x0**: Group tokens created
- Same transaction has both burns AND mints

### Payment Gateway Flow

```
Source → ... → Router → Gateway → (return to Source)
                         ↑
                    (trusts groups)
```

Payment gateways:
1. Trust specific groups (allowing them to receive group tokens)
2. Receive group tokens from routed transfers
3. Return ALL received tokens back to the original sender

## Common Issues

### ERC1155InsufficientBalance

**Cause**: Collateral burn happened AFTER mint operation
**Fix**: `SortEdgesForMintDependencies()` ensures burn edges precede mint edges

### isPermittedFlow Error

**Cause**: Consented flow validation ran before Router insertion
**Fix**: Pipeline reorder - `InsertRouterInTransfers` runs BEFORE `ValidateConsentedFlow`

### No Path Found

Check:
1. Source has CRC balance
2. Sink is reachable through trust network
3. Groups involved trust the necessary intermediaries

## Creating Regression Test Fixtures

After investigating a transaction, create a fixture for automated testing:

```json
{
  "id": "payment-gateway-group-mint-001",
  "name": "Payment Gateway Group Mint Test",
  "category": "payment-gateway",
  "block": 44288768,
  "source": "0x4b6F72008e7ACa33De36B6565eF30264626B21dB",
  "sink": "0x1f6db4d3cd8a506307952897a5b6d3bdedffbd1e",
  "description": "Routed transfer to payment gateway with group trust",
  "shouldFindPath": true,
  "minFlow": "1000000000000000000",
  "runOnAnvil": true,
  "tags": ["payment-gateway", "group-minting", "router"]
}
```

Save to `src/Pathfinder/Circles.Pathfinder.Tests/RegressionScenarios/`.

## Running Regression Tests

```bash
# Run all pathfinder tests
./scripts/test.sh pathfinder

# Run against staging test environment
TEST_ENV_URL=https://staging.circlesubi.network/test-env ./scripts/test.sh pathfinder

# Run specific test
dotnet test --filter "Name~payment-gateway"
```

## Test Environment

The staging test environment provides:
- Anvil fork at any block
- PostgreSQL snapshot
- Pathfinder API

```bash
# Create session at specific block
curl -X POST "https://staging.circlesubi.network/test-env/sessions" \
  -H "Content-Type: application/json" \
  -d '{"block": 44288768, "features": ["db", "anvil"], "ttl": "30m"}'
```
