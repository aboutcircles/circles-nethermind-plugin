#!/usr/bin/env bash
#
# Test HTTP endpoints (non-RPC) for Circles services
# Tests: /profiles (profile-service), /pathfinder (pathfinder service)
#

set -euo pipefail

# Colors (disabled if not a terminal)
if [[ -t 1 ]]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[0;33m'
    BLUE='\033[0;34m'
    NC='\033[0m'
else
    RED='' GREEN='' YELLOW='' BLUE='' NC=''
fi

# Defaults
DEFAULT_URL="http://localhost"
VERBOSE=false
JSON_OUTPUT=false

# Test addresses (same as test-rpc.sh for consistency)
TEST_ADDR_1="0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0"
TEST_ADDR_2="0x42cEDde51198D1773590311E2A340DC06B24cB37"
TEST_ADDR_3="0xDE374ece6fA50e781E81Aac78e811b33D16912c7"

usage() {
    cat <<EOF
Usage: $(basename "$0") [URL] [OPTIONS]

Test HTTP endpoints for Circles services (profiles, pathfinder).

Arguments:
  URL                    Base URL to test (default: $DEFAULT_URL)

Options:
  -v, --verbose          Show full response bodies
  -j, --json             Output results as JSON
  -h, --help             Show this help message

Examples:
  $(basename "$0")                                       # Test localhost
  $(basename "$0") https://staging.circlesubi.network    # Test staging
  $(basename "$0") https://rpc.aboutcircles.com          # Test production
  $(basename "$0") -v                                    # Verbose output

Endpoints tested:
  /profiles/health           GET  - Profile service health check
  /profiles/search/addresses POST - Profile service address lookup
  /profiles/get              GET  - Profile get by CID (tests 400 for invalid)
  /pathfinder/snapshot       GET  - Pathfinder network snapshot
  /pathfinder/findMaxFlow    GET  - Pathfinder max flow calculation
EOF
    exit 0
}

# Parse arguments
BASE_URL="$DEFAULT_URL"
while [[ $# -gt 0 ]]; do
    case $1 in
        -v|--verbose) VERBOSE=true; shift ;;
        -j|--json) JSON_OUTPUT=true; shift ;;
        -h|--help) usage ;;
        -*) echo "Unknown option: $1"; usage ;;
        *) BASE_URL="$1"; shift ;;
    esac
done

# Remove trailing slash
BASE_URL="${BASE_URL%/}"

# Counters
PASSED=0
FAILED=0
SKIPPED=0

# Results array for JSON output
declare -a RESULTS=()

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_pass() { echo -e "${GREEN}[PASS]${NC} $1"; }
log_fail() { echo -e "${RED}[FAIL]${NC} $1"; }
log_skip() { echo -e "${YELLOW}[SKIP]${NC} $1"; }

# Run HTTP test
# Args: name, method, endpoint, expected_status, [data]
run_test() {
    local name="$1"
    local method="$2"
    local endpoint="$3"
    local expected_status="$4"
    local data="${5:-}"

    local url="${BASE_URL}${endpoint}"
    local curl_args=(-s -w "\n%{http_code}" -X "$method")

    if [[ -n "$data" ]]; then
        curl_args+=(-H "Content-Type: application/json" -d "$data")
    fi

    # Make request
    local response
    local status
    if response=$(curl "${curl_args[@]}" "$url" 2>/dev/null); then
        status=$(echo "$response" | tail -n1)
        response=$(echo "$response" | sed '$d')
    else
        status="000"
        response="Connection failed"
    fi

    # Check result
    local result="fail"
    if [[ "$status" == "$expected_status" ]]; then
        result="pass"
        ((PASSED++))
        if [[ "$JSON_OUTPUT" != "true" ]]; then
            log_pass "$name (HTTP $status)"
        fi
    elif [[ "$status" == "000" ]]; then
        result="skip"
        ((SKIPPED++))
        if [[ "$JSON_OUTPUT" != "true" ]]; then
            log_skip "$name - Connection failed"
        fi
    else
        result="fail"
        ((FAILED++))
        if [[ "$JSON_OUTPUT" != "true" ]]; then
            log_fail "$name (expected $expected_status, got $status)"
        fi
    fi

    # Show response if verbose
    if [[ "$VERBOSE" == "true" && "$JSON_OUTPUT" != "true" && -n "$response" ]]; then
        echo "$response" | head -20
        echo ""
    fi

    # Store result for JSON
    RESULTS+=("{\"name\":\"$name\",\"endpoint\":\"$endpoint\",\"method\":\"$method\",\"status\":$status,\"expected\":$expected_status,\"result\":\"$result\"}")
}

# Main
if [[ "$JSON_OUTPUT" != "true" ]]; then
    echo ""
    echo "============================================"
    echo "  Circles HTTP Endpoint Tests"
    echo "  Target: $BASE_URL"
    echo "============================================"
    echo ""
fi

# ─────────────────────────────────────────────────────────────
# Profile Service Tests (/profiles)
# Actual endpoints: /health, /get, /getBatch, /search, /search/addresses, /pin
# ─────────────────────────────────────────────────────────────
if [[ "$JSON_OUTPUT" != "true" ]]; then
    log_info "Testing Profile Service..."
fi

run_test "Profile health" \
    "GET" "/profiles/health" 200

run_test "Profile search by addresses" \
    "POST" "/profiles/search/addresses" 200 \
    "{\"addresses\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\"]}"

# Test with a valid CID (from a known profile)
# Note: This may return 500 if IPFS gateway is unavailable on staging - that's a service issue
# The profile-service needs IPFS_GATEWAY env var configured to fetch profile data
VALID_CID="QmaVs3ThCpa69JySWUxQcv86XfLRo6TndukDmi2BpnV79p"
run_test "Profile get by CID (valid)" \
    "GET" "/profiles/get?cid=$VALID_CID" 200

# Test with invalid CID returns 400
run_test "Profile get by CID (invalid)" \
    "GET" "/profiles/get?cid=invalid" 400

# ─────────────────────────────────────────────────────────────
# Pathfinder Tests (/pathfinder)
# Actual endpoints: /findMaxFlow, /findPath, /snapshot
# ─────────────────────────────────────────────────────────────
if [[ "$JSON_OUTPUT" != "true" ]]; then
    echo ""
    log_info "Testing Pathfinder Service..."
fi

run_test "Pathfinder snapshot" \
    "GET" "/pathfinder/snapshot" 200

# findMaxFlow requires from, to, and amount parameters
run_test "Pathfinder findMaxFlow" \
    "GET" "/pathfinder/findMaxFlow?from=$TEST_ADDR_1&to=$TEST_ADDR_2&amount=1000000000000000000" 200

# ─────────────────────────────────────────────────────────────
# Results
# ─────────────────────────────────────────────────────────────
if [[ "$JSON_OUTPUT" == "true" ]]; then
    # Join results array
    results_json=$(IFS=,; echo "${RESULTS[*]}")
    cat <<EOF
{
  "url": "$BASE_URL",
  "summary": {
    "passed": $PASSED,
    "failed": $FAILED,
    "skipped": $SKIPPED,
    "total": $((PASSED + FAILED + SKIPPED))
  },
  "tests": [$results_json]
}
EOF
else
    echo ""
    echo "============================================"
    echo -e "  Results: ${GREEN}$PASSED passed${NC}, ${RED}$FAILED failed${NC}, ${YELLOW}$SKIPPED skipped${NC}"
    echo "============================================"
    echo ""
fi

# Exit with failure if any tests failed
[[ $FAILED -eq 0 ]]
