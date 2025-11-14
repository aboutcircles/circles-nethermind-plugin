#!/usr/bin/env bash
set -e

# NuGet Pack Script
# Packs NuGet packages for Circles projects

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/nupkgs"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${BLUE}Packing NuGet packages...${NC}\n"

# Configuration
CONFIGURATION="${BUILD_CONFIGURATION:-Release}"

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Clean previous packages if --clean flag is passed
if [[ "$*" == *"--clean"* ]]; then
  echo -e "${YELLOW}Cleaning previous packages...${NC}"
  rm -rf "$OUTPUT_DIR"/*.nupkg
  echo -e "${GREEN}✓ Cleaned output directory${NC}\n"
fi

# Array of projects to pack
# Format: "path/to/project.csproj:package-name"
PROJECTS=(
  "src/Index/Circles.Index/Circles.Index.csproj:Gnosis.Circles.Nethermind.Plugin"
  "src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj:Gnosis.Circles.Pathfinder"
)

# Function to pack a project
pack_project() {
  local project_path=$1
  local package_name=$2

  echo -e "${GREEN}Packing $package_name...${NC}"
  echo -e "${YELLOW}Project: $project_path${NC}"

  dotnet pack "$PROJECT_ROOT/$project_path" \
    -c "$CONFIGURATION" \
    --output "$OUTPUT_DIR" \
    --include-symbols \
    -p:SymbolPackageFormat=snupkg

  if [ $? -eq 0 ]; then
    echo -e "${GREEN}✓ Successfully packed $package_name${NC}\n"
  else
    echo -e "${RED}✗ Failed to pack $package_name${NC}\n"
    exit 1
  fi
}

# Pack all projects
for project in "${PROJECTS[@]}"; do
  IFS=':' read -r path name <<< "$project"
  pack_project "$path" "$name"
done

# List created packages
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Packaging completed!${NC}"
echo -e "${GREEN}========================================${NC}"
echo -e "${YELLOW}Packages created in: $OUTPUT_DIR${NC}\n"

if [ -d "$OUTPUT_DIR" ]; then
  ls -lh "$OUTPUT_DIR"/*.nupkg 2>/dev/null || echo "No .nupkg files found"
  echo ""
  echo -e "${BLUE}Symbol packages:${NC}"
  ls -lh "$OUTPUT_DIR"/*.snupkg 2>/dev/null || echo "No .snupkg files found"
fi
