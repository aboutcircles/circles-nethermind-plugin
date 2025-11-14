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

echo -e "${BLUE}Building all Docker images...${NC}\n"

cd "$DOCKER_DIR"

# Array of images to build
IMAGES=(
  "index:Index.Dockerfile"
  "pathfinder-host:pathfinder-host.Dockerfile"
  "rpc-host:rpc-host.Dockerfile"
)

# Parse arguments for specific image
BUILD_ALL=true
SPECIFIC_IMAGE=""

for arg in "$@"; do
  case $arg in
    index|pathfinder|rpc)
      BUILD_ALL=false
      SPECIFIC_IMAGE="$arg"
      shift
      ;;
    --help)
      echo "Usage: ./docker-build.sh [image]"
      echo ""
      echo "Images:"
      echo "  index         Build Nethermind plugin (Index.Dockerfile)"
      echo "  pathfinder    Build Pathfinder host (pathfinder-host.Dockerfile)"
      echo "  rpc           Build RPC host (rpc-host.Dockerfile)"
      echo ""
      echo "If no image is specified, all images will be built."
      echo ""
      echo "Examples:"
      echo "  ./docker-build.sh              # Build all images"
      echo "  ./docker-build.sh pathfinder   # Build only pathfinder image"
      exit 0
      ;;
  esac
done

# Function to build a single image
build_image() {
  local name=$1
  local dockerfile=$2

  echo -e "${GREEN}Building $name from $dockerfile...${NC}"
  docker build -f "$dockerfile" -t "circles-$name:latest" "$PROJECT_ROOT"

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
