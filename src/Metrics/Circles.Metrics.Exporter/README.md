# Circles Metrics Exporter

A Prometheus metrics exporter for the Circles protocol on Gnosis Chain. Collects business KPIs and liquidity monitoring metrics from the Circles indexer database and exposes them for Prometheus scraping.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Circles Metrics Exporter                     │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────┐    ┌─────────────────────────────────┐ │
│  │  KpiCollectorService │    │  LiquidityCollectorService      │ │
│  │  (60s interval)      │    │  (300s interval)                │ │
│  └──────────┬──────────┘    └──────────────┬──────────────────┘ │
│             │                              │                     │
│  ┌──────────▼──────────┐    ┌──────────────▼──────────────────┐ │
│  │    KpiRepository     │    │     LiquidityRepository         │ │
│  │  (Business metrics)  │    │  (Vault, treasuries, whales)    │ │
│  └──────────┬──────────┘    └──────────────┬──────────────────┘ │
│             │                              │                     │
│             └──────────────┬───────────────┘                     │
│                            ▼                                     │
│                   ┌─────────────────┐                            │
│                   │  PostgreSQL DB  │                            │
│                   │  (Circles Indexer)                           │
│                   └─────────────────┘                            │
├─────────────────────────────────────────────────────────────────┤
│  Endpoints:                                                      │
│  • /metrics  - Prometheus metrics (prometheus-net)               │
│  • /health   - Health check                                      │
│  • /ready    - Readiness check                                   │
│  • /         - Service info                                      │
└─────────────────────────────────────────────────────────────────┘
```

## Features

1. **Business KPIs** - User registrations, trusts, minting, transfers
2. **Liquidity Monitoring** - Balancer vault balances, group treasuries
3. **Drain Detection** - Z-score based anomaly detection for unusual outflows
4. **Whale Tracking** - Large transfers (>100 CRC) to/from Balancer vault

## What Gets Tracked

### Balancer Vault Liquidity

The exporter tracks **ALL tokens with non-zero balance** in the Balancer vault at address `0xba12222222228d8ba445958a75a0704d566bf2c8`.

Data source: `V_CrcV2_Erc20BalancerVaultBalance_1h` (hourly snapshots)

This includes:
- Group CRC tokens (ERC20 wrappers)
- Personal CRC tokens with liquidity
- Any Circles token deposited to the vault

The query LEFT JOINs to `V_CrcV2_Groups` to resolve token names for group tokens.

### Group Treasuries

Tracks **ALL groups with non-zero treasury balance** (filter: `SUM(balance) > 0`).

Data sources:
- `V_CrcV2_Groups` - Group metadata (address, name, symbol, memberCount)
- `CrcV2_CreateVault` - Vault creation events
- `V_CrcV2_GroupVaultBalancesByToken` - Collateral balances per vault

### Drain Detection

Uses **z-score based statistical anomaly detection** comparing the current hourly change against a 30-day historical baseline:

```
z-score = (current_change - mean_change) / std_dev_change
```

Thresholds:
- `z < -2.0` → Warning (2+ standard deviations below normal)
- `z < -3.0` → Critical (3+ std devs, ~0.1% probability under normal conditions)

### Whale Transfers

Tracks individual transfers **>100 CRC** (1e20 wei) to/from the Balancer vault.

Data source: `CrcV2_Erc20WrapperTransfer` table

## Configuration

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `ConnectionStrings__CirclesDb` | PostgreSQL connection string for Circles indexer | Yes |

### appsettings.json

```json
{
  "ConnectionStrings": {
    "CirclesDb": "Host=...;Port=5432;Database=...;Username=...;Password=..."
  },
  "Metrics": {
    "KpiCollectionIntervalSeconds": 60,
    "LiquidityCollectionIntervalSeconds": 300
  }
}
```

## Exposed Metrics

### Business KPIs (`BusinessKpiMetrics.cs`)

| Metric | Type | Description |
|--------|------|-------------|
| `circles_total_humans` | Gauge | Total registered users |
| `circles_active_trusts` | Gauge | Current trust relationships |
| `circles_daily_mint_volume_crc` | Gauge | CRC minted in last 24h |
| `circles_daily_transfer_volume_crc` | Gauge | CRC transferred in last 24h |
| `circles_gini_coefficient` | Gauge | Wealth distribution (0=equal, 1=unequal) |
| `circles_money_velocity` | Gauge | Transfer volume / total supply |
| ... | | See `BusinessKpiMetrics.cs` for full list |

### Liquidity Metrics (`LiquidityMetrics.cs`)

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `circles_balancer_vault_balance` | Gauge | `token_address`, `token_name` | Per-token balance in vault |
| `circles_balancer_vault_balance_total` | Gauge | - | Sum of all token balances |
| `circles_balancer_vault_tokens_count` | Gauge | - | Distinct tokens with liquidity |
| `circles_balancer_vault_change_1h` | Gauge | `token_address` | Hourly balance change |
| `circles_balancer_vault_zscore_1h` | Gauge | `token_address` | Z-score of hourly change |
| `circles_balancer_vault_anomaly` | Gauge | `token_address`, `severity` | 1 if anomaly detected |
| `circles_balancer_vault_drain_events_total` | Counter | `token_address`, `severity` | Cumulative drain events |
| `circles_group_treasury_total` | Gauge | `group_address`, `group_name` | Total collateral per group |
| `circles_group_member_count` | Gauge | `group_address`, `group_name` | Members per group |
| `circles_whale_transfer_total` | Counter | `token_address`, `direction` | Count of whale transfers |
| `circles_whale_transfer_volume` | Counter | `token_address`, `direction` | Volume of whale transfers |
| `circles_liquidity_collection_duration_seconds_total` | Counter | - | Time spent collecting |
| `circles_liquidity_collection_errors_total` | Counter | `metric` | Errors by type |

## File Structure

```
Circles.Metrics.Exporter/
├── Program.cs                    # Entry point, DI setup
├── BusinessKpiMetrics.cs         # Business KPI metric definitions
├── LiquidityMetrics.cs           # Liquidity metric definitions
├── appsettings.json              # Configuration
└── Services/
    ├── KpiRepository.cs          # SQL queries for business KPIs
    ├── KpiCollectorService.cs    # Background service (60s)
    ├── LiquidityRepository.cs    # SQL queries for liquidity
    ├── LiquidityCollectorService.cs  # Background service (5min)
    └── PriceService.cs           # CoinGecko price fetching (optional)
```

## Running

### Local Development

```bash
cd src/Metrics/Circles.Metrics.Exporter
dotnet run
```

Access:
- http://localhost:5000/metrics - Prometheus metrics
- http://localhost:5000/health - Health check
- http://localhost:5000/ - Service info

### Docker

The exporter is included in the observability stack:

```bash
docker compose -f docker/docker-compose.observability.yml up -d
```

Prometheus is pre-configured to scrape `metrics-exporter:9100`.

## Database Views Used

| View | Purpose |
|------|---------|
| `V_CrcV2_Erc20BalancerVaultBalance_1h` | Hourly Balancer vault snapshots |
| `V_CrcV2_Groups` | Group metadata |
| `V_CrcV2_GroupVaultBalancesByToken` | Group vault collateral |
| `V_CrcV2_TrustRelations` | Active trust relationships |
| `V_CrcV2_BalancesByAccountAndToken` | Account balances |
| `CrcV2_CreateVault` | Vault creation events |
| `CrcV2_Erc20WrapperTransfer` | Token transfers |
| `CrcV1_Signup` / `CrcV2_RegisterHuman` | User registrations |
| `CrcV2_PersonalMint` | Minting events |
| `CrcV2_TransferSingle` | Transfer events |

## Alert Rules

See `docker/observability/prometheus-alerts-liquidity.yml` for Prometheus alert rules covering:

- Drain detection (z-score thresholds)
- Whale activity spikes
- Large withdrawals
- Group treasury depletion
- Collection health
