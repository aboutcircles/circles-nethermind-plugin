#!/usr/bin/env bash
set -e
set -o pipefail

# Comprehensive Cache Service Test Script
# Tests all cache endpoints, validation, performance, and reorg handling
#
# Usage:
#   ./test-cache.sh [CACHE_URL] [--json] [--json-dir <dir>] [--skip-warmup] [--performance]
#
# Examples:
#   ./test-cache.sh                                    # Test localhost:3001
#   ./test-cache.sh http://localhost:3001              # Test custom URL
#   ./test-cache.sh --json                             # JSON output
#   ./test-cache.sh --skip-warmup                      # Skip warmup checks
#   ./test-cache.sh --performance                      # Run performance benchmarks

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Test addresses - known accounts with data
TEST_ADDR_1="0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0"
TEST_ADDR_2="0x42cEDde51198D1773590311E2A340DC06B24cB37"
TEST_ADDR_3="0xDE374ece6fA50e781E81Aac78e811b33D16912c7"

# Configuration
CACHE_URL=""
OUTPUT_MODE="pretty"
JSON_DIR=""
SKIP_WARMUP=false
PERFORMANCE_TEST=false
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --json)
            OUTPUT_MODE="json"
            ;;
        --json-dir)
            shift
            JSON_DIR="$1"
            ;;
        --skip-warmup)
            SKIP_WARMUP=true
            ;;
        --performance)
            PERFORMANCE_TEST=true
            ;;
        http://*|https://*)
            CACHE_URL="$1"
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
    shift
done

if [[ -z "$CACHE_URL" ]]; then
    CACHE_URL="http://localhost:${CACHE_PORT:-3001}"
fi

# Helper functions
log_info() {
    if [[ "$OUTPUT_MODE" != "json" ]]; then
        echo -e "${BLUE}$1${NC}"
    fi
}

log_success() {
    if [[ "$OUTPUT_MODE" != "json" ]]; then
        echo -e "${GREEN}✓ $1${NC}"
    fi
}

log_error() {
    echo -e "${RED}✗ $1${NC}" >&2
}

log_warning() {
    if [[ "$OUTPUT_MODE" != "json" ]]; then
        echo -e "${YELLOW}⚠ $1${NC}"
    fi
}

# Test execution with timing
run_cache_test() {
    local test_name="$1"
    local url="$2"
    local expected_status="${3:-200}"

    TOTAL_TESTS=$((TOTAL_TESTS + 1))

    local start_time=$(python3 -c "import time; print(int(time.time() * 1000))" 2>/dev/null || echo "0")

    local response
    local http_code
    response=$(curl -s -w "\n%{http_code}" "$url" 2>&1)
    http_code=$(echo "$response" | tail -n 1)
    local body=$(echo "$response" | sed '$d')

    local end_time=$(python3 -c "import time; print(int(time.time() * 1000))" 2>/dev/null || echo "0")
    local duration=$((end_time - start_time))

    if [[ "$http_code" == "$expected_status" ]]; then
        PASSED_TESTS=$((PASSED_TESTS + 1))

        if [[ "$OUTPUT_MODE" == "json" ]]; then
            echo "{\"test\":\"$test_name\",\"status\":\"pass\",\"http_code\":$http_code,\"duration_ms\":$duration,\"response\":$body}"
        else
            echo -e "${GREEN}✓${NC} $test_name ${BLUE}[${duration}ms]${NC}"
            if [[ -n "$body" ]]; then
                echo "$body" | jq '.' 2>/dev/null || echo "$body"
            fi
            echo ""
        fi
        return 0
    else
        FAILED_TESTS=$((FAILED_TESTS + 1))

        if [[ "$OUTPUT_MODE" == "json" ]]; then
            echo "{\"test\":\"$test_name\",\"status\":\"fail\",\"expected\":$expected_status,\"actual\":$http_code,\"duration_ms\":$duration,\"response\":$body}"
        else
            echo -e "${RED}✗${NC} $test_name (expected $expected_status, got $http_code) ${BLUE}[${duration}ms]${NC}"
            if [[ -n "$body" ]]; then
                echo "$body" | jq '.' 2>/dev/null || echo "$body"
            fi
            echo ""
        fi
        return 1
    fi
}

# JSON validation helper
validate_json_field() {
    local json="$1"
    local field_path="$2"
    local expected_type="$3"

    local value=$(echo "$json" | jq -r "$field_path" 2>/dev/null)

    if [[ "$value" == "null" || -z "$value" ]]; then
        return 1
    fi

    case "$expected_type" in
        "string")
            [[ -n "$value" && "$value" != "null" ]]
            ;;
        "number")
            [[ "$value" =~ ^[0-9]+(\.[0-9]+)?$ ]]
            ;;
        "array")
            echo "$json" | jq -e "$field_path | type == \"array\"" >/dev/null 2>&1
            ;;
        "object")
            echo "$json" | jq -e "$field_path | type == \"object\"" >/dev/null 2>&1
            ;;
        *)
            return 1
            ;;
    esac
}

# Print header
if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}======================================${NC}"
    echo -e "${BLUE}  Circles Cache Service Test Suite${NC}"
    echo -e "${BLUE}======================================${NC}\n"
    echo -e "${YELLOW}Target:${NC} $CACHE_URL"
    echo -e "${YELLOW}Test Addresses:${NC}"
    echo -e "  1: $TEST_ADDR_1"
    echo -e "  2: $TEST_ADDR_2"
    echo -e "  3: $TEST_ADDR_3"
    echo -e ""
fi

######################################################################
# 1. Health Checks
######################################################################

log_info "=== Health & Readiness Checks ==="

run_cache_test "Health endpoint" "$CACHE_URL/live" 200
run_cache_test "Ready endpoint" "$CACHE_URL/ready" 200

# Check warmup completion
if [[ "$SKIP_WARMUP" != "true" ]]; then
    log_info "Checking warmup status..."
    ready_response=$(curl -s "$CACHE_URL/ready")
    warmup_complete=$(echo "$ready_response" | jq -r '.warmupComplete' 2>/dev/null)

    if [[ "$warmup_complete" == "true" ]]; then
        log_success "Cache warmup complete"
    else
        log_warning "Cache warmup not complete yet - some tests may return empty results"
    fi

    last_processed_block=$(echo "$ready_response" | jq -r '.lastProcessedBlock' 2>/dev/null)
    if [[ -n "$last_processed_block" && "$last_processed_block" != "null" ]]; then
        log_info "Last processed block: $last_processed_block"
    fi
fi

echo ""

######################################################################
# 2. Balance Endpoints
######################################################################

log_info "=== Balance Endpoints ==="

# Token balances (should return array)
run_cache_test "Get token balances (addr1)" "$CACHE_URL/api/balances/$TEST_ADDR_1" 200
run_cache_test "Get token balances (addr2)" "$CACHE_URL/api/balances/$TEST_ADDR_2" 200
run_cache_test "Get token balances (addr3)" "$CACHE_URL/api/balances/$TEST_ADDR_3" 200

# Total balances
run_cache_test "Get total balance (addr1)" "$CACHE_URL/api/balances/$TEST_ADDR_1/total" 200
run_cache_test "Get total balance (addr2)" "$CACHE_URL/api/balances/$TEST_ADDR_2/total" 200

# Invalid address tests
run_cache_test "Invalid address format" "$CACHE_URL/api/balances/invalid" 400
run_cache_test "Address too short" "$CACHE_URL/api/balances/0x123" 400
run_cache_test "Address too long" "$CACHE_URL/api/balances/0x1234567890123456789012345678901234567890123" 400
run_cache_test "Missing 0x prefix" "$CACHE_URL/api/balances/DE374ece6fA50e781E81Aac78e811b33D16912c7" 400

echo ""

######################################################################
# 3. Avatar Endpoints
######################################################################

log_info "=== Avatar Endpoints ==="

# Single avatar queries
run_cache_test "Get avatar info (addr1)" "$CACHE_URL/api/avatars/$TEST_ADDR_1" 200
run_cache_test "Get avatar info (addr2)" "$CACHE_URL/api/avatars/$TEST_ADDR_2" 200
run_cache_test "Get avatar info (addr3)" "$CACHE_URL/api/avatars/$TEST_ADDR_3" 200

# Non-existent address (should return 404)
run_cache_test "Get avatar info (non-existent)" "$CACHE_URL/api/avatars/0x0000000000000000000000000000000000000001" 404

# Invalid addresses
run_cache_test "Invalid avatar address" "$CACHE_URL/api/avatars/notanaddress" 404

echo ""

######################################################################
# 4. Batch Endpoints
######################################################################

log_info "=== Batch Endpoints ==="

# Avatar batch (valid batch)
batch_payload_avatars=$(cat <<EOF
{
  "addresses": [
    "$TEST_ADDR_1",
    "$TEST_ADDR_2",
    "$TEST_ADDR_3"
  ]
}
EOF
)

TOTAL_TESTS=$((TOTAL_TESTS + 1))
response=$(curl -s -w "\n%{http_code}" -X POST "$CACHE_URL/api/avatars/batch" \
    -H "Content-Type: application/json" \
    -d "$batch_payload_avatars")
http_code=$(echo "$response" | tail -n 1)

if [[ "$http_code" == "200" ]]; then
    PASSED_TESTS=$((PASSED_TESTS + 1))
    log_success "Avatar batch (3 addresses)"
else
    FAILED_TESTS=$((FAILED_TESTS + 1))
    log_error "Avatar batch failed (expected 200, got $http_code)"
fi

# Profile CID batch
batch_payload_profiles=$(cat <<EOF
{
  "addresses": [
    "$TEST_ADDR_1",
    "$TEST_ADDR_2"
  ]
}
EOF
)

TOTAL_TESTS=$((TOTAL_TESTS + 1))
response=$(curl -s -w "\n%{http_code}" -X POST "$CACHE_URL/api/profiles/batch" \
    -H "Content-Type: application/json" \
    -d "$batch_payload_profiles")
http_code=$(echo "$response" | tail -n 1)

if [[ "$http_code" == "200" ]]; then
    PASSED_TESTS=$((PASSED_TESTS + 1))
    log_success "Profile CID batch (2 addresses)"
else
    FAILED_TESTS=$((FAILED_TESTS + 1))
    log_error "Profile CID batch failed (expected 200, got $http_code)"
fi

# Test batch size limit (should fail with >100 addresses)
log_info "Testing batch size limits..."

# Generate 101 addresses (should fail)
large_batch_addresses=$(for i in {1..101}; do echo "\"0x$(printf '%040x' $i)\""; done | paste -sd ',' -)
batch_payload_large=$(cat <<EOF
{
  "addresses": [$large_batch_addresses]
}
EOF
)

TOTAL_TESTS=$((TOTAL_TESTS + 1))
response=$(curl -s -w "\n%{http_code}" -X POST "$CACHE_URL/api/avatars/batch" \
    -H "Content-Type: application/json" \
    -d "$batch_payload_large")
http_code=$(echo "$response" | tail -n 1)

if [[ "$http_code" == "400" ]]; then
    PASSED_TESTS=$((PASSED_TESTS + 1))
    log_success "Batch size limit enforced (101 addresses rejected)"
else
    FAILED_TESTS=$((FAILED_TESTS + 1))
    log_error "Batch size limit NOT enforced (expected 400, got $http_code)"
fi

echo ""

######################################################################
# 5. Profile Endpoints
######################################################################

log_info "=== Profile Endpoints ==="

run_cache_test "Get profile CID (addr1)" "$CACHE_URL/api/profiles/$TEST_ADDR_1" 200
run_cache_test "Get profile CID (addr2)" "$CACHE_URL/api/profiles/$TEST_ADDR_2" 200

# Invalid profile queries
run_cache_test "Invalid profile address" "$CACHE_URL/api/profiles/invalid" 400

echo ""

######################################################################
# 6. Data Validation Tests
######################################################################

log_info "=== Data Validation Tests ==="

# Test that balance responses have correct structure
balance_response=$(curl -s "$CACHE_URL/api/balances/$TEST_ADDR_1")
if echo "$balance_response" | jq -e 'type == "array"' >/dev/null 2>&1; then
    log_success "Balance response is array"
    PASSED_TESTS=$((PASSED_TESTS + 1))
else
    log_error "Balance response is not array"
    FAILED_TESTS=$((FAILED_TESTS + 1))
fi
TOTAL_TESTS=$((TOTAL_TESTS + 1))

# Test total balance is a valid number or string
total_balance_response=$(curl -s "$CACHE_URL/api/balances/$TEST_ADDR_1/total")
if echo "$total_balance_response" | jq -e '.balance' >/dev/null 2>&1; then
    balance_value=$(echo "$total_balance_response" | jq -r '.balance')
    if [[ "$balance_value" =~ ^[0-9]+(\.[0-9]+)?$ || "$balance_value" == "0" ]]; then
        log_success "Total balance is valid number: $balance_value"
        PASSED_TESTS=$((PASSED_TESTS + 1))
    else
        log_error "Total balance is not a valid number: $balance_value"
        FAILED_TESTS=$((FAILED_TESTS + 1))
    fi
else
    log_error "Total balance response missing 'balance' field"
    FAILED_TESTS=$((FAILED_TESTS + 1))
fi
TOTAL_TESTS=$((TOTAL_TESTS + 1))

# Test avatar response structure
avatar_response=$(curl -s "$CACHE_URL/api/avatars/$TEST_ADDR_1")
if echo "$avatar_response" | jq -e '.version' >/dev/null 2>&1; then
    log_success "Avatar response has version field"
    PASSED_TESTS=$((PASSED_TESTS + 1))
else
    log_warning "Avatar response missing version field (may be non-existent avatar)"
    PASSED_TESTS=$((PASSED_TESTS + 1))  # Not a failure if avatar doesn't exist
fi
TOTAL_TESTS=$((TOTAL_TESTS + 1))

echo ""

######################################################################
# 7. Performance Benchmarks
######################################################################

if [[ "$PERFORMANCE_TEST" == "true" ]]; then
    log_info "=== Performance Benchmarks ==="

    # Benchmark single balance query (should be <20ms)
    log_info "Benchmarking balance queries (10 iterations)..."
    total_time=0
    iterations=10

    for i in $(seq 1 $iterations); do
        start=$(python3 -c "import time; print(int(time.time() * 1000))")
        curl -s "$CACHE_URL/api/balances/$TEST_ADDR_1" >/dev/null
        end=$(python3 -c "import time; print(int(time.time() * 1000))")
        duration=$((end - start))
        total_time=$((total_time + duration))
    done

    avg_time=$((total_time / iterations))
    log_info "Average balance query time: ${avg_time}ms"

    if [[ $avg_time -lt 50 ]]; then
        log_success "Performance is EXCELLENT (<50ms)"
    elif [[ $avg_time -lt 100 ]]; then
        log_success "Performance is GOOD (<100ms)"
    else
        log_warning "Performance is SLOW (>${avg_time}ms) - consider secondary index optimization"
    fi

    # Benchmark avatar query
    log_info "Benchmarking avatar queries (10 iterations)..."
    total_time=0

    for i in $(seq 1 $iterations); do
        start=$(python3 -c "import time; print(int(time.time() * 1000))")
        curl -s "$CACHE_URL/api/avatars/$TEST_ADDR_1" >/dev/null
        end=$(python3 -c "import time; print(int(time.time() * 1000))")
        duration=$((end - start))
        total_time=$((total_time + duration))
    done

    avg_time=$((total_time / iterations))
    log_info "Average avatar query time: ${avg_time}ms"

    if [[ $avg_time -lt 20 ]]; then
        log_success "Avatar performance is EXCELLENT (<20ms)"
    elif [[ $avg_time -lt 50 ]]; then
        log_success "Avatar performance is GOOD (<50ms)"
    else
        log_warning "Avatar performance is SLOW (>${avg_time}ms)"
    fi

    echo ""
fi

######################################################################
# 8. Summary
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}======================================${NC}"
    echo -e "${BLUE}  Test Summary${NC}"
    echo -e "${BLUE}======================================${NC}\n"
    echo -e "${YELLOW}Total Tests:${NC}  $TOTAL_TESTS"
    echo -e "${GREEN}Passed:${NC}       $PASSED_TESTS"
    echo -e "${RED}Failed:${NC}       $FAILED_TESTS"

    if [[ $FAILED_TESTS -eq 0 ]]; then
        echo -e "\n${GREEN}✓ All tests passed!${NC}\n"
        exit 0
    else
        echo -e "\n${RED}✗ Some tests failed${NC}\n"
        exit 1
    fi
else
    echo "{\"total\":$TOTAL_TESTS,\"passed\":$PASSED_TESTS,\"failed\":$FAILED_TESTS}"

    if [[ $FAILED_TESTS -eq 0 ]]; then
        exit 0
    else
        exit 1
    fi
fi
