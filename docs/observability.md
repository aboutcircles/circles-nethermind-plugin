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
- **Retention**: 30 days

Scrapes metrics from:
- `prometheus:9090` - Self-monitoring
- `node-exporter:9100` - Host system metrics
- `nethermind-gnosis:6060` - Blockchain node metrics
- `pathfinder:8080` - Path calculation service
- `rpc:8080` - RPC service
- `cache-service:3001` - Cache service
- `consensus-gnosis:5054` - Consensus client

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

## Future Enhancements

- [ ] Business KPI metrics (user growth, transfers, mints)
- [ ] RPC method-level metrics
- [ ] Alertmanager integration (Slack/Telegram)
- [ ] Recording rules for metric persistence
- [ ] Index plugin Prometheus metrics (currently console-only)
