# Observability Stack

> **Note**: The observability stack configuration has been migrated to the
> [aboutcircles-infrastructure](https://github.com/aboutcircles/aboutcircles-infrastructure)
> repository. The canonical source for Prometheus configs, Grafana dashboards, and alert rules
> is now in `aboutcircles-infrastructure/observability/`.
>
> The Metrics Exporter (`src/Metrics/Circles.Metrics.Exporter/`) remains in this repository
> due to its tight coupling with the Circles database schema.

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
    ┌──────────────────────┼───────────────────┼───────────────────┐
    │                      │                   │                   │
┌───▼───┐ ┌────────┐ ┌─────▼────┐ ┌────────┐ ┌─▼───────┐ ┌─────────▼───────┐
│cAdvisor│ │Services│ │Nethermind│ │Blackbox│ │Promtail │ │ Docker Containers│
│        │ │/metrics│ │  :6060   │ │Exporter│ │(Logs)   │ │                  │
└────────┘ └────────┘ └──────────┘ └────────┘ └─────────┘ └──────────────────┘
    │           │                       │
    │     ┌─────┴─────────────┐         │
    │     │                   │         │
    ▼     ▼                   ▼         ▼
┌──────────────────────────────────────────────────────┐
│ Core: rpc, pathfinder, cache-service, nethermind     │
│ Ecosystem: invitations, auth                         │
│ Infra: postgres-exporter, caddy, node-exporter       │
│ External: IPFS gateway, staging.circlesubi.network   │
└──────────────────────────────────────────────────────┘
```

## Components

### Prometheus (Metrics)

- **Port**: 9090
- **Config**: `docker/observability/prometheus.yml`
- **Data**: `.state/prometheus/`
- **Retention**: 365 days

Scrapes metrics from:

- `prometheus:9090` - Self-monitoring
- `node-exporter:9100` - Host system metrics
- `cadvisor:8080` - Container-level resource metrics (CPU, memory, network, disk per container)
- `nethermind-gnosis:6060` - Blockchain node metrics
- `pathfinder:8080` - Path calculation service
- `rpc:8080` - RPC service
- `cache-service:3001` - Cache service
- `consensus-gnosis:5054` - Consensus client
- `metrics-exporter:9100` - Business KPIs
- `postgres-exporter:9187` - PostgreSQL-specific metrics
- `caddy:2019` - Reverse proxy metrics
- `invitation-app:3000` - Referral invitations service
- `auth-service:3001` - Authentication service (SIWE, Passkeys)
- `blackbox-exporter:9115` - Health endpoint probing

### Loki (Logs)

- **Port**: 3100
- **Config**: `docker/observability/loki-config.yml`
- **Data**: `.state/loki/`
- **Retention**: 7 days

### Promtail (Log Collection)

- **Config**: `docker/observability/promtail-config.yml`
- **Data**: `.state/promtail/`

Collects logs from:

- Docker containers (via docker socket)
- System syslog

### Grafana (Visualization)

- **Port**: 3000
- **Provisioning**: `docker/observability/grafana/provisioning/`
- **Data**: `.state/grafana/`

Datasources:

- Prometheus (uid: `prometheus`)
- Loki (uid: `loki`)

---

## Grafana Dashboards

All dashboards are provisioned automatically from `docker/observability/grafana/provisioning/dashboards/`.

### 1. Circles Overview (`circles-overview.json`)

**Purpose**: High-level operational view of the entire stack.

**Key Panels**:
- **Service Status**: Up/down indicators for all services
- **Cache Statistics**: Avatar counts, balance entries, groups
- **Database Lag**: How far the cache is behind the indexer
- **Request Rates**: HTTP requests per second across services
- **Latency Percentiles**: p50, p95, p99 response times

**Use Case**: First dashboard to check when investigating issues. Shows if services are healthy and processing requests normally.

---

### 2. Circles KPIs (`circles-kpis.json`)

**Purpose**: Business metrics aligned with Dune Analytics dashboards.

**Key Panels**:
- **Total Humans (V1/V2)**: Registered users by protocol version
- **Active Trusts**: Current trust relationships in the network
- **Active Minters**: Users who minted in the last 24h
- **Daily Volume**: CRC minted and transferred
- **User Growth**: New registrations over time windows

**Use Case**: Business stakeholders tracking network growth and adoption.

---

### 3. Monetary Economics (`monetary-economics.json`)

**Purpose**: Deep dive into CRC as a monetary system. Critical for a "fair money" project.

**Sections**:

#### Money Supply
| Panel | Metric | Why It Matters |
|-------|--------|----------------|
| Total CRC Supply | `circles_total_crc_supply` | Current circulating supply after demurrage |
| Total Minted All Time | `circles_total_minted_all_time_crc` | Historical issuance (before demurrage decay) |
| Net Inflow | `circles_net_inflow_crc{window}` | New CRC entering circulation per period |
| Active Balance Holders | `circles_active_balance_holders` | Accounts with non-zero balance |

#### Wealth Distribution
| Panel | Metric | Why It Matters |
|-------|--------|----------------|
| Gini Coefficient | `circles_gini_coefficient` | 0=equal, 1=unequal. Excludes infrastructure addresses |
| Gini by Type | `circles_gini_coefficient_by_type` | Inequality within humans/groups/orgs |
| Average Balance | `circles_average_balance_crc` | Mean CRC per holder |
| Median Balance | `circles_median_balance_crc` | Typical user balance (less affected by whales) |
| Top Holder Concentration | `circles_top_holder_concentration{top_n}` | % of supply held by top 10/100/1000 |
| Infrastructure vs Economic | `circles_infrastructure_holdings_*` | Pie chart: protocol holdings vs economic actors |

#### Money Velocity
| Panel | Metric | Why It Matters |
|-------|--------|----------------|
| Velocity (7d/30d/90d) | `circles_money_velocity{window}` | How often CRC changes hands. Higher = more economic activity |

#### User Activity
| Panel | Metric | Why It Matters |
|-------|--------|----------------|
| DAW/WAW/MAW | `circles_daily/weekly/monthly_active_wallets` | Active user counts (like DAU/MAU) |
| DAW/MAW Stickiness | Ratio | How "sticky" is usage? High = users return frequently |
| Retention Rate | `circles_user_retention_rate{window}` | % of users active in consecutive periods |
| First-Time Transactors | `circles_first_time_transactors{window}` | New users making first transfer |

#### Transfer Analysis
| Panel | Metric | Why It Matters |
|-------|--------|----------------|
| Avg/Median Transfer | `circles_average/median_transfer_amount_crc` | Typical transaction size |
| Transfer Percentiles | `circles_transfer_size_percentile_crc{percentile}` | P10/P25/P75/P90 distribution |
| Micro Transactions | `circles_micro_transaction_count{window}` | Transfers < 1 CRC (potential spam) |
| Large Transactions | `circles_large_transaction_count{window}` | Transfers > 100 CRC (significant moves) |

**Use Case**: Understanding CRC as a currency - is it being used? Is wealth concentrated? Is velocity healthy?

---

### 4. Sybil Detection (`sybil-detection.json`)

**Purpose**: Identify potential bot networks and fake accounts farming CRC.

**Why Sybil Detection Matters**:
Circles UBI works on trust. Sybil attackers create fake identities to farm CRC by:
1. Registering many accounts
2. Having them trust each other (closed ring)
3. Minting CRC daily
4. Draining to a single wallet

**Sections**:

#### Account Quality Overview
| Panel | Metric | Description |
|-------|--------|-------------|
| Organic Accounts | `circles_organic_accounts` | Accounts with profile + incoming trust + activity |
| Suspicious Accounts | `circles_suspicious_accounts` | Accounts matching multiple sybil indicators |
| Suspicious Ratio | Calculated | % of accounts flagged as suspicious |

#### Sybil Indicators
| Panel | Metric | Why It's Suspicious |
|-------|--------|---------------------|
| No Profile | `circles_accounts_no_profile` | Real users typically set up profiles |
| No Incoming Trust | `circles_accounts_no_trust_received` | No one trusts them = isolated/fake |
| Batch Registrations | `circles_batch_registrations{window}` | >5 accounts in same block = scripted |
| Mint-and-Drain | `circles_mint_and_drain_accounts{window}` | Minted but balance=0 = farmed and drained |
| High-Volume Inviters | `circles_high_volume_inviters{window}` | >10 invites = possible sybil farmer |

#### Network Health
| Panel | Metric | Description |
|-------|--------|-------------|
| Avg Trust Connections | `circles_average_trust_connections` | Network connectivity density |
| Isolated Accounts | `circles_isolated_accounts` | Zero connections in either direction |
| Isolation Rate | Calculated | % of users with no connections |

**Sybil Detection Logic**:

```
Suspicious Account = (No Profile) AND (No Incoming Trust) AND (Has Minted)

Organic Account = (Has Profile) AND (Has Incoming Trust) AND (Recent Activity)
```

**Use Case**: Governance and community health monitoring. Identify sybil patterns before they drain significant UBI.

---

### 5. Service Health (`service-health.json`)

**Purpose**: Real-time and historical health of all services.

**Key Panels**:
- **Live/Ready Status**: Current state of each service
- **Health History**: Timeline of up/down states
- **Response Times**: Health check latency
- **Uptime %**: Rolling 24h availability

**Health Endpoint Semantics**:

| Service | `/live` | `/ready` |
|---------|---------|----------|
| **RPC** | Process running | Nethermind synced + DB connected + Pathfinder reachable + Indexer synced |
| **Pathfinder** | Process running | Trust graphs loaded |
| **Cache** | Process running | Warmup complete + pg_notify connected + lag acceptable |
| **Nethermind** | Process running | Blockchain synced |
| **Consensus** | Process running | Beacon chain synced |
| **Invitations** | Process running | Database connected + referral system ready |
| **Auth** | Process running | Database connected + SIWE/Passkey services ready |

**Important**: RPC `/ready` is the **ultimate stack health check**. It validates the entire Circles stack is operational, not just individual services.

**Use Case**: Incident response. Quickly identify which service is unhealthy.

---

### 6. RPC Analytics (`rpc-analytics.json`)

**Purpose**: Detailed analysis of JSON-RPC endpoint usage.

**Key Panels**:
- **Request Rate by Method**: Which RPC methods are most used
- **Latency by Method**: p50/p95/p99 per method
- **Error Rate**: Errors by method and error type
- **Top Methods**: Ranked by request volume

**Use Case**: Performance optimization. Identify slow or error-prone RPC methods.

---

### 7. Alerting (`alerting.json`)

**Purpose**: Visualize alert thresholds and current state.

**Panels**:
- Service health with threshold lines
- Error rate trends (1h/24h rolling)
- Disk usage approaching limits
- Active alerts list

**Use Case**: Understanding why alerts fired and current proximity to thresholds.

---

### 8. Node Infrastructure (`node-infrastructure.json`)

**Purpose**: Host system resource monitoring.

**Panels**:
- CPU usage (per core, total)
- Memory usage (used, cached, buffers)
- Disk I/O (reads/writes per second)
- Network traffic (bytes in/out)
- Filesystem usage table

**Use Case**: Capacity planning and resource troubleshooting.

---

### 9. Logs (`logs.json`, `services-logs.json`)

**Purpose**: Centralized log exploration.

**Features**:
- Full-text search across all containers
- Filter by service, log level
- Error/warning aggregation
- Log volume over time

**Use Case**: Debugging and incident investigation.

---

### 10. Circles Liquidity Monitoring (`circles-liquidity.json`)

**Purpose**: Monitor Balancer vault liquidity and group treasuries, detect drains via aggregate TVL changes, and track whale transfers.

**Source Code**: `src/Metrics/Circles.Metrics.Exporter/Services/LiquidityCollectorService.cs`

**Collection Interval**: 5 minutes (configurable via `Metrics:LiquidityCollectionIntervalSeconds`)

**Alert Strategy**: Uses **aggregate TVL monitoring** instead of per-token z-scores to reduce noise while catching real attacks. Individual token volatility is expected; aggregate drops indicate coordinated activity.

**Sections**:

#### Overview Row

| Panel | Metric | Description |
|-------|--------|-------------|
| Balancer TVL (CRC) | `circles_balancer_tvl_total` | Total value locked across all tokens |
| Group Treasury TVL | `circles_group_treasury_tvl_total` | Total collateral across all groups |
| Active Tokens | `circles_balancer_vault_tokens_count` | Distinct tokens with liquidity |
| TVL Change (1h) | `circles_balancer_tvl_change_pct_1h` | Percentage change in total TVL |
| Whale Transfers (1h) | `circles_whale_transfer_total` | Large transfers (>100 CRC) in last hour |

#### Balancer Pool Liquidity

| Panel | Metric | Description |
|-------|--------|-------------|
| Top 10 Tokens by Balance | `circles_balancer_vault_balance` | Per-token balance in vault |
| Token Balances Over Time | `circles_balancer_vault_balance` | Historical balance trends |
| Balancer Vault TVL Over Time | `circles_balancer_tvl_total` | Aggregate TVL history |

#### TVL Change Monitoring (Primary Alert Source)

| Panel | Metric | Description |
|-------|--------|-------------|
| TVL Change % (1 hour) | `circles_balancer_tvl_change_pct_1h`, `circles_group_treasury_tvl_change_pct_1h` | Percentage change in aggregate TVL |

**TVL Alert Thresholds**:

| Alert | Threshold | Severity |
|-------|-----------|----------|
| Balancer TVL Warning | -5% in 1h | warning |
| Balancer TVL Critical | -10% in 1h | critical |
| Group Treasury Warning | -10% in 1h | warning |
| Group Treasury Critical | -20% in 1h | critical |

#### Z-Score Analysis (Dashboard Only, No Alerts)

Per-token z-scores are still collected for investigation but don't trigger alerts (too noisy with 600+ tokens).

| Panel | Metric | Description |
|-------|--------|-------------|
| Z-Score by Token | `circles_balancer_vault_zscore_1h` | Statistical anomaly indicator |
| Hourly Balance Change | `circles_balancer_vault_change_1h` | Per-token hourly delta |

**Z-Score Interpretation** (for manual investigation):

- `z > -1.0`: Normal activity
- `-2.0 < z < -1.0`: Slightly elevated outflow
- `-3.0 < z < -2.0`: Unusual outflow (2+ std devs)
- `z < -3.0`: Severe anomaly (3+ std devs)

#### Whale Transfers
| Panel | Metric | Description |
|-------|--------|-------------|
| Whale Transfers by Direction | `circles_whale_transfer_total` | Count of deposits vs withdrawals |
| Whale Transfer Volume | `circles_whale_transfer_volume` | Volume by direction |

**Whale Threshold**: 100 CRC (1e20 wei)

#### Group Treasuries
| Panel | Metric | Description |
|-------|--------|-------------|
| Top 10 Group Treasuries | `circles_group_treasury_total` | Collateral per group |
| Group Treasury vs Members | `circles_group_member_count` | Treasury correlation with membership |

#### Collection Health
| Panel | Metric | Description |
|-------|--------|-------------|
| Collection Duration | `circles_liquidity_collection_duration_seconds_total` | Time spent collecting metrics |
| Collection Errors | `circles_liquidity_collection_errors_total` | Errors by metric type |

**Use Case**: Security monitoring and early warning for liquidity pool drains or coordinated attacks.

---

### 11. Circles Ecosystem KPIs (`ecosystem-kpis.json`)

**Purpose**: Monitor the Invitations and Auth ecosystem services that support user onboarding and authentication.

**Sections**:

#### Invitations Service Overview
| Panel | Metric | Description |
|-------|--------|-------------|
| Total Referrals Stored | `circles_referrals_stored_total` | All-time referral invitations created |
| Pending Referrals | `circles_referrals_pending_keys_total` | Unclaimed referral keys (thresholds: green <5k, yellow 5-10k, red >10k) |
| Total Claimed | `circles_referrals_claimed_total` | Referrals successfully claimed |
| Unique Inviters | `circles_referrals_unique_inviters_total` | Users who have sent invitations |
| Claim Rate (1h) | Calculated | Claimed / (claimed + expired) ratio |
| Referrals/Hour | `increase(circles_referrals_stored_total[1h])` | New referral creation rate |

#### Invitations Trends
| Panel | Metrics | Description |
|-------|---------|-------------|
| Referral Activity | stored, claimed, expired rates | Funnel visualization of referral lifecycle |
| Time to Claim | `circles_referrals_key_age_at_claim_hours_bucket` | p50/p95 histogram of claim latency |

#### Auth Service Overview
| Panel | Metric | Description |
|-------|--------|-------------|
| SIWE Sessions (Total) | `circles_auth_siwe_sessions_created_total` | Sign-In With Ethereum sessions |
| Passkey Sessions (Total) | `circles_auth_passkey_sessions_created_total` | WebAuthn passkey sessions |
| Registered Passkeys | `circles_auth_total_passkeys` | Total passkeys enrolled |
| Passkey Users | `circles_auth_unique_passkey_users` | Unique users with passkeys |
| SIWE Success Rate | Calculated | Successful verifications / total attempts |
| Auths/Hour | Combined SIWE + Passkey rate | Authentication throughput |

#### Auth Service Trends
| Panel | Metrics | Description |
|-------|---------|-------------|
| Authentication Activity | SIWE and Passkey session rates | Method comparison over time |
| SIWE Verification Results | `circles_auth_siwe_verifications_total{result}` | Success/failure breakdown |

#### Request Performance
| Panel | Metrics | Description |
|-------|---------|-------------|
| Invitations Request Latency | p50/p95/p99 from histogram | Response time percentiles |
| Auth Request Latency | p50/p95/p99 from histogram | Response time percentiles |
| Request Rate | `circles_referrals_http_requests_total`, `circles_auth_http_requests_total` | Throughput per service |
| Error Rate | 5xx responses / total | Service error rates |

**Use Case**: Track user onboarding funnel and authentication method adoption. Monitor for invitation spam or auth service issues.

---

### 12. Token Offers & Marketplace (`token-offers.json`)

**Purpose**: Monitor the GNO Bonus Cycle and Payment Gateway systems that enable CRC-to-token exchanges.

**External Link**: [Dune: Circles V2 Offers](https://dune.com/gnosischain_team/circles-v2-offers)

**Sections**:

#### Token Offers Overview (GNO Bonus Cycle)
| Panel | Metric | Description |
|-------|--------|-------------|
| Offer Cycles | `circles_token_offer_cycles_total` | Number of bonus rounds created |
| Total Claims | `circles_token_offer_claims_total` | All-time offer claims |
| CRC Spent (Total) | `circles_token_offer_crc_spent_total` | CRC exchanged for tokens |
| GNO Received (Total) | `circles_token_offer_tokens_received_total` | Tokens distributed to users |
| GNO Price (CRC) | `circles_token_offer_current_price_crc` | Current exchange rate |
| Accepted CRC Types | `circles_token_offer_accepted_crc_count` | Token types eligible for offers |

#### Average Metrics
| Panel | Metric | Description |
|-------|--------|-------------|
| Avg CRC per Claim | `circles_token_offer_avg_crc_per_claim` | Mean claim size |
| Avg Payment Size | `circles_payment_gateway_avg_payment_size` | Mean gateway payment |
| Avg Claim Value (USD) | Calculated with `circles_crc_price_usd` | USD-denominated claim value |
| Avg Payment Value (USD) | Calculated with `circles_crc_price_usd` | USD-denominated payment value |

#### Offer Claims Activity
| Panel | Metrics | Description |
|-------|---------|-------------|
| Offer Claims Over Time | 24h and 7d windows | Claim volume trends |
| Unique Claimers | 24h/7d/30d windows | User engagement metrics |

#### CRC & GNO Flow
| Panel | Metrics | Description |
|-------|---------|-------------|
| CRC Spent on Offers | 24h and 7d windows | CRC flowing into offers |
| GNO Received from Offers | 24h and 7d windows | Tokens flowing to users |

#### Offer Pricing
| Panel | Metrics | Description |
|-------|---------|-------------|
| GNO Offer Price (CRC) | Historical price | Exchange rate evolution |
| Offer Configuration | Limit and accepted types | Current offer parameters |

#### Payment Gateway
| Panel | Metric | Description |
|-------|--------|-------------|
| Total Gateways | `circles_payment_gateways_total` | Payment gateways created |
| Total Payments | `circles_payment_gateway_payments_total` | All-time payment count |
| Total Volume (CRC) | `circles_payment_gateway_volume_total_crc` | All-time CRC processed |
| New Gateways (24h) | `circles_payment_gateways_created{window="24h"}` | Recent gateway creation |
| Gateway Activity | Payments and gateway creation rates | Activity over time |
| Unique Payers & Payees | By window | User participation |
| Payment Gateway Volume | 24h/7d/30d windows | CRC volume trends |

**Use Case**: Track the effectiveness of CRC-to-GNO incentive programs and payment gateway adoption.

---

### 13. Resource Usage (`resource-usage.json`)

**Purpose**: Container-level resource monitoring using cAdvisor metrics. Provides per-container breakdown of CPU, memory, network, and disk I/O.

**Sections**:

#### Host Overview
| Panel | Metric | Description |
|-------|--------|-------------|
| Host CPU Usage | `100 - avg(irate(node_cpu_seconds_total{mode="idle"}[5m])) * 100` | Overall CPU utilization (thresholds: green ≤70%, yellow 70-90%, red >90%) |
| Host Memory Usage | `(1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes) * 100` | Memory utilization (thresholds: green ≤70%, yellow 70-90%, red >90%) |
| Host Disk Usage (/) | Root filesystem percentage | Disk utilization (thresholds: green ≤70%, yellow 70-85%, red >85%) |
| Total Memory | `node_memory_MemTotal_bytes` | Available host memory |

#### Container CPU Usage
| Panel | Metric | Description |
|-------|--------|-------------|
| CPU Usage by Container | `container_cpu_usage_seconds_total` | Per-container CPU (100% = 1 core) |
| Top 10 CPU Consumers | Ranked bar gauge | Highest CPU containers |
| CPU Usage Stacked | Stacked timeseries | Total CPU distribution |

#### Container Memory Usage
| Panel | Metric | Description |
|-------|--------|-------------|
| Memory Usage by Container | `container_memory_usage_bytes` | Per-container memory |
| Top 10 Memory Consumers | Ranked bar gauge | Highest memory containers (yellow >2GB, red >4GB) |
| Memory Usage Stacked | Stacked timeseries | Total memory distribution |

#### Container Network I/O
| Panel | Metric | Description |
|-------|--------|-------------|
| Network Receive Rate | `container_network_receive_bytes_total` | Incoming traffic per container |
| Network Transmit Rate | `container_network_transmit_bytes_total` | Outgoing traffic per container |

#### Container Disk I/O
| Panel | Metric | Description |
|-------|--------|-------------|
| Disk Read Rate | `container_fs_reads_bytes_total` | Read throughput per container |
| Disk Write Rate | `container_fs_writes_bytes_total` | Write throughput per container |

**Use Case**: Identify resource-hungry containers, capacity planning, and troubleshooting performance issues at the container level.

---

## Business KPI Metrics Explained

All business KPIs are collected by the `metrics-exporter` service every 60 seconds from PostgreSQL.

**Source Code**: `src/Metrics/Circles.Metrics.Exporter/`

### User Growth Metrics

| Metric | Labels | Calculation | Purpose |
|--------|--------|-------------|---------|
| `circles_total_humans` | `version` | `COUNT(*) FROM CrcV1_Signup` / `CrcV2_RegisterHuman` | Total registered users |
| `circles_total_organizations` | - | `COUNT(*) FROM CrcV2_RegisterOrganization` | Business entities |
| `circles_total_groups` | - | `COUNT(*) FROM CrcV2_RegisterGroup` | Community groups |
| `circles_new_users` | `window` | Registration events in 24h/7d/30d | Growth rate |

### Trust Network Metrics

| Metric | Labels | Calculation | Purpose |
|--------|--------|-------------|---------|
| `circles_active_trusts` | - | `COUNT(*) FROM V_CrcV2_TrustRelations` | Network size |
| `circles_new_trusts` | `window` | Trust events in time window | Network growth |
| `circles_trust_changes` | `window`, `type` | Added/removed trusts | Churn analysis |

### Economic Activity Metrics

| Metric | Labels | Calculation | Purpose |
|--------|--------|-------------|---------|
| `circles_daily_mint_volume_crc` | - | `SUM(amount)` from PersonalMint in 24h | Daily UBI issuance |
| `circles_daily_mint_count` | - | `COUNT(*)` from PersonalMint in 24h | Minting activity |
| `circles_daily_transfer_volume_crc` | - | `SUM(value)` from TransferSingle in 24h | Economic activity |
| `circles_daily_transfer_count` | - | `COUNT(*)` from TransferSingle in 24h | Transaction count |
| `circles_active_minters` | `window` | `COUNT(DISTINCT human)` who minted | Active UBI claimers |

### Activity Rate Metrics

These show what **percentage** of registered users are actively using the system.

| Metric | Labels | Calculation | Interpretation |
|--------|--------|-------------|----------------|
| `circles_minting_rate` | `window` | Minters / Total Humans | % claiming UBI (target: >50%) |
| `circles_spending_rate` | `window` | Spenders / Total Humans | % using CRC (target: >20%) |

### Advanced Monetary Metrics

#### Supply Metrics

| Metric | Calculation | Interpretation |
|--------|-------------|----------------|
| `circles_total_crc_supply` | `SUM(demurragedTotalBalance)` | Current circulating supply |
| `circles_total_minted_all_time_crc` | `SUM(amount)` from all mints | Historical issuance |
| `circles_demurrage_paid_crc{window}` | `SUM(startValue - endValue)` | CRC lost to demurrage |
| `circles_net_inflow_crc{window}` | Minted in window | New CRC entering system |

#### Distribution Metrics

| Metric | Calculation | Interpretation |
|--------|-------------|----------------|
| `circles_gini_coefficient` | Standard Gini formula (excludes infrastructure) | 0=perfect equality, 1=one person has all |
| `circles_gini_coefficient_by_type{account_type}` | Gini per account type | Inequality within humans/groups/orgs |
| `circles_gini_coefficient_non_custodial` | Humans with <50 unique tokens | Excludes exchanges/aggregators |
| `circles_average_balance_crc` | Mean of account balances | Affected by whales |
| `circles_median_balance_crc` | Median of account balances | Typical user balance |
| `circles_top_holder_concentration{top_n}` | Top N balance / Total | Whale concentration |

**Gini Coefficient Calculation** (excludes infrastructure addresses):
```sql
WITH infrastructure_addresses AS (
    -- Balancer vault addresses (hardcoded)
    SELECT '0xba12222222228d8ba445958a75a0704d566bf2c8' as address  -- V2 (compromised)
    UNION ALL
    SELECT '0xba1333333333a1ba1108e8412f11850a5c319ba9' as address  -- V3 (active)
    UNION ALL
    -- Group treasury vaults (dynamic)
    SELECT vault as address FROM "CrcV2_CreateVault"
),
balances AS (
    SELECT SUM(demurragedTotalBalance / 1e18) as balance
    FROM V_CrcV2_BalancesByAccountAndToken
    WHERE account NOT IN (SELECT address FROM infrastructure_addresses)
    GROUP BY account ORDER BY balance
),
indexed AS (
    SELECT balance, ROW_NUMBER() OVER (ORDER BY balance) as i,
           COUNT(*) OVER () as n
    FROM balances
)
SELECT (2.0 * SUM(i * balance) / (n * SUM(balance))) - ((n + 1.0) / n)
FROM indexed GROUP BY n
```

#### Infrastructure vs Economic Actors

| Metric | Labels | Description |
|--------|--------|-------------|
| `circles_infrastructure_holdings_crc` | - | CRC held by Balancer vaults + group treasuries |
| `circles_economic_actors_holdings_crc` | - | CRC held by humans, groups, orgs (excluding infrastructure) |
| `circles_infrastructure_holdings_percentage` | - | % of total CRC in infrastructure (0-100) |
| `circles_economic_actors_holdings_percentage` | - | % of total CRC held by economic actors (0-100) |
| `circles_infrastructure_address_count` | - | Infrastructure addresses with non-zero balance |
| `circles_economic_actors_count` | - | Economic actor addresses with non-zero balance |

**Infrastructure Addresses**:
- Balancer V2 Vault: `0xba12222222228d8ba445958a75a0704d566bf2c8` (compromised Dec 2024, tracked for historical)
- Balancer V3 Vault: `0xba1333333333a1ba1108e8412f11850a5c319ba9` (active)
- Group treasury vaults (dynamically queried from `CrcV2_CreateVault` table)

**Why Separate?**: Infrastructure addresses hold tokens for liquidity/collateral, not economic activity. Including them in inequality metrics (Gini) would skew results.

#### Velocity Metrics

| Metric | Calculation | Interpretation |
|--------|-------------|----------------|
| `circles_money_velocity{window}` | Transfer Volume / Total Supply | Times CRC changes hands |

**Healthy velocity**: 0.5-2.0 over 30 days. Lower = hoarding. Higher = hyperactive trading.

#### Activity Metrics

| Metric | Calculation | Interpretation |
|--------|-------------|----------------|
| `circles_daily_active_wallets` | Unique senders + receivers in 24h | DAU equivalent |
| `circles_weekly_active_wallets` | Unique in 7d | WAU equivalent |
| `circles_monthly_active_wallets` | Unique in 30d | MAU equivalent |
| `circles_user_retention_rate{window}` | Users active in both periods / Users in previous | Stickiness |
| `circles_first_time_transactors{window}` | Users with first-ever transfer in window | Onboarding |

**DAW/MAW Ratio** (Stickiness): 0.2-0.4 is healthy for financial apps. Higher = users return daily.

---

## Liquidity Monitoring Metrics Explained

Liquidity metrics are collected by `LiquidityCollectorService` every 5 minutes from the Circles indexer database.

**Source Code**: `src/Metrics/Circles.Metrics.Exporter/Services/LiquidityCollectorService.cs`

### Token Architecture: Balancer vs Group Treasuries

Circles liquidity exists in two separate systems:

```text
┌─────────────────────────────────────────────────────────────────────┐
│                        PERSONAL TOKENS                               │
│  (Minted daily by humans, ERC1155 internally, can be ERC20-wrapped) │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
           ┌───────────────┴───────────────┐
           ▼                               ▼
┌─────────────────────────┐    ┌─────────────────────────────────────┐
│     BALANCER VAULTS     │    │          GROUP TREASURIES           │
│  (Liquidity Provision)  │    │     (Collateral for Group Tokens)   │
├─────────────────────────┤    ├─────────────────────────────────────┤
│ • ~600 personal tokens  │    │ • 42 groups                         │
│ • ERC20-wrapped         │    │ • Personal tokens locked as         │
│ • V2 vault (compromised)│    │   collateral when minting group     │
│ • V3 vault (active)     │    │   tokens                            │
│                         │    │ • Redeemable by burning group token │
├─────────────────────────┤    ├─────────────────────────────────────┤
│ Events:                 │    │ Events:                             │
│ • Erc20WrapperTransfer  │    │ • CollateralLockedSingle/Batch      │
│   (to/from vault)       │    │ • GroupRedeemCollateralReturn/Burn  │
├─────────────────────────┤    ├─────────────────────────────────────┤
│ Metric:                 │    │ Metric:                             │
│ circles_balancer_tvl_*  │    │ circles_group_treasury_tvl_*        │
└─────────────────────────┘    └─────────────────────────────────────┘
```

**Key Difference**: Group tokens are NOT in Balancer. When you lock personal tokens as collateral in a group treasury, you receive group tokens. The collateral stays in the treasury vault until redeemed.

### Balancer Vault Metrics

| Metric | Labels | Source Table | Purpose |
|--------|--------|--------------|---------|
| `circles_balancer_vault_balance` | `token_address`, `token_name` | `V_CrcV2_Erc20BalancerVaultBalance_1h` | Per-token balance in vault |
| `circles_balancer_vault_balance_total` | - | Aggregated | Sum of all token balances |
| `circles_balancer_vault_tokens_count` | - | Aggregated | Distinct tokens with liquidity |

### Why Statistical Anomaly Detection?

Traditional threshold-based alerts (e.g., "alert if balance drops below X") fail for liquidity pools because:

- **Different tokens have different scales**: A 1000 CRC drop is normal for high-volume tokens, critical for low-volume ones
- **Normal variation differs**: Some tokens fluctuate wildly, others are stable
- **Static thresholds require constant tuning**: What's anomalous changes over time

**Z-score based detection** solves this by measuring "how unusual is this compared to this token's normal behavior":

- Calculates mean and standard deviation of hourly changes over 30 days
- Compares current change to that baseline
- Normalizes across all tokens regardless of scale

**Z-Score Interpretation**:

| Z-Score | Meaning | Probability (if normal) | Action |
|---------|---------|-------------------------|--------|
| > -1.0 | Normal | ~84% | None |
| -1.0 to -2.0 | Slightly elevated outflow | ~16% | Monitor |
| -2.0 to -3.0 | Unusual outflow | ~2.5% | **Warning alert** |
| < -3.0 | Severe anomaly | ~0.1% | **Critical alert** |

A z-score of -3.0 means "if this token's behavior were random noise, we'd expect this level of outflow only 0.1% of the time."

### Drain Detection Metrics (Z-Score Based)

| Metric | Labels | Calculation | Purpose |
|--------|--------|-------------|---------|
| `circles_balancer_vault_change_1h` | `token_address` | Current - Previous hour | Hourly balance delta |
| `circles_balancer_vault_zscore_1h` | `token_address` | `(change - mean) / stddev` | Statistical anomaly indicator |
| `circles_balancer_vault_anomaly` | `token_address`, `severity` | `1` if z-score below threshold | Binary anomaly flag |
| `circles_balancer_vault_drain_events_total` | `token_address`, `severity` | Counter | Cumulative anomaly count |

**Z-Score Calculation**:
```sql
WITH hourly_changes AS (
    SELECT "tokenAddress", "timestamp",
           value - LAG(value) OVER (PARTITION BY "tokenAddress" ORDER BY "timestamp") as change
    FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
    WHERE "timestamp" > NOW() - interval '30 days'
),
stats AS (
    SELECT "tokenAddress", AVG(change) as mean_change, STDDEV(change) as stddev_change
    FROM hourly_changes WHERE change IS NOT NULL GROUP BY "tokenAddress"
)
SELECT (latest_change - mean_change) / stddev_change as z_score
```

**Thresholds**:

- `z < -2.0`: Warning severity (unusual outflow)
- `z < -3.0`: Critical severity (severe drain)

#### Dashboard: Drain Events by Severity

The `circles_balancer_vault_drain_events_total` metric is a **cumulative counter** (not a gauge). The Grafana panel shows:

- **Y-axis**: Total anomaly events since collector started
- **Stair-step pattern**: Counter increments when anomaly detected
- **Colors by severity label**: warning (orange), critical (red)
- **Counter resets**: Drops to 0 indicate service restarts

This helps identify:

- Frequency of anomalies over time
- Whether anomalies cluster together (coordinated activity)
- Historical pattern of drain events

### Whale Transfer Metrics

| Metric | Labels | Source Table | Purpose |
|--------|--------|--------------|---------|
| `circles_whale_transfer_total` | `token_address`, `direction` | `CrcV2_Erc20WrapperTransfer` | Count of large transfers |
| `circles_whale_transfer_volume` | `token_address`, `direction` | `CrcV2_Erc20WrapperTransfer` | Volume of large transfers |
| `circles_whale_transfer_last` | `token_address`, `from`, `to`, `direction` | `CrcV2_Erc20WrapperTransfer` | Most recent whale transfer |
| `circles_whale_transfer_timestamp` | `token_address` | `CrcV2_Erc20WrapperTransfer` | Timestamp of last transfer |

**Direction Labels**: `deposit` (to vault), `withdrawal` (from vault)

**Whale Threshold**: 100 CRC (1e20 wei for 18-decimal tokens)

**Balancer Vault Addresses**:
| Vault | Address | Status |
|-------|---------|--------|
| V2 | `0xba12222222228d8ba445958a75a0704d566bf2c8` | Compromised (Dec 2024) - tracked for historical data |
| V3 | `0xba1333333333a1ba1108e8412f11850a5c319ba9` | **Active** - current vault |

### Group Treasury Metrics

| Metric | Labels | Source Table | Purpose |
|--------|--------|--------------|---------|
| `circles_group_treasury_total` | `group_address`, `group_name` | `V_CrcV2_GroupVaultBalancesByToken` | Total collateral per group |
| `circles_group_member_count` | `group_address`, `group_name` | `V_CrcV2_Groups` | Members per group |

### Collection Health Metrics

| Metric | Labels | Purpose |
|--------|--------|---------|
| `circles_liquidity_collection_duration_seconds_total` | - | Time spent collecting metrics |
| `circles_liquidity_collection_errors_total` | `metric` | Errors by collection type |
| `circles_liquidity_last_collection_timestamp` | - | Unix timestamp of last successful collection |

---

## Sybil Detection Metrics Explained

### Why These Indicators?

Circles UBI requires human verification through trust. Sybil attackers exploit this by:

1. **Mass Registration**: Create many accounts at once (detectable: batch registrations)
2. **No Real Identity**: Skip profile setup (detectable: no profile)
3. **Closed Trust Rings**: Only trust each other (detectable: no external incoming trust)
4. **Farm and Drain**: Mint daily, transfer to main wallet (detectable: zero balance after minting)
5. **Invite Farming**: One account invites many sybils (detectable: high-volume inviters)

### Metric Details

| Metric | SQL Logic | Threshold | Rationale |
|--------|-----------|-----------|-----------|
| `circles_accounts_no_profile` | `LEFT JOIN UpdateMetadataDigest IS NULL` | - | Real users set profiles |
| `circles_accounts_no_trust_received` | `NOT IN (SELECT trustee)` | - | Real users get trusted |
| `circles_batch_registrations{window}` | `GROUP BY blockNumber HAVING COUNT(*) > 5` | >5/block | Normal users don't register in batches |
| `circles_mint_and_drain_accounts{window}` | Minted but `balance = 0` | - | Why mint and immediately drain? |
| `circles_high_volume_inviters{window}` | `GROUP BY inviter HAVING COUNT(*) > 10` | >10 | Normal users don't invite 10+ people |
| `circles_suspicious_accounts` | `(no profile) AND (no trust) AND (minted)` | - | Combined red flags |
| `circles_organic_accounts` | `(has profile) AND (has trust) AND (active)` | - | Healthy account indicators |

### Recommended Thresholds

- **Suspicious Ratio > 10%**: Investigate sybil patterns
- **Batch Registrations > 20/day**: Possible coordinated attack
- **High-Volume Inviters > 5**: Review inviter accounts
- **Mint-and-Drain > 5% of minters**: Economic attack in progress

---

## Health Endpoint Architecture

### The RPC `/ready` Endpoint

This is the **ultimate health check** for the Circles stack. It validates:

1. **Nethermind Sync**: Execution client is synced with chain head
2. **Circles Plugin Sync**: Indexer has processed all blocks
3. **Database Connection**: PostgreSQL is reachable
4. **Pathfinder Health**: Path calculation service is ready
5. **Block Lag**: Not more than N blocks behind

**During Initial Sync**: Returns `503 Service Unavailable` for several hours while:
- Nethermind syncs the blockchain (~1-2 hours)
- Circles plugin indexes historical events (~2-4 hours)

**This is expected behavior, not an error.**

### Blackbox Exporter Probes

| Job | Target | Interval | Description |
|-----|--------|----------|-------------|
| `blackbox-ready` | Service `/ready` endpoints | 30s | Validates services are ready to serve traffic |
| `blackbox-live` | Service `/live` endpoints | 15s | Validates services are alive (liveness) |
| `blackbox-nethermind` | Nethermind `/health` | 30s | Blockchain node health |
| `blackbox-consensus` | Lighthouse `/eth/v1/node/health` | 30s | Accepts 200 (synced) and 206 (syncing) |
| `blackbox-caddy` | Caddy admin API `/config/` | 30s | Reverse proxy health |
| `blackbox-external` | Public HTTPS endpoints | 60s | External domain monitoring + SSL cert expiry |
| `blackbox-rpc-functional` | JSON-RPC method validation | 60s | Validates actual RPC responses, not just HTTP 200 |
| `blackbox-ipfs-gateway` | Filebase IPFS gateway | 60s | Fetches known Circles profile CID |

**Services monitored via `/ready` and `/live`**:
- rpc, pathfinder, cache-service, invitations, auth

**Functional RPC Probes** validate these methods:
- `circles_health`, `circles_tables`, `circles_getTotalBalance`
- `circles_getAvatarInfo`, `circles_getTrustRelations`
- `circles_query`, `circles_events`

**External Endpoints** monitored:
- `https://staging.circlesubi.network/live` - Main RPC
- `https://staging.circlesubi.network/pathfinder/live` - Pathfinder
- `https://staging.circlesubi.network/chain-rpc/health` - Nethermind

### Sync-Aware Alerting

The alerting rules distinguish between:

1. **Initial Sync**: RPC `/ready` fails but Nethermind is healthy and RPC `/live` works
   - Severity: `info` (not actionable)
   - Duration: Can last hours

2. **Actual Failure**: RPC `/ready` fails after being healthy
   - Severity: `critical`
   - Duration: Alert after 5m

---

## Recording Rules

Recording rules pre-aggregate metrics for:
1. **Performance**: Dashboards query pre-computed values
2. **Persistence**: Metrics survive exporter restarts
3. **Retention**: Stored in Prometheus TSDB for 365 days

**Location**: `docker/observability/prometheus-recording-rules.yml`

Key recording rules:

```yaml
# KPI aggregations
circles:total_humans:sum
circles:active_trusts:gauge
circles:daily_mint_count:gauge

# RPC performance
circles:rpc_requests_per_second:rate1m
circles:rpc_errors_per_second:rate1m
circles:rpc_latency_p95:gauge

# Monetary metrics
circles:total_crc_supply:gauge
circles:money_velocity_30d:gauge
circles:gini_coefficient:gauge

# Sybil metrics
circles:suspicious_accounts:gauge
circles:suspicious_account_ratio:gauge
```

---

## Alerting

### Alert Rules (`docker/observability/prometheus-alerts.yml`)

| Alert | Severity | Condition | Description |
|-------|----------|-----------|-------------|
| `ServiceDown` | critical | `up == 0` for 1m | Service unreachable |
| `HealthCheckFailed` | critical | `probe_success == 0` for 5m | Health endpoint failing |
| `InitialSyncInProgress` | info | RPC not ready but Nethermind healthy | Expected during startup |
| `HighRpcErrorRate1h` | warning | `>5 errors in 1h` | Elevated error rate |
| `HighRpcErrorRate24h` | critical | `>20 errors in 24h` | Critical error rate |
| `IndexerLag` | warning | `lag > 100 blocks` for 5m | Cache falling behind |
| `CacheListenerDisconnected` | critical | `listener == 0` for 2m | DB notify disconnected |
| `HighRpcLatency` | warning | `p95 > 5s` for 5m | Slow RPC responses |
| `DiskSpaceLow` | warning | `>80% used` | Disk space warning |
| `DiskSpaceCritical` | critical | `>90% used` | Disk space critical |

### Liquidity Alert Rules (`docker/observability/prometheus-alerts-liquidity.yml`)

**Alert Strategy**: Uses **aggregate TVL monitoring** with two detection windows:

- **15-minute window**: Fast detection of active attacks (higher thresholds)
- **1-hour window**: Detection of coordinated drains (lower thresholds)

With 600+ personal tokens in Balancer, per-token alerts generated ~4 critical/hour from normal volatility. Aggregate TVL drops indicate coordinated activity worth investigating.

#### Rapid Drain Alerts (15-minute window)

| Alert                      | Severity | Condition                 | Description           |
|----------------------------|----------|---------------------------|-----------------------|
| `BalancerRapidDrain`       | critical | TVL change < -10% in 15m  | Fast attack detection |
| `GroupTreasuryRapidDrain`  | critical | TVL change < -15% in 15m  | Fast treasury drain   |

**Detection Timeline**: With 5-minute collection + 2-minute `for` clause = **~7-12 minutes** to alert.

#### Primary Alerts (1-hour window)

| Alert | Severity | Condition | Description |
|-------|----------|-----------|-------------|
| `BalancerTvlDropWarning` | warning | TVL change < -5% in 1h | Moderate aggregate outflow |
| `BalancerTvlDropCritical` | critical | TVL change < -10% in 1h | Significant outflow - possible attack |
| `GroupTreasuryTvlDropWarning` | warning | TVL change < -10% in 1h | Moderate treasury outflow |
| `GroupTreasuryTvlDropCritical` | critical | TVL change < -20% in 1h | Severe treasury outflow - investigate |

#### Informational Alerts

| Alert | Severity | Condition | Description |
|-------|----------|-----------|-------------|
| `WhaleActivitySpike` | info | >20 whale transfers/hour for 15m | High large transfer activity |
| `GroupTreasuryLow` | info | <1 CRC with >10 members for 1h | Underfunded group |

#### Collection Health Alerts

| Alert | Severity | Condition | Description |
|-------|----------|-----------|-------------|
| `LiquidityCollectionStale` | warning | No collection for 10m | Collector may be stuck |
| `LiquidityCollectionErrors` | warning | >10 errors/hour for 5m | Check DB connectivity |
| `LiquidityCollectionSlow` | warning | >60s collection time for 15m | Database performance issue |

### Alertmanager (`alertmanager:9093`)

**Config**: `docker/observability/alertmanager.yml`

**Receivers**:
- **Slack** (default): All alerts
- **Telegram** (optional): Critical alerts only

**Environment Variables**:
```bash
SLACK_WEBHOOK_URL=https://hooks.slack.com/services/...
TELEGRAM_BOT_TOKEN=your-bot-token  # Optional
TELEGRAM_CHAT_ID=your-chat-id      # Optional
```

**Testing Slack Alerts**:

To verify Slack integration is working, send a test alert:

```bash
# From the server running the observability stack
docker compose -f docker/docker-compose.observability.yml exec alertmanager \
  wget -q -O- \
  '--post-data=[{"labels":{"alertname":"TestAlert","severity":"warning"},"annotations":{"summary":"Test alert from CLI","description":"This is a test to verify Slack integration works."}}]' \
  '--header=Content-Type: application/json' \
  http://localhost:9093/api/v2/alerts
```

If you don't see the alert in Slack:

1. Check alertmanager logs: `docker compose -f docker/docker-compose.observability.yml logs alertmanager --tail=20`
2. Verify `SLACK_WEBHOOK_URL` is set in `docker/.env`
3. Ensure the Slack channel exists and the webhook has access
4. Restart alertmanager after changing env vars: `docker compose -f docker/docker-compose.observability.yml restart alertmanager`

#### Alternative: Trigger a Real Alert

Stop a service briefly to trigger a real alert:
```bash
# Stop RPC for ~2 minutes to trigger ServiceNotAlive
docker compose -f docker/docker-compose.gnosis.yml stop rpc
# Wait 1-2 minutes for alert to fire, then restart
docker compose -f docker/docker-compose.gnosis.yml start rpc
```

---

## Data Persistence

| Component | Host Path | Retention |
|-----------|-----------|-----------|
| Prometheus TSDB | `.state/prometheus/` | 365 days |
| Loki chunks | `.state/loki/` | 7 days |
| Alertmanager | `.state/alertmanager/` | Silences/notifications |
| Grafana | `.state/grafana/` | Dashboards/settings |
| Promtail | `.state/promtail/` | Position tracking |

---

## Docker Compose Files

### `docker-compose.observability.yml`

Contains:
- Prometheus
- Node Exporter
- Loki
- Promtail
- Alertmanager
- Blackbox Exporter
- Metrics Exporter

### `docker-compose.grafana.yml`

Contains:
- Grafana (with provisioned dashboards and datasources)

### Usage

```bash
# Start full stack
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

### RPC Not Ready During Sync

This is **expected behavior**. During initial sync:
- Nethermind syncs blockchain (1-2 hours)
- Circles plugin indexes events (2-4 hours)
- RPC `/ready` returns 503 until complete

Check `InitialSyncInProgress` alert - it's informational, not an error.

---

## Available UIs

| Service | URL | Purpose |
|---------|-----|---------|
| **Grafana** | `http://localhost:3000` | Dashboards, logs, alerting |
| **Prometheus** | `http://localhost:9090` | Query metrics, view targets |
| **Alertmanager** | `http://localhost:9093` | View/silence alerts |

---

## Future Enhancements

- [x] CRC Price metric (`circles_crc_price_usd` - used in token-offers dashboard)
- [x] Container-level resource monitoring (cAdvisor integration)
- [x] Ecosystem services monitoring (invitations, auth)
- [x] External endpoint and SSL monitoring
- [x] Functional RPC probes (validates method responses)
- [ ] Index plugin Prometheus metrics (currently console-only)
- [ ] Database indexes for heavy sybil detection queries
- [ ] Trust graph analysis metrics (clustering coefficient, diameter)
- [ ] Arbitrage bot metrics integration (requires separate `bot_activity` database connection)
