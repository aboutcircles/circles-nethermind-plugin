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

LOCAL_OUTPUT="$RUN_DIR/local.json"
REMOTE_OUTPUT="$RUN_DIR/remote.json"
DIFF_OUTPUT="$RUN_DIR/diff.txt"
SUMMARY_OUTPUT="$RUN_DIR/summary.txt"
METHODS_OUTPUT="$RUN_DIR/methods.txt"
NORMALIZED_OUTPUT="$RUN_DIR/normalized.txt"
TEMP_CONFIG="$RUN_DIR/parsed_config.txt"

# Default configuration (can be overridden by config file)
DEFAULT_TOLERANCE=0.001
BALANCE_TOLERANCE=0.1  # 0.1% for balance-related methods
TIMESTAMP_TOLERANCE=5.0  # 5% for timestamp-sensitive data

# Method-specific tolerance rules (method:tolerance pairs)
METHOD_TOLERANCES="
circles_getTotalBalance:0.1
circlesV2_getTotalBalance:0.1
circles_getTokenBalances:0.1
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

# Start both test runs simultaneously
(
    if ! "$SCRIPT_DIR/test-rpc.sh" "$LOCAL_URL" --json > "$LOCAL_OUTPUT" 2>&1; then
        echo -e "${RED}Error: Failed to run tests against local endpoint${NC}" >&2
        exit 1
    fi
) &
LOCAL_PID=$!

(
    if ! "$SCRIPT_DIR/test-rpc.sh" "$REMOTE_URL" --json > "$REMOTE_OUTPUT" 2>&1; then
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
    echo -e "${RED}Check $LOCAL_OUTPUT for details${NC}"
    exit 1
fi

if [[ $REMOTE_EXIT -ne 0 ]]; then
    echo -e "${RED}Error: Failed to run tests against remote endpoint${NC}"
    echo -e "${RED}Check $REMOTE_OUTPUT for details${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Parallel tests completed${NC}\n"

# Step 2: Parse test results
echo -e "${YELLOW}[2/4] Parsing test results...${NC}"

# Parse test names properly (extract complete test names from JSON)
LOCAL_TESTS=$(jq -r '.test' "$LOCAL_OUTPUT" 2>/dev/null | sort -u)
REMOTE_TESTS=$(jq -r '.test' "$REMOTE_OUTPUT" 2>/dev/null | sort -u)

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

echo -e "${GREEN}✓ Parsing completed${NC}\n"

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

    # Handle zero values
    if command -v bc &> /dev/null; then
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
    else
        # Fallback without bc - use awk
        awk -v v1="$val1" -v v2="$val2" -v tol="$tolerance" 'BEGIN {
            if (v1 == 0 && v2 == 0) exit 0;
            if (v1 == 0 || v2 == 0) exit 1;
            diff = (v1 > v2) ? v1 - v2 : v2 - v1;
            avg = (v1 + v2) / 2;
            percent = (diff / avg) * 100;
            exit (percent < tol) ? 0 : 1;
        }'
    fi
}

# Function to extract all numeric values from JSON
extract_numbers() {
    local json=$1
    echo "$json" | grep -oE '[0-9]+\.?[0-9]*([eE][+-]?[0-9]+)?' | grep -v '^[0-9]$' | head -n 100
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
    if [[ "$norm1" == "$norm2" ]]; then
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

# Get all unique test names
ALL_TESTS=$(echo -e "$LOCAL_TESTS\n$REMOTE_TESTS" | sort -u)

# Progress counter
current=0
total=$(echo "$ALL_TESTS" | grep -c . || echo 0)

# Compare each test
while IFS= read -r test_name; do
    [[ -z "$test_name" ]] && continue

    current=$((current + 1))
    echo -ne "\rAnalyzing test $current/$total: $test_name..."

    TOTAL_TESTS=$((TOTAL_TESTS + 1))

    # Check if test exists in both
    local_exists=$(echo "$LOCAL_TESTS" | grep -Fx "$test_name" || true)
    remote_exists=$(echo "$REMOTE_TESTS" | grep -Fx "$test_name" || true)

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
    local_response=$(get_response "$LOCAL_OUTPUT" "$test_name")
    remote_response=$(get_response "$REMOTE_OUTPUT" "$test_name")

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
            } >> "$DIFF_OUTPUT"
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
            } >> "$DIFF_OUTPUT"
            ;;
    esac
done <<< "$ALL_TESTS"

echo -e "\r${GREEN}✓ Analysis completed (${total} tests)${NC}\n"

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
  Local output:      $LOCAL_OUTPUT
  Remote output:     $REMOTE_OUTPUT
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

echo -e "${GREEN}✓ Reports generated${NC}\n"

# Print summary to console
echo ""
echo -e "${BLUE}=== Summary ===${NC}"
cat "$SUMMARY_OUTPUT"

echo -e "\n${CYAN}Detailed reports:${NC}"
echo -e "  ${CYAN}Methods:     $METHODS_OUTPUT${NC}"
echo -e "  ${CYAN}Differences: $DIFF_OUTPUT${NC}"
echo -e "  ${CYAN}Normalized:  $NORMALIZED_OUTPUT${NC}"

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
