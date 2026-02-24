#!/bin/bash
# Pathfinder Test Suite
# Quick commands to test pathfinder issues

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TEST_PROJECT="$PROJECT_ROOT/src/Pathfinder/Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

print_header() {
    echo -e "\n${YELLOW}=== $1 ===${NC}\n"
}

# Default TEST_ENV_URL if not set
: "${TEST_ENV_URL:=https://staging.circlesubi.network/test-env}"
export TEST_ENV_URL

case "${1:-help}" in
    unit)
        print_header "Running Unit Tests (no network required)"
        dotnet test "$TEST_PROJECT" --filter "Category=Unit" -v n
        ;;

    integration)
        print_header "Running Integration Tests (requires TEST_ENV_URL=$TEST_ENV_URL)"
        dotnet test "$TEST_PROJECT" --filter "Category=Integration" -v n
        ;;

    consented)
        print_header "Running Consented Flow Tests"
        dotnet test "$TEST_PROJECT" --filter "FullyQualifiedName~Consented" -v n
        ;;

    payment)
        print_header "Running Payment Gateway Tests"
        dotnet test "$TEST_PROJECT" --filter "FullyQualifiedName~PaymentGateway" -v n
        ;;

    regression)
        print_header "Running Regression Tests (requires TEST_ENV_URL=$TEST_ENV_URL)"
        dotnet test "$TEST_PROJECT" --filter "FullyQualifiedName~RegressionTests" -v n
        ;;

    edge)
        print_header "Running Edge Ordering Tests"
        dotnet test "$TEST_PROJECT" --filter "FullyQualifiedName~EdgeOrdering" -v n
        ;;

    all)
        print_header "Running ALL Pathfinder Tests"
        dotnet test "$TEST_PROJECT" -v n
        ;;

    quick)
        print_header "Quick Smoke Test (unit only)"
        dotnet test "$TEST_PROJECT" --filter "Category=Unit" -v m --no-build 2>/dev/null || \
        dotnet test "$TEST_PROJECT" --filter "Category=Unit" -v m
        ;;

    rpc)
        print_header "Testing RPC Endpoints"
        RPC_URL="${2:-http://localhost:8081}"
        echo "Testing: $RPC_URL"
        "$SCRIPT_DIR/test-rpc.sh" "$RPC_URL"
        ;;

    staging)
        print_header "Testing Staging Environment"
        echo "TEST_ENV_URL: $TEST_ENV_URL"

        echo -e "\n${GREEN}1. Checking block exists endpoint...${NC}"
        curl -s "$TEST_ENV_URL/api/v1/blocks/43500000/exists" | jq .

        echo -e "\n${GREEN}2. Checking latest block...${NC}"
        curl -s "$TEST_ENV_URL/api/v1/blocks/latest" | jq . 2>/dev/null || echo "No response"

        echo -e "\n${GREEN}3. Running quick integration test...${NC}"
        dotnet test "$TEST_PROJECT" \
            --filter "FullyQualifiedName~RegressionScenario_UnitTest" \
            -v m --no-build 2>/dev/null || \
        dotnet test "$TEST_PROJECT" \
            --filter "FullyQualifiedName~RegressionScenario_UnitTest" -v m
        ;;

    debug)
        print_header "Debug: Show test environment info"
        echo "TEST_ENV_URL: $TEST_ENV_URL"
        echo "PROJECT_ROOT: $PROJECT_ROOT"
        echo ""
        echo "Available scenarios:"
        ls -la "$PROJECT_ROOT/src/Pathfinder/Circles.Pathfinder.Tests/RegressionScenarios/"*.json 2>/dev/null | awk '{print "  " $NF}'
        ;;

    help|*)
        echo "Pathfinder Test Suite"
        echo ""
        echo "Usage: $0 <command> [options]"
        echo ""
        echo "Commands:"
        echo "  unit        Run unit tests (no network)"
        echo "  integration Run integration tests (needs TEST_ENV_URL)"
        echo "  consented   Run consented flow tests"
        echo "  payment     Run payment gateway tests"
        echo "  regression  Run regression tests (needs TEST_ENV_URL)"
        echo "  edge        Run edge ordering tests"
        echo "  all         Run ALL tests"
        echo "  quick       Quick smoke test (unit only, fast)"
        echo "  rpc [url]   Test RPC endpoints (default: localhost:8081)"
        echo "  staging     Test staging environment connectivity"
        echo "  debug       Show test environment info"
        echo ""
        echo "Environment:"
        echo "  TEST_ENV_URL  Test environment URL (default: staging)"
        echo ""
        echo "Examples:"
        echo "  $0 unit                    # Fast unit tests"
        echo "  $0 staging                 # Check staging works"
        echo "  $0 consented               # Test consented flow"
        echo "  TEST_ENV_URL=http://localhost:5000/test-env $0 integration"
        ;;
esac
