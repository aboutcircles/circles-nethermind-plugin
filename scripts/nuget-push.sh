#!/usr/bin/env bash
set -e

# NuGet Push Script
# Pushes NuGet packages to nuget.org

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/nupkgs"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${BLUE}Pushing NuGet packages...${NC}\n"

# Configuration from environment variables or defaults
NUGET_API_KEY="${NUGET_API_KEY:-}"
NUGET_SOURCE="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"

# Check if API key is set
if [ -z "$NUGET_API_KEY" ]; then
  echo -e "${RED}Error: NUGET_API_KEY environment variable is not set${NC}"
  echo -e "${YELLOW}Please set it using:${NC}"
  echo -e "  export NUGET_API_KEY='your-api-key-here'"
  echo -e ""
  echo -e "${YELLOW}Or pass it inline:${NC}"
  echo -e "  NUGET_API_KEY='your-key' ./nuget-push.sh"
  exit 1
fi

# Check if packages directory exists
if [ ! -d "$OUTPUT_DIR" ]; then
  echo -e "${RED}Error: Package directory not found: $OUTPUT_DIR${NC}"
  echo -e "${YELLOW}Run ./nuget-pack.sh first to create packages${NC}"
  exit 1
fi

# Find all .nupkg files (excluding symbol packages)
PACKAGES=($(find "$OUTPUT_DIR" -name "*.nupkg" ! -name "*.snupkg" -type f))

if [ ${#PACKAGES[@]} -eq 0 ]; then
  echo -e "${RED}Error: No .nupkg files found in $OUTPUT_DIR${NC}"
  echo -e "${YELLOW}Run ./nuget-pack.sh first to create packages${NC}"
  exit 1
fi

echo -e "${BLUE}Found ${#PACKAGES[@]} package(s) to push:${NC}"
for pkg in "${PACKAGES[@]}"; do
  echo -e "  - $(basename "$pkg")"
done
echo ""

# Ask for confirmation unless --yes flag is passed
if [[ "$*" != *"--yes"* ]] && [[ "$*" != *"-y"* ]]; then
  echo -e "${YELLOW}Push to: $NUGET_SOURCE${NC}"
  printf "Continue? (y/N): "
  read -r REPLY
  if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo -e "${YELLOW}Push cancelled${NC}"
    exit 0
  fi
fi

# Push each package
SUCCESS_COUNT=0
FAILED_COUNT=0
FAILED_PACKAGES=()

for package in "${PACKAGES[@]}"; do
  package_name=$(basename "$package")
  echo -e "${GREEN}Pushing $package_name...${NC}"

  if dotnet nuget push "$package" \
    --api-key "$NUGET_API_KEY" \
    --source "$NUGET_SOURCE" \
    --skip-duplicate; then
    echo -e "${GREEN}✓ Successfully pushed $package_name${NC}\n"
    ((SUCCESS_COUNT++))
  else
    echo -e "${RED}✗ Failed to push $package_name${NC}\n"
    ((FAILED_COUNT++))
    FAILED_PACKAGES+=("$package_name")
  fi
done

# Summary
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Push Summary${NC}"
echo -e "${GREEN}========================================${NC}"
echo -e "Successfully pushed: ${GREEN}$SUCCESS_COUNT${NC} package(s)"
echo -e "Failed: ${RED}$FAILED_COUNT${NC} package(s)"

if [ $FAILED_COUNT -gt 0 ]; then
  echo -e "\n${RED}Failed packages:${NC}"
  for pkg in "${FAILED_PACKAGES[@]}"; do
    echo -e "  - $pkg"
  done
  exit 1
fi

echo -e "\n${GREEN}All packages pushed successfully!${NC}"
