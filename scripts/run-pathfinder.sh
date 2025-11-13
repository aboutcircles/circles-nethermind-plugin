#!/usr/bin/env bash
set -e

# Pathfinder Host Development Runner
# Runs the Pathfinder host application using dotnet run

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_PATH="$PROJECT_ROOT/src/Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${BLUE}Starting Pathfinder Host...${NC}\n"

# Configuration
CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"

# Environment variables for development
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:5001}"

# Database connection (customize as needed)
export ConnectionStrings__Database="${ConnectionStrings__Database:-Host=localhost;Port=5432;Database=circles;Username=postgres;Password=postgres}"

# Logging
export Logging__LogLevel__Default="${Logging__LogLevel__Default:-Information}"

echo -e "${YELLOW}Configuration:${NC}"
echo -e "  Environment: $ASPNETCORE_ENVIRONMENT"
echo -e "  URLs: $ASPNETCORE_URLS"
echo -e "  Build Config: $CONFIGURATION"
echo -e "  Database: ${ConnectionStrings__Database%%Password=*}Password=***"
echo ""

# Check if project exists
if [ ! -f "$PROJECT_PATH" ]; then
  echo -e "${RED}Error: Project not found at $PROJECT_PATH${NC}"
  exit 1
fi

# Run the application
echo -e "${GREEN}Running Pathfinder Host...${NC}"
echo -e "${YELLOW}Press Ctrl+C to stop${NC}\n"

cd "$PROJECT_ROOT/src/Pathfinder/Circles.Pathfinder.Host"
dotnet run --configuration "$CONFIGURATION" "$@"
