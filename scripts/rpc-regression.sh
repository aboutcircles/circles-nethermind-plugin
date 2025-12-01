#!/usr/bin/env bash
set -e

# RPC Regression Testing Script V2
# Enhanced version with parallel execution, method-specific tolerances, and normalization
# Compatible with Bash 3.2+ (macOS default)
#
# Features:
# - Parallel test execution to minimize timing differences
# - Method-specific tolerance rules
# - Dynamic field normalization
# - Expected failures configuration
# - Detailed reporting with categorization
#
# Usage:
#   ./rpc-regression-v2.sh [LOCAL_URL] [REMOTE_URL] [--config CONFIG_FILE]
#
# Examples:
#   ./rpc-regression-v2.sh                                                    # Default: localhost vs production
#   ./rpc-regression-v2.sh http://localhost:8081 https://rpc.aboutcircles.com # Custom URLs
#   ./rpc-regression-v2.sh http://localhost:8081 https://rpc.aboutcircles.com --config custom-config.json

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
NC='\033[0m' # No Color

# Parse arguments
LOCAL_URL="${1:-http://localhost:8081}"
REMOTE_URL="${2:-https://rpc.aboutcircles.com}"
CONFIG_FILE=""

# Check if --config flag is present
if [[ "$3" == "--config" ]]; then
    CONFIG_FILE="$4"
elif [[ "$2" == "--config" ]]; then
    CONFIG_FILE="$3"
    REMOTE_URL="https://rpc.aboutcircles.com"
fi

# Output directory for test results
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/RegressionTestResults"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
RUN_DIR="$OUTPUT_DIR/$TIMESTAMP"

# Set default config file if not provided
if [[ -z "$CONFIG_FILE" ]]; then
    CONFIG_FILE="$SCRIPT_DIR/rpc-regression-config.json"
fi

mkdir -p "$RUN_DIR"

LOCAL_OUTPUT_DIR="$RUN_DIR/local"
REMOTE_OUTPUT_DIR="$RUN_DIR/remote"
LOCAL_MANIFEST="$LOCAL_OUTPUT_DIR/manifest.json"
REMOTE_MANIFEST="$REMOTE_OUTPUT_DIR/manifest.json"
LOCAL_LOG="$RUN_DIR/local-run.log"
REMOTE_LOG="$RUN_DIR/remote-run.log"
DIFF_OUTPUT="$RUN_DIR/diff.txt"
SUMMARY_OUTPUT="$RUN_DIR/summary.txt"
METHODS_OUTPUT="$RUN_DIR/methods.txt"
NORMALIZED_OUTPUT="$RUN_DIR/normalized.txt"
TEMP_CONFIG="$RUN_DIR/parsed_config.txt"
CATEGORY_DIFF_DIR="$RUN_DIR/category-diffs"

mkdir -p "$CATEGORY_DIFF_DIR"

# Default configuration (can be overridden by config file)
DEFAULT_TOLERANCE=0.001
BALANCE_TOLERANCE=0.1  # 0.1% for balance-related methods
TIMESTAMP_TOLERANCE=5.0  # 5% for timestamp-sensitive data

# Method-specific tolerance rules (method:tolerance pairs)
METHOD_TOLERANCES="
circles_getTotalBalance:1.0
circlesV2_getTotalBalance:1.0
circles_getTokenBalances:1.0
circlesV2_findPath:1.0
"

# Fields to normalize (remove before comparison)
NORMALIZE_FIELDS="
timestamp
blockNumber
transactionIndex
logIndex
transactionHash
"

# Expected failures (tests that are known to differ)
EXPECTED_FAILURES="
"

# Function to get tolerance for a specific method from the list
get_method_tolerance() {
    local test_name=$1
    local tolerance=""

    # Check each method tolerance rule
    while IFS=: read -r method tol; do
        [[ -z "$method" ]] && continue
        if [[ "$test_name" == *"$method"* ]]; then
            tolerance="$tol"
            break
        fi
    done <<< "$METHOD_TOLERANCES"

    # If no specific tolerance found, check if it's balance-related
    if [[ -z "$tolerance" ]]; then
        if [[ "$test_name" == *"Balance"* ]] || [[ "$test_name" == *"balance"* ]]; then
            tolerance="$BALANCE_TOLERANCE"
        else
            tolerance="$DEFAULT_TOLERANCE"
        fi
    fi

    echo "$tolerance"
}

# Function to check if test is in expected failures list
is_expected_failure() {
    local test_name=$1
    while read -r expected; do
        [[ -z "$expected" ]] && continue
        if [[ "$test_name" == "$expected" ]]; then
            return 0
        fi
    done <<< "$EXPECTED_FAILURES"
    return 1
}

# Load configuration file if provided
if [[ -n "$CONFIG_FILE" ]] && [[ -f "$CONFIG_FILE" ]]; then
    echo -e "${CYAN}Loading configuration from: $CONFIG_FILE${NC}"

    # Parse JSON config and update settings
    if command -v jq &> /dev/null; then
        # Update default tolerance
        new_default=$(jq -r '.defaultTolerance // empty' "$CONFIG_FILE" 2>/dev/null)
        [[ -n "$new_default" ]] && DEFAULT_TOLERANCE="$new_default"

        # Update method-specific tolerances
        METHOD_TOLERANCES=$(jq -r '.methodTolerances // {} | to_entries | .[] | "\(.key):\(.value)"' "$CONFIG_FILE" 2>/dev/null | tr '\n' '\n')

        # Update normalize fields
        NORMALIZE_FIELDS=$(jq -r '.normalizeFields[]? // empty' "$CONFIG_FILE" 2>/dev/null | tr '\n' '\n')

        # Update expected failures
        EXPECTED_FAILURES=$(jq -r '.expectedFailures[]? // empty' "$CONFIG_FILE" 2>/dev/null | tr '\n' '\n')
    else
        echo -e "${YELLOW}Warning: jq not found, using default configuration${NC}"
    fi
fi

echo -e "${BLUE}=== RPC Regression Testing V2 ===${NC}"
echo -e "${CYAN}Local URL:           $LOCAL_URL${NC}"
echo -e "${CYAN}Remote URL:          $REMOTE_URL${NC}"
echo -e "${CYAN}Default Tolerance:   ${DEFAULT_TOLERANCE}%${NC}"
echo -e "${CYAN}Balance Tolerance:   ${BALANCE_TOLERANCE}%${NC}"
echo -e "${CYAN}Results:             $RUN_DIR${NC}\n"

# Step 1: Run tests in parallel
echo -e "${YELLOW}[1/4] Running tests in parallel...${NC}"

mkdir -p "$LOCAL_OUTPUT_DIR" "$REMOTE_OUTPUT_DIR"

# Start both test runs simultaneously
(
    if ! "$SCRIPT_DIR/test-rpc.sh" "$LOCAL_URL" --json --json-dir "$LOCAL_OUTPUT_DIR" > "$LOCAL_LOG" 2>&1; then
        echo -e "${RED}Error: Failed to run tests against local endpoint${NC}" >&2
        exit 1
    fi
) &
LOCAL_PID=$!

(
    if ! "$SCRIPT_DIR/test-rpc.sh" "$REMOTE_URL" --json --json-dir "$REMOTE_OUTPUT_DIR" > "$REMOTE_LOG" 2>&1; then
        echo -e "${RED}Error: Failed to run tests against remote endpoint${NC}" >&2
        exit 1
    fi
) &
REMOTE_PID=$!

# Wait for both to complete
wait $LOCAL_PID
LOCAL_EXIT=$?
wait $REMOTE_PID
REMOTE_EXIT=$?

if [[ $LOCAL_EXIT -ne 0 ]]; then
    echo -e "${RED}Error: Failed to run tests against local endpoint${NC}"
    echo -e "${RED}Check $LOCAL_LOG for details${NC}"
    exit 1
fi

if [[ $REMOTE_EXIT -ne 0 ]]; then
    echo -e "${RED}Error: Failed to run tests against remote endpoint${NC}"
    echo -e "${RED}Check $REMOTE_LOG for details${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Parallel tests completed${NC}\n"

# Step 2: Parse test results
echo -e "${YELLOW}[2/4] Parsing test results...${NC}"

if [[ ! -f "$LOCAL_MANIFEST" ]]; then
    echo -e "${RED}Error: Missing manifest at $LOCAL_MANIFEST${NC}"
    echo -e "${YELLOW}Check $LOCAL_LOG for details.${NC}"
    exit 1
fi

if [[ ! -f "$REMOTE_MANIFEST" ]]; then
    echo -e "${RED}Error: Missing manifest at $REMOTE_MANIFEST${NC}"
    echo -e "${YELLOW}Check $REMOTE_LOG for details.${NC}"
    exit 1
fi

if ! diff -q "$LOCAL_MANIFEST" "$REMOTE_MANIFEST" >/dev/null 2>&1; then
    echo -e "${YELLOW}Warning: Manifests differ between local and remote; using local ordering.${NC}"
fi

CATEGORY_JSON=()
CATEGORY_JSON_RAW=$(jq -c '.categories[]' "$LOCAL_MANIFEST" 2>/dev/null || true)
if [[ -z "$CATEGORY_JSON_RAW" ]]; then
    echo -e "${RED}Error: No categories found in $LOCAL_MANIFEST${NC}"
    exit 1
fi

while IFS= read -r category_line; do
    [[ -z "$category_line" ]] && continue
    CATEGORY_JSON+=("$category_line")
done <<< "$CATEGORY_JSON_RAW"

declare -a CATEGORY_KEYS_ORDERED
declare -a CATEGORY_FILES
declare -a CATEGORY_LABELS
declare -a CATEGORY_DIFF_FILES

idx=0
for entry in "${CATEGORY_JSON[@]}"; do
    key=$(echo "$entry" | jq -r '.key')
    file=$(echo "$entry" | jq -r '.file')
    label=$(echo "$entry" | jq -r '.label')
    CATEGORY_KEYS_ORDERED+=("$key")
    CATEGORY_FILES+=("$file")
    CATEGORY_LABELS+=("$label")
    diff_file=$(printf '%s/%02d-%s.diff.txt' "$CATEGORY_DIFF_DIR" "$idx" "$key")
    CATEGORY_DIFF_FILES+=("$diff_file")
    : > "$diff_file"
    touch "$LOCAL_OUTPUT_DIR/$file" "$REMOTE_OUTPUT_DIR/$file"
    idx=$((idx + 1))
done

# Initialize counters
TOTAL_TESTS=0
MATCHING_TESTS=0
DIFFERENT_TESTS=0
NUMERIC_TOLERANCE_MATCHES=0
MISSING_LOCAL=0
MISSING_REMOTE=0
EXPECTED_FAILURE_COUNT=0

# Temporary files for storing test lists
TESTS_WITH_DIFFERENCES_FILE="$RUN_DIR/tests_different.tmp"
TESTS_MISSING_LOCAL_FILE="$RUN_DIR/tests_missing_local.tmp"
TESTS_MISSING_REMOTE_FILE="$RUN_DIR/tests_missing_remote.tmp"
TESTS_MATCH_FILE="$RUN_DIR/tests_match.tmp"
TESTS_EXPECTED_FAILURE_FILE="$RUN_DIR/tests_expected.tmp"

: > "$TESTS_WITH_DIFFERENCES_FILE"
: > "$TESTS_MISSING_LOCAL_FILE"
: > "$TESTS_MISSING_REMOTE_FILE"
: > "$TESTS_MATCH_FILE"
: > "$TESTS_EXPECTED_FAILURE_FILE"

echo -e "${GREEN}✓ Parsing completed (${#CATEGORY_KEYS_ORDERED[@]} categories)${NC}\n"

# Step 3: Analyze differences
echo -e "${YELLOW}[3/4] Analyzing differences with normalization...${NC}"

# Create output files
{
    echo "=== RPC Regression Test Results V2 ==="
    echo "Timestamp: $(date)"
    echo "Local URL:  $LOCAL_URL"
    echo "Remote URL: $REMOTE_URL"
    echo "Default Tolerance: ${DEFAULT_TOLERANCE}%"
    echo ""
} > "$DIFF_OUTPUT"

{
    echo "=== RPC Methods Comparison ==="
    echo "Timestamp: $(date)"
    echo ""
} > "$METHODS_OUTPUT"

{
    echo "=== Normalized Responses ==="
    echo "Timestamp: $(date)"
    echo ""
} > "$NORMALIZED_OUTPUT"

# Function to extract response for a test
get_response() {
    local file=$1
    local test_name=$2
    jq -c --arg test "$test_name" 'select(.test == $test) | .response' "$file" 2>/dev/null | head -n 1
}

# Function to normalize response (remove dynamic fields)
normalize_response() {
    local response=$1
    local normalized="$response"

    # Remove each normalize field using jq
    while read -r field; do
        [[ -z "$field" ]] && continue
        normalized=$(echo "$normalized" | jq --arg field "$field" 'walk(if type == "object" then del(.[$field]) else . end)' 2>/dev/null || echo "$normalized")
    done <<< "$NORMALIZE_FIELDS"

    # Normalize Int/BigInt type differences in circles_tables responses
    # This normalizes {"type": "Int"} to {"type": "BigInt"} for comparison
    # since both are semantically acceptable for bigint database columns
    normalized=$(echo "$normalized" | jq 'walk(if type == "object" and has("type") and .type == "Int" then .type = "BigInt" else . end)' 2>/dev/null || echo "$normalized")

    # Sort arrays in result to handle ordering differences (e.g. namespaces in circles_tables)
    normalized=$(echo "$normalized" | jq 'if .result | type == "array" then .result |= sort_by(.namespace // .column // .) else . end' 2>/dev/null || echo "$normalized")

    echo "$normalized"
}

# Function to compare numeric values with tolerance
numeric_compare() {
    local val1=$1
    local val2=$2
    local tolerance=$3

    # Check if both are numbers (including scientific notation)
    if ! [[ "$val1" =~ ^[0-9.eE+-]+$ ]] || ! [[ "$val2" =~ ^[0-9.eE+-]+$ ]]; then
        return 1
    fi

    # For very large integers (>15 digits), use awk instead of bc to avoid precision issues
    local val1_len=${#val1}
    local val2_len=${#val2}
    local use_awk=0

    if [[ $val1_len -gt 15 ]] || [[ $val2_len -gt 15 ]]; then
        use_awk=1
    fi

    # Use python for large numbers (handles arbitrary precision)
    if [[ $use_awk -eq 1 ]] || ! command -v bc &> /dev/null; then
        python3 -c "
from decimal import Decimal
v1 = Decimal('$val1')
v2 = Decimal('$val2')
tol = Decimal('$tolerance')
if v1 == 0 and v2 == 0:
    exit(0)
if v1 == 0 or v2 == 0:
    exit(1)
diff = abs(v1 - v2)
avg = (v1 + v2) / 2
percent = (diff / avg) * 100
exit(0 if percent < tol else 1)
" 2>/dev/null || return 1
    else
        # Use bc for smaller numbers and floating point
        if (( $(echo "$val1 == 0" | bc -l 2>/dev/null || echo 0) )) && (( $(echo "$val2 == 0" | bc -l 2>/dev/null || echo 0) )); then
            return 0
        fi

        # Avoid division by zero
        if (( $(echo "$val1 == 0 || $val2 == 0" | bc -l 2>/dev/null || echo 1) )); then
            return 1
        fi

        # Use bc for floating point comparison
        local diff=$(echo "scale=20; if ($val1 > $val2) $val1 - $val2 else $val2 - $val1" | bc -l 2>/dev/null || echo "999")
        local avg=$(echo "scale=20; ($val1 + $val2) / 2" | bc -l 2>/dev/null || echo "1")
        local percent_diff=$(echo "scale=10; ($diff / $avg) * 100" | bc -l 2>/dev/null || echo "100")

        # Compare with tolerance
        local within_tolerance=$(echo "$percent_diff < $tolerance" | bc -l 2>/dev/null || echo "0")
        [[ "$within_tolerance" == "1" ]]
    fi
}

# Function to extract all numeric values from JSON
extract_numbers() {
    local json=$1
    # Use jq to recursively extract all numeric values (handles large integers correctly)
    echo "$json" | jq -r '.. | numbers' 2>/dev/null | head -n 100
}

# Function to compare responses with normalization and tolerance
compare_responses() {
    local resp1=$1
    local resp2=$2
    local tolerance=$3

    # Normalize both responses
    local norm1=$(normalize_response "$resp1")
    local norm2=$(normalize_response "$resp2")

    # First try exact match on normalized
    # Use jq -S to sort keys for semantic comparison
    local sorted1=$(echo "$norm1" | jq -S -c '.' 2>/dev/null || echo "$norm1")
    local sorted2=$(echo "$norm2" | jq -S -c '.' 2>/dev/null || echo "$norm2")

    if [[ "$sorted1" == "$sorted2" ]]; then
        echo "exact"
        return
    fi

    # Check if responses contain error
    if echo "$resp1" | jq -e '.error' >/dev/null 2>&1 || echo "$resp2" | jq -e '.error' >/dev/null 2>&1; then
        echo "different"
        return
    fi

    # Extract all numeric values
    local nums1=$(extract_numbers "$norm1")
    local nums2=$(extract_numbers "$norm2")

    if [[ -z "$nums1" ]] || [[ -z "$nums2" ]]; then
        echo "different"
        return
    fi

    # Convert to arrays
    local -a arr1
    local -a arr2
    local i=0
    while read -r num; do
        arr1[$i]="$num"
        i=$((i + 1))
    done <<< "$nums1"

    i=0
    while read -r num; do
        arr2[$i]="$num"
        i=$((i + 1))
    done <<< "$nums2"

    # Check if same number of numeric values
    if [[ ${#arr1[@]} -ne ${#arr2[@]} ]]; then
        echo "different"
        return
    fi

    # Compare all numeric values with tolerance
    local all_match=true
    for ((i=0; i<${#arr1[@]}; i++)); do
        if ! numeric_compare "${arr1[$i]}" "${arr2[$i]}" "$tolerance"; then
            all_match=false
            break
        fi
    done

    if $all_match && [[ ${#arr1[@]} -gt 0 ]]; then
        echo "tolerance"
    else
        echo "different"
    fi
}

for idx in "${!CATEGORY_KEYS_ORDERED[@]}"; do
    key="${CATEGORY_KEYS_ORDERED[$idx]}"
    label="${CATEGORY_LABELS[$idx]}"
    local_file="$LOCAL_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"
    remote_file="$REMOTE_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"
    category_diff_file="${CATEGORY_DIFF_FILES[$idx]}"

    if [[ -s "$local_file" ]]; then
        local_tests=$(jq -r '.test' "$local_file" 2>/dev/null | sort -u)
    else
        local_tests=""
    fi

    if [[ -s "$remote_file" ]]; then
        remote_tests=$(jq -r '.test' "$remote_file" 2>/dev/null | sort -u)
    else
        remote_tests=""
    fi

    cat_all_tests=$(echo -e "$local_tests\n$remote_tests" | sort -u)
    cat_total=$(echo "$cat_all_tests" | grep -c . || echo 0)

    echo -e "${CYAN}Analyzing ${label} (${key}) - ${cat_total} tests${NC}"

    if [[ $cat_total -eq 0 ]]; then
        continue
    fi

    while IFS= read -r test_name; do
        [[ -z "$test_name" ]] && continue

        TOTAL_TESTS=$((TOTAL_TESTS + 1))

        # Check if test exists in both
        if [[ -n "$local_tests" ]]; then
            local_exists=$(echo "$local_tests" | grep -Fx "$test_name" || true)
        else
            local_exists=""
        fi

        if [[ -n "$remote_tests" ]]; then
            remote_exists=$(echo "$remote_tests" | grep -Fx "$test_name" || true)
        else
            remote_exists=""
        fi

        if [[ -z "$local_exists" ]]; then
            echo "$test_name" >> "$TESTS_MISSING_LOCAL_FILE"
            MISSING_LOCAL=$((MISSING_LOCAL + 1))
            continue
        fi

        if [[ -z "$remote_exists" ]]; then
            echo "$test_name" >> "$TESTS_MISSING_REMOTE_FILE"
            MISSING_REMOTE=$((MISSING_REMOTE + 1))
            continue
        fi

        # Extract responses
        local_response=$(get_response "$local_file" "$test_name")
        remote_response=$(get_response "$remote_file" "$test_name")

    # Get tolerance for this specific test
    tolerance=$(get_method_tolerance "$test_name")

    # Check for errors
    local_error=$(echo "$local_response" | jq -r '.error.message // empty' 2>/dev/null)
    remote_error=$(echo "$remote_response" | jq -r '.error.message // empty' 2>/dev/null)

    if [[ -n "$local_error" ]] || [[ -n "$remote_error" ]]; then
        if [[ "$local_error" != "$remote_error" ]]; then
            if is_expected_failure "$test_name"; then
                echo "$test_name" >> "$TESTS_EXPECTED_FAILURE_FILE"
                EXPECTED_FAILURE_COUNT=$((EXPECTED_FAILURE_COUNT + 1))
            else
                echo "$test_name" >> "$TESTS_WITH_DIFFERENCES_FILE"
                DIFFERENT_TESTS=$((DIFFERENT_TESTS + 1))
            fi

            {
                echo "--- Test: $test_name ---"
                echo "Tolerance: ${tolerance}%"
                if is_expected_failure "$test_name"; then
                    echo "⚠ EXPECTED FAILURE - ERROR MISMATCH"
                else
                    echo "✗ ERROR MISMATCH"
                fi
                echo "Local error: $local_error"
                echo "Remote error: $remote_error"
                echo ""
            } | tee -a "$category_diff_file" >> "$DIFF_OUTPUT"
        else
            echo "$test_name" >> "$TESTS_MATCH_FILE"
            MATCHING_TESTS=$((MATCHING_TESTS + 1))
        fi
        continue
    fi

    # Compare responses
    comparison=$(compare_responses "$local_response" "$remote_response" "$tolerance")

    case "$comparison" in
        exact)
            echo "$test_name" >> "$TESTS_MATCH_FILE"
            MATCHING_TESTS=$((MATCHING_TESTS + 1))
            ;;
        tolerance)
            echo "$test_name" >> "$TESTS_MATCH_FILE"
            NUMERIC_TOLERANCE_MATCHES=$((NUMERIC_TOLERANCE_MATCHES + 1))
            MATCHING_TESTS=$((MATCHING_TESTS + 1))
            ;;
        different)
            if is_expected_failure "$test_name"; then
                echo "$test_name" >> "$TESTS_EXPECTED_FAILURE_FILE"
                EXPECTED_FAILURE_COUNT=$((EXPECTED_FAILURE_COUNT + 1))
            else
                echo "$test_name" >> "$TESTS_WITH_DIFFERENCES_FILE"
                DIFFERENT_TESTS=$((DIFFERENT_TESTS + 1))
            fi

            {
                echo "--- Test: $test_name ---"
                echo "Tolerance: ${tolerance}%"
                if is_expected_failure "$test_name"; then
                    echo "⚠ EXPECTED FAILURE"
                else
                    echo "✗ DIFFERENT"
                fi
                echo ""
                echo "Local response:"
                echo "$local_response" | jq '.' 2>&1 || echo "$local_response"
                echo ""
                echo "Remote response:"
                echo "$remote_response" | jq '.' 2>&1 || echo "$remote_response"
                echo ""
                echo "Normalized comparison:"
                norm1=$(normalize_response "$local_response")
                norm2=$(normalize_response "$remote_response")
                echo "Local (normalized):"
                echo "$norm1" | jq '.' 2>&1 || echo "$norm1"
                echo ""
                echo "Remote (normalized):"
                echo "$norm2" | jq '.' 2>&1 || echo "$norm2"
                echo ""
                echo "Diff:"
                diff <(echo "$norm1" | jq -S '.' 2>/dev/null || echo "$norm1") \
                     <(echo "$norm2" | jq -S '.' 2>/dev/null || echo "$norm2") 2>&1 || true
                echo ""
            } | tee -a "$category_diff_file" >> "$DIFF_OUTPUT"
            ;;
    esac
    done <<< "$cat_all_tests"
done

echo -e "${GREEN}✓ Analysis completed (${TOTAL_TESTS} tests)${NC}\n"

# Step 4: Generate reports
echo -e "${YELLOW}[4/4] Generating reports...${NC}"

# Write methods comparison
{
    echo "=== Methods Available in BOTH ==="
    echo "Total: $((TOTAL_TESTS - MISSING_LOCAL - MISSING_REMOTE))"
    echo ""
    echo "Matching (${MATCHING_TESTS}):"
    while read -r test; do
        [[ -z "$test" ]] && continue
        tolerance=$(get_method_tolerance "$test")
        echo "  ✓ $test (tolerance: ${tolerance}%)"
    done < "$TESTS_MATCH_FILE"
    echo ""
    echo "Different (${DIFFERENT_TESTS}):"
    while read -r test; do
        [[ -z "$test" ]] && continue
        tolerance=$(get_method_tolerance "$test")
        echo "  ✗ $test (tolerance: ${tolerance}%)"
    done < "$TESTS_WITH_DIFFERENCES_FILE"
    echo ""
    echo "Expected Failures (${EXPECTED_FAILURE_COUNT}):"
    while read -r test; do
        [[ -z "$test" ]] && continue
        echo "  ⚠ $test"
    done < "$TESTS_EXPECTED_FAILURE_FILE"
    echo ""

    echo "=== Methods ONLY in LOCAL ($MISSING_REMOTE total) ==="
    while read -r test; do
        [[ -z "$test" ]] && continue
        echo "  $test"
    done < "$TESTS_MISSING_REMOTE_FILE"
    echo ""

    echo "=== Methods ONLY in REMOTE ($MISSING_LOCAL total) ==="
    while read -r test; do
        [[ -z "$test" ]] && continue
        echo "  $test"
    done < "$TESTS_MISSING_LOCAL_FILE"
} >> "$METHODS_OUTPUT"

# Generate summary
cat > "$SUMMARY_OUTPUT" << EOF
=== RPC Regression Test Summary V2 ===
Timestamp: $(date)
Local URL:  $LOCAL_URL
Remote URL: $REMOTE_URL
Default Tolerance: ${DEFAULT_TOLERANCE}%

Configuration:
  Default Tolerance:       ${DEFAULT_TOLERANCE}%
  Balance Tolerance:       ${BALANCE_TOLERANCE}%
  Timestamp Tolerance:     ${TIMESTAMP_TOLERANCE}%

Results:
  Total unique tests:        $TOTAL_TESTS
  Tests in both endpoints:   $((TOTAL_TESTS - MISSING_LOCAL - MISSING_REMOTE))

  ✓ Matching responses:      $MATCHING_TESTS
    - Exact matches:         $((MATCHING_TESTS - NUMERIC_TOLERANCE_MATCHES))
    - Within tolerance:      $NUMERIC_TOLERANCE_MATCHES

  ✗ Different responses:     $DIFFERENT_TESTS
  ⚠ Expected failures:       $EXPECTED_FAILURE_COUNT

  Methods only in local:     $MISSING_REMOTE
  Methods only in remote:    $MISSING_LOCAL

Files:
    Local output dir:  $LOCAL_OUTPUT_DIR
    Remote output dir: $REMOTE_OUTPUT_DIR
    Category diffs:    $CATEGORY_DIFF_DIR
  Detailed diff:     $DIFF_OUTPUT
  Methods compare:   $METHODS_OUTPUT
  Normalized data:   $NORMALIZED_OUTPUT
  This summary:      $SUMMARY_OUTPUT

EOF

# Add specific differences to summary if any
if [[ -s "$TESTS_WITH_DIFFERENCES_FILE" ]]; then
    echo "Unexpected differences found in:" >> "$SUMMARY_OUTPUT"
    while read -r test; do
        [[ -z "$test" ]] && continue
        tolerance=$(get_method_tolerance "$test")
        echo "  - $test (tolerance: ${tolerance}%)" >> "$SUMMARY_OUTPUT"
    done < "$TESTS_WITH_DIFFERENCES_FILE"
    echo "" >> "$SUMMARY_OUTPUT"
fi

# Add expected failures
if [[ -s "$TESTS_EXPECTED_FAILURE_FILE" ]]; then
    echo "Expected failures (as configured):" >> "$SUMMARY_OUTPUT"
    while read -r test; do
        [[ -z "$test" ]] && continue
        echo "  - $test" >> "$SUMMARY_OUTPUT"
    done < "$TESTS_EXPECTED_FAILURE_FILE"
    echo "" >> "$SUMMARY_OUTPUT"
fi

# Generate timing comparison report
TIMING_OUTPUT="$RUN_DIR/timing_comparison.txt"
echo -e "${YELLOW}Generating timing comparison...${NC}"

{
    echo "=== Timing Comparison Report ==="
    echo "Timestamp: $(date)"
    echo "Local (Staging):  $LOCAL_URL"
    echo "Remote (Production): $REMOTE_URL"
    echo ""
    echo "Performance Summary:"
    echo "===================="
    echo ""

    # Process timing data for all tests
    local_faster_count=0
    remote_faster_count=0
    total_timing_tests=0
    local_total_time=0
    remote_total_time=0

    printf "%-60s %12s %12s %12s %s\n" "Test Name" "Local (ms)" "Remote (ms)" "Diff (%)" "Winner"
    echo "=========================================================================================================="

    for idx in "${!CATEGORY_KEYS_ORDERED[@]}"; do
        local_file="$LOCAL_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"
        remote_file="$REMOTE_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"

        if [[ ! -s "$local_file" ]] || [[ ! -s "$remote_file" ]]; then
            continue
        fi

        # Get all test names from both files
        local_tests=$(jq -r '.test' "$local_file" 2>/dev/null | sort -u)
        remote_tests=$(jq -r '.test' "$remote_file" 2>/dev/null | sort -u)
        all_tests=$(echo -e "$local_tests\n$remote_tests" | sort -u)

        while IFS= read -r test_name; do
            [[ -z "$test_name" ]] && continue

            # Get timing from both endpoints
            local_timing=$(jq -r --arg test "$test_name" 'select(.test == $test) | .timing.total_ms // empty' "$local_file" 2>/dev/null | head -1)
            remote_timing=$(jq -r --arg test "$test_name" 'select(.test == $test) | .timing.total_ms // empty' "$remote_file" 2>/dev/null | head -1)

            # Skip if timing data missing
            if [[ -z "$local_timing" ]] || [[ -z "$remote_timing" ]] || [[ "$local_timing" == "null" ]] || [[ "$remote_timing" == "null" ]]; then
                continue
            fi

            total_timing_tests=$((total_timing_tests + 1))
            local_total_time=$((local_total_time + local_timing))
            remote_total_time=$((remote_total_time + remote_timing))

            # Calculate difference percentage
            if [[ $remote_timing -gt 0 ]]; then
                diff_pct=$(echo "scale=1; (($local_timing - $remote_timing) * 100) / $remote_timing" | bc -l 2>/dev/null || echo "0")
            else
                diff_pct="N/A"
            fi

            # Determine winner
            winner="="
            if [[ $local_timing -lt $remote_timing ]]; then
                local_faster_count=$((local_faster_count + 1))
                winner="LOCAL"
            elif [[ $local_timing -gt $remote_timing ]]; then
                remote_faster_count=$((remote_faster_count + 1))
                winner="REMOTE"
            fi

            printf "%-60s %12d %12d %11s%% %s\n" "$test_name" "$local_timing" "$remote_timing" "$diff_pct" "$winner"

        done <<< "$all_tests"
    done

    echo "=========================================================================================================="
    echo ""
    echo "Aggregate Statistics:"
    echo "---------------------"
    printf "Total tests with timing data:     %d\n" "$total_timing_tests"
    printf "Local (staging) faster:            %d tests\n" "$local_faster_count"
    printf "Remote (production) faster:        %d tests\n" "$remote_faster_count"
    printf "Equal performance:                 %d tests\n" "$((total_timing_tests - local_faster_count - remote_faster_count))"
    echo ""
    printf "Total time (all tests):\n"
    printf "  Local:                           %d ms\n" "$local_total_time"
    printf "  Remote:                          %d ms\n" "$remote_total_time"
    if [[ $total_timing_tests -gt 0 ]]; then
        local_avg=$((local_total_time / total_timing_tests))
        remote_avg=$((remote_total_time / total_timing_tests))
        printf "  Average per test (local):        %d ms\n" "$local_avg"
        printf "  Average per test (remote):       %d ms\n" "$remote_avg"

        if [[ $remote_avg -gt 0 ]]; then
            overall_diff=$(echo "scale=1; (($local_avg - $remote_avg) * 100) / $remote_avg" | bc -l 2>/dev/null || echo "0")
            printf "  Overall performance difference:  %s%%\n" "$overall_diff"

            if (( $(echo "$local_avg < $remote_avg" | bc -l 2>/dev/null || echo 0) )); then
                echo ""
                echo "  🚀 LOCAL (STAGING) IS FASTER OVERALL"
            elif (( $(echo "$local_avg > $remote_avg" | bc -l 2>/dev/null || echo 0) )); then
                echo ""
                echo "  ⚠️  REMOTE (PRODUCTION) IS FASTER OVERALL"
            else
                echo ""
                echo "  ⚖️  PERFORMANCE IS EQUAL"
            fi
        fi
    fi
    echo ""

    # Top 10 slowest endpoints on each
    echo "Top 10 Slowest Endpoints (Local):"
    echo "-----------------------------------"
    for idx in "${!CATEGORY_KEYS_ORDERED[@]}"; do
        local_file="$LOCAL_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"
        [[ -s "$local_file" ]] && jq -r '[.test, (.timing.total_ms // 0)] | @tsv' "$local_file" 2>/dev/null
    done | sort -t$'\t' -k2 -n -r | head -10 | while IFS=$'\t' read -r test time; do
        printf "  %-60s %12d ms\n" "$test" "$time"
    done
    echo ""

    echo "Top 10 Slowest Endpoints (Remote):"
    echo "------------------------------------"
    for idx in "${!CATEGORY_KEYS_ORDERED[@]}"; do
        remote_file="$REMOTE_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"
        [[ -s "$remote_file" ]] && jq -r '[.test, (.timing.total_ms // 0)] | @tsv' "$remote_file" 2>/dev/null
    done | sort -t$'\t' -k2 -n -r | head -10 | while IFS=$'\t' read -r test time; do
        printf "  %-60s %12d ms\n" "$test" "$time"
    done
    echo ""

    # Biggest performance differences
    echo "Top 10 Biggest Performance Differences:"
    echo "----------------------------------------"
    for idx in "${!CATEGORY_KEYS_ORDERED[@]}"; do
        local_file="$LOCAL_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"
        remote_file="$REMOTE_OUTPUT_DIR/${CATEGORY_FILES[$idx]}"

        if [[ ! -s "$local_file" ]] || [[ ! -s "$remote_file" ]]; then
            continue
        fi

        local_tests=$(jq -r '.test' "$local_file" 2>/dev/null | sort -u)
        while IFS= read -r test_name; do
            [[ -z "$test_name" ]] && continue

            local_timing=$(jq -r --arg test "$test_name" 'select(.test == $test) | .timing.total_ms // empty' "$local_file" 2>/dev/null | head -1)
            remote_timing=$(jq -r --arg test "$test_name" 'select(.test == $test) | .timing.total_ms // empty' "$remote_file" 2>/dev/null | head -1)

            if [[ -n "$local_timing" ]] && [[ -n "$remote_timing" ]] && [[ "$local_timing" != "null" ]] && [[ "$remote_timing" != "null" ]]; then
                abs_diff=$((local_timing > remote_timing ? local_timing - remote_timing : remote_timing - local_timing))
                echo -e "$test_name\t$local_timing\t$remote_timing\t$abs_diff"
            fi
        done <<< "$local_tests"
    done | sort -t$'\t' -k4 -n -r | head -10 | while IFS=$'\t' read -r test local_t remote_t diff; do
        if [[ $local_t -lt $remote_t ]]; then
            winner="LOCAL"
        else
            winner="REMOTE"
        fi
        printf "  %-50s  L:%5dms  R:%5dms  Diff:%5dms  Winner:%s\n" "$test" "$local_t" "$remote_t" "$diff" "$winner"
    done

} > "$TIMING_OUTPUT"

echo -e "${GREEN}✓ Timing analysis complete${NC}"
echo -e "${GREEN}✓ Reports generated${NC}\n"

# Print summary to console
echo ""
echo -e "${BLUE}=== Summary ===${NC}"
cat "$SUMMARY_OUTPUT"

echo -e "\n${CYAN}Detailed reports:${NC}"
echo -e "  ${CYAN}Methods:     $METHODS_OUTPUT${NC}"
echo -e "  ${CYAN}Differences: $DIFF_OUTPUT${NC}"
echo -e "  ${CYAN}Normalized:  $NORMALIZED_OUTPUT${NC}"
echo -e "  ${CYAN}Timing:      $TIMING_OUTPUT${NC}"
echo -e "  ${CYAN}Category diffs dir: $CATEGORY_DIFF_DIR${NC}"

# Print timing summary
echo ""
echo -e "${BLUE}=== Performance Summary ===${NC}"
if [[ -f "$TIMING_OUTPUT" ]]; then
    # Extract key stats from timing output
    grep -A 20 "Aggregate Statistics:" "$TIMING_OUTPUT" | head -25
fi

# Print status with colors
echo ""
if [[ $DIFFERENT_TESTS -gt 0 ]]; then
    echo -e "${RED}✗ Found $DIFFERENT_TESTS unexpected differences${NC}"
    echo -e "${YELLOW}Review detailed diff: $DIFF_OUTPUT${NC}"
fi

if [[ $EXPECTED_FAILURE_COUNT -gt 0 ]]; then
    echo -e "${MAGENTA}⚠ $EXPECTED_FAILURE_COUNT expected failures occurred (as configured)${NC}"
fi

if [[ $MISSING_LOCAL -gt 0 ]] || [[ $MISSING_REMOTE -gt 0 ]]; then
    echo -e "${YELLOW}⚠ Method coverage mismatch:${NC}"
    [[ $MISSING_REMOTE -gt 0 ]] && echo -e "${YELLOW}  - $MISSING_REMOTE methods only in local${NC}"
    [[ $MISSING_LOCAL -gt 0 ]] && echo -e "${YELLOW}  - $MISSING_LOCAL methods only in remote${NC}"
    echo -e "${YELLOW}Review methods comparison: $METHODS_OUTPUT${NC}"
fi

# Cleanup temp files
rm -f "$TESTS_WITH_DIFFERENCES_FILE" "$TESTS_MISSING_LOCAL_FILE" "$TESTS_MISSING_REMOTE_FILE" \
      "$TESTS_MATCH_FILE" "$TESTS_EXPECTED_FAILURE_FILE" "$TEMP_CONFIG"

if [[ $DIFFERENT_TESTS -eq 0 ]] && [[ $MISSING_LOCAL -eq 0 ]] && [[ $MISSING_REMOTE -eq 0 ]]; then
    echo -e "${GREEN}✓✓✓ All tests match perfectly! ✓✓✓${NC}"
    exit 0
else
    # Exit with error only if there are unexpected differences
    if [[ $DIFFERENT_TESTS -gt 0 ]]; then
        exit 1
    else
        # Only coverage mismatches or expected failures - warn but don't fail
        exit 0
    fi
fi
