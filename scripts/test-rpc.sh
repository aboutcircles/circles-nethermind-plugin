#!/usr/bin/env bash
set -e
set -o pipefail

# Test script for RPC and Pathfinder hosts
# Runs all curl commands from the documentation against the local services.
# Groups RPC method invocations into logical categories for easier inspection.
#
# Usage:
#   ./test-rpc.sh [RPC_URL] [--json] [--json-dir <dir>]
#
# Examples:
#   ./test-rpc.sh                                          # Test localhost:8081 (pretty output)
#   ./test-rpc.sh http://localhost:8081                    # Test custom local URL
#   ./test-rpc.sh https://rpc.aboutcircles.com --json      # JSON output to stdout
#   ./test-rpc.sh --json --json-dir /tmp/results           # JSON output split per category

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Test addresses - three known accounts for comprehensive testing
TEST_ADDR_1="0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0"
TEST_ADDR_2="0x42cEDde51198D1773590311E2A340DC06B24cB37"
TEST_ADDR_3="0xDE374ece6fA50e781E81Aac78e811b33D16912c7"

TOKEN_ADDR_1="0x6D5e20F62C177765f73aee343a307D949c08B9DC"
TOKEN_ADDR_2="0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673"
TOKEN_ADDR_3="0x448eabde0dc9ad70a9b68a8a03aa91da872f95bd"

CATEGORY_KEYS=(
    "system"
    "balance"
    "avatar"
    "trust"
    "sdk"
    "query"
    "events"
)

CATEGORY_LABELS=(
    "System Methods"
    "Balance & Token Methods"
    "Avatar & Profile Methods"
    "Trust & Network Methods"
    "SDK Enablement Methods"
    "Query Methods"
    "Events Methods"
)

CATEGORY_FILE_NAMES=(
    "01-system-methods.jsonl"
    "02-balance-token-methods.jsonl"
    "03-avatar-profile-methods.jsonl"
    "04-trust-network-methods.jsonl"
    "05-sdk-methods.jsonl"
    "06-query-methods.jsonl"
    "07-events-methods.jsonl"
)

CATEGORY_COUNT=${#CATEGORY_KEYS[@]}
if [[ ${#CATEGORY_LABELS[@]} -ne $CATEGORY_COUNT || ${#CATEGORY_FILE_NAMES[@]} -ne $CATEGORY_COUNT ]]; then
    echo "Category configuration arrays must have matching lengths" >&2
    exit 1
fi

CATEGORY_FILE_PATHS=()
for ((i=0; i<CATEGORY_COUNT; i++)); do
    CATEGORY_FILE_PATHS+=("")
done

get_category_index() {
    local key="$1"
    for ((i=0; i<CATEGORY_COUNT; i++)); do
        if [[ "${CATEGORY_KEYS[$i]}" == "$key" ]]; then
            echo "$i"
            return 0
        fi
    done
    echo "-1"
    return 1
}

get_category_label() {
    local key="$1"
    local idx
    idx=$(get_category_index "$key") || true
    if (( idx >= 0 )); then
        echo "${CATEGORY_LABELS[$idx]}"
    fi
}

get_category_file_name() {
    local key="$1"
    local idx
    idx=$(get_category_index "$key") || true
    if (( idx >= 0 )); then
        echo "${CATEGORY_FILE_NAMES[$idx]}"
    fi
}

get_category_file_path() {
    local key="$1"
    local idx
    idx=$(get_category_index "$key") || true
    if (( idx >= 0 )); then
        echo "${CATEGORY_FILE_PATHS[$idx]}"
    fi
}

set_category_file_path() {
    local key="$1"
    local value="$2"
    local idx
    idx=$(get_category_index "$key") || true
    if (( idx >= 0 )); then
        CATEGORY_FILE_PATHS[$idx]="$value"
    fi
}

JSON_DIR=""
MANIFEST_FILE=""
OUTPUT_MODE="pretty"
RPC_URL=""
CURRENT_CATEGORY=""
CURRENT_CATEGORY_INDEX=-1
RUN_TEST_LAST_RESPONSE=""

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

    for ((i=0; i<CATEGORY_COUNT; i++)); do
        key="${CATEGORY_KEYS[$i]}"
        path="$JSON_DIR/${CATEGORY_FILE_NAMES[$i]}"
        set_category_file_path "$key" "$path"
        : > "$path"
    done
}

init_json_output

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}Running tests against RPC host at $RPC_URL${NC}\n"
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

# Function to execute and print curl commands (with timing)
run_test() {
    local category="$1"
    local test_name="$2"
    local curl_cmd="$3"

    ensure_category_header "$category"

    # Add curl timing flags: -w outputs timing info, -o /dev/stderr sends response to stderr
    # This allows us to capture timing separately from response
    local timed_curl="${curl_cmd% \'*\'} -w '\n__TIMING__{\"total\":%{time_total},\"dns\":%{time_namelookup},\"connect\":%{time_connect},\"ttfb\":%{time_starttransfer}}__TIMING__' -s"

    local full_output
    local exit_code
    # Cross-platform timing fallback (milliseconds)
    # macOS date doesn't support %N, so we use a simpler approach
    local start_time=$(python3 -c "import time; print(int(time.time() * 1000))" 2>/dev/null || echo "0")
    full_output=$(eval "$timed_curl" 2>&1)
    exit_code=$?
    local end_time=$(python3 -c "import time; print(int(time.time() * 1000))" 2>/dev/null || echo "0")

    if [[ $exit_code -ne 0 ]]; then
        echo -e "${RED}Error executing request for $test_name:${NC} $full_output" >&2
        return 1
    fi

    # Extract response and timing (timing is on last line between markers)
    local response=$(echo "$full_output" | sed -n '1,/__TIMING__/p' | sed '$d')
    local timing_json=$(echo "$full_output" | grep -o '__TIMING__.*__TIMING__' | sed 's/__TIMING__//g' | head -1)

    # Fallback: calculate timing from bash if curl timing failed
    local time_total_ms=0
    local time_dns_ms=0
    local time_connect_ms=0
    local time_ttfb_ms=0

    if [[ -n "$timing_json" ]] && echo "$timing_json" | jq -e . >/dev/null 2>&1; then
        time_total_ms=$(echo "$timing_json" | jq -r '(.total * 1000) | floor')
        time_dns_ms=$(echo "$timing_json" | jq -r '(.dns * 1000) | floor')
        time_connect_ms=$(echo "$timing_json" | jq -r '(.connect * 1000) | floor')
        time_ttfb_ms=$(echo "$timing_json" | jq -r '(.ttfb * 1000) | floor')
    else
        # Fallback: use python timing (cross-platform, millisecond precision)
        if [[ $start_time -gt 0 ]] && [[ $end_time -gt 0 ]]; then
            time_total_ms=$(( end_time - start_time ))
        else
            time_total_ms=0
        fi
    fi

    local response_min
    if ! response_min=$(echo "$response" | jq -c '.' 2>/dev/null); then
        echo -e "${RED}Invalid JSON response for $test_name:${NC} $response" >&2
        return 1
    fi

    RUN_TEST_LAST_RESPONSE="$response_min"

    if [[ "$OUTPUT_MODE" == "json" ]]; then
        if [[ -n "$JSON_DIR" ]]; then
            local category_file_path
            category_file_path=$(get_category_file_path "$category")
            # Include timing in JSON output
            echo "{\"test\":\"$test_name\",\"response\":$response_min,\"timing\":{\"total_ms\":$time_total_ms,\"dns_ms\":$time_dns_ms,\"connect_ms\":$time_connect_ms,\"ttfb_ms\":$time_ttfb_ms}}" >> "$category_file_path"
        else
            echo "{\"test\":\"$test_name\",\"response\":$response_min,\"timing\":{\"total_ms\":$time_total_ms,\"dns_ms\":$time_dns_ms,\"connect_ms\":$time_connect_ms,\"ttfb_ms\":$time_ttfb_ms}}"
        fi
    else
        echo -e "${YELLOW}Testing: $test_name${NC} ${BLUE}[${time_total_ms}ms]${NC}"
        echo -e "${GREEN}Request:${NC}"
        echo "$curl_cmd"
        echo -e "${GREEN}Response:${NC}"
        echo "$response" | jq '.'
        echo ""
    fi
}

generate_order_clause() {
    local columns_json="$1"
    jq -c -n --argjson cols "$columns_json" '
        ["blockNumber","transactionIndex","logIndex"]
        | map(select($cols | index(.) != null))
        | map({Column:., SortOrder:"DESC"})
    '
}

generate_query_payload() {
    local namespace="$1"
    local table="$2"
    local order_clause_json="${3:-[]}"
    jq -c -n \
        --arg ns "$namespace" \
        --arg tbl "$table" \
        --argjson order "$order_clause_json" '
        {
            jsonrpc:"2.0",
            id:1,
            method:"circles_query",
            params:[{
                Namespace:$ns,
                Table:$tbl,
                Columns:[],
                Limit:1,
                Order:$order
            }]
        }
    '
}

is_event_table() {
    local columns_json="$1"
    jq -r -n --argjson cols "$columns_json" '
        (["blockNumber","transactionIndex","logIndex","transactionHash"]
            | map(select($cols | index(.) != null))
            | length) == 4
    '
}

generate_events_payload() {
    local table_name="$1"
    jq -c -n --arg tbl "$table_name" '
        {
            jsonrpc:"2.0",
            id:1,
            method:"circles_events",
            params:[null,null,null,[$tbl],null,false]
        }
    '
}

SCHEMA_METADATA_AVAILABLE=false
declare -a SCHEMA_TABLES=()
declare -a EVENT_TABLE_NAMES=()

######################################################################
# System Methods (health + tables)
######################################################################

run_test "system" "circles_health" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_health\",\"params\":[]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "system" "circles_tables" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circles_tables\",\"params\":[]}' -H \"Content-Type: application/json\" $RPC_URL"

if [[ -n "$RUN_TEST_LAST_RESPONSE" ]]; then
    TABLES_JSON=$(echo "$RUN_TEST_LAST_RESPONSE" | jq '.result' 2>/dev/null || true)
    if [[ -n "$TABLES_JSON" && "$TABLES_JSON" != "null" ]]; then
        SCHEMA_TABLES=()
        SCHEMA_TABLES_RAW=$(echo "$TABLES_JSON" | jq -rc '.[] | .Namespace as $ns | (.Tables // [])[] | {namespace:$ns, table:.Table, columns:(.Columns // [] | map(.Column))}')
        if [[ -n "$SCHEMA_TABLES_RAW" ]]; then
            while IFS= read -r table_entry; do
                [[ -z "$table_entry" ]] && continue
                SCHEMA_TABLES+=("$table_entry")
            done <<< "$SCHEMA_TABLES_RAW"
        fi
        if [[ ${#SCHEMA_TABLES[@]} -gt 0 ]]; then
            SCHEMA_METADATA_AVAILABLE=true
        fi
    fi
fi

if [[ "$SCHEMA_METADATA_AVAILABLE" != "true" && "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${YELLOW}Warning: circles_query/events coverage will be skipped (no schema metadata).${NC}\n"
fi

######################################################################
# Balance & Token Methods
######################################################################

run_test "balance" "circles_getTotalBalance (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTotalBalance (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTotalBalance (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "balance" "circles_getTotalBalance (raw)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circlesV2_getTotalBalance (raw)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "balance" "circles_getTokenBalances (v1, addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenBalances (v1, addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenBalances (v1, addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "balance" "circlesV2_getTotalBalance (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circlesV2_getTotalBalance (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circlesV2_getTotalBalance (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "balance" "circles_getTokenBalances (v2, addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_1\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenBalances (v2, addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_2\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenBalances (v2, addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_3\"]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "balance" "circles_getTokenInfo (token1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfo\",\"params\":[\"$TOKEN_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenInfo (token2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfo\",\"params\":[\"$TOKEN_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenInfo (token3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfo\",\"params\":[\"$TOKEN_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "balance" "circles_getTokenInfoBatch (multiple tokens)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfoBatch\",\"params\":[[\"'$TOKEN_ADDR_1'\",\"'$TOKEN_ADDR_2'\",\"'$TOKEN_ADDR_3'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenInfoBatch (single token)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfoBatch\",\"params\":[[\"'$TOKEN_ADDR_1'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "balance" "circles_getTokenInfoBatch (different set of addresses)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfoBatch\",\"params\":[[\"'$TOKEN_ADDR_1'\",\"'$TOKEN_ADDR_2'\",\"'$TOKEN_ADDR_3'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Avatar & Profile Methods
######################################################################

run_test "avatar" "circles_getAvatarInfo (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getAvatarInfo (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getAvatarInfo (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_getAvatarInfoBatch (multiple addresses)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfoBatch\",\"params\":[[\"'$TEST_ADDR_1'\",\"'$TEST_ADDR_2'\",\"'$TEST_ADDR_3'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getAvatarInfoBatch (single address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfoBatch\",\"params\":[[\"'$TEST_ADDR_1'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getAvatarInfoBatch (two addresses)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfoBatch\",\"params\":[[\"'$TEST_ADDR_1'\",\"'$TEST_ADDR_2'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getAvatarInfoBatch (different order)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfoBatch\",\"params\":[[\"'$TEST_ADDR_3'\",\"'$TEST_ADDR_1'\",\"'$TEST_ADDR_2'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_getProfileCid (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCid\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getProfileCid (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCid\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getProfileCid (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCid\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_getProfileCidBatch (multiple addresses)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCidBatch\",\"params\":[[\"'$TEST_ADDR_1'\",\"'$TEST_ADDR_2'\",\"'$TEST_ADDR_3'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getProfileCidBatch (single address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCidBatch\",\"params\":[[\"'$TEST_ADDR_1'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_getProfileByCid" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByCid\",\"params\":[\"Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getProfileByCidBatch" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByCidBatch\",\"params\":[[\"Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W\",\"QmZuR1Jkhs9RLXVY28eTTRSnqbxLTBSoggp18Yde858xCM\",\"QmanRNbDjbiSFdxcYT9S9wpk3gaCVnM81MVAHkmJj6AqE5\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_getProfileByAddress (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getProfileByAddress (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_getProfileByAddress (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_getProfileByAddressBatch (with test addresses)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddressBatch\",\"params\":[[\"'$TEST_ADDR_1'\",\"'$TEST_ADDR_2'\",\"'$TEST_ADDR_3'\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_searchProfiles (using addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_1\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_searchProfiles (using addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_2\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "avatar" "circles_searchProfiles (using addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_3\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "avatar" "circles_searchProfiles (filter type)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_1\",10,0,[\"CrcV1_Signup\"]]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Trust & Network Methods
######################################################################

run_test "trust" "circles_getTrustRelations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getTrustRelations (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getTrustRelations (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "trust" "circles_getCommonTrust (addr1+addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getCommonTrust (addr2+addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_2\",\"$TEST_ADDR_3\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getCommonTrust (addr1+addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_3\"]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "trust" "circles_getCommonTrust (v1 only)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\", 1]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getCommonTrust (v2 only)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\", 2]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "trust" "circles_getNetworkSnapshot" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getNetworkSnapshot\",\"params\":[],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "trust" "circlesV2_findPath (addr1->addr3, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (addr2->addr3, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_2\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_2\",\"Token\":\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (addr1->addr2, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_2\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (addr3->addr1, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_3\",\"sink\":\"$TEST_ADDR_1\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_3\",\"Token\":\"0x1de1C49E7a623Cb3D1114bA0D40063F243ceeA\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (circular path with token swap)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_3\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"100000000000000000000\",\"fromTokens\":[\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\"],\"toTokens\":[\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\"],\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_3\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"100000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (multiple token balances)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"500000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"300000000000000000000\",\"IsWrapped\":false},{\"Holder\":\"$TEST_ADDR_2\",\"Token\":\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\",\"Amount\":\"200000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "trust" "circlesV2_findPath (exclusions)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000\",\"excludedFromTokens\":[\"$TOKEN_ADDR_2\"],\"maxTransfers\":5}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (with fromTokens restriction)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000\",\"fromTokens\":[\"$TOKEN_ADDR_1\"]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (with toTokens restriction)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000\",\"toTokens\":[\"$TOKEN_ADDR_3\"]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circlesV2_findPath (simulated trusts)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_2\",\"targetFlow\":\"1000\",\"simulatedTrusts\":[{\"Truster\":\"$TEST_ADDR_1\",\"Trustee\":\"$TEST_ADDR_2\",\"ExpiryTime\":9999999999}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# SDK Enablement Methods
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- SDK Enablement Methods ---${NC}\n"
fi

# Profile View (consolidates 6-7 calls into 1)
run_test "sdk" "circles_getProfileView (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getProfileView (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getProfileView (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Trust Network Summary (consolidates 4 calls into 1)
run_test "sdk" "circles_getTrustNetworkSummary (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustNetworkSummary\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTrustNetworkSummary (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustNetworkSummary\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTrustNetworkSummary (addr3, with maxDepth)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustNetworkSummary\",\"params\":[\"$TEST_ADDR_3\",2],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Aggregated Trust Relations Enriched (server-side aggregation with avatar info)
run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Valid Inviters (consolidates 3-4 calls into 1)
run_test "sdk" "circles_getValidInviters (addr1, default min)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getValidInviters\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getValidInviters (addr2, min 96)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getValidInviters\",\"params\":[\"$TEST_ADDR_2\",\"96\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getValidInviters (addr3, min 50)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getValidInviters\",\"params\":[\"$TEST_ADDR_3\",\"50\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Transaction History Enriched (server-side participant enrichment)
run_test "sdk" "circles_getTransactionHistoryEnriched (addr1, recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$TEST_ADDR_1\",30282299],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransactionHistoryEnriched (addr2, with limit)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$TEST_ADDR_2\",30282299,null,10],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransactionHistoryEnriched (addr3, block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$TEST_ADDR_3\",30282299,30283000,20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Search Profile by Address or Name (unified search with auto-detection)
run_test "sdk" "circles_searchProfileByAddressOrName (by address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_searchProfileByAddressOrName (by address prefix)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"0xde37\",10,0],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_searchProfileByAddressOrName (by text)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"berlin\",10,0,[\"CrcV2_RegisterHuman\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Query Methods
######################################################################

run_test "query" "circles_query (trust relations)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"CrcV2\",\"Table\":\"Stopped\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37\"]}]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (transaction history)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"CrcV1\",\"Table\":\"HubTransfer\",\"Limit\":10,\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":[\"0xc5d6c75087780e0c18820883cf5a580bb3a4d834\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"to\",\"Value\":[\"0xc5d6c75087780e0c18820883cf5a580bb3a4d834\"]}]}],\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"},{\"Column\":\"transactionIndex\",\"SortOrder\":\"DESC\"},{\"Column\":\"logIndex\",\"SortOrder\":\"DESC\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "query" "circles_query (v2 trust relations for addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_1\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"$TEST_ADDR_1\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (v2 trust relations for addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_2\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"$TEST_ADDR_2\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (v2 trust relations for addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_3\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"$TEST_ADDR_3\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "query" "circles_query (token info for addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (token info for addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (token info for addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0x1de1C49E7a623Cb3D1114bA0D40063F243ceeA\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "query" "circles_query (V_CrcV2_TrustRelations complex filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_1\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"expiryTime\",\"Value\":0}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (V_Crc_Tokens with type filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"type\",\"Value\":[\"CrcV2_PersonalMint\"]}],\"Limit\":5,\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"

if [[ "$SCHEMA_METADATA_AVAILABLE" == "true" ]]; then
    EVENT_TABLE_NAMES=()

    if [[ "$OUTPUT_MODE" != "json" ]]; then
        echo -e "${BLUE}--- circles_query table coverage (${#SCHEMA_TABLES[@]} tables) ---${NC}\n"
    fi

    for table_json in "${SCHEMA_TABLES[@]}"; do
        namespace=$(echo "$table_json" | jq -r '.namespace')
        table=$(echo "$table_json" | jq -r '.table')
        columns_json=$(echo "$table_json" | jq -c '.columns')
        order_clause=$(generate_order_clause "$columns_json")
        query_payload=$(generate_query_payload "$namespace" "$table" "$order_clause")
        run_test "query" "circles_query (${namespace}_${table})" "curl -s -X POST --data '$query_payload' -H \"Content-Type: application/json\" $RPC_URL"

        if [[ "$(is_event_table "$columns_json")" == "true" ]]; then
            EVENT_TABLE_NAMES+=("${namespace}_${table}")
        fi
    done
fi

######################################################################
# Events Methods
######################################################################

run_test "events" "circles_events (basic)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (sort ascending)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],null,true]}' -H \"Content-Type: application/json\" $RPC_URL"

if [[ ${#EVENT_TABLE_NAMES[@]} -gt 0 ]]; then
    if [[ "$OUTPUT_MODE" != "json" ]]; then
        echo -e "${BLUE}--- circles_events coverage (${#EVENT_TABLE_NAMES[@]} tables) ---${NC}\n"
    fi

    for event_table in "${EVENT_TABLE_NAMES[@]}"; do
        events_payload=$(generate_events_payload "$event_table")
        run_test "events" "circles_events ($event_table)" "curl -s -X POST --data '$events_payload' -H \"Content-Type: application/json\" $RPC_URL"
    done
fi

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Advanced Filter Predicate Tests ---${NC}\n"
fi

run_test "events" "circles_events (filter: blockNumber > 38000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: blockNumber <= 39000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38999000,39000000,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThanOrEquals\",\"Column\":\"blockNumber\",\"Value\":39000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: blockNumber IN array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38000100,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"In\",\"Column\":\"blockNumber\",\"Value\":[38000000,38000001,38000002]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: avatar != address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"NotEquals\",\"Column\":\"avatar\",\"Value\":\"0x0000000000000000000000000000000000000000\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: transactionHash IS NOT NULL)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: blockNumber > 38000000 AND blockNumber < 38001000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000},{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThan\",\"Column\":\"blockNumber\",\"Value\":38001000}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: blockNumber < 100 OR blockNumber > 40000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Signup\",\"CrcV2_RegisterHuman\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThan\",\"Column\":\"blockNumber\",\"Value\":38000100},{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000900}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: nested AND/OR with test addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38100000,[\"CrcV1_Trust\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000},{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"canSendTo\",\"Value\":\"$TEST_ADDR_3\"},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"user\",\"Value\":\"$TEST_ADDR_3\"}]}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: transactionHash LIKE pattern)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Like\",\"Column\":\"transactionHash\",\"Value\":\"0x%\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: blockNumber NOT IN array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"NotIn\",\"Column\":\"blockNumber\",\"Value\":[1,2,3,4,5]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: blockNumber >= 38000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThanOrEquals\",\"Column\":\"blockNumber\",\"Value\":38000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (filter: combined with test addr3 and block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$TEST_ADDR_3\",38000000,39000000,[\"CrcV1_Trust\",\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_Trust with expiry)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38500000,38501000,[\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"expiryTime\",\"Value\":0}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_TransferSingle with amount filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38500000,38501000,[\"CrcV2_TransferSingle\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"value\",\"Value\":0}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}All tests completed.${NC}\n"
fi
