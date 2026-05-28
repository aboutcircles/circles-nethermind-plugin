# Score-Group Mint Along Path

This document describes how score-group minting is represented in the indexer,
cache graph, and pathfinder.

## Scope

The implementation is not hard-coded to one router address in the graph model:
groups can have individual router addresses, and the pathfinder stores a
`group -> router` mapping.

The score-group mint-limit calculation is specific to mint policies that emit the
score-group events indexed by `Circles.Index.CirclesV2.ScoreGroup`:

- `GroupInitialized`
- `MerkleRootUpdated`
- `HistoricalSupply`
- `PersonalMinted`
- `RouterMinted`

Other custom mint policies can reuse the generic parts:

- indexed event tables
- per-group router mapping
- cache graph extension fields
- graph capacity hooks

They still need their own parser/schema and, if they have custom mint limits,
their own capacity calculation. The current `ScoreGroupMintLimitReader` encodes
the score-group policy formula and should not be assumed correct for arbitrary
mint policies.

## Router Selection

Base groups continue to use `V2_BASE_GROUP_ROUTER`.

Score groups are included when:

- their `mint` address is configured in `V2_SCORE_GROUP_MINT_POLICIES`, and
- a router is available through indexed `GroupInitialized.pathMintRouter`

If no indexed per-group router exists, the score group is not routed through the
base-group router and is not routed through an environment fallback. This avoids
producing paths that would use a router not configured in contract state.

Multiple score routers are supported when the contracts emit distinct
`GroupInitialized.pathMintRouter` values per group.

## Cache Graph Fields

The cache service's `/api/pathfinder/graph` response includes optional fields:

- `groupRouters`: maps each routed group to the router that should handle its
  mint path.
- `scoreGroupMintLimits`: maps `(group, collateralToken)` to the current
  available mint capacity.

They are optional for backward compatibility and for deployments that load the
pathfinder graph directly from Postgres. They are needed when the pathfinder runs
from the cache graph snapshot instead of direct SQL. Without these fields, a
cache-backed pathfinder could either use the wrong router or over-route into a
score group beyond what the mint policy will accept.

## Score-Group Mint Capacity

The available capacity for a `(group, collateral)` pair is calculated as:

```text
available =
  demurraged(latest historical max supply for collateral)
  + demurraged(latest personal minted amount for group/collateral)
  - current demurraged treasury collateral balance
```

If no `HistoricalSupply` event exists yet for a collateral, the reader falls
back to current demurraged supply from `V_CrcV2_BalancesByAccountAndToken`.

The pathfinder applies the result as a cap on the pool-to-score-group edge. A
zero or negative available limit removes that collateral edge for the score
group.

### Contract Correspondence

The formula mirrors the router/migration branch in the score mint policy source
copied under `../circles-groups/src/deployment/OffchainScoreBasedMintPolicy.sol.sol`:

```solidity
uint256 historicalSupplyOnToday = getHistoricalSupplyOnToday(collateral[i]);
uint256 mintedAmountOnToday = getMintedAmountOnToday(group, collateral[i]);
uint256 maxLimit = historicalSupplyOnToday + mintedAmountOnToday;
uint256 treasuryBalance = HUB.balanceOf(treasury, collateral[i]);
uint256 currentLimit = maxLimit - treasuryBalance;
if (amounts[i] > currentLimit) revert AmountExceedsCollateralLimit();
```

The indexer/pathfinder does not replace the contract check. It estimates the
same limit from indexed events and balances before building a path. The deployed
contract remains authoritative at execution time, so live routing applies
`PATHFINDER_DEMURRAGE_SAFETY_MARGIN` below the estimated limit to reduce the
risk of crossing the on-chain limit between graph build and transaction
execution.

Before production rollout, verify that the deployed mint policy bytecode/source
for `V2_SCORE_GROUP_MINT_POLICIES` matches the copied deployment source used for
this correspondence check.

## Configuration

```bash
V2_SCORE_GROUP_MINT_POLICIES=0x...
```

`V2_SCORE_GROUP_MINT_POLICIES` is comma-separated. Score-router addresses are
not configured as a global fallback; they are read from indexed
`GroupInitialized.pathMintRouter` events.

## Adding Another Mint Policy

For a mint policy with different rules:

1. Add an indexer parser/schema for its events.
2. Add any router-discovery event to the graph load path.
3. Add a capacity reader if the policy has mint caps.
4. Add cache graph fields only if the pathfinder must work from the cache
   snapshot.
5. Add tests for:
   - env unset behavior
   - base-group behavior unchanged
   - router selection
   - capacity capping
   - cache-backed and direct-SQL graph parity
