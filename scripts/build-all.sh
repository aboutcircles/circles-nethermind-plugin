#!/usr/bin/env bash
set -e

# Circles Build Script
# This script builds all Docker images, packs NuGet packages, and runs tests

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}Circles Build Script${NC}"
echo -e "${BLUE}========================================${NC}"

# Parse arguments
BUILD_DOCKER=false
PACK_NUGET=false
RUN_TESTS=false
PUSH_NUGET=false

for arg in "$@"; do
  case $arg in
    --docker)
      BUILD_DOCKER=true
      shift
      ;;
    --pack)
      PACK_NUGET=true
      shift
      ;;
    --test)
      RUN_TESTS=true
      shift
      ;;
    --push)
      PUSH_NUGET=true
      shift
      ;;
    --all)
      BUILD_DOCKER=true
      PACK_NUGET=true
      RUN_TESTS=true
      shift
      ;;
    --help)
      echo "Usage: ./build-all.sh [options]"
      echo ""
      echo "Options:"
      echo "  --docker    Build all Docker images"
      echo "  --pack      Pack NuGet packages"
      echo "  --test      Run all tests"
      echo "  --push      Push NuGet packages to nuget.org (requires --pack)"
      echo "  --all       Run all build steps (docker, pack, test)"
      echo "  --help      Show this help message"
      echo ""
      echo "Examples:"
      echo "  ./build-all.sh --all                    # Build everything"
      echo "  ./build-all.sh --pack --push            # Pack and push NuGet packages"
      echo "  ./build-all.sh --docker --test          # Build Docker images and run tests"
      exit 0
      ;;
    *)
      echo -e "${RED}Unknown argument: $arg${NC}"
      echo "Use --help for usage information"
      exit 1
      ;;
  esac
done

# If no arguments, show help
if [ "$BUILD_DOCKER" = false ] && [ "$PACK_NUGET" = false ] && [ "$RUN_TESTS" = false ] && [ "$PUSH_NUGET" = false ]; then
  echo "No build options specified. Use --help for usage information."
  exit 1
fi

# Build Docker images
if [ "$BUILD_DOCKER" = true ]; then
  echo -e "\n${GREEN}Building Docker images...${NC}"
  "$SCRIPT_DIR/docker-build.sh"
fi

# Run tests
if [ "$RUN_TESTS" = true ]; then
  echo -e "\n${GREEN}Running tests...${NC}"
  "$SCRIPT_DIR/test.sh"
fi

# Pack NuGet packages
if [ "$PACK_NUGET" = true ]; then
  echo -e "\n${GREEN}Packing NuGet packages...${NC}"
  "$SCRIPT_DIR/nuget-pack.sh"
fi

# Push NuGet packages
if [ "$PUSH_NUGET" = true ]; then
  if [ "$PACK_NUGET" = false ]; then
    echo -e "${RED}Warning: --push requires --pack. Running pack first...${NC}"
    "$SCRIPT_DIR/nuget-pack.sh"
  fi
  echo -e "\n${GREEN}Pushing NuGet packages...${NC}"
  "$SCRIPT_DIR/nuget-push.sh"
fi

echo -e "\n${GREEN}========================================${NC}"
echo -e "${GREEN}Build completed successfully!${NC}"
echo -e "${GREEN}========================================${NC}"
