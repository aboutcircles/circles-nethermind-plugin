#!/usr/bin/env bash
set -e

# Index Plugin Development Runner
# Builds and runs Nethermind with the Circles Index plugin for local development

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Default paths (can be overridden with environment variables)
NETHERMIND_SOURCE="${NETHERMIND_SOURCE:-$PROJECT_ROOT/src/nethermind}"
NETHERMIND_BIN_DIR="$PROJECT_ROOT/nethermind-dev/nethermind"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}Starting Nethermind with Circles Index Plugin...${NC}\n"

# Network is always gnosis for now
NETWORK="gnosis"

echo -e "${YELLOW}Configuration:${NC}"
echo -e "  Network: $NETWORK"
echo -e "  Project Root: $PROJECT_ROOT"
echo -e "  Nethermind Source: $NETHERMIND_SOURCE"
echo ""

# Check if Nethermind source exists
if [ ! -d "$NETHERMIND_SOURCE" ]; then
    echo -e "${RED}Error: Nethermind source not found at $NETHERMIND_SOURCE${NC}"
    echo -e "${YELLOW}This script requires Nethermind source for local development.${NC}"
    echo -e "${YELLOW}Options:${NC}"
    echo -e "${YELLOW}  1. Clone Nethermind to $NETHERMIND_SOURCE:${NC}"
    echo -e "${YELLOW}     git clone https://github.com/NethermindEth/nethermind.git $NETHERMIND_SOURCE${NC}"
    echo -e "${YELLOW}  2. Set NETHERMIND_SOURCE environment variable to your Nethermind location${NC}"
    echo -e "${YELLOW}  3. Use Docker instead (doesn't require Nethermind source):${NC}"
    echo -e "${YELLOW}     docker compose -f docker/docker-compose.gnosis.yml up${NC}"
    echo ""
    echo -e "${BLUE}Note: The Nethermind submodule is no longer included by default.${NC}"
    echo -e "${BLUE}It's only needed for local development with custom Nethermind builds.${NC}"
    exit 1
fi

# Build Nethermind if necessary
if [ ! -f "$NETHERMIND_BIN_DIR/nethermind" ]; then
    echo -e "${YELLOW}🛠️  Building Nethermind executable...${NC}"
    mkdir -p "$NETHERMIND_BIN_DIR"
    cd "$NETHERMIND_SOURCE/src/Nethermind/Nethermind.Runner"
    dotnet publish -c mainnet -o "$NETHERMIND_BIN_DIR"
    echo -e "${GREEN}✓ Nethermind built successfully${NC}\n"
fi

# Load environment variables
ENV_FILE="$PROJECT_ROOT/.env.local"
if [ ! -f "$ENV_FILE" ]; then
    ENV_FILE="$PROJECT_ROOT/.env.example"
fi

if [ -f "$ENV_FILE" ]; then
    echo -e "${YELLOW}⚙️  Loading environment variables from $(basename $ENV_FILE)...${NC}"
    set -a
    . "$ENV_FILE"
    set +a
else
    echo -e "${YELLOW}Warning: No .env file found. Using default values.${NC}"
fi

# Set defaults for PostgreSQL credentials if not already set
POSTGRES_USER="${POSTGRES_USER:-postgres}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-postgres}"
POSTGRES_DB="${POSTGRES_DB:-postgres}"

# Export environment variables for the plugin
export NETHERMIND_RPC_URL="${NETHERMIND_RPC_URL:-http://localhost:8545}"
export POSTGRES_CONNECTION_STRING="Server=localhost;Port=5432;Database=${POSTGRES_DB};User Id=${POSTGRES_USER};Password=${POSTGRES_PASSWORD};Include Error Detail=true;"
export POSTGRES_READONLY_CONNECTION_STRING="$POSTGRES_CONNECTION_STRING"
export START_BLOCK="${START_BLOCK:-12000000}"
export EXTERNAL_PATHFINDER_URL="${EXTERNAL_PATHFINDER_URL:-http://localhost:8080}"
export IPFS_GATEWAYS="${IPFS_GATEWAYS:-https://circles-profiles.myfilebase.com}"
export IPFS_MAX_PARALLELISM="${IPFS_MAX_PARALLELISM:-192}"

echo -e "${GREEN}✓ Environment configured${NC}\n"

# Check if PostgreSQL is running
echo -e "${YELLOW}Checking PostgreSQL connection...${NC}"
if ! command -v psql &> /dev/null; then
    echo -e "${YELLOW}Warning: psql command not found, skipping database check${NC}"
else
    POSTGRES_HOST="${POSTGRES_HOST:-localhost}"  
    POSTGRES_PORT="${POSTGRES_PORT:-5432}"  
    # Try to connect to PostgreSQL  
    if ! PGPASSWORD="$POSTGRES_PASSWORD" psql \
        --host="$POSTGRES_HOST" \
        --port="$POSTGRES_PORT" \
        --username="$POSTGRES_USER" \
        --dbname="$POSTGRES_DB" \
        --no-align --tuples-only \
        -c "SELECT 1" &> /dev/null; then
        echo -e "${RED}Error: Cannot connect to PostgreSQL${NC}"
        echo -e "${YELLOW}Please start PostgreSQL first:${NC}"
        echo -e "${YELLOW}  docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis${NC}"
        echo ""
        echo -e "${YELLOW}Or install and start PostgreSQL locally${NC}"
        exit 1
    fi
    echo -e "${GREEN}✓ PostgreSQL is running${NC}\n"
fi

# Build and publish the plugin
echo -e "${YELLOW}🏗️  Building and publishing Circles.Index plugin...${NC}"
cd "$PROJECT_ROOT"
mkdir -p "$NETHERMIND_BIN_DIR/plugins"
dotnet publish src/Index/Circles.Index/Circles.Index.csproj -c Debug -o "$NETHERMIND_BIN_DIR/plugins"
echo -e "${GREEN}✓ Plugin published to $NETHERMIND_BIN_DIR/plugins${NC}\n"

# Ensure JWT secret exists
JWT_SECRET_FILE="$PROJECT_ROOT/.state/jwtsecret-gnosis/jwt.hex"
mkdir -p "$(dirname "$JWT_SECRET_FILE")"
if [ ! -f "$JWT_SECRET_FILE" ]; then
    echo -e "${YELLOW}Generating JWT secret...${NC}"
    openssl rand -hex 32 > "$JWT_SECRET_FILE"
    chmod 0644 "$JWT_SECRET_FILE"
fi

# Create data directory
DATA_DIR="$PROJECT_ROOT/.state/nethermind-gnosis"
mkdir -p "$DATA_DIR"

# Start Nethermind with the plugin
echo -e "${GREEN}🚀 Starting Nethermind with Circles Index plugin on $NETWORK...${NC}"
echo -e "${YELLOW}Press Ctrl+C to stop${NC}\n"
echo -e "${BLUE}Logs will appear below:${NC}"
echo -e "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}\n"

cd "$NETHERMIND_BIN_DIR"

# Run Nethermind in the foreground
# ./nethermind \
#     --config=gnosis \
#     --datadir="$DATA_DIR" \
#     --log=INFO \
#     --JsonRpc.Enabled=true \
#     --JsonRpc.Host=0.0.0.0 \
#     --JsonRpc.Port=8545 \
#     --JsonRpc.EnabledModules='[Web3,Eth,Subscribe,Net,Circles]' \
#     --JsonRpc.JwtSecretFile="$JWT_SECRET_FILE" \
#     --JsonRpc.EngineHost=0.0.0.0 \
#     --JsonRpc.EnginePort=8551 \
#     --Network.DiscoveryPort=30303 \
#     --Network.MaxActivePeers=100 \
#     --HealthChecks.Enabled=true \
#     --HealthChecks.UIEnabled=true
