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
CONFIGURATION="${BUILD_CONFIGURATION:-Release}"
VERBOSITY="${TEST_VERBOSITY:-normal}"

# Test projects: discovered from the solution so a newly added test project
# can never be silently missing here (a hand-maintained list previously
# dropped Circles.Index.CirclesV2.Tests and Circles.Index.CirclesViews.Tests
# from CI). Capture the sln listing first so a dotnet failure aborts via
# set -e instead of silently truncating the list inside process substitution.
# Portable loop — macOS ships bash 3.2 without mapfile.
sln_output=$(dotnet sln "$SOLUTION_FILE" list)
TEST_PROJECTS=()
while IFS= read -r project; do
  TEST_PROJECTS+=("$project")
done < <(printf '%s\n' "$sln_output" | tr '\\' '/' | grep -E 'Tests\.csproj$' | sort)

if [ ${#TEST_PROJECTS[@]} -eq 0 ]; then
  echo -e "${RED}No test projects found in $SOLUTION_FILE${NC}"
  exit 1
fi

# Parse arguments
RUN_ALL=true
SPECIFIC_PROJECTS=()
COLLECT_COVERAGE=false
FILTER=""

for arg in "$@"; do
  case $arg in
    pathfinder)
      RUN_ALL=false
      SPECIFIC_PROJECTS=("src/Pathfinder/Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj")
      shift
      ;;
    query)
      RUN_ALL=false
      SPECIFIC_PROJECTS=("src/Index/Circles.Index.Query.Tests/Circles.Index.Query.Tests.csproj")
      shift
      ;;
    common)
      RUN_ALL=false
      SPECIFIC_PROJECTS=("src/Common/Circles.Common.Tests/Circles.Common.Tests.csproj")
      shift
      ;;
    index)
      RUN_ALL=false
      SPECIFIC_PROJECTS=(
        "src/Index/Circles.Index.Query.Tests/Circles.Index.Query.Tests.csproj"
        "src/Index/Circles.Index.CirclesV2.Tests/Circles.Index.CirclesV2.Tests.csproj"
        "src/Index/Circles.Index.CirclesViews.Tests/Circles.Index.CirclesViews.Tests.csproj"
        "src/Common/Circles.Common.Tests/Circles.Common.Tests.csproj"
      )
      shift
      ;;
    cache)
      RUN_ALL=false
      SPECIFIC_PROJECTS=("src/Cache/Circles.Cache.Service.Tests/Circles.Cache.Service.Tests.csproj")
      shift
      ;;
    rpc)
      RUN_ALL=false
      SPECIFIC_PROJECTS=("src/Rpc/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj")
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
      echo "  index         Run only Index tests (query + circlesv2 + views + common)"
      echo "  cache         Run only Cache tests"
      echo "  rpc           Run only RPC tests"
      echo ""
      echo "Options:"
      echo "  --coverage            Collect code coverage"
      echo "  --filter=<expression> Filter tests by expression"
      echo ""
      echo "Environment Variables:"
      echo "  BUILD_CONFIGURATION   Build configuration (default: Release)"
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

  # Integration/E2E tests self-skip via Assert.Ignore when TEST_ENV_URL
  # or POSTGRES_CONNECTION_STRING aren't set — no Docker startup needed.
  # For local full-stack testing, use: docker compose up + dotnet test directly.

  # Use .runsettings if available for the project (enables parallel fixtures)
  local runsettings="$PROJECT_ROOT/$(dirname "$project")/test.runsettings"
  local project_test_args=("${TEST_ARGS[@]}")
  if [ -f "$runsettings" ]; then
    project_test_args+=("--settings" "$runsettings")
  fi

  local test_result=0
  if dotnet test "$PROJECT_ROOT/$project" "${project_test_args[@]}"; then
    echo -e "${GREEN}✓ $project_name tests passed${NC}\n"
    test_result=0
  else
    echo -e "${RED}✗ $project_name tests failed${NC}\n"
    test_result=1
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
    ((++TOTAL_PROJECTS))
    if run_test_project "$project"; then
      ((++PASSED_PROJECTS))
    else
      ((++FAILED_PROJECTS))
      FAILED_PROJECT_NAMES+=("$(basename "$(dirname "$project")")")
    fi
  done
else
  # Run specific projects
  for project in "${SPECIFIC_PROJECTS[@]}"; do
    ((++TOTAL_PROJECTS))
    if run_test_project "$project"; then
      ((++PASSED_PROJECTS))
    else
      ((++FAILED_PROJECTS))
      FAILED_PROJECT_NAMES+=("$(basename "$(dirname "$project")")")
    fi
  done
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
