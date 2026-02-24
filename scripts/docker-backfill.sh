#!/usr/bin/env bash
set -e

# Docker Backfill Runner
# Runs the backfill tool inside a container on the circles-gnosis network

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

show_help() {
    echo "Usage: ./docker-backfill.sh <command> [options]"
    echo ""
    echo "Commands:"
    echo "  backfill    Backfill specific tables"
    echo "  list-tables List available tables"
    echo "  status      Show backfill progress"
    echo ""
    echo "Examples:"
    echo "  # List available tables"
    echo "  ./docker-backfill.sh list-tables"
    echo ""
    echo "  # Backfill PaymentGateway tables from block 43610000"
    echo "  ./docker-backfill.sh backfill -t CrcV2_PaymentGateway_GatewayCreated \\"
    echo "      CrcV2_PaymentGateway_PaymentReceived CrcV2_PaymentGateway_TrustUpdated \\"
    echo "      -f 43610000"
    echo ""
    echo "  # Dry run (parse only, don't write)"
    echo "  ./docker-backfill.sh backfill -t CrcV2_PaymentGateway_GatewayCreated -f 43610000 --dry-run"
    echo ""
    echo "Environment:"
    echo "  Reads POSTGRES_USER and POSTGRES_PASSWORD from .env"
    echo "  Connects to postgres-gnosis and nethermind-gnosis on circles-gnosis network"
    exit 0
}

if [[ "$1" == "--help" ]] || [[ "$1" == "-h" ]] || [[ -z "$1" ]]; then
    show_help
fi

# Load environment
if [[ -f "$PROJECT_ROOT/.env" ]]; then
    set -a
    source "$PROJECT_ROOT/.env"
    set +a
else
    echo -e "${RED}Error: .env file not found at $PROJECT_ROOT/.env${NC}"
    exit 1
fi

# Verify required env vars
if [[ -z "$POSTGRES_USER" ]] || [[ -z "$POSTGRES_PASSWORD" ]]; then
    echo -e "${RED}Error: POSTGRES_USER and POSTGRES_PASSWORD must be set in .env${NC}"
    exit 1
fi

# Check if circles-gnosis network exists
if ! docker network inspect circles-gnosis >/dev/null 2>&1; then
    echo -e "${RED}Error: circles-gnosis network not found. Start the stack first:${NC}"
    echo -e "${YELLOW}  ./scripts/docker-run.sh gnosis up -d${NC}"
    exit 1
fi

# Build the backfill image if needed
IMAGE_NAME="circles-backfill:local"
echo -e "${BLUE}Building backfill image...${NC}"
docker build -t "$IMAGE_NAME" -f "$PROJECT_ROOT/docker/backfill.Dockerfile" "$PROJECT_ROOT" --quiet

# Construct connection string for docker network
POSTGRES_CONNECTION_STRING="Server=postgres-gnosis;Port=5432;Database=postgres;User Id=${POSTGRES_USER};Password=${POSTGRES_PASSWORD};"

echo -e "${BLUE}Running backfill tool...${NC}"
echo -e "${YELLOW}Command: $*${NC}\n"

# Run the backfill container
# Use -t for TTY if available, otherwise just -i
TTY_FLAG=""
if [ -t 0 ]; then
    TTY_FLAG="-t"
fi

# Build command array
CMD_ARGS=("$@")

# Add --rpc-url only for backfill command (not list-tables or status)
if [[ "$1" == "backfill" ]]; then
    CMD_ARGS+=("--rpc-url" "http://nethermind-gnosis:8545")
fi

docker run --rm -i $TTY_FLAG \
    --network circles-gnosis \
    -e POSTGRES_CONNECTION_STRING="$POSTGRES_CONNECTION_STRING" \
    -e CIRCLES_PLUGIN_DISABLED="${CIRCLES_PLUGIN_DISABLED:-false}" \
    "$IMAGE_NAME" \
    "${CMD_ARGS[@]}"

echo -e "\n${GREEN}Done!${NC}"
