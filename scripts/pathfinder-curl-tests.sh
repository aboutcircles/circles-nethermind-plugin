#!/usr/bin/env bash
# Pathfinder API curl tests - Comprehensive test suite
# Run against staging: ./scripts/pathfinder-curl-tests.sh
# Run against local:   ./scripts/pathfinder-curl-tests.sh http://localhost:8081
# Run single test:     ./scripts/pathfinder-curl-tests.sh staging 18a

set -euo pipefail

# Default endpoint
ENDPOINT="${1:-https://staging.circlesubi.network/}"
FILTER="${2:-}"

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Test counter
PASSED=0
FAILED=0

run_test() {
    local name="$1"
    local description="$2"
    local expected="$3"  # "flow" or "noflow" or "debug"
    local payload="$4"

    # Skip if filter is set and doesn't match
    if [[ -n "$FILTER" && "$name" != *"$FILTER"* ]]; then
        return
    fi

    echo -e "${BLUE}[$name]${NC} $description"

    local response
    response=$(curl -s -X POST "$ENDPOINT" \
        -H "Content-Type: application/json" \
        -d "$payload" 2>&1) || true

    local maxFlow
    maxFlow=$(echo "$response" | jq -r '.result.maxFlow // .maxFlow // "error"' 2>/dev/null || echo "error")

    case "$expected" in
        "flow")
            if [[ "$maxFlow" != "0" && "$maxFlow" != "error" && "$maxFlow" != "null" ]]; then
                echo -e "  ${GREEN}PASS${NC} maxFlow=$maxFlow"
                ((PASSED++))
            else
                echo -e "  ${YELLOW}FAIL${NC} expected flow, got maxFlow=$maxFlow"
                echo "  Response: $(echo "$response" | jq -c '.result // .' 2>/dev/null || echo "$response")"
                ((FAILED++))
            fi
            ;;
        "noflow")
            if [[ "$maxFlow" == "0" ]]; then
                echo -e "  ${GREEN}PASS${NC} maxFlow=0 (expected)"
                ((PASSED++))
            else
                echo -e "  ${YELLOW}FAIL${NC} expected maxFlow=0, got $maxFlow"
                ((FAILED++))
            fi
            ;;
        "debug")
            local hasDebug
            hasDebug=$(echo "$response" | jq -r '.result.debug // .debug // "none"' 2>/dev/null || echo "none")
            if [[ "$hasDebug" != "none" && "$hasDebug" != "null" ]]; then
                echo -e "  ${GREEN}PASS${NC} debug object present, maxFlow=$maxFlow"
                ((PASSED++))
            else
                echo -e "  ${YELLOW}FAIL${NC} expected debug object"
                ((FAILED++))
            fi
            ;;
    esac
    echo ""
}

echo "========================================"
echo "Pathfinder API Test Suite"
echo "Endpoint: $ENDPOINT"
echo "========================================"
echo ""

# =============================================================================
# PART 1: quantizedMode Issue Tests
# =============================================================================
echo "=== PART 1: quantizedMode Issue Tests ==="
echo ""

run_test "Test-02" "quantizedMode WITHOUT ToTokens (auto-discovery)" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"96000000000000000000","quantizedMode":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-03" "quantizedMode WITH ToTokens" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"96000000000000000000","ToTokens":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"],"quantizedMode":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-06" "Swap mode (Source==Sink)" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","TargetFlow":"96000000000000000000","FromTokens":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"],"ToTokens":["0x25dc0d9883ceaa766456c71c81f18eba1ebb562b"],"quantizedMode":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-12" "SimulatedBalances + quantizedMode" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"192000000000000000000","quantizedMode":true,"SimulatedBalances":[{"Holder":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Token":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Amount":"500000000000000000000"}]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-16" "Org sink (expected: no flow)" "noflow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xc7d3df890952a327af94d5ba6fdc1bf145188a1b","Sink":"0x00738aca013B7B2e6cfE1690F0021C3182Fa40B5","TargetFlow":"96000000000000000000","quantizedMode":true}],"id":1,"jsonrpc":"2.0"}'

# =============================================================================
# PART 2: General Pathfinder Tests
# =============================================================================
echo "=== PART 2: General Pathfinder Tests ==="
echo ""

run_test "Test-01" "Basic transfer" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000"}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-04" "SimulatedBalances" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"500000000000000000000","SimulatedBalances":[{"Holder":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Token":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Amount":"500000000000000000000"}]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-05" "SimulatedTrusts" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0xfaecbab4a55de492bff76385a668ba0994f166bd","TargetFlow":"100000000000000000000","SimulatedTrusts":[{"Truster":"0xfaecbab4a55de492bff76385a668ba0994f166bd","Trustee":"0xd40133ea712e7012a95fdd3c008ab58f7918b446"}]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-07" "MaxTransfers limit" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","MaxTransfers":1}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-08" "FromTokens filter" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","FromTokens":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-09" "ExcludedFromTokens (expected: no flow)" "noflow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","ExcludedFromTokens":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-10" "ExcludedToTokens (multi-hop)" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","ExcludedToTokens":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-11" "WithWrap" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","WithWrap":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-13" "Group minting (via group token)" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0x0afd8899bca011bb95611409f09c8efbf6b169cf","Sink":"0xf7bd3d83df90b4682725adf668791d4d1499207f","TargetFlow":"1000000000000000000"}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-14" "Consented flow" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0x227642ebd3a801e7b44a5bb956c02c2d97ca71f0","Sink":"0x211d4db9d1d2290fcc62b66574e2bbafba734c98","TargetFlow":"1000000000000000000"}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-15" "SimulatedConsentedAvatars (no new path)" "noflow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","SimulatedConsentedAvatars":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"]}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-17" "Payment gateway (multi-hop group tokens)" "flow" \
'{"method":"circlesV2_findPath","params":[{"Source":"0x4b6F72008e7ACa33De36B6565eF30264626B21dB","Sink":"0x1f6db4d3cd8a506307952897a5b6d3bdedffbd1e","TargetFlow":"1000000000000000000"}],"id":1,"jsonrpc":"2.0"}'

# =============================================================================
# PART 3: Debug Flag Tests
# =============================================================================
echo "=== PART 3: Debug Flag Tests ==="
echo ""

run_test "Test-18a" "Debug: Basic transfer (shows tpool nodes)" "debug" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","debugShowIntermediateSteps":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-18b" "Debug: quantizedMode (self-loop aggregation)" "debug" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"96000000000000000000","quantizedMode":true,"debugShowIntermediateSteps":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-18c" "Debug: Payment gateway (4-step path)" "debug" \
'{"method":"circlesV2_findPath","params":[{"Source":"0x4b6F72008e7ACa33De36B6565eF30264626B21dB","Sink":"0x1f6db4d3cd8a506307952897a5b6d3bdedffbd1e","TargetFlow":"1000000000000000000","debugShowIntermediateSteps":true}],"id":1,"jsonrpc":"2.0"}'

run_test "Test-18d" "Debug: Forced multi-hop (with filters)" "debug" \
'{"method":"circlesV2_findPath","params":[{"Source":"0xd40133ea712e7012a95fdd3c008ab58f7918b446","Sink":"0x25dc0d9883ceaa766456c71c81f18eba1ebb562b","TargetFlow":"100000000000000000000","ExcludedToTokens":["0xd40133ea712e7012a95fdd3c008ab58f7918b446"],"ToTokens":["0x1aca75e38263c79d9d4f10df0635cc6fcfe6f026"],"debugShowIntermediateSteps":true}],"id":1,"jsonrpc":"2.0"}'

# =============================================================================
# PART 4: Metrics Endpoint Tests
# =============================================================================
echo "=== PART 4: Metrics Endpoint Tests ==="
echo ""

# Helper function for metrics assertions
assert_metric_exists() {
    local metrics="$1"
    local metric_name="$2"
    local description="$3"

    if echo "$metrics" | grep -q "$metric_name"; then
        echo -e "  ${GREEN}✓${NC} $description"
        return 0
    else
        echo -e "  ${YELLOW}✗${NC} $description"
        return 1
    fi
}

test_metrics_endpoint() {
    local name="Test-Metrics"
    local description="Prometheus metrics endpoint"

    # Skip if filter is set and doesn't match
    if [[ -n "$FILTER" && "$name" != *"$FILTER"* ]]; then
        return
    fi

    echo -e "${BLUE}[$name]${NC} $description"

    # Derive metrics URL from endpoint
    # Metrics are typically only exposed locally (not through reverse proxy)
    local metrics_url
    if [[ "$ENDPOINT" == *"localhost"* ]] || [[ "$ENDPOINT" == *"127.0.0.1"* ]]; then
        # Local development - use port 8080 for pathfinder metrics
        local base="${ENDPOINT%/}"
        base="${base%/pathfinder}"  # Remove trailing /pathfinder if present
        # Replace port with 8080 if present
        if [[ "$base" =~ :([0-9]+) ]]; then
            metrics_url="${base%:*}:8080/metrics"
        else
            metrics_url="${base}/metrics"
        fi
    else
        # Remote endpoints (staging/production) - metrics not exposed externally
        echo -e "  ${YELLOW}SKIP${NC} Metrics endpoint not exposed externally (use localhost for metrics tests)"
        echo ""
        return
    fi

    echo -e "  Fetching from: $metrics_url"

    local metrics
    metrics=$(curl -s --max-time 10 "$metrics_url" 2>&1) || true

    if [[ -z "$metrics" ]] || [[ "$metrics" == *"Not found"* ]] || [[ ${#metrics} -lt 100 ]]; then
        echo -e "  ${YELLOW}SKIP${NC} Could not reach metrics endpoint at $metrics_url"
        echo ""
        return
    fi

    local metrics_passed=0
    local metrics_failed=0

    # Check for graph update metrics
    if assert_metric_exists "$metrics" "circles_graph_update_total" "circles_graph_update_total present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    if assert_metric_exists "$metrics" "circles_graph_consecutive_errors" "circles_graph_consecutive_errors present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    if assert_metric_exists "$metrics" "circles_graph_last_update_timestamp" "circles_graph_last_update_timestamp present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    if assert_metric_exists "$metrics" "circles_graph_last_processed_block" "circles_graph_last_processed_block present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    if assert_metric_exists "$metrics" "circles_graph_update_duration_seconds" "circles_graph_update_duration_seconds histogram present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    # Check for status labels on update_total
    if assert_metric_exists "$metrics" 'circles_graph_update_total{status="success"}' "success status label present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    # Check for graph labels on duration histogram
    if assert_metric_exists "$metrics" 'graph="trust"' "trust graph label present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    if assert_metric_exists "$metrics" 'graph="balance"' "balance graph label present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    if assert_metric_exists "$metrics" 'graph="total"' "total graph label present"; then
        ((metrics_passed++))
    else
        ((metrics_failed++))
    fi

    # Summary for this test
    if [[ $metrics_failed -eq 0 ]]; then
        echo -e "  ${GREEN}PASS${NC} All $metrics_passed metrics checks passed"
        ((PASSED++))
    else
        echo -e "  ${YELLOW}FAIL${NC} $metrics_passed passed, $metrics_failed failed"
        ((FAILED++))
    fi
    echo ""
}

# Run metrics test
test_metrics_endpoint

# =============================================================================
# Summary
# =============================================================================
echo "========================================"
echo "Results: $PASSED passed, $FAILED failed"
echo "========================================"

if [[ $FAILED -gt 0 ]]; then
    exit 1
fi
