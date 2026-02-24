#!/usr/bin/env bash
set -e

# Docker Compose Runner
# Convenient wrapper for running Docker Compose configurations

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKER_DIR="$PROJECT_ROOT/docker"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Available compose files
COMPOSE_FILES=(
  "gnosis:docker-compose.gnosis.yml:Gnosis mainnet"
)

# Parse arguments
NETWORK=""
COMMAND=("up" "-d")

show_help() {
  echo "Usage: ./docker-run.sh <network> [command]"
  echo ""
  echo "Networks:"
  for compose in "${COMPOSE_FILES[@]}"; do
    IFS=':' read -r name file desc <<< "$compose"
    printf "  %-12s %s\n" "$name" "$desc"
  done
  echo ""
  echo "Commands:"
  echo "  up           Start services (default: detached mode)"
  echo "  down         Stop and remove services"
  echo "  logs         View logs (use -f to follow)"
  echo "  ps           List running services"
  echo "  restart      Restart services"
  echo "  stop         Stop services"
  echo "  start        Start stopped services"
  echo ""
  echo "Examples:"
  echo "  ./docker-run.sh gnosis                    # Start Gnosis mainnet"
  echo "  ./docker-run.sh gnosis logs -f            # Follow logs"
  echo "  ./docker-run.sh gnosis down               # Stop services"
  echo "  ./docker-run.sh gnosis ps                 # List services"
  exit 0
}

# Check for help
if [[ "$1" == "--help" ]] || [[ "$1" == "-h" ]] || [[ -z "$1" ]]; then
  show_help
fi

# Get network
NETWORK="$1"
shift

# Get command if provided
if [ $# -gt 0 ]; then
  COMMAND=("$@")  
fi

# Find compose file for network
COMPOSE_FILE=""
for compose in "${COMPOSE_FILES[@]}"; do
  IFS=':' read -r name file desc <<< "$compose"
  if [ "$name" = "$NETWORK" ]; then
    COMPOSE_FILE="$file"
    break
  fi
done

if [ -z "$COMPOSE_FILE" ]; then
  echo -e "${RED}Error: Unknown network '$NETWORK'${NC}"
  echo ""
  show_help
fi

# Check if compose file exists
if [ ! -f "$DOCKER_DIR/$COMPOSE_FILE" ]; then
  echo -e "${RED}Error: Compose file not found: $DOCKER_DIR/$COMPOSE_FILE${NC}"
  exit 1
fi

echo -e "${BLUE}Running Docker Compose for $NETWORK...${NC}"
echo -e "${YELLOW}Compose file: $COMPOSE_FILE${NC}"
echo -e "${YELLOW}Command: ${COMMAND[*]}${NC}\n"

# Run docker compose
docker compose --env-file "$PROJECT_ROOT/.env" -f "$DOCKER_DIR/$COMPOSE_FILE" "${COMMAND[@]}"

echo -e "\n${GREEN}Command completed successfully!${NC}"

# Show helpful tips based on command
if [[ "${COMMAND[0]}" == "up" ]]; then
  echo -e "${YELLOW}View logs: ./docker-run.sh $NETWORK logs -f${NC}"
  echo -e "${YELLOW}Stop services: ./docker-run.sh $NETWORK down${NC}"
elif [[ "${COMMAND[0]}" == "down" ]]; then
  echo -e "${YELLOW}Start again: ./docker-run.sh $NETWORK up${NC}"
fi
