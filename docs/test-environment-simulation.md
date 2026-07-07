# Test Environment & Simulation Boundaries

How to validate pathfinder / RPC output against real historical on-chain state **before**
deploying, and the boundaries of what that validation does — and does not — cover.

## Table of Contents

- [Overview](#overview)
- [What gets pinned to a block](#what-gets-pinned-to-a-block)
- [The core boundary: routing is on pinned Postgres, not Anvil overlay](#the-core-boundary-routing-is-on-pinned-postgres-not-anvil-overlay)
- [Hypothetical state: use `simulated*`, not fork mutation](#hypothetical-state-use-simulated-not-fork-mutation)
- [Known partial pins and approximations](#known-partial-pins-and-approximations)
- [Using the test environment](#using-the-test-environment)
- [Pre-deploy regression gate](#pre-deploy-regression-gate)

---

## Overview

The **circles-test-environment** is a non-production tool that pins the data planes to a past
block so you can ask *"does the path the pathfinder computes for real block-N state actually
execute on-chain?"* — instead of choosing between testing with tiny amounts on head and a
blind deploy.

It exposes a session API: create a session for a block number and a set of features
(`db`, `rpc`, `pathfinder`, `anvil`), then drive the returned per-session proxy URLs. The
proxies inject the pinning header server-side, so clients (including the SDK and the
flow-visualization time-travel UI) need no header hacks.

> The test environment is **staging-only**. There is no production test-env, and the pinning
> planes below are the plugin's own capability — they exist on `staging`, `dev`, and `master`
> regardless of whether a test-env container is deployed.

---

## What gets pinned to a block

| Plane | Pinned? | Mechanism |
| --- | --- | --- |
| **Postgres (DB)** | ✅ | `circles_at_block` twin views; header-gated `SET search_path` |
| **RPC** | ✅ | `X-Max-Block-Number` → `search_path` switch + cache-bypass |
| **Pathfinder** | ✅ | `X-Max-Block-Number` → `ExecuteHistorical` → `HistoricalGraphCache` → `HistoricalLoadGraph` |
| **Anvil (fork)** | ✅ | real fork at `--fork-block-number N` |
| **Cache Service** | ⚠️ head-only | **bypassed** when the header is present (reads go to the pinned DB path) |

A request without `X-Max-Block-Number` behaves exactly as production: live state, served
through the cache. The header is what activates every pinned path above.

---

## The core boundary: routing is on pinned Postgres, not Anvil overlay

The pathfinder computes routes from **pinned Postgres state** (trust, balances, group mint
limits as of block N). It does **not** route against the Anvil fork's overlay.

Practically:

- ✅ You **can** test *"the path pathfinder computed for real block-N state executes on-chain
  without reverting"* — the Anvil fork is block-N state, and `execute-on-fork` /
  `AnvilExecutionHelper` run the resulting `operateFlowMatrix` via `eth_call` as ground truth.
- ❌ You **cannot** mutate the Anvil fork (`setBalance`, mint, impersonate) and have the
  pathfinder *re-route* against that mutated state. Fork mutation changes the chain, not the
  Postgres the solver reads.

`execute-on-fork` is therefore a *validator* of a computed path against real block-N state —
not a what-if routing engine.

---

## Hypothetical state: use `simulated*`, not fork mutation

For "what if this avatar had this balance / trust / consent?" scenarios, use the pathfinder's
`simulated*` request fields — they overlay onto the solver's input:

- `simulatedBalances`
- `simulatedTrusts`
- `simulatedConsentedAvatars`

These compose with block pinning: a `simulated*` overlay on top of a pinned block N answers
*"what path would exist at block N if this hypothetical held?"*.

**Caveat:** `simulated*` overlays are validated on **solver output** (max-flow, transfers, the
solver-integrity validator), **not** via `eth_call`. Hypothetical state is not on-chain, so it
cannot be checked against the fork. Reserve the Anvil differential check for non-simulated
scenarios.

---

## Known partial pins and approximations

- **Cache Service is head-only.** It is bypassed when `X-Max-Block-Number` is set, so pinned
  reads go straight to the DB path. Do not expect the cache to serve pinned state.
- **ScoreGroup treasury supply is approximate.** The treasury-supply component reads the
  current (unpinned) balance view; the token mapping is exact but the magnitude is
  head-state, not block-N. Group mint-limit *routing capacity* is otherwise pinned.

---

## Using the test environment

1. `POST /api/v1/session` with `{ "blockNumber": N, "features": ["db","rpc","pathfinder","anvil"] }`.
2. Drive the returned session-proxy URLs:
   - `…/session/{id}/query` — pinned Postgres
   - `…/session/{id}/rpc` — pinned RPC (`X-Max-Block-Number` injected)
   - `…/session/{id}/pathfinder/findPath` — pinned pathfinding
   - `…/session/{id}/anvil` — the block-N fork
3. Sessions are held for their TTL under a **global `MaxConcurrentSessions` cap** (default 10).
   Over the cap, session creation returns HTTP 503. Session-pool saturation, pinning
   degradation, and Anvil spawn failures are exported as `testenv_*` Prometheus metrics.

The **flow-visualization** time-travel feature is the interactive front end for this: pick a
past block, see the graph, run `simulated*` overlays as of that block, and optionally execute
the path on the session's Anvil fork. It is opt-in (`VITE_TEST_ENV_URL` must be set) and
staging-only.

---

## Pre-deploy regression gate

`Category=AnvilRegression` (CI job `anvil-regression`) is the pre-deploy gate: it forks the
pinned block, builds the pathfinder's `operateFlowMatrix`, and asserts it does not revert —
covering the mint-along-path path that was previously un-gated. It runs even when the broader
`snapshot-tests` differential job is red, because those flake against the shared test-env
under load.

All test-env-session-consuming CI jobs share one fleet-wide concurrency group so they never
compete for the global session cap, and each carries a `timeout-minutes` bound so a hung run
fails fast instead of holding the group.
