# Observability Stack

This document describes the observability infrastructure for the Circles Nethermind Plugin stack.

## Architecture Overview

```
                                   ┌─────────────────────┐
                                   │      Grafana        │
                                   │    (Dashboards)     │
                                   └──────────┬──────────┘
                                              │
                          ┌───────────────────┼───────────────────┐
                          │                   │                   │
                    ┌─────▼─────┐      ┌──────▼─────┐      ┌──────▼─────┐
                    │Prometheus │      │    Loki    │      │Alertmanager│
                    │ (Metrics) │      │   (Logs)   │      │  (Alerts)  │
                    └─────┬─────┘      └──────┬─────┘      └────────────┘
                          │                   │
         ┌────────────────┼────────────────┐  │
         │                │                │  │
    ┌────▼────┐     ┌─────▼────┐    ┌──────▼──────┐
    │ Services│     │Nethermind│    │  Promtail   │
    │ /metrics│     │  :6060   │    │(Log scraper)│
    └─────────┘     └──────────┘    └──────┬──────┘
                                           │
                                   ┌───────▼───────┐
                                   │Docker Containers│
                                   └───────────────┘
```

## Components

### Prometheus (Metrics)
- **Port**: 9090
- **Config**: `docker/prometheus.yml`
- **Data**: `.state/prometheus/`
- **Retention**: 365 days

Scrapes metrics from:
- `prometheus:9090` - Self-monitoring
- `node-exporter:9100` - Host system metrics
- `nethermind-gnosis:6060` - Blockchain node metrics
- `pathfinder:8080` - Path calculation service
- `rpc:8080` - RPC service
- `cache-service:3001` - Cache service
- `consensus-gnosis:5054` - Consensus client
- `metrics-exporter:9100` - Business KPIs
- `blackbox-exporter:9115` - Health endpoint probing

### Loki (Logs)
- **Port**: 3100
- **Config**: `docker/loki-config.yml`
- **Data**: `.state/loki/`
- **Retention**: 7 days

### Promtail (Log Collection)
- **Config**: `docker/promtail-config.yml`
- **Data**: `.state/promtail/`

Collects logs from:
- Docker containers (via docker socket)
- System syslog

### Grafana (Visualization)
- **Port**: 3000
- **Provisioning**: `docker/grafana/provisioning/`
- **Data**: `.state/grafana/`

Datasources:
- Prometheus (uid: `prometheus`)
- Loki (uid: `loki`)

---

## Metrics Inventory

### Cache Service (`cache-service:3001/metrics`)

The most comprehensive metrics, defined in `src/Cache/Circles.Cache.Service/CacheMetrics.cs`:

**State Metrics (Gauges)**:
| Metric | Description |
|--------|-------------|
| `circles_cache_last_processed_block` | Last block number processed |
| `circles_cache_database_lag_blocks` | Blocks behind the database |
| `circles_cache_warmup_complete` | 1 if warmup complete, 0 otherwise |
| `circles_cache_listener_connected` | 1 if pg_notify connected |

**Size Metrics (Gauges)**:
| Metric | Description |
|--------|-------------|
| `circles_cache_v1_avatars_total` | V1 avatars in cache |
| `circles_cache_v2_avatars_total` | V2 avatars in cache |
| `circles_cache_groups_total` | Groups in cache |
| `circles_cache_v1_balances_total` | V1 balance entries |
| `circles_cache_v2_balances_total` | V2 balance entries |
| `circles_cache_indexed_addresses_v1_total` | V1 addresses in index |
| `circles_cache_indexed_addresses_v2_total` | V2 addresses in index |
| `circles_cache_entries_total` | Total cache entries |

**Operation Counters**:
| Metric | Description |
|--------|-------------|
| `circles_cache_reorgs_detected_total` | Blockchain reorgs handled |
| `circles_cache_blocks_processed_total` | Blocks processed |
| `circles_cache_notifications_received_total` | pg_notify notifications |

**Query Metrics**:
| Metric | Labels | Description |
|--------|--------|-------------|
| `circles_cache_balance_queries_total` | `version` | Balance query count |
| `circles_cache_avatar_queries_total` | `batch` | Avatar info query count |
| `circles_cache_profile_queries_total` | `batch` | Profile CID query count |
| `circles_cache_balance_query_duration_seconds` | `version` | Balance query latency |

### Pathfinder (`pathfinder:8080/metrics`)

Defined in `src/Pathfinder/Circles.Pathfinder.Host/FindPathMetrics.cs`:

| Metric | Type | Description |
|--------|------|-------------|
| `circles_findpath_inflight_requests` | Gauge | Concurrent requests in progress |
| `circles_findpath_rejected_requests_total` | Counter | Requests rejected (concurrency limit) |

Plus standard HTTP metrics from prometheus-net middleware.

### RPC Host (`rpc:8080/metrics`)

Currently only exposes standard HTTP middleware metrics from prometheus-net:
- `http_request_duration_seconds` - Request latency histogram
- `http_requests_received_total` - Request count

### Index Plugin (Console Only)

Defined in `src/Index/Circles.Index/IndexPerformanceMetrics.cs`:

Logs to console only (not scraped by Prometheus):
- Blocks/second
- Transactions/second
- Logs/second

### Nethermind (`nethermind-gnosis:6060/metrics`)

Standard Nethermind metrics including:
- Block processing
- Peer connections
- Sync status
- Database operations

### Node Exporter (`node-exporter:9100/metrics`)

Host system metrics:
- CPU usage (`node_cpu_seconds_total`)
- Memory (`node_memory_*`)
- Disk (`node_filesystem_*`)
- Network (`node_network_*`)

---

## Dashboards

### 1. Circles Overview (`circles-overview.json`)

**Purpose**: Service health and cache statistics

**Panels**:
- Service Status (up/down indicators)
- Cache Size (avatars, groups, balances)
- Database Lag
- HTTP Request Rate
- Request Latency (p50, p95, p99)

### 2. Node Infrastructure (`node-infrastructure.json`)

**Purpose**: Host system monitoring

**Panels**:
- CPU Usage
- Memory Usage
- Disk I/O
- Network Traffic
- Filesystem Usage

### 3. Logs Explorer (`logs.json`)

**Purpose**: General log aggregation

**Panels**:
- Log stream viewer with filters
- Log volume over time

### 4. Services Logs (`services-logs.json`)

**Purpose**: Filtered logs for key services

**Sections**:
- **Errors & Warnings**: Cross-service error/warning aggregation
- **Circles Stack**: Nethermind (filtered), RPC, Pathfinder, Cache, Postgres
- **Infrastructure**: Nethermind (full), Consensus, Caddy

---

## Adding New Metrics

### 1. Add prometheus-net Package

```xml
<PackageReference Include="prometheus-net" Version="8.2.1" />
<PackageReference Include="prometheus-net.AspNetCore" Version="8.2.1" />
```

### 2. Define Metrics Class

```csharp
using Prometheus;

public static class MyMetrics
{
    public static readonly Counter RequestsTotal = Metrics.CreateCounter(
        "myservice_requests_total",
        "Total requests",
        new CounterConfiguration { LabelNames = new[] { "method" } });

    public static readonly Histogram RequestDuration = Metrics.CreateHistogram(
        "myservice_request_duration_seconds",
        "Request duration",
        new HistogramConfiguration
        {
            LabelNames = new[] { "method" },
            Buckets = Histogram.ExponentialBuckets(0.001, 2, 10)
        });

    public static readonly Gauge ActiveConnections = Metrics.CreateGauge(
        "myservice_active_connections",
        "Active connections");
}
```

### 3. Use Metrics in Code

```csharp
// Counter
MyMetrics.RequestsTotal.WithLabels("GET").Inc();

// Histogram
using (MyMetrics.RequestDuration.WithLabels("GET").NewTimer())
{
    await ProcessRequest();
}

// Gauge
MyMetrics.ActiveConnections.Inc();
// ... work ...
MyMetrics.ActiveConnections.Dec();
```

### 4. Expose Metrics Endpoint

```csharp
// In Program.cs
app.MapMetrics(); // Adds /metrics endpoint
```

### 5. Add Prometheus Scrape Config

In `docker/prometheus.yml`:

```yaml
- job_name: 'myservice'
  metrics_path: /metrics
  static_configs:
    - targets: ['myservice:8080']
```

---

## Docker Compose Files

### `docker-compose.observability.yml`

Contains:
- Prometheus
- Node Exporter
- Loki
- Promtail

### `docker-compose.grafana.yml`

Contains:
- Grafana (with provisioned dashboards and datasources)

### Usage

```bash
# Start observability stack
docker compose -f docker/docker-compose.gnosis.yml \
               -f docker/docker-compose.observability.yml \
               -f docker/docker-compose.grafana.yml up -d

# Access Grafana
open http://localhost:3000
```

---

## Troubleshooting

### Metrics Not Appearing

1. Check service is running: `docker ps | grep <service>`
2. Check metrics endpoint: `curl http://localhost:<port>/metrics`
3. Check Prometheus targets: http://localhost:9090/targets
4. Verify scrape config in `prometheus.yml`

### Logs Not Appearing in Loki

1. Check Promtail status: `docker logs promtail`
2. Verify Docker socket access
3. Check container labels
4. Test Loki API: `curl http://localhost:3100/ready`

### Dashboard Shows "No Data"

1. Verify datasource UID matches (`prometheus` or `loki`)
2. Check time range (recent data may not exist yet)
3. Test query in Grafana Explore panel
4. Verify metric exists: http://localhost:9090/graph

---

## Business KPI Metrics (`metrics-exporter:9100/metrics`)

The Metrics Exporter service queries PostgreSQL periodically (every 60s) to expose business KPIs.

**Source**: `src/Metrics/Circles.Metrics.Exporter/`

### Dune Dashboard Coverage

| Dune KPI | Metric | Status |
|----------|--------|--------|
| Total Humans | `circles_total_humans{version="v1\|v2"}` | ✅ Implemented |
| Total Backers | `circles_total_backers` | ✅ Implemented |
| Active Trusts | `circles_active_trusts` | ✅ Implemented |
| Active Minters | `circles_active_minters{window="24h"}` | ✅ Implemented |
| Metri Profiles | `circles_profiles_created{window="total\|24h"}` | ✅ Implemented |
| Trust Changes | `circles_trust_changes{window,type}` | ✅ Implemented |
| Daily Mints (Volume) | `circles_daily_mint_volume_crc` | ✅ Implemented |
| Daily Mints (Count) | `circles_daily_mint_count` | ✅ Implemented |
| New Backers | `circles_new_backers{window}` | ✅ Implemented |
| 14-Day Minting Fraction | `circles_minting_fraction_14d` | ✅ Implemented |
| New Organizations | `circles_new_organizations{window}` | ✅ Implemented |
| New Groups | `circles_new_groups{window}` | ✅ Implemented |
| CRC Price | - | ❌ Needs external API |
| Gnosis App Txs | - | ❌ App-specific |

### User Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_total_humans` | `version` | `COUNT(*) FROM "CrcV1"."Signup"` / `"CrcV2"."RegisterHuman"` | Total registered humans |
| `circles_total_organizations` | - | `COUNT(*) FROM "CrcV2"."RegisterOrganization"` | Total organizations |
| `circles_total_groups` | - | `COUNT(*) FROM "CrcV2"."RegisterGroup"` | Total groups |
| `circles_new_users` | `window` | `WHERE timestamp > NOW() - interval` | New users in 24h/7d/30d |

### Trust Network Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_active_trusts` | - | `COUNT(*) FROM "V_CrcV2_TrustRelations"` | Active trust relationships |
| `circles_new_trusts` | `window` | `WHERE timestamp > NOW() - interval` | New trusts in 24h/7d |
| `circles_trust_changes` | `window`, `type` | Trust/Untrust events | Added/removed trusts |

### Economic Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_total_backers` | - | `COUNT(DISTINCT backer) FROM "CrcV2"."CirclesBackingInitiated"` | LBP backers |
| `circles_active_minters` | `window` | `COUNT(DISTINCT human) FROM "CrcV2"."PersonalMint"` | Unique minters in window |
| `circles_daily_mint_volume_crc` | - | `SUM(amount) FROM "CrcV2"."PersonalMint"` | CRC minted in 24h |
| `circles_daily_transfer_volume_crc` | - | `SUM(value) FROM "CrcV2"."TransferSingle"` | CRC transferred in 24h |
| `circles_daily_transfer_count` | - | `COUNT(*) FROM "CrcV2"."TransferSingle"` | Transfers in 24h |
| `circles_unique_transacting_addresses` | `window` | Distinct from/to addresses | DAU/WAU equivalent |

### Group Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_group_members_total` | - | Group membership count | Total group memberships |
| `circles_group_mint_volume` | `window` | Group mint events | Group mint volume |

### Profile Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_profiles_created` | `window` | Profile registration events | Profiles created (total/24h) |

### Activity Rate Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_minting_rate` | `window` | Minters in window / total humans | % of users who minted (14d/30d/90d) |
| `circles_spending_rate` | `window` | Spenders in window / total humans | % of users who transferred (14d/30d/90d) |
| `circles_transfer_volume` | `window` | `SUM(value) FROM "CrcV2"."TransferSingle"` | Transfer volume by window (24h/7d/30d) |
| `circles_mint_volume` | `window` | `SUM(amount) FROM "CrcV2"."PersonalMint"` | Mint volume by window (24h/7d/30d) |

### Sybil Detection Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_accounts_without_profile` | - | LEFT JOIN on metadata | Accounts with no profile CID |
| `circles_accounts_without_incoming_trust` | - | NOT IN trustee list | Accounts trusted by no one |
| `circles_batch_registrations` | `window` | GROUP BY blockNumber | Accounts in batches (>5/block) |
| `circles_mint_and_drain_accounts` | `window` | Zero balance after minting | Minted then drained accounts |
| `circles_high_volume_inviters` | `window` | GROUP BY inviter | Inviters with >10 invites |
| `circles_suspicious_accounts` | - | Combined sybil indicators | Total suspicious accounts |
| `circles_organic_accounts` | - | Has profile + trust + activity | Total organic accounts |

### Network Health Metrics

| Metric | Labels | SQL Source | Description |
|--------|--------|------------|-------------|
| `circles_average_trust_connections` | - | AVG trusts per avatar | Network connectivity |
| `circles_isolated_accounts` | - | Accounts with 0 trusts | Completely isolated users |

### Collection Metrics

| Metric | Description |
|--------|-------------|
| `circles_kpi_collection_duration_seconds_total` | Time spent collecting KPIs |
| `circles_kpi_collection_errors_total{metric}` | Collection errors by metric |
| `circles_kpi_last_collection_timestamp` | Unix timestamp of last collection |

---

## RPC Method Metrics (`rpc:8080/metrics`)

**Source**: `src/Rpc/Circles.Rpc.Host/RpcMetrics.cs`

| Metric | Labels | Type | Description |
|--------|--------|------|-------------|
| `circles_rpc_requests_total` | `method` | Counter | Total requests by RPC method |
| `circles_rpc_request_duration_seconds` | `method` | Histogram | Request duration by method |
| `circles_rpc_errors_total` | `method`, `error_type` | Counter | Errors by method and type |
| `circles_rpc_inflight_requests` | `method` | Gauge | Concurrent requests by method |
| `circles_rpc_active_subscriptions` | - | Gauge | Active WebSocket subscriptions |

**Buckets**: 1ms, 5ms, 10ms, 25ms, 50ms, 100ms, 250ms, 500ms, 1s, 2.5s, 5s, 10s

---

## Consensus Client Metrics (`consensus-gnosis:5054/metrics`)

Lighthouse exposes standard Ethereum consensus metrics.

**Required Configuration** (in `docker-compose.gnosis.yml`):

```yaml
command: |
  lighthouse
  beacon_node
  --metrics
  --metrics-address=0.0.0.0
  --metrics-port=5054
  # ... other flags
```

| Metric | Description |
|--------|-------------|
| `beacon_head_state_slot` | Current beacon head slot |
| `beacon_finalized_epoch` | Last finalized epoch |
| `sync_eth2_synced` | 1 if synced, 0 if syncing |
| `libp2p_peers` | Connected peers |
| `beacon_participation_*` | Attestation participation rates |

---

## Alerting

### Alertmanager (`alertmanager:9093`)

**Config**: `docker/alertmanager.yml`

**Receivers**:
- **Slack** (default): All alerts go to `#circles-alerts`
- **Telegram** (optional): Critical alerts only (uncomment in config)

**Environment Variables**:
```bash
SLACK_WEBHOOK_URL=https://hooks.slack.com/services/...
TELEGRAM_BOT_TOKEN=your-bot-token  # Optional
TELEGRAM_CHAT_ID=your-chat-id      # Optional
```

### Alert Rules (`docker/prometheus-alerts.yml`)

| Alert | Severity | Condition | Description |
|-------|----------|-----------|-------------|
| `ServiceDown` | critical | `up == 0` for 1m | Service unreachable |
| `HighRpcErrorRate1h` | warning | `>5 errors in 1h` | Elevated error rate |
| `HighRpcErrorRate24h` | critical | `>20 errors in 24h` | Critical error rate |
| `IndexerLag` | warning | `lag > 100 blocks` for 5m | Cache falling behind |
| `CacheListenerDisconnected` | critical | `listener == 0` for 2m | DB notify disconnected |
| `HighRpcLatency` | warning | `p95 > 5s` for 5m | Slow RPC responses |
| `DiskSpaceLow` | warning | `>80% used` | Disk space warning |
| `DiskSpaceCritical` | critical | `>90% used` | Disk space critical |

### Recording Rules (`docker/prometheus-recording-rules.yml`)

Pre-aggregated metrics for persistence and dashboard efficiency:

```yaml
circles:total_humans:sum          # Sum of all humans
circles:active_trusts:sum         # Current active trusts
circles:daily_mints:sum           # Daily mint volume
circles:rpc_request_rate:5m       # RPC request rate
circles:rpc_error_rate:5m         # RPC error rate
circles:rpc_latency_p95:5m        # RPC p95 latency
```

---

## Data Persistence

All data persists across container restarts via Docker volumes:

| Component | Host Path | Container Path | Retention |
|-----------|-----------|----------------|-----------|
| Prometheus TSDB | `.state/prometheus/` | `/prometheus` | 365 days |
| Loki chunks | `.state/loki/` | `/loki` | 7 days |
| Alertmanager | `.state/alertmanager/` | `/alertmanager` | Silences/notifications |
| Grafana | `.state/grafana/` | `/var/lib/grafana` | Dashboards/settings |
| Promtail | `.state/promtail/` | `/tmp/promtail` | Position tracking |

**Recording Rules** ensure KPI metrics are persisted in Prometheus TSDB even if the metrics-exporter restarts.

---

## Available UIs

| Service | URL | Purpose |
|---------|-----|---------|
| **Grafana** | `http://localhost:3000` | Dashboards, logs, alerting visualization |
| **Prometheus** | `http://localhost:9090` | Query metrics, view targets, check alert rules |
| **Alertmanager** | `http://localhost:9093` | View/silence alerts, see notification history |
| **Loki** | `http://localhost:3100` | API only (use Grafana for UI) |

### Prometheus UI Features
- `/targets` - Check which services are being scraped
- `/alerts` - View all alert rules and their states
- `/graph` - Query and graph metrics directly
- `/config` - View current configuration

### Alertmanager UI Features
- View firing alerts
- Silence alerts (temporary mute)
- See notification history
- Test alert routing

### Grafana Alert Integration
- **Alerting menu** (bell icon) shows all Prometheus alerts
- Create Grafana-native alerts on any metric
- Configure notification channels

### Testing Alerting

```bash
# Send test alert to Alertmanager
curl -X POST 'http://localhost:9093/api/v1/alerts' \
  -H 'Content-Type: application/json' \
  -d '[{
    "labels": {"alertname": "TestAlert", "severity": "warning"},
    "annotations": {"summary": "Test alert", "description": "Testing alerting pipeline"}
  }]'

# Check alert status
curl http://localhost:9093/api/v1/alerts

# Check Prometheus alert rules
curl http://localhost:9090/api/v1/rules
```

---

## Dashboards

### 1. Circles Overview (`circles-overview.json`)
- Service health (Nethermind, Lighthouse, Pathfinder, RPC, Cache)
- Beacon sync status
- Block/slot heights
- Cache statistics

### 2. Circles KPIs (`circles-kpis.json`)
- Total Humans, Backers, Active Trusts, Minters
- User growth trends (24h/7d/30d)
- Economic activity (mint/transfer volume)

### 3. RPC Analytics (`rpc-analytics.json`)
- Request rate by method
- Latency percentiles (p50/p95/p99)
- Errors by method and type
- Top methods by volume

### 4. Alerting (`alerting.json`)
- Service health status
- Alert threshold visualization
- Error rate trends (1h/24h)

### 5. Node Infrastructure (`node-infrastructure.json`)
- CPU, Memory, Disk, Network
- Filesystem usage table

### 6. Logs Explorer (`logs.json`)
- Container log volume
- Error/warning filtering
- Full log search

### 7. Services Logs (`services-logs.json`)
- Per-service log filtering

### 8. Sybil Detection (`sybil-detection.json`)

- Organic vs suspicious account breakdown
- Account quality indicators (profile adoption, trust reception)
- Sybil patterns: batch registrations, mint-and-drain, high-volume inviters
- Network health: average trust connections, isolation rate
- Pie charts showing account distribution

### 9. Service Health (`service-health.json`)

- Live/Ready status for all services (RPC, Pathfinder, Cache)
- Health check history timeline
- Response time monitoring
- Uptime statistics (24h rolling)

---

## Blackbox Exporter (`blackbox-exporter:9115`)

Probes service health endpoints using HTTP checks.

**Config**: `docker/blackbox-exporter.yml`

### Probed Endpoints

| Job | Endpoint | Interval | Purpose |
|-----|----------|----------|---------|
| `blackbox-ready` | `/ready` | 30s | Service readiness (sync, dependencies) |
| `blackbox-live` | `/live` | 15s | Service liveness (process running) |

### Exposed Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `probe_success` | Gauge | 1 if probe succeeded, 0 otherwise |
| `probe_duration_seconds` | Gauge | Time taken for probe |
| `probe_http_status_code` | Gauge | HTTP status code returned |

### Health Endpoint Semantics

| Service | `/live` | `/ready` |
|---------|---------|----------|
| **RPC** | Always 200 | Checks Nethermind sync, DB, Pathfinder |
| **Pathfinder** | Always 200 | Checks graphs loaded |
| **Cache** | Always 200 | Checks warmup complete, pg_notify connected |

---

## Future Enhancements

- [ ] CRC Price metric (needs external API - Balancer/CoW/DIA)
- [ ] Index plugin Prometheus metrics (currently console-only)
- [ ] Database indexes for heavy sybil detection queries
