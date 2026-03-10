# Materialized Views Architecture

## Overview

The Circles indexer uses materialized views to pre-compute expensive queries. These are refreshed periodically, trading real-time accuracy for query performance.

For real-time balance data, the **Cache Service** is the primary source — materialized views serve as fallbacks and power aggregate queries (profile search, trust scores, group listings).

## Materialized Views

| Matview | Wraps View | Purpose | Refresh Tier |
|---------|-----------|---------|-------------|
| `M_CrcV2_BalancesByAccountAndToken` | `V_CrcV2_BalancesByAccountAndToken` | Pre-aggregated token balances (demurrage applied at query time) | Fast (5 min) |
| `M_CrcV2_Avatars` | — | Avatar registry (3-way UNION + LATERAL CID lookup) | Fast (5 min) |
| `M_CrcV2_ReceiveCount` | — | Transfer receive counts for profile search ranking | Fast (5 min) |
| `M_CrcV2_Groups` | `V_CrcV2_Groups` | Group details (8 LEFT JOINs + ROW_NUMBER member counts) | Fast (5 min) |
| `V_TrustScores_Current` | — | Trust scores (network position, reciprocity, age) | Slow (1 hour) |

### Wrapping Pattern

When a matview replaces a regular view, the view is rewritten to `SELECT * FROM "M_..."`. This preserves backward compatibility — all existing queries against the view transparently get matview performance.

Example (`V_CrcV2_Groups.sql`):
```sql
CREATE OR REPLACE VIEW "V_CrcV2_Groups" AS SELECT * FROM "M_CrcV2_Groups";
```

### SQL File Conventions

- `M_*.sql` — Materialized views. Discovered by `DiscoverFunctionSql()`, executed before regular views.
- `V_*.sql` — Regular views. Discovered by `ExtractSchemaInfoFromResourceName()`, used for schema discovery.
- `F_*.sql` — Functions. Same execution path as `M_*`.

The `-- COLUMNS:` header in `V_*` files is parsed by the schema discovery system for column types. `M_*` files don't need this header.

## Refresh Tiers

### Fast Tier (~5 minutes / 60 blocks on Gnosis)

Cheap views that directly impact query latency. Refresh takes seconds.

**Controlled by**: `MATVIEW_REFRESH_FAST_BLOCKS` (default: 60)

### Slow Tier (~1 hour / 720 blocks on Gnosis)

Expensive views where staleness is acceptable. Trust scores involve a full-table window function over `CrcV2_Trust`.

**Controlled by**: `MATVIEW_REFRESH_SLOW_BLOCKS` (default: 720)

### Legacy Override

`MATVIEW_REFRESH_INTERVAL_BLOCKS` overrides both tiers if set (backward compatibility).

### Disable

`MATVIEW_REFRESH_ENABLED=false` disables all in-process matview refreshes (Docker cron still runs).

## Staleness Impact

| Matview | Max Staleness | User-Visible Effect |
|---------|--------------|-------------------|
| Balances | 5 min | Balance queries via SQL fallback may lag. Cache Service provides real-time data. |
| Avatars | 5 min | New registrations may take up to 5 min to appear in search results. |
| Groups | 5 min | New groups / member count changes lag up to 5 min. |
| Receive Count | 5 min | Profile search ranking may be slightly stale. |
| Trust Scores | 1 hour | Trust score updates lag up to 1 hour. Acceptable for ranking. |

## Refresh Mechanisms

Two independent refresh systems run in parallel for redundancy:

### 1. Pathfinder In-Process (primary)

`NetworkStateUpdaterService.RefreshMaterializedViewsIfDue()` runs matview refreshes as part of the block-processing loop. Tracks two block counters for fast/slow cadence.

**Metrics**: `circles_matview_refresh_duration_seconds`, `circles_matview_refresh_total`, `circles_matview_refresh_errors_total`, `circles_matview_last_refresh_block{tier}`

### 2. Docker Cron Container (backup)

The `matview-refresh` Docker container runs an independent cron loop:
- Inner loop: refreshes fast-tier views every 5 minutes (12 iterations)
- Outer loop: refreshes trust scores every 60 minutes

This ensures matviews stay fresh even if the Pathfinder is down or restarting.

## Operational Notes

### Manual Refresh

```sql
-- Refresh a specific matview (blocking)
REFRESH MATERIALIZED VIEW "M_CrcV2_Groups";

-- Refresh concurrently (requires unique index, doesn't block reads)
REFRESH MATERIALIZED VIEW CONCURRENTLY "M_CrcV2_Groups";
```

### Check Matview Status

```sql
-- List all matviews
SELECT matviewname, ispopulated FROM pg_matviews WHERE schemaname = 'public';

-- Check staleness (compare row counts)
SELECT 'M_CrcV2_Groups' AS name, count(*) FROM "M_CrcV2_Groups"
UNION ALL
SELECT 'M_CrcV2_Avatars', count(*) FROM "M_CrcV2_Avatars";
```

### Initial Population

All matviews are populated on creation (no `WITH NO DATA`). This means migrations block briefly while the initial query runs, but eliminates the `55000` error fallback dance on first refresh. The `55000` catch in `NetworkStateUpdaterService` and the Docker cron is retained as a safety net for manual matview recreation.

### Monitoring

#### Prometheus Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `circles_matview_refresh_duration_seconds` | Histogram | `view` | Time taken per matview refresh |
| `circles_matview_refresh_total` | Counter | `view` | Total successful refreshes |
| `circles_matview_refresh_errors_total` | Counter | `view` | Total refresh failures |
| `circles_matview_last_refresh_block` | Gauge | `tier` | Block number of last refresh (fast/slow) |

#### Alertmanager Rules

Defined in `aboutcircles-infrastructure/observability/prometheus/alerts/prometheus-alerts-matview.yml`:

| Alert | Condition | Severity |
|-------|-----------|----------|
| `MatviewRefreshFailing` | Error rate > 0 for 15min | warning |
| `MatviewRefreshStale` | No fast-tier refresh for 30min | critical |
| `MatviewTrustScoresStale` | No slow-tier refresh for 3h | warning |
| `MatviewRefreshSlow` | p95 refresh > 120s | warning |

#### Grafana Dashboard

Panel "45 Materialized Views" at `/grafana/d/circles-matviews` in the pathfinder dashboard group. Shows:
- Refresh duration per view (time series)
- Refresh errors per view (stat)
- Last refresh block per tier (stat with thresholds)
- Refresh success rate (gauge)

#### What to Watch

- `circles_matview_refresh_errors_total` increasing → check Postgres logs
- `circles_matview_last_refresh_block{tier="fast"}` not advancing → Pathfinder may be stuck
- `ispopulated = false` in `pg_matviews` → matview never populated, needs manual refresh
