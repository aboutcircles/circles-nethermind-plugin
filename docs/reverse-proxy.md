# Caddy Reverse Proxy Configuration

This document describes the Caddy reverse proxy setup for the Circles RPC infrastructure.

## Overview

Caddy serves as the main entry point for all HTTP/HTTPS and WebSocket traffic, routing requests to the appropriate backend services. It provides:

- **Automatic HTTPS** with Let's Encrypt certificates
- **HTTP/2 and HTTP/3** support out of the box
- **WebSocket proxying** with automatic upgrade handling
- **Path-based routing** to multiple backend services

## Quick Start

```bash
cd docker

# Set your domain (or use localhost for local development)
export DOMAIN=circles.example.com

# Start all services
docker compose -f docker-compose.caddy.yml up -d
```

## Architecture

```
                    ┌─────────────────────────────────────────────────────────┐
                    │                      Caddy                              │
                    │                  (ports 80, 443)                        │
                    └─────────────────────────────────────────────────────────┘
                                              │
              ┌───────────────────────────────┼───────────────────────────────┐
              │                               │                               │
              ▼                               ▼                               ▼
┌─────────────────────────┐   ┌─────────────────────────┐   ┌─────────────────────────┐
│      RPC Service        │   │   Nethermind (JSON-RPC) │   │    Profile Service      │
│      (port 8080)        │   │      (port 8545)        │   │      (port 3000)        │
│                         │   │                         │   │                         │
│  - JSON-RPC API         │   │  - Ethereum JSON-RPC    │   │  - Profile CRUD         │
│  - WebSocket /ws/sub    │   │  - Circles RPC module   │   │  - IPFS integration     │
└─────────────────────────┘   └─────────────────────────┘   └─────────────────────────┘
              │
              ▼
┌─────────────────────────┐
│      Pathfinder         │
│      (port 8080)        │
│                         │
│  - Path computation     │
│  - Flow calculations    │
└─────────────────────────┘
```

## Routing Table

| External Endpoint | Backend Service | Port | Description |
|-------------------|-----------------|------|-------------|
| `/` | `rpc` | 8080 | Primary RPC endpoint (JSON-RPC POST) |
| `/ws/subscribe` | `rpc` | 8080 | WebSocket subscriptions |
| `/rpc/*` | `rpc` | 8080 | Alternative RPC path |
| `/rpc/ws/subscribe` | `rpc` | 8080 | WebSocket via /rpc path |
| `/nethermind/*` | `nethermind-gnosis` | 8545 | Direct Nethermind JSON-RPC |
| `/profile-service/*` | `profile-service` | 3000 | Profile pinning service |
| `/pathfinder/*` | `pathfinder` | 8080 | Path computation service |

## Endpoint Details

### RPC Service (`/` and `/rpc/*`)

The main Circles RPC service handles JSON-RPC 2.0 requests.

**HTTP POST Example:**
```bash
curl -X POST https://your-domain.com/ \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "circles_getTotalBalance",
    "params": ["0x123..."],
    "id": 1
  }'
```

**Available Methods:**
- `circles_getTotalBalance` - Get total CRC balance
- `circles_getTokenBalances` - Get individual token balances
- `circles_getAvatarInfo` - Get avatar information
- `circles_getTrustRelations` - Get trust relationships
- `circles_events` - Query indexed events
- `circles_query` - Execute custom queries
- `circlesV2_findPath` - Find transfer paths
- [See full API documentation](.//rpc-reference.md)

### WebSocket Subscriptions (`/ws/subscribe`)

Real-time event subscriptions via WebSocket.

**JavaScript Example:**
```javascript
const ws = new WebSocket('wss://your-domain.com/ws/subscribe');

ws.onopen = () => {
  // Subscribe to events for a specific address
  ws.send(JSON.stringify({
    jsonrpc: '2.0',
    method: 'circles_subscribe',
    params: { address: '0x1234567890abcdef...' },
    id: 1
  }));
};

ws.onmessage = (event) => {
  const data = JSON.parse(event.data);
  console.log('Received:', data);
};
```

**Subscription Response:**
```json
{
  "jsonrpc": "2.0",
  "result": "subscription-uuid-here",
  "id": 1
}
```

**Event Notification Format:**
```json
{
  "jsonrpc": "2.0",
  "method": "circles_subscription",
  "params": {
    "subscription": "subscription-uuid-here",
    "result": { /* event data */ }
  }
}
```

### Nethermind JSON-RPC (`/nethermind/*`)

Direct access to the Nethermind execution client with Circles indexer.

```bash
# Get latest block number
curl -X POST https://your-domain.com/nethermind/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}'

# Use Circles-specific methods directly
curl -X POST https://your-domain.com/nethermind/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"circles_getTotalBalance","params":["0x..."],"id":1}'
```

### Profile Service (`/profile-service/*`)

IPFS-backed profile storage and retrieval.

```bash
# Get profile by address
curl https://your-domain.com/profile-service/profiles/0x123...

# Search profiles
curl "https://your-domain.com/profile-service/profiles?search=alice"
```

### Pathfinder (`/pathfinder/*`)

Graph-based path computation for Circles transfers.

```bash
# Compute transfer path
curl -X POST https://your-domain.com/pathfinder/flow \
  -H "Content-Type: application/json" \
  -d '{
    "source": "0xsender...",
    "sink": "0xreceiver...",
    "targetFlow": "1000000000000000000"
  }'
```

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DOMAIN` | `localhost` | Domain name for HTTPS certificates |
| `POSTGRES_HOST` | `postgres-gnosis` | PostgreSQL host |
| `POSTGRES_USER` | - | PostgreSQL username |
| `POSTGRES_PASSWORD` | - | PostgreSQL password |

### Caddyfile Structure

The `Caddyfile` uses Caddy's [handle directive](https://caddyserver.com/docs/caddyfile/directives/handle) for path-based routing:

```caddyfile
{$DOMAIN:localhost} {
    # Order matters - more specific paths first
    handle /ws/subscribe {
        reverse_proxy rpc:8080
    }
    
    handle /rpc/* {
        uri strip_prefix /rpc
        reverse_proxy rpc:8080
    }
    
    # ... more routes ...
    
    # Fallback
    handle {
        reverse_proxy rpc:8080
    }
}
```

### SSL/TLS Certificates

Caddy automatically provisions certificates via ACME (Let's Encrypt):

- **Production**: Set `DOMAIN` to your real domain, ensure ports 80/443 are accessible
- **Staging/Testing**: Use `DOMAIN=localhost` for self-signed certificates
- **Custom certificates**: Mount them to `/data/caddy/certificates/`

## Health Checks

The RPC service exposes health check endpoints:

| Endpoint | Purpose |
|----------|---------|
| `/live` | Liveness probe (always returns 200 if service is running) |
| `/ready` | Readiness probe (checks DB, Nethermind sync, Pathfinder) |
| `/health` | Nethermind connection status |

**Example:**
```bash
curl https://your-domain.com/live
curl https://your-domain.com/ready
```

## Docker Compose Services

The `docker-compose.caddy.yml` includes:

| Service | Status | Description |
|---------|--------|-------------|
| `caddy` | ✅ Active | Reverse proxy |
| `nethermind-gnosis` | ✅ Active | Execution client + indexer |
| `rpc` | ✅ Active | RPC host service |
| `profile-service` | ✅ Active | Profile pinning |
| `pathfinder` | ✅ Active | Path computation |
| `consensus-gnosis` | 💤 Optional | Lighthouse beacon node |

### Enabling Consensus Client

Uncomment the `consensus-gnosis` service in `docker-compose.caddy.yml` if you need a local beacon node:

```yaml
consensus-gnosis:
  container_name: consensus-gnosis
  image: sigp/lighthouse:v7.1.0
  # ... rest of configuration
```

## Troubleshooting

### WebSocket Connection Issues

1. **Check if WebSocket upgrade is working:**
   ```bash
   curl -i -N \
     -H "Connection: Upgrade" \
     -H "Upgrade: websocket" \
     -H "Sec-WebSocket-Version: 13" \
     -H "Sec-WebSocket-Key: $(openssl rand -base64 16)" \
     https://your-domain.com/ws/subscribe
   ```

2. **Verify Caddy logs:**
   ```bash
   docker logs caddy
   ```

### Certificate Issues

1. **Check certificate status:**
   ```bash
   docker exec caddy caddy list-certs
   ```

2. **Force certificate renewal:**
   ```bash
   docker exec caddy caddy reload
   ```

### Service Connectivity

1. **Test internal DNS resolution:**
   ```bash
   docker exec caddy nslookup rpc
   docker exec caddy nslookup nethermind-gnosis
   ```

2. **Test backend connectivity:**
   ```bash
   docker exec caddy wget -qO- http://rpc:8080/live
   ```

## Related Documentation

- [Caddy Documentation](https://caddyserver.com/docs/)
- [Docker Compose Reference](../docker/docker-compose.caddy.yml)
- [Circles RPC API](../docs/rpc-reference.md)
- [Development Guide](../DEVELOPMENT.md)
