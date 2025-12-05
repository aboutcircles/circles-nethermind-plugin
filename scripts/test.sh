#!/usr/bin/env bash
set -e

# Test Runner Script
# Runs all tests in the Circles solution

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOLUTION_FILE="$PROJECT_ROOT/Circles.sln"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${BLUE}Running Circles Tests...${NC}\n"

# Configuration
CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
VERBOSITY="${TEST_VERBOSITY:-normal}"

# Test projects
TEST_PROJECTS=(
  "src/Pathfinder/Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj"
  "src/Index/Circles.Index.Query.Tests/Circles.Index.Query.Tests.csproj"
  "src/Index/Circles.Index.Common.Tests/Circles.Index.Common.Tests.csproj"
  "src/Cache/Circles.Cache.Service.Tests/Circles.Cache.Service.Tests.csproj"
  "src/Rpc/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj"
)

# Parse arguments
RUN_ALL=true
SPECIFIC_PROJECT=""
COLLECT_COVERAGE=false
FILTER=""

for arg in "$@"; do
  case $arg in
    pathfinder)
      RUN_ALL=false
      SPECIFIC_PROJECT="src/Pathfinder/Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj"
      shift
      ;;
    query)
      RUN_ALL=false
      SPECIFIC_PROJECT="src/Index/Circles.Index.Query.Tests/Circles.Index.Query.Tests.csproj"
      shift
      ;;
    common)
      RUN_ALL=false
      SPECIFIC_PROJECT="src/Index/Circles.Index.Common.Tests/Circles.Index.Common.Tests.csproj"
      shift
      ;;
    cache)
      RUN_ALL=false
      SPECIFIC_PROJECT="src/Cache/Circles.Cache.Tests/Circles.Cache.Tests.csproj"
      shift
      ;;
    rpc)
      RUN_ALL=false
      SPECIFIC_PROJECT="src/Rpc/Circles.Rpc.Tests/Circles.Rpc.Tests.csproj"
      shift
      ;;
    --coverage)
      COLLECT_COVERAGE=true
      shift
      ;;
    --filter=*)
      FILTER="${arg#*=}"
      shift
      ;;
    --help)
      echo "Usage: ./test.sh [project] [options]"
      echo ""
      echo "Projects:"
      echo "  pathfinder    Run only Pathfinder tests"
      echo "  query         Run only Query tests"
      echo "  common        Run only Common tests"
      echo "  cache         Run only Cache tests"
      echo "  rpc           Run only RPC tests"
      echo ""
      echo "Options:"
      echo "  --coverage            Collect code coverage"
      echo "  --filter=<expression> Filter tests by expression"
      echo ""
      echo "Environment Variables:"
      echo "  BUILD_CONFIGURATION   Build configuration (default: Debug)"
      echo "  TEST_VERBOSITY        Test verbosity (default: normal)"
      echo ""
      echo "Examples:"
      echo "  ./test.sh                              # Run all tests"
      echo "  ./test.sh pathfinder                   # Run only Pathfinder tests"
      echo "  ./test.sh --coverage                   # Run all tests with coverage"
      echo "  ./test.sh --filter=TestName            # Run specific test"
      exit 0
      ;;
    *)
      echo -e "${RED}Unknown argument: $arg${NC}"
      echo "Use --help for usage information"
      exit 1
      ;;
  esac
done

# Build dotnet test command arguments
TEST_ARGS=(
  "--configuration" "$CONFIGURATION"
  "--verbosity" "$VERBOSITY"
  "--no-restore"
)

if [ "$COLLECT_COVERAGE" = true ]; then
  TEST_ARGS+=(
    "--collect:XPlat Code Coverage"
    "--results-directory" "$PROJECT_ROOT/TestResults"
  )
fi

if [ -n "$FILTER" ]; then
  TEST_ARGS+=("--filter" "$FILTER")
fi

# Function to run tests for a project
run_test_project() {
  local project=$1
  local project_name=$(basename "$(dirname "$project")")

  echo -e "${GREEN}Running tests for $project_name...${NC}"

  local services_started=false
  if [[ "$project_name" == "Circles.Pathfinder.Tests" ]]; then
    echo -e "${YELLOW}Starting required services for Pathfinder tests...${NC}"
    # Start PostgreSQL if not running
    if ! docker ps | grep -q circles-postgres; then
      echo -e "${YELLOW}Starting PostgreSQL...${NC}"
      docker run -d --name circles-postgres \
        -e POSTGRES_DB=postgres \
        -e POSTGRES_USER=postgres \
        -e POSTGRES_PASSWORD=postgres \
        -p 5432:5432 \
        postgres:15
      sleep 5
    fi
    # Start Pathfinder service
    echo -e "${YELLOW}Starting Pathfinder service...${NC}"
    ./scripts/run-pathfinder.sh &
    PATHFINDER_PID=$!
    sleep 5
    # Start RPC service
    echo -e "${YELLOW}Starting RPC service...${NC}"
    ./scripts/run-rpc.sh &
    RPC_PID=$!
    sleep 5
    services_started=true
  fi

  local test_result=0
  if dotnet test "$PROJECT_ROOT/$project" "${TEST_ARGS[@]}"; then
    echo -e "${GREEN}✓ $project_name tests passed${NC}\n"
    test_result=0
  else
    echo -e "${RED}✗ $project_name tests failed${NC}\n"
    test_result=1
  fi

  if [[ "$services_started" == true ]]; then
    echo -e "${YELLOW}Stopping services...${NC}"
    kill $RPC_PID 2>/dev/null || true
    kill $PATHFINDER_PID 2>/dev/null || true
    docker stop circles-postgres 2>/dev/null || true
    docker rm circles-postgres 2>/dev/null || true
  fi

  return $test_result
}

# Run tests
TOTAL_PROJECTS=0
PASSED_PROJECTS=0
FAILED_PROJECTS=0
FAILED_PROJECT_NAMES=()

if [ "$RUN_ALL" = true ]; then
  # Run all test projects
  for project in "${TEST_PROJECTS[@]}"; do
    ((TOTAL_PROJECTS++))
    if run_test_project "$project"; then
      ((PASSED_PROJECTS++))
    else
      ((FAILED_PROJECTS++))
      FAILED_PROJECT_NAMES+=("$(basename "$(dirname "$project")")")
    fi
  done
else
  # Run specific project
  ((TOTAL_PROJECTS++))
  if run_test_project "$SPECIFIC_PROJECT"; then
    ((PASSED_PROJECTS++))
  else
    ((FAILED_PROJECTS++))
    FAILED_PROJECT_NAMES+=("$(basename "$(dirname "$SPECIFIC_PROJECT")")")
  fi
fi

# Summary
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Test Summary${NC}"
echo -e "${GREEN}========================================${NC}"
echo -e "Total projects: $TOTAL_PROJECTS"
echo -e "Passed: ${GREEN}$PASSED_PROJECTS${NC}"
echo -e "Failed: ${RED}$FAILED_PROJECTS${NC}"

if [ $FAILED_PROJECTS -gt 0 ]; then
  echo -e "\n${RED}Failed projects:${NC}"
  for proj in "${FAILED_PROJECT_NAMES[@]}"; do
    echo -e "  - $proj"
  done
  exit 1
fi

if [ "$COLLECT_COVERAGE" = true ]; then
  echo -e "\n${YELLOW}Coverage results saved to: $PROJECT_ROOT/TestResults${NC}"
fi

echo -e "\n${GREEN}All tests passed!${NC}"
