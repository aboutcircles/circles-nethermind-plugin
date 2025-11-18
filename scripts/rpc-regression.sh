#!/usr/bin/env bash
set -e

# RPC Regression Testing Script
# Compares responses between two RPC endpoints (typically local vs production)
#
# This script runs all tests from test-rpc.sh against both endpoints and compares:
# - Basic RPC methods (getTotalBalance, getTokenBalances, etc.)
# - Query methods with complex filters
# - Advanced FilterPredicate features (GreaterThan, In, Conjunction, etc.)
# - Profile and avatar methods
# - Event queries with filters
#
# Usage:
#   ./rpc-regression.sh [LOCAL_URL] [REMOTE_URL]
#
# Examples:
#   ./rpc-regression.sh                                                    # Default: localhost vs production
#   ./rpc-regression.sh http://localhost:8081 https://rpc.aboutcircles.com # Custom URLs

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Parse arguments
LOCAL_URL="${1:-http://localhost:8081}"
REMOTE_URL="${2:-https://rpc.aboutcircles.com}"

# Output directory for test results
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/RegressionTestResults"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
RUN_DIR="$OUTPUT_DIR/$TIMESTAMP"

mkdir -p "$RUN_DIR"

LOCAL_OUTPUT="$RUN_DIR/local.json"
REMOTE_OUTPUT="$RUN_DIR/remote.json"
DIFF_OUTPUT="$RUN_DIR/diff.txt"
SUMMARY_OUTPUT="$RUN_DIR/summary.txt"

echo -e "${BLUE}=== RPC Regression Testing ===${NC}"
echo -e "${CYAN}Local URL:  $LOCAL_URL${NC}"
echo -e "${CYAN}Remote URL: $REMOTE_URL${NC}"
echo -e "${CYAN}Results:    $RUN_DIR${NC}\n"

# Step 1: Run tests against local
echo -e "${YELLOW}[1/4] Running tests against local endpoint...${NC}"
if ! ./scripts/test-rpc.sh "$LOCAL_URL" --json > "$LOCAL_OUTPUT" 2>&1; then
    echo -e "${RED}Error: Failed to run tests against local endpoint${NC}"
    echo -e "${RED}Check $LOCAL_OUTPUT for details${NC}"
    exit 1
fi
echo -e "${GREEN}✓ Local tests completed${NC}\n"

# Step 2: Run tests against remote
echo -e "${YELLOW}[2/4] Running tests against remote endpoint...${NC}"
if ! ./scripts/test-rpc.sh "$REMOTE_URL" --json > "$REMOTE_OUTPUT" 2>&1; then
    echo -e "${RED}Error: Failed to run tests against remote endpoint${NC}"
    echo -e "${RED}Check $REMOTE_OUTPUT for details${NC}"
    exit 1
fi
echo -e "${GREEN}✓ Remote tests completed${NC}\n"

# Step 3: Parse and compare results
echo -e "${YELLOW}[3/4] Analyzing differences...${NC}"

# Parse JSON line by line and compare
LOCAL_TESTS=$(grep -o '"test":"[^"]*"' "$LOCAL_OUTPUT" | cut -d'"' -f4 | sort)
REMOTE_TESTS=$(grep -o '"test":"[^"]*"' "$REMOTE_OUTPUT" | cut -d'"' -f4 | sort)

# Initialize counters
TOTAL_TESTS=0
MATCHING_TESTS=0
DIFFERENT_TESTS=0
MISSING_LOCAL=0
MISSING_REMOTE=0

# Create detailed diff output
echo "=== RPC Regression Test Results ===" > "$DIFF_OUTPUT"
echo "Timestamp: $(date)" >> "$DIFF_OUTPUT"
echo "Local URL:  $LOCAL_URL" >> "$DIFF_OUTPUT"
echo "Remote URL: $REMOTE_URL" >> "$DIFF_OUTPUT"
echo "" >> "$DIFF_OUTPUT"

# Function to extract response for a test
get_response() {
    local file=$1
    local test_name=$2
    grep -F -A 1 "\"test\":\"$test_name\"" "$file" | tail -n 1 | jq -c '.result // .error // .'
}

# Function to normalize response (remove dynamic fields)
normalize_response() {
    local response=$1
    # Remove timestamp and other potentially dynamic fields if needed
    # For now, just pass through - can add normalization rules later
    echo "$response"
}

# Compare each test
for test_name in $(echo "$LOCAL_TESTS" | sort -u); do
    TOTAL_TESTS=$((TOTAL_TESTS + 1))

    echo "--- Test: $test_name ---" >> "$DIFF_OUTPUT"

    # Check if test exists in remote
    if ! echo "$REMOTE_TESTS" | grep -q "^$test_name$"; then
        echo "MISSING IN REMOTE" >> "$DIFF_OUTPUT"
        echo "" >> "$DIFF_OUTPUT"
        MISSING_REMOTE=$((MISSING_REMOTE + 1))
        continue
    fi

    # Extract responses
    local_response=$(get_response "$LOCAL_OUTPUT" "$test_name")
    remote_response=$(get_response "$REMOTE_OUTPUT" "$test_name")

    # Normalize responses
    local_normalized=$(normalize_response "$local_response")
    remote_normalized=$(normalize_response "$remote_response")

    # Compare
    if [[ "$local_normalized" == "$remote_normalized" ]]; then
        echo "✓ MATCH" >> "$DIFF_OUTPUT"
        MATCHING_TESTS=$((MATCHING_TESTS + 1))
    else
        echo "✗ DIFFERENT" >> "$DIFF_OUTPUT"
        echo "" >> "$DIFF_OUTPUT"
        echo "Local response:" >> "$DIFF_OUTPUT"
        echo "$local_response" | jq '.' >> "$DIFF_OUTPUT" 2>&1 || echo "$local_response" >> "$DIFF_OUTPUT"
        echo "" >> "$DIFF_OUTPUT"
        echo "Remote response:" >> "$DIFF_OUTPUT"
        echo "$remote_response" | jq '.' >> "$DIFF_OUTPUT" 2>&1 || echo "$remote_response" >> "$DIFF_OUTPUT"
        echo "" >> "$DIFF_OUTPUT"
        echo "Diff:" >> "$DIFF_OUTPUT"
        diff <(echo "$local_response" | jq -S '.' 2>/dev/null || echo "$local_response") \
             <(echo "$remote_response" | jq -S '.' 2>/dev/null || echo "$remote_response") >> "$DIFF_OUTPUT" 2>&1 || true
        DIFFERENT_TESTS=$((DIFFERENT_TESTS + 1))
    fi
    echo "" >> "$DIFF_OUTPUT"
done

# Check for tests only in remote
for test_name in $(echo "$REMOTE_TESTS" | sort -u); do
    if ! echo "$LOCAL_TESTS" | grep -q "^$test_name$"; then
        echo "--- Test: $test_name ---" >> "$DIFF_OUTPUT"
        echo "MISSING IN LOCAL" >> "$DIFF_OUTPUT"
        echo "" >> "$DIFF_OUTPUT"
        MISSING_LOCAL=$((MISSING_LOCAL + 1))
    fi
done

echo -e "${GREEN}✓ Analysis completed${NC}\n"

# Step 4: Generate summary
echo -e "${YELLOW}[4/4] Generating summary...${NC}"

cat > "$SUMMARY_OUTPUT" << EOF
=== RPC Regression Test Summary ===
Timestamp: $(date)
Local URL:  $LOCAL_URL
Remote URL: $REMOTE_URL

Results:
  Total tests compared: $TOTAL_TESTS
  Matching responses:   $MATCHING_TESTS
  Different responses:  $DIFFERENT_TESTS
  Missing in remote:    $MISSING_REMOTE
  Missing in local:     $MISSING_LOCAL

Files:
  Local output:    $LOCAL_OUTPUT
  Remote output:   $REMOTE_OUTPUT
  Detailed diff:   $DIFF_OUTPUT
  This summary:    $SUMMARY_OUTPUT

EOF

# Print summary to console
echo ""
echo -e "${BLUE}=== Summary ===${NC}"
cat "$SUMMARY_OUTPUT"

# Print status
if [[ $DIFFERENT_TESTS -gt 0 ]] || [[ $MISSING_LOCAL -gt 0 ]] || [[ $MISSING_REMOTE -gt 0 ]]; then
    echo -e "${RED}⚠ Discrepancies found!${NC}"
    echo -e "${YELLOW}Review detailed diff: $DIFF_OUTPUT${NC}"
    exit 1
else
    echo -e "${GREEN}✓ All tests match!${NC}"
    exit 0
fi
