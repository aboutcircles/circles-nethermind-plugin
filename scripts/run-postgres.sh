#!/usr/bin/env bash
set -e

# PostgreSQL Development Runner
# Starts PostgreSQL database using Docker Compose

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKER_DIR="$PROJECT_ROOT/docker"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}Starting PostgreSQL Database...${NC}\n"

# Default network
NETWORK="${1:-gnosis}"

# Map networks to compose files and service names
case "$NETWORK" in
  gnosis)
    COMPOSE_FILE="docker-compose.gnosis.yml"
    POSTGRES_SERVICE="postgres-gnosis"
    ;;
  chiado)
    COMPOSE_FILE="docker-compose.chiado.yml"
    POSTGRES_SERVICE="postgres-chiado"
    ;;
  spaceneth)
    COMPOSE_FILE="docker-compose.spaceneth.yml"
    POSTGRES_SERVICE="postgres-spaceneth"
    ;;
  *)
    echo -e "${RED}Error: Unknown network '$NETWORK'${NC}"
    echo -e "${YELLOW}Supported networks: gnosis, chiado, spaceneth${NC}"
    exit 1
    ;;
esac

echo -e "${YELLOW}Configuration:${NC}"
echo -e "  Network: $NETWORK"
echo -e "  Compose file: $COMPOSE_FILE"
echo -e "  Service: $POSTGRES_SERVICE"
echo ""

# Check if compose file exists
if [ ! -f "$DOCKER_DIR/$COMPOSE_FILE" ]; then
  echo -e "${RED}Error: Compose file not found: $DOCKER_DIR/$COMPOSE_FILE${NC}"
  exit 1
fi

# Check if service is already running
if docker compose --env-file "$PROJECT_ROOT/.env" -f "$DOCKER_DIR/$COMPOSE_FILE" ps "$POSTGRES_SERVICE" | grep -q "Up"; then
  echo -e "${GREEN}✓ PostgreSQL is already running${NC}"
  echo -e "${YELLOW}To stop: docker compose -f $DOCKER_DIR/$COMPOSE_FILE down $POSTGRES_SERVICE${NC}"
  echo -e "${YELLOW}To view logs: docker compose -f $DOCKER_DIR/$COMPOSE_FILE logs -f $POSTGRES_SERVICE${NC}"
  exit 0
fi

# Start PostgreSQL service
echo -e "${YELLOW}Starting PostgreSQL service...${NC}"
docker compose --env-file "$PROJECT_ROOT/.env" -f "$DOCKER_DIR/$COMPOSE_FILE" up -d "$POSTGRES_SERVICE"

# Wait for service to be healthy
echo -e "${YELLOW}Waiting for PostgreSQL to be ready...${NC}"
timeout=60
counter=0
while [ $counter -lt $timeout ]; do
  if docker compose --env-file "$PROJECT_ROOT/.env" -f "$DOCKER_DIR/$COMPOSE_FILE" ps "$POSTGRES_SERVICE" | grep -q "Up"; then
    echo -e "${GREEN}✓ PostgreSQL is running and ready${NC}"
    echo ""
    echo -e "${YELLOW}Connection details:${NC}"
    echo -e "  Host: localhost"
    echo -e "  Port: 5432"
    echo -e "  Database: postgres"
    echo -e "  User: \${POSTGRES_USER}"
    echo -e "  Password: \${POSTGRES_PASSWORD}"
    echo ""
    echo -e "${YELLOW}To stop: docker compose -f $DOCKER_DIR/$COMPOSE_FILE down $POSTGRES_SERVICE${NC}"
    echo -e "${YELLOW}To view logs: docker compose -f $DOCKER_DIR/$COMPOSE_FILE logs -f $POSTGRES_SERVICE${NC}"
    exit 0
  fi
  counter=$((counter + 1))
  sleep 1
done

echo -e "${RED}Error: PostgreSQL failed to start within $timeout seconds${NC}"
echo -e "${YELLOW}Check logs: docker compose -f $DOCKER_DIR/$COMPOSE_FILE logs $POSTGRES_SERVICE${NC}"
exit 1