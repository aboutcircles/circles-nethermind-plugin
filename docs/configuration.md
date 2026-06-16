# Configuration Reference

Every environment variable read by the five runtime services, in one place.
Unless marked **required**, all variables are optional and fall back to the
listed default. Variables are read once at process startup (the Pathfinder
Host re-reads its settings on each access, but since the process environment
is static this is effectively equivalent).

The Index plugin, RPC Host, Pathfinder, and Cache Service read plain
environment variables (`Environment.GetEnvironmentVariable`). The Metrics
Exporter uses ASP.NET Core configuration — its keys live in
`appsettings.json` and can be overridden via environment variables using the
`__` separator (e.g. `ConnectionStrings__CirclesDb`).

Sources of truth: `src/Common/Circles.Common/Settings.cs`,
`src/Rpc/Circles.Rpc.Host/Settings.cs`,
`src/Pathfinder/Circles.Pathfinder/Settings.cs`,
`src/Pathfinder/Circles.Pathfinder.Host/Settings.cs`,
`src/Cache/Circles.Cache.Service/CacheServiceSettings.cs`,
`src/Metrics/Circles.Metrics.Exporter/appsettings.json`.

## Shared (all DB-connected services)

| Variable | Type | Default | Effect |
|---|---|---|---|
| `POSTGRES_CONNECTION_STRING` | string | — **required** | Primary PostgreSQL connection string (write access). Index plugin and Pathfinder Host throw at startup when missing. |
| `POSTGRES_READONLY_CONNECTION_STRING` | string | falls back to `POSTGRES_CONNECTION_STRING` | Read-only connection string used for query paths. |
| `CIRCLES_PG_NOTIFY_CHANNEL` | string | `circles_index_events` | PostgreSQL NOTIFY channel for new-block notifications. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | string | unset | Enables OTLP trace export when set (read by the RPC Host, Pathfinder Host, and Cache Service). |

## Index plugin (Nethermind)

| Variable | Type | Default | Effect |
|---|---|---|---|
| `CIRCLES_PLUGIN_DISABLED` | bool | `false` | Disables the Circles indexing plugin entirely. |
| `START_BLOCK` | long | `0` | First block to index. |
| `BLOCK_BUFFER_SIZE` | int | `20000` | Blocks buffered before processing. |
| `EVENT_BUFFER_SIZE` | int | `100000` | Events buffered before flushing to the DB. |
| `INDEXER_WRITE_MODE` | enum | `Auto` | Batch write strategy: `Copy`, `Upsert`, or `Auto`. |
| `REINDEX_FROM_BLOCK` | long | unset | Reindex from this block. Combine with `REINDEX_TABLES` or `REINDEX_ALL_TABLES`. Remove after the reindex completes. |
| `REINDEX_ALL_TABLES` | bool | `false` | Reindex all tables from `REINDEX_FROM_BLOCK`. |
| `REINDEX_TABLES` | string[] (comma-sep.) | `[]` | Specific tables to reindex from `REINDEX_FROM_BLOCK`. |
| `REINDEX_ALLOW_PARTIAL_DEPENDENCIES` | bool | `false` | Allow a partial-table reindex even when dependent tables are not included. |
| `PATHFINDER_MAX_CONCURRENT_REQUESTS` | int | `cores` | Parsed but currently unused — superseded by `MAX_CONCURRENT_REQUESTS` in the Pathfinder Host. |

### IPFS profile downloader (Index plugin)

| Variable | Type | Default | Effect |
|---|---|---|---|
| `IPFS_GATEWAYS` | string[] (comma-sep.) | `https://circles-profiles.myfilebase.com` | Gateway URLs for profile downloads. |
| `IPFS_MAX_PARALLELISM` | int | `192` | Max parallel downloads. |
| `IPFS_HTTP_TIMEOUT_SEC` | int | `1` | Per-request HTTP timeout. |
| `IPFS_MAX_BACKOFF_SEC` | int | `259200` | Max retry backoff (72 h). |
| `IPFS_STATS_INTERVAL_SEC` | int | `30` | Statistics log interval. |
| `IPFS_ERROR_MAX_LEN` | int | `1024` | Max stored error-message length. |
| `IPFS_WRITER_BATCH_SIZE` | int | `256` | DB write batch size. |
| `IPFS_MAX_DOWNLOAD_BYTES` | long | `167936` | Max bytes per profile download (160 KiB + 4 KiB). |

## RPC Host (port 8081)

| Variable | Type | Default | Effect |
|---|---|---|---|
| `NETHERMIND_RPC_URL` | string | `http://localhost:8545` | Upstream Nethermind JSON-RPC. |
| `NETHERMIND_WS_URL` | string | derived from `NETHERMIND_RPC_URL` (`http→ws`) | Upstream WebSocket endpoint. |
| `ETH_SUBSCRIBE_ENABLED` | bool | `true` | Enable `eth_subscribe` WebSocket subscriptions. |
| `RPC_MAX_CONCURRENT_WS_SESSIONS` | int | `1000` | Global cap on concurrent WebSocket sessions; excess upgrades get HTTP 503. Values ≤ 0 disable the cap. |
| `RPC_MAX_CONCURRENT_REQUESTS` | int | `max(cores×4, 32)` | Request-concurrency semaphore. |
| `RPC_RATE_LIMIT_PER_SECOND` | int | `100` | Per-IP rate limit (calls/s); `0` disables. |
| `RPC_RATE_LIMIT_BURST` | int | `200` | Per-IP burst allowance. |
| `BALANCE_MODE` | string | `live` | Balance source: `live` (chain) or `database`. |
| `USE_CACHE_SERVICE` | bool | `false` | Serve balance/avatar queries from the Cache Service (also needs `CACHE_SERVICE_URL`). |
| `CACHE_SERVICE_URL` | string | unset | Cache Service base URL. |
| `PROFILE_PINNING_SERVICE_URL` | string | unset | Fast profile-search proxy URL. |
| `EXTERNAL_PATHFINDER_URL` | string | unset | Delegate pathfinding RPC methods to an external pathfinder (also used by the health check, and read by the in-plugin RPC of the Index plugin). |
| `DATABASE_QUERY_TIMEOUT_SECONDS` | int | `30` | General DB query timeout. |
| `PROFILE_SEARCH_TIMEOUT_SECONDS` | int | `30` | Profile search query timeout. |
| `INDEXER_MAX_LAG_BLOCKS` | long | `100` | Max indexer lag before `/ready` reports unhealthy. |
| `CORS_ALLOWED_ORIGINS` | string | `*` | Comma-separated allowed origins. |

## Pathfinder (port 8080)

| Variable | Type | Default | Effect |
|---|---|---|---|
| `NETHERMIND_RPC_URL` | string | — **required** | Upstream Nethermind JSON-RPC (host throws when missing). |
| `MAX_CONCURRENT_REQUESTS` | int | `max(cores×2, 8)` | Request-concurrency semaphore. |
| `PATHFINDER_SOLVER_TIMEOUT_SECONDS` | int | `10` | MaxFlow solver timeout. |
| `PATHFINDER_BALANCE_TIMEOUT_SECONDS` | int | `300` | Balance query timeout. |
| `PATHFINDER_TRUST_TIMEOUT_SECONDS` | int | `120` | Trust query timeout. |
| `PATHFINDER_GROUP_TIMEOUT_SECONDS` | int | `60` | Group query timeout. |
| `PATHFINDER_DEMURRAGE_SAFETY_MARGIN` | double | `0.999999` | Safety factor on demurraged balances (absorbs clock drift between graph load and execution). |
| `PATHFINDER_INCREMENTAL_ENABLED` | bool | `true` | Incremental graph updates; `false` = full refresh every block. |
| `PATHFINDER_FULL_REFRESH_INTERVAL_BLOCKS` | int | `200` | Blocks between full DB refreshes (drift correction). |
| `PATHFINDER_EXCLUDE_CONSENTED_INTERMEDIARIES` | bool | `true` | Exclude consented avatars from intermediary positions in flow paths. |
| `PATHFINDER_DISABLE_CONSENTED_FLOW` | bool | — | Deprecated alias for `PATHFINDER_EXCLUDE_CONSENTED_INTERMEDIARIES` (read only when the new name is unset). |
| `V2_EXCLUDED_ROUTING_ADDRESSES` | string | empty | Comma-separated addresses dropped from pathfinding entirely — deprecated groups, their wrappers, and custom sink-wrapper contracts. A listed **group** cascades to its indexed ERC20 wrappers; custom sink wrappers (not `ERC20WrapperDeployed` rows) must be listed **explicitly**. Default empty = no effect. Use to retire an owner-deprecated group/wrapper the chain still treats as valid. |
| `USE_CACHE_GRAPH_SOURCE` | bool | `true` iff `CACHE_SERVICE_URL` set | Load graph data from the Cache Service instead of the DB. |
| `CACHE_SERVICE_URL` | string | unset | Cache Service base URL. |
| `CACHE_GRAPH_REQUEST_TIMEOUT_SECONDS` | int | `60` | Cache Service graph request timeout. |
| `CACHE_GRAPH_FALLBACK_TO_DB` | bool | `true` | Fall back to the DB when the Cache Service is unavailable. |
| `MATVIEW_REFRESH_ENABLED` | bool | `true` | Refresh materialized views from the pathfinder. |
| `MATVIEW_REFRESH_FAST_BLOCKS` | int | `60` | Blocks between fast-tier matview refreshes (balances, avatars, groups). |
| `MATVIEW_REFRESH_SLOW_BLOCKS` | int | `180` | Blocks between slow-tier matview refreshes (trust scores). |
| `MATVIEW_REFRESH_INTERVAL_BLOCKS` | int | unset | Legacy: overrides both fast and slow tiers when set. |
| `MATVIEW_DB_CONNECTION_STRING` | string | falls back to read-only conn. string | Connection used for matview refreshes (needs write access on the views). |
| `HISTORICAL_GRAPH_CACHE_MAX_ENTRIES` | int | `5` | Max cached historical (block-pinned) graph snapshots. |
| `HISTORICAL_MAX_CONCURRENT_LOADS` | int | `2` | Max concurrent historical graph loads. |
| `CANARY_SIMULATION_ENABLED` | bool | `false` | Enable the on-chain simulation canary for solver results. |
| `CANARY_SIMULATION_QUEUE_SIZE` | int | `10` | Canary background simulation queue size. |
| `CANARY_BALANCE_PROBE_ENABLED` | bool | `true` | Kill switch for the canary balance probe — set to `false` to disable. |
| `CORS_ALLOWED_ORIGINS` | string | `*` | Comma-separated allowed origins. |
| `PATHFINDER_BASE_PATH` | string | `/pathfinder` | Public base path used for the generated OpenAPI server URL when behind a prefix-stripping reverse proxy. |

## Cache Service (port 5002)

| Variable | Type | Default | Effect |
|---|---|---|---|
| `PORT` | int | `5002` | HTTP listen port. |
| `ROLLBACK_CAPACITY` | int | `12` | Blocks of rollback history retained per cache. |
| `REORG_DETECTION_WINDOW` | int | `256` | Recent block hashes kept for reorg detection (must be ≥ `ROLLBACK_CAPACITY`). |
| `MAX_CATCHUP_LAG` | int | `10` | Max lag (blocks) before `/ready` reports not-ready. |
| `RECONCILIATION_ENABLED` | bool | `true` | Tail self-heal: re-derive recently active account balances from the DB. |
| `RECONCILIATION_WINDOW_BLOCKS` | int | `256` | Look-back window for reconciliation. |
| `RECONCILIATION_INTERVAL_BLOCKS` | int | `8` | Minimum blocks between reconciliation passes. |
| `RECONCILIATION_MAX_ACCOUNTS` | int | `5000` | Cap on accounts per reconciliation pass. |
| `IPFS_CACHE_MAX_ENTRIES` | int | `50000` | Max cached IPFS profile entries. |
| `CORS_ALLOWED_ORIGINS` | string | `*` | Comma-separated allowed origins. |

## Metrics Exporter (port 9100)

ASP.NET configuration keys (override with `__` separator as env vars):

| Key | Default | Effect |
|---|---|---|
| `ConnectionStrings:CirclesDb` | required (`appsettings.json` ships a localhost default — override in production) | Main Circles indexer database. |
| `ConnectionStrings:RepScoreDb` | unset | Reputation-score / blacklist database; rep_score metrics are disabled gracefully when missing. |
| `Metrics:CollectionIntervalSeconds` | per `appsettings.json` | KPI collection cadence. |
| `CoinGecko:ApiKey` | unset | Fiat pricing API key (falls back to Balancer GraphQL). |
| `Balancer:ApiUrl` | `https://api-v3.balancer.fi/graphql` (hardcoded in `BalancerPriceService.cs`) | Balancer V3 GraphQL endpoint for SCRC pricing. |

See `src/Metrics/Circles.Metrics.Exporter/README.md` and its
`appsettings.json` for the full key set (RepScore thresholds, deployment
prober environments).

## Backfill CLI

| Source | Effect |
|---|---|
| `--connection-string` / `-c` | Explicit connection string; takes precedence. |
| `POSTGRES_CONNECTION_STRING` | Fallback when the option is omitted. |
| `POSTGRES_USER` + `POSTGRES_PASSWORD` | Last-resort fallback for a constructed connection string. |
| `POSTGRES_HOST` / `POSTGRES_PORT` / `POSTGRES_DB` | Optional overrides for the constructed connection string (defaults: `localhost` / `5432` / `postgres`). |

## Contract addresses (shared)

Defaults are the Gnosis Chain production deployments
(`src/Common/Circles.Common/Settings.cs`); override only for other chains or
test deployments. Values are lower-cased on read (one Pathfinder Host read
site — `RouterAddress` reading `V2_BASE_GROUP_ROUTER` — preserves case);
list-valued variables are comma-separated.

`V1_HUB_ADDRESS`, `V2_HUB_ADDRESS`, `V1_NAME_REGISTRY_ADDRESS`,
`V2_NAME_REGISTRY_ADDRESS`, `V2_ERC20_LIFT_ADDRESS`,
`V2_STANDARD_TREASURY_ADDRESS`, `V2_BASE_GROUP_ROUTER`,
`V2_AFFILIATE_GROUP_REGISTRY_ADDRESS`, `V2_OIC_ADDRESS`,
`BASE_GROUP_DEPLOYER`, `V2_LBP_FACTORY_ADDRESS`,
`V2_TOKEN_OFFER_FACTORY_ADDRESS`, `V2_PAYMENT_GATEWAY_FACTORY_ADDRESS`,
`V2_CMGROUP_DEPLOYER`, `SAFE_PROXY_FACTORY_ADDRESSES`,
`V2_INVITATION_ESCROW_ADDRESS`, `V2_INVITATION_AT_SCALE_FARM_ADDRESSES`,
`V2_INVITATION_AT_SCALE_MODULE_ADDRESSES`,
`V2_INVITATION_AT_SCALE_REFERRALS_MODULE_ADDRESSES`,
`V2_INVITATION_AT_SCALE_QUOTA_GRANT_MODULE_ADDRESSES`,
`V2_SCORE_GROUP_MINT_POLICIES` (allowlist, default empty),
`SCORE_TREASURY_SUBTREASURIES` (mapping, format `agg:sub1,sub2;agg2:sub3`),
`V2_STANDARD_MINT_POLICY` (default lives in
`src/Pathfinder/Circles.Pathfinder/Settings.cs`, not Common).

## Test-only variables

| Variable | Effect |
|---|---|
| `TEST_ENV_URL` | Base URL of the circles-test-environment; gates scenario/E2E test suites. |
| `RUN_CACHE_INTEGRATION_TESTS` | Tri-state: `true` force-on, `false` force-off, unset = auto-detect Docker for Testcontainers-based cache tests. |
| `CIRCLES_CONNECTION_STRING` | Real indexer DB connection string; gates the Index CirclesV2 E2E tests (e.g. `TransferDataE2ETests`). |
| `PATHFINDER_URL` | Running pathfinder base URL for network-dependent tests (`MetricsEndpointTests` skips when unset; `NetworkPathfinderTests` defaults to `http://localhost:8080`). |
| `RUN_PATHFINDER_NETWORK_TESTS` | Opt-in switch for `NetworkPathfinderTests` (network-dependent). |
