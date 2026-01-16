#!/usr/bin/env bash
set -e

# Docker Build Script
# Builds all Docker images in the docker folder

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKER_DIR="$PROJECT_ROOT/docker"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Detect platform dynamically (use DOCKER_DEFAULT_PLATFORM if set, else auto-detect)
if [ -n "$DOCKER_DEFAULT_PLATFORM" ]; then
    PLATFORM="--platform $DOCKER_DEFAULT_PLATFORM"
    echo -e "${BLUE}Using DOCKER_DEFAULT_PLATFORM: $DOCKER_DEFAULT_PLATFORM${NC}"
else
    ARCH=$(uname -m)
    if [ "$ARCH" = "arm64" ] || [ "$ARCH" = "aarch64" ]; then
        PLATFORM="--platform linux/arm64"
    elif [ "$ARCH" = "x86_64" ]; then
        PLATFORM="--platform linux/amd64"
    else
        echo -e "${YELLOW}Unknown architecture: $ARCH, defaulting to linux/amd64${NC}"
        PLATFORM="--platform linux/amd64"
    fi
    echo -e "${BLUE}Auto-detected platform: $(echo $PLATFORM | cut -d' ' -f2)${NC}"
fi

echo -e "${BLUE}Building all Docker images...${NC}\n"

cd "$DOCKER_DIR"

# Array of core images to build
# test-environment is optional and built via docker compose (requires submodule)
# Note: caddy Dockerfile moved to aboutcircles-infrastructure repo
IMAGES=(
  "cache-service:cache-service.Dockerfile"
  "index:Index.Dockerfile"
  "metrics-exporter:metrics-exporter.Dockerfile"
  "pathfinder-host:pathfinder-host.Dockerfile"
  "rpc-host:rpc-host.Dockerfile"
)

# Parse arguments for specific image
BUILD_ALL=true
SPECIFIC_IMAGE=""

for arg in "$@"; do
  case $arg in
    index|pathfinder|rpc|cache|test-environment)
      BUILD_ALL=false
      SPECIFIC_IMAGE="$arg"
      shift
      ;;
    --help)
      echo "Usage: ./docker-build.sh [image]"
      echo ""
      echo "Images:"
      echo "  cache             Build cache service (cache-service.Dockerfile)"
      echo "  index             Build Nethermind plugin (Index.Dockerfile)"
      echo "  metrics-exporter  Build metrics exporter (metrics-exporter.Dockerfile)"
      echo "  pathfinder        Build Pathfinder host (pathfinder-host.Dockerfile)"
      echo "  rpc               Build RPC host (rpc-host.Dockerfile)"
      echo "  test-environment  Build test environment (requires submodule init)"
      echo ""
      echo "Note: Caddy is built from aboutcircles-infrastructure repo"
      echo ""
      echo "Platform detection:"
      echo "  - Uses DOCKER_DEFAULT_PLATFORM if set (e.g., export DOCKER_DEFAULT_PLATFORM=linux/arm64)"
      echo "  - Otherwise auto-detects: arm64/aarch64 -> linux/arm64, x86_64 -> linux/amd64"
      echo ""
      echo "If no image is specified, all images will be built."
      echo ""
      echo "Examples:"
      echo "  ./docker-build.sh                          # Build all images (auto-detect platform)"
      echo "  ./docker-build.sh pathfinder               # Build only pathfinder image"
      echo "  DOCKER_DEFAULT_PLATFORM=linux/amd64 ./docker-build.sh  # Override platform"
      exit 0
      ;;
  esac
done

# Function to build a single image
build_image() {
  local name=$1
  local dockerfile=$2

  echo -e "${GREEN}Building $name from $dockerfile...${NC}"
  docker build -f "$dockerfile" -t "circles-$name:latest" "$PROJECT_ROOT" --no-cache

  if [ $? -eq 0 ]; then
    echo -e "${GREEN}✓ Successfully built circles-$name:latest${NC}\n"
  else
    echo -e "${RED}✗ Failed to build circles-$name:latest${NC}\n"
    exit 1
  fi
}

# Build images
if [ "$BUILD_ALL" = true ]; then
  for image in "${IMAGES[@]}"; do
    IFS=':' read -r name dockerfile <<< "$image"
    build_image "$name" "$dockerfile"
  done
else
  # Build specific image
  case $SPECIFIC_IMAGE in
    index)
      build_image "index" "Index.Dockerfile"
      ;;
    pathfinder)
      build_image "pathfinder-host" "pathfinder-host.Dockerfile"
      ;;
    rpc)
      build_image "rpc-host" "rpc-host.Dockerfile"
      ;;
    cache)
      build_image "cache-service" "cache-service.Dockerfile"
      ;;
    test-environment)
      # Test environment is in a submodule, use docker compose to build
      TEST_ENV_DIR="$PROJECT_ROOT/circles-test-environment"
      if [ ! -d "$TEST_ENV_DIR" ]; then
        echo -e "${RED}circles-test-environment submodule not found${NC}"
        echo "Run: git submodule update --init circles-test-environment"
        exit 1
      fi
      echo -e "${GREEN}Building test-environment via docker compose...${NC}"
      docker compose --env-file "$PROJECT_ROOT/.env" -f "$DOCKER_DIR/docker-compose.test-environment.yml" build --no-cache
      echo -e "${GREEN}✓ Successfully built test-environment${NC}\n"
      ;;
    *)
      echo -e "${RED}Unknown image: $SPECIFIC_IMAGE${NC}"
      echo "Use --help for available images"
      exit 1
      ;;
  esac
fi

echo -e "${GREEN}Docker build completed!${NC}"
echo -e "${YELLOW}Available images:${NC}"
docker images | grep "circles-" | head -n 10
