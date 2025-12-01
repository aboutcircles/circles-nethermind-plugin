#!/usr/bin/env bash
set -e
set -o pipefail

# Test script for RPC with timing metrics
# Enhanced version that captures response time for each endpoint
#
# Usage:
#   ./test-rpc-with-timing.sh [RPC_URL] [--json] [--json-dir <dir>]

# Source the original test-rpc.sh but override the run_test function
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ORIGINAL_SCRIPT="$SCRIPT_DIR/test-rpc.sh"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Check if original script exists
if [[ ! -f "$ORIGINAL_SCRIPT" ]]; then
    echo -e "${RED}Error: Cannot find test-rpc.sh at $ORIGINAL_SCRIPT${NC}"
    exit 1
fi

# Extract configuration from original script
source <(grep -E '^(TEST_ADDR_|TOKEN_ADDR_|CATEGORY_)' "$ORIGINAL_SCRIPT" | head -50)
source <(sed -n '/^get_category_index/,/^}$/p' "$ORIGINAL_SCRIPT")
source <(sed -n '/^get_category_label/,/^}$/p' "$ORIGINAL_SCRIPT")
source <(sed -n '/^get_category_file_name/,/^}$/p' "$ORIGINAL_SCRIPT")
source <(sed -n '/^get_category_file_path/,/^}$/p' "$ORIGINAL_SCRIPT")
source <(sed -n '/^set_category_file_path/,/^}$/p' "$ORIGINAL_SCRIPT")
source <(sed -n '/^generate_order_clause/,/^}$/p' "$ORIGINAL_SCRIPT")
source <(sed -n '/^generate_query_payload/,/^}$/p' "$ORIGINAL_SCRIPT")

# Initialize variables
JSON_DIR=""
MANIFEST_FILE=""
OUTPUT_MODE="pretty"
RPC_URL=""
CURRENT_CATEGORY=""
CURRENT_CATEGORY_INDEX=-1
RUN_TEST_LAST_RESPONSE=""
TIMING_SUMMARY_FILE=""

CATEGORY_FILE_PATHS=()
for ((i=0; i<CATEGORY_COUNT; i++)); do
    CATEGORY_FILE_PATHS+=("")
done

# Parse arguments
ARGS=("$@")
while [[ $# -gt 0 ]]; do
    case "$1" in
        --json)
            OUTPUT_MODE="json"
            ;;
        --json-dir)
            shift
            if [[ -z "$1" ]]; then
                echo "Error: --json-dir requires a path" >&2
                exit 1
            fi
            JSON_DIR="$1"
            ;;
        http://*|https://*)
            RPC_URL="$1"
            ;;
        *)
            if [[ -z "$RPC_URL" ]]; then
                RPC_URL="$1"
            else
                echo "Unknown argument: $1" >&2
                exit 1
            fi
            ;;
    esac
    shift
done

if [[ -z "$RPC_URL" ]]; then
    RPC_URL="http://localhost:${RPC_PORT:-8081}"
fi

if [[ -n "$JSON_DIR" && "$OUTPUT_MODE" != "json" ]]; then
    echo "Error: --json-dir requires --json output" >&2
    exit 1
fi

init_json_output() {
    [[ -z "$JSON_DIR" ]] && return

    mkdir -p "$JSON_DIR"
    MANIFEST_FILE="$JSON_DIR/manifest.json"
    TIMING_SUMMARY_FILE="$JSON_DIR/timing_summary.json"

    {
        echo '{'
        echo '  "categories": ['
        for ((i=0; i<CATEGORY_COUNT; i++)); do
            key="${CATEGORY_KEYS[$i]}"
            label="${CATEGORY_LABELS[$i]}"
            file="${CATEGORY_FILE_NAMES[$i]}"
            printf '    {"key":"%s","label":"%s","file":"%s"}' "$key" "$label" "$file"
            if (( i < CATEGORY_COUNT - 1 )); then
                echo ','
            else
                echo ''
            fi
        done
        echo '  ]'
        echo '}'
    } > "$MANIFEST_FILE"

    # Initialize timing summary
    echo '{"url":"'"$RPC_URL"'","tests":[]}' > "$TIMING_SUMMARY_FILE"

    for ((i=0; i<CATEGORY_COUNT; i++)); do
        key="${CATEGORY_KEYS[$i]}"
        path="$JSON_DIR/${CATEGORY_FILE_NAMES[$i]}"
        set_category_file_path "$key" "$path"
        : > "$path"
    done
}

init_json_output

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}Running tests with timing metrics against RPC host at $RPC_URL${NC}\n"
    echo -e "${YELLOW}Test addresses:${NC}"
    echo -e "  1: $TEST_ADDR_1"
    echo -e "  2: $TEST_ADDR_2"
    echo -e "  3: $TEST_ADDR_3"
    echo -e ""
fi

ensure_category_header() {
    local category="$1"
    local idx
    idx=$(get_category_index "$category") || true

    if (( idx < 0 )); then
        echo "Unknown category: $category" >&2
        exit 1
    fi

    if (( idx < CURRENT_CATEGORY_INDEX )); then
        echo "Category order violation: $category" >&2
        exit 1
    fi

    if [[ "$category" != "$CURRENT_CATEGORY" ]]; then
        CURRENT_CATEGORY="$category"
        CURRENT_CATEGORY_INDEX=$idx
        if [[ "$OUTPUT_MODE" != "json" ]]; then
            echo -e "${BLUE}=== $(get_category_label "$category") ===${NC}\n"
        fi
    fi
}

# Enhanced run_test function with timing
run_test() {
    local category="$1"
    local test_name="$2"
    local curl_cmd="$3"

    ensure_category_header "$category"

    # Add timing using curl's built-in timing with -w flag
    local timed_curl_cmd="${curl_cmd% \'*\'} -w '\n{\"time_total\":%{time_total},\"time_namelookup\":%{time_namelookup},\"time_connect\":%{time_connect},\"time_starttransfer\":%{time_starttransfer}}' -s"

    local full_response
    local exit_code

    # Capture start time for backup
    local start_time=$(date +%s.%N)

    full_response=$(eval "$timed_curl_cmd" 2>&1)
    exit_code=$?

    # Capture end time for backup
    local end_time=$(date +%s.%N)

    if [[ $exit_code -ne 0 ]]; then
        echo -e "${RED}Error executing request for $test_name:${NC} $full_response" >&2
        return 1
    fi

    # Split response and timing info (last line contains timing JSON)
    local response=$(echo "$full_response" | head -n -1)
    local timing_line=$(echo "$full_response" | tail -n 1)

    # Parse timing from curl output
    local time_total=0
    local time_namelookup=0
    local time_connect=0
    local time_starttransfer=0

    if echo "$timing_line" | jq -e . >/dev/null 2>&1; then
        time_total=$(echo "$timing_line" | jq -r '.time_total // 0')
        time_namelookup=$(echo "$timing_line" | jq -r '.time_namelookup // 0')
        time_connect=$(echo "$timing_line" | jq -r '.time_connect // 0')
        time_starttransfer=$(echo "$timing_line" | jq -r '.time_starttransfer // 0')
    else
        # Fallback to bash timing if curl timing failed
        time_total=$(echo "$end_time - $start_time" | bc -l 2>/dev/null || echo "0")
    fi

    local response_min
    if ! response_min=$(echo "$response" | jq -c '.' 2>/dev/null); then
        echo -e "${RED}Invalid JSON response for $test_name:${NC} $response" >&2
        return 1
    fi

    RUN_TEST_LAST_RESPONSE="$response_min"

    # Add timing info to JSON output
    if [[ "$OUTPUT_MODE" == "json" ]]; then
        if [[ -n "$JSON_DIR" ]]; then
            local category_file_path
            category_file_path=$(get_category_file_path "$category")

            # Write test result with timing
            echo "{\"test\":\"$test_name\",\"response\":$response_min,\"timing\":{\"total\":$time_total,\"dns\":$time_namelookup,\"connect\":$time_connect,\"first_byte\":$time_starttransfer}}" >> "$category_file_path"

            # Append to timing summary
            if [[ -f "$TIMING_SUMMARY_FILE" ]]; then
                local temp_file=$(mktemp)
                jq --arg name "$test_name" \
                   --arg cat "$category" \
                   --argjson total "$time_total" \
                   --argjson dns "$time_namelookup" \
                   --argjson conn "$time_connect" \
                   --argjson ttfb "$time_starttransfer" \
                   '.tests += [{
                       "name": $name,
                       "category": $cat,
                       "time_total_ms": ($total * 1000 | floor),
                       "time_dns_ms": ($dns * 1000 | floor),
                       "time_connect_ms": ($conn * 1000 | floor),
                       "time_first_byte_ms": ($ttfb * 1000 | floor)
                   }]' "$TIMING_SUMMARY_FILE" > "$temp_file" && mv "$temp_file" "$TIMING_SUMMARY_FILE"
            fi
        else
            echo "{\"test\":\"$test_name\",\"response\":$response_min,\"timing\":{\"total\":$time_total,\"dns\":$time_namelookup,\"connect\":$time_connect,\"first_byte\":$time_starttransfer}}"
        fi
    else
        echo -e "${YELLOW}Testing: $test_name${NC}"
        echo -e "${CYAN}Timing: ${time_total}s (DNS: ${time_namelookup}s, Connect: ${time_connect}s, TTFB: ${time_starttransfer}s)${NC}"
        echo -e "${GREEN}Request:${NC}"
        echo "$curl_cmd"
        echo -e "${GREEN}Response:${NC}"
        echo "$response" | jq '.'
        echo ""
    fi
}

# Source the test definitions from original script
# We need to extract all the test calls
source <(sed -n '/^# System Methods/,/^exit 0/p' "$ORIGINAL_SCRIPT" | grep -v '^exit 0')
