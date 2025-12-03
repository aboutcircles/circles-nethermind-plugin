#!/usr/bin/env bash
set -e

# Load testing script for RPC endpoints
# Tests performance with cache enabled vs disabled, varying concurrency and request counts
#
# Usage:
#   ./load-test-rpc.sh [OPTIONS]
#
# Options:
#   --rpc-url URL          RPC service URL (default: http://localhost:8081)
#   --cache-url URL        Cache service URL (default: http://localhost:3001)
#   --duration SECONDS     Duration of each test phase (default: 60)
#   --concurrency NUM      Number of concurrent requests (default: 10,50,100)
#   --output-dir DIR       Directory for results (default: LoadTestResults/TIMESTAMP)
#   --skip-cache-test      Skip cache-enabled tests
#   --skip-nocache-test    Skip cache-disabled tests
#
# Examples:
#   ./load-test-rpc.sh                           # Full test with defaults
#   ./load-test-rpc.sh --duration 30             # Shorter test
#   ./load-test-rpc.sh --concurrency 10,20,50    # Custom concurrency levels

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# Default settings
RPC_URL="http://localhost:8081"
CACHE_URL="http://localhost:3001"
DURATION=60
CONCURRENCY_LEVELS="10,50,100"
OUTPUT_DIR=""
SKIP_CACHE=false
SKIP_NOCACHE=false

# Test addresses
TEST_ADDR_1="0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0"
TEST_ADDR_2="0x42cEDde51198D1773590311E2A340DC06B24cB37"
TEST_ADDR_3="0xDE374ece6fA50e781E81Aac78e811b33D16912c7"

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --rpc-url)
            RPC_URL="$2"
            shift 2
            ;;
        --cache-url)
            CACHE_URL="$2"
            shift 2
            ;;
        --duration)
            DURATION="$2"
            shift 2
            ;;
        --concurrency)
            CONCURRENCY_LEVELS="$2"
            shift 2
            ;;
        --output-dir)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --skip-cache-test)
            SKIP_CACHE=true
            shift
            ;;
        --skip-nocache-test)
            SKIP_NOCACHE=true
            shift
            ;;
        --help)
            grep '^#' "$0" | grep -v '#!/usr/bin/env' | sed 's/^# //'
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Setup output directory
if [[ -z "$OUTPUT_DIR" ]]; then
    TIMESTAMP=$(date +"%Y%m%d_%H%M%S")
    OUTPUT_DIR="LoadTestResults/$TIMESTAMP"
fi

mkdir -p "$OUTPUT_DIR"

echo -e "${BLUE}=== Circles RPC Load Testing ===${NC}"
echo -e "RPC URL:         $RPC_URL"
echo -e "Cache URL:       $CACHE_URL"
echo -e "Duration:        ${DURATION}s per test"
echo -e "Concurrency:     $CONCURRENCY_LEVELS"
echo -e "Output:          $OUTPUT_DIR"
echo ""

# Check dependencies
if ! command -v ab &> /dev/null; then
    echo -e "${RED}Error: 'ab' (ApacheBench) not found. Install with:${NC}"
    echo "  macOS:  brew install apache2"
    echo "  Ubuntu: sudo apt-get install apache2-utils"
    exit 1
fi

if ! command -v jq &> /dev/null; then
    echo -e "${YELLOW}Warning: 'jq' not found. Installing...${NC}"
    if [[ "$OSTYPE" == "darwin"* ]]; then
        brew install jq
    else
        sudo apt-get install -y jq
    fi
fi

# Test endpoint availability
check_endpoint() {
    local url="$1"
    local name="$2"
    
    echo -n "Checking $name... "
    if curl -sf "$url" > /dev/null 2>&1 || curl -sf "$url/health" > /dev/null 2>&1; then
        echo -e "${GREEN}OK${NC}"
        return 0
    else
        echo -e "${RED}FAILED${NC}"
        return 1
    fi
}

check_endpoint "$RPC_URL" "RPC service" || exit 1

if ! $SKIP_CACHE; then
    check_endpoint "$CACHE_URL" "Cache service" || {
        echo -e "${YELLOW}Cache service not available, skipping cache tests${NC}"
        SKIP_CACHE=true
    }
fi

echo ""

# Create test payloads
create_payloads() {
    local dir="$1"
    mkdir -p "$dir"
    
    # Balance queries
    cat > "$dir/getTotalBalance_v1.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getTotalBalance","params":["$TEST_ADDR_1",1,true]}
EOF
    
    cat > "$dir/getTotalBalance_v2.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circlesV2_getTotalBalance","params":["$TEST_ADDR_1",2,true]}
EOF
    
    # Avatar info
    cat > "$dir/getAvatarInfo.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getAvatarInfo","params":["$TEST_ADDR_1"]}
EOF
    
    # Profile
    cat > "$dir/getProfileByAddress.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getProfileByAddress","params":["$TEST_ADDR_1"]}
EOF
    
    # SDK Enablement - Profile View
    cat > "$dir/getProfileView.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getProfileView","params":["$TEST_ADDR_1"]}
EOF
    
    # SDK Enablement - Trust Network Summary
    cat > "$dir/getTrustNetworkSummary.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getTrustNetworkSummary","params":["$TEST_ADDR_1",2]}
EOF
    
    # SDK Enablement - Aggregated Trust Relations
    cat > "$dir/getAggregatedTrustRelations.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getAggregatedTrustRelations","params":["$TEST_ADDR_1"]}
EOF
    
    # SDK Enablement - Valid Inviters
    cat > "$dir/getValidInviters.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getValidInviters","params":["$TEST_ADDR_1","50.0"]}
EOF
    
    # SDK Enablement - Transaction History Enriched
    cat > "$dir/getTransactionHistoryEnriched.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_getTransactionHistoryEnriched","params":["$TEST_ADDR_1",30282299,null,20]}
EOF
    
    # SDK Enablement - Search Profile
    cat > "$dir/searchProfileByAddressOrName.json" <<EOF
{"jsonrpc":"2.0","id":1,"method":"circles_searchProfileByAddressOrName","params":["berlin",10,0]}
EOF
}

# Run load test for a specific endpoint
run_endpoint_test() {
    local test_name="$1"
    local payload_file="$2"
    local concurrency="$3"
    local total_requests=$((DURATION * concurrency))
    local output_file="$4"
    
    echo -ne "  ├─ ${test_name} (c=$concurrency, n=$total_requests)... "
    
    # Use ab (ApacheBench) for load testing
    ab -c "$concurrency" \
       -n "$total_requests" \
       -t "$DURATION" \
       -p "$payload_file" \
       -T "application/json" \
       -g "$output_file.tsv" \
       "$RPC_URL/" > "$output_file.txt" 2>&1
    
    # Extract key metrics
    local rps=$(grep "Requests per second" "$output_file.txt" | awk '{print $4}')
    local mean_time=$(grep "Time per request.*mean\)" "$output_file.txt" | head -1 | awk '{print $4}')
    local p95_time=$(grep "95%" "$output_file.txt" | awk '{print $2}')
    
    echo -e "${GREEN}✓${NC} RPS: $rps, Mean: ${mean_time}ms, P95: ${p95_time}ms"
    
    # Write summary JSON
    cat > "$output_file.json" <<EOF
{
  "test_name": "$test_name",
  "concurrency": $concurrency,
  "total_requests": $total_requests,
  "duration": $DURATION,
  "requests_per_second": $rps,
  "mean_time_ms": $mean_time,
  "p95_time_ms": $p95_time
}
EOF
}

# Run test suite for all endpoints
run_test_suite() {
    local mode="$1"
    local output_subdir="$2"
    
    echo -e "${BLUE}=== Testing with $mode ===${NC}"
    
    local payloads_dir="$output_subdir/payloads"
    create_payloads "$payloads_dir"
    
    IFS=',' read -ra LEVELS <<< "$CONCURRENCY_LEVELS"
    
    for level in "${LEVELS[@]}"; do
        echo -e "${YELLOW}Concurrency level: $level${NC}"
        
        run_endpoint_test "getTotalBalance_v1" "$payloads_dir/getTotalBalance_v1.json" "$level" "$output_subdir/c${level}_getTotalBalance_v1"
        run_endpoint_test "getTotalBalance_v2" "$payloads_dir/getTotalBalance_v2.json" "$level" "$output_subdir/c${level}_getTotalBalance_v2"
        run_endpoint_test "getAvatarInfo" "$payloads_dir/getAvatarInfo.json" "$level" "$output_subdir/c${level}_getAvatarInfo"
        run_endpoint_test "getProfileByAddress" "$payloads_dir/getProfileByAddress.json" "$level" "$output_subdir/c${level}_getProfileByAddress"
        run_endpoint_test "getProfileView" "$payloads_dir/getProfileView.json" "$level" "$output_subdir/c${level}_getProfileView"
        run_endpoint_test "getTrustNetworkSummary" "$payloads_dir/getTrustNetworkSummary.json" "$level" "$output_subdir/c${level}_getTrustNetworkSummary"
        run_endpoint_test "getAggregatedTrustRelations" "$payloads_dir/getAggregatedTrustRelations.json" "$level" "$output_subdir/c${level}_getAggregatedTrustRelations"
        run_endpoint_test "getValidInviters" "$payloads_dir/getValidInviters.json" "$level" "$output_subdir/c${level}_getValidInviters"
        run_endpoint_test "getTransactionHistoryEnriched" "$payloads_dir/getTransactionHistoryEnriched.json" "$level" "$output_subdir/c${level}_getTransactionHistoryEnriched"
        run_endpoint_test "searchProfileByAddressOrName" "$payloads_dir/searchProfileByAddressOrName.json" "$level" "$output_subdir/c${level}_searchProfileByAddressOrName"
        
        echo ""
    done
}

# Check cache service readiness
check_cache_ready() {
    local status
    status=$(curl -s "$CACHE_URL/ready" | jq -r '.status' 2>/dev/null || echo "error")
    
    if [[ "$status" == "ready" ]]; then
        return 0
    else
        return 1
    fi
}

# Run tests
if ! $SKIP_NOCACHE; then
    echo -e "${YELLOW}Phase 1: Testing without cache (database mode)${NC}"
    echo "Please ensure RPC is configured with USE_CACHE_SERVICE=false"
    read -p "Press Enter to continue..."
    run_test_suite "Database (No Cache)" "$OUTPUT_DIR/nocache"
fi

if ! $SKIP_CACHE; then
    echo ""
    echo -e "${YELLOW}Phase 2: Testing with cache enabled${NC}"
    echo "Please ensure:"
    echo "  1. Cache service is running and warmed up"
    echo "  2. RPC is configured with USE_CACHE_SERVICE=true"
    echo "  3. Cache service URL is set correctly"
    read -p "Press Enter to continue..."
    
    if check_cache_ready; then
        echo -e "${GREEN}Cache service is ready!${NC}"
        run_test_suite "Cache Enabled" "$OUTPUT_DIR/cache"
    else
        echo -e "${RED}Cache service not ready. Skipping cache tests.${NC}"
    fi
fi

# Generate comparison report
echo ""
echo -e "${BLUE}=== Generating Comparison Report ===${NC}"

cat > "$OUTPUT_DIR/report.md" <<'EOFMD'
# Circles RPC Load Test Results

## Test Configuration

- **RPC URL**: `RPC_URL_PLACEHOLDER`
- **Cache URL**: `CACHE_URL_PLACEHOLDER`
- **Duration**: DURATION_PLACEHOLDER seconds per test
- **Concurrency Levels**: CONCURRENCY_PLACEHOLDER
- **Timestamp**: TIMESTAMP_PLACEHOLDER

## Results Summary

### Performance Comparison: Cache vs Database

| Endpoint | Concurrency | No Cache RPS | Cache RPS | Speedup | No Cache P95 | Cache P95 | Improvement |
|----------|-------------|--------------|-----------|---------|--------------|-----------|-------------|
RESULTS_TABLE_PLACEHOLDER

## Detailed Metrics

### Without Cache (Database Mode)

NOCACHE_DETAILS_PLACEHOLDER

### With Cache Enabled

CACHE_DETAILS_PLACEHOLDER

## Conclusions

CONCLUSIONS_PLACEHOLDER

## Raw Data

- No Cache Results: `nocache/`
- Cache Results: `cache/`
- TSV Data: `*.tsv` files for gnuplot graphing
- JSON Summaries: `*.json` files for programmatic analysis

EOFMD

# Populate report
sed -i '' "s|RPC_URL_PLACEHOLDER|$RPC_URL|g" "$OUTPUT_DIR/report.md"
sed -i '' "s|CACHE_URL_PLACEHOLDER|$CACHE_URL|g" "$OUTPUT_DIR/report.md"
sed -i '' "s|DURATION_PLACEHOLDER|$DURATION|g" "$OUTPUT_DIR/report.md"
sed -i '' "s|CONCURRENCY_PLACEHOLDER|$CONCURRENCY_LEVELS|g" "$OUTPUT_DIR/report.md"
sed -i '' "s|TIMESTAMP_PLACEHOLDER|$(date)|g" "$OUTPUT_DIR/report.md"

# Build results table
results_table=""
IFS=',' read -ra LEVELS <<< "$CONCURRENCY_LEVELS"
for level in "${LEVELS[@]}"; do
    for endpoint in getTotalBalance_v1 getTotalBalance_v2 getAvatarInfo getProfileByAddress getProfileView getTrustNetworkSummary; do
        if [[ -f "$OUTPUT_DIR/nocache/c${level}_${endpoint}.json" && -f "$OUTPUT_DIR/cache/c${level}_${endpoint}.json" ]]; then
            nocache_rps=$(jq -r '.requests_per_second' "$OUTPUT_DIR/nocache/c${level}_${endpoint}.json")
            cache_rps=$(jq -r '.requests_per_second' "$OUTPUT_DIR/cache/c${level}_${endpoint}.json")
            nocache_p95=$(jq -r '.p95_time_ms' "$OUTPUT_DIR/nocache/c${level}_${endpoint}.json")
            cache_p95=$(jq -r '.p95_time_ms' "$OUTPUT_DIR/cache/c${level}_${endpoint}.json")
            
            speedup=$(echo "scale=2; $cache_rps / $nocache_rps" | bc)
            improvement=$(echo "scale=1; ($nocache_p95 - $cache_p95) / $nocache_p95 * 100" | bc)
            
            results_table+="| $endpoint | $level | $nocache_rps | $cache_rps | ${speedup}x | ${nocache_p95}ms | ${cache_p95}ms | ${improvement}% |\n"
        fi
    done
done

sed -i '' "s|RESULTS_TABLE_PLACEHOLDER|$results_table|g" "$OUTPUT_DIR/report.md"

echo -e "${GREEN}✓ Load test complete!${NC}"
echo -e "Results saved to: ${BLUE}$OUTPUT_DIR${NC}"
echo -e "Report: ${BLUE}$OUTPUT_DIR/report.md${NC}"
echo ""
echo "View report:"
echo "  cat $OUTPUT_DIR/report.md"
echo ""
echo "Analyze with gnuplot:"
echo "  gnuplot> plot '$OUTPUT_DIR/nocache/c10_getTotalBalance_v1.tsv' using 9 with lines title 'No Cache'"
echo "  gnuplot> replot '$OUTPUT_DIR/cache/c10_getTotalBalance_v1.tsv' using 9 with lines title 'Cache'"
