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

# Diverse test addresses for comprehensive coverage
# Group addresses (RegisterGroup events)
GROUP_ADDR_1="0xc19bc204eb1c1d5b3fe500e5e5dfabab625f286c"  # Active group with GroupMint activity
GROUP_ADDR_2="0xfbb2caa8750be8d2e3057a9fc76af57bd641db8e"  # test-e2e-group
GROUP_ADDR_3="0xa646fc7956376a641d30448a0473348bcc5638e5"  # Frutero Club

# Organization addresses (RegisterOrganization events)
ORG_ADDR_1="0xa28c43f92f6498afae4266b29a668624ba031913"   # CirclesArbbotV2
ORG_ADDR_2="0xee310371e110ca0a6862e99a2d03d8e07f501ab4"   # Cow Swaper

# High-activity addresses (many transfers)
HIGH_ACTIVITY_ADDR_1="0x0afd8899bca011bb95611409f09c8efbf6b169cf"
HIGH_ACTIVITY_ADDR_2="0x97fd8f7829a019946329f6d2e763a72741047518"

# V1 addresses (CrcV1 Signup)
V1_USER_1="0x9393f1dc71b7a13d67453c6d7a8d4f0b112e5866"
V1_USER_2="0xaa01b45ff3e0a7aa27c3ca8d38d8b97ec39416f0"
V1_TOKEN_1="0xa553e1591e725765dc0f419f2b2c41e4819095fa"

# Addresses with ERC20 wrapper
WRAPPER_ADDR_1="0x3356876481f164bf6b4a82a64a8ce5ed28f753ed"
ERC20_WRAPPER_1="0x726f084f94e28821655ccae52c889130054f55ff"

# Stopped avatar (revoked)
STOPPED_AVATAR="0xeb94174e82d6a070dcb0135b09270de4a3a3bce0"

# Safe proxy addresses
SAFE_PROXY_1="0xccfe783b4dce0869beb7d4c9db4d4bd61e3aee31"

# Addresses with GroupMint activity
GROUPMINT_USER="0xf9117e9931e6ab91f025e1afa4e70cafa5e0aa1e"

# CrcV1 HubTransfer addresses
V1_TRANSFER_FROM="0xa9f4ef92c814f01f16b92d472595a6820f48e36a"
V1_TRANSFER_TO="0x16c6aea3d4069994c0fe7dc26884a4cc1d3dc255"

# Invitation test addresses
INVITE_ESCROW_USER="0x6366dd088e7979a4d16f4ab892b78d41051f45a6"      # Registered via escrow (InvitationRedeemed)
INVITE_ATSCALE_USER="0x4e7cee95f04a44f3b67f0f329212f14a18b65b16"     # Registered via at-scale
INVITE_STANDARD_USER="0xd40133ea712e7012a95fdd3c008ab58f7918b446"    # Registered via standard V2
INVITE_PENDING_ESCROW="0x0031e792b8703b66c086b2ab5af1e792f4c3be40"   # Has active escrow invitation
INVITE_PENDING_ATSCALE="0x360925b571db7d8029959a702896afd12919da8e"  # Has unclaimed at-scale account

CATEGORY_KEYS=(
    "system"
    "balance"
    "avatar"
    "trust"
    "sdk"
    "query"
    "events"
    "consistency"
    "pagination"
    "precision"
    "edgecases"
    "gas_estimation"
)

CATEGORY_LABELS=(
    "System Methods"
    "Balance & Token Methods"
    "Avatar & Profile Methods"
    "Trust & Network Methods"
    "SDK Enablement Methods"
    "Query Methods"
    "Events Methods"
    "Data Consistency Tests"
    "Pagination Deep Comparison Tests"
    "Balance Precision Tests"
    "Edge Case & Error Handling Tests"
    "Gas Estimation Tests"
)

CATEGORY_FILE_NAMES=(
    "01-system-methods.jsonl"
    "02-balance-token-methods.jsonl"
    "03-avatar-profile-methods.jsonl"
    "04-trust-network-methods.jsonl"
    "05-sdk-methods.jsonl"
    "06-query-methods.jsonl"
    "07-events-methods.jsonl"
    "08-consistency-tests.jsonl"
    "09-pagination-tests.jsonl"
    "10-precision-tests.jsonl"
    "11-edgecase-tests.jsonl"
    "12-gas-estimation-tests.jsonl"
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

    # Extract the JSON payload from curl command for regression testing pagination support
    local request_payload=""
    if [[ "$curl_cmd" =~ --data[[:space:]]+\'([^\']+)\' ]]; then
        request_payload="${BASH_REMATCH[1]}"
    fi
    local request_escaped=""
    if [[ -n "$request_payload" ]]; then
        request_escaped=$(echo "$request_payload" | jq -c '.' 2>/dev/null || echo "")
    fi

    if [[ "$OUTPUT_MODE" == "json" ]]; then
        if [[ -n "$JSON_DIR" ]]; then
            local category_file_path
            category_file_path=$(get_category_file_path "$category")
            # Include timing and request in JSON output for regression testing
            if [[ -n "$request_escaped" ]]; then
                echo "{\"test\":\"$test_name\",\"response\":$response_min,\"request\":$request_escaped,\"timing\":{\"total_ms\":$time_total_ms,\"dns_ms\":$time_dns_ms,\"connect_ms\":$time_connect_ms,\"ttfb_ms\":$time_ttfb_ms}}" >> "$category_file_path"
            else
                echo "{\"test\":\"$test_name\",\"response\":$response_min,\"timing\":{\"total_ms\":$time_total_ms,\"dns_ms\":$time_dns_ms,\"connect_ms\":$time_connect_ms,\"ttfb_ms\":$time_ttfb_ms}}" >> "$category_file_path"
            fi
        else
            if [[ -n "$request_escaped" ]]; then
                echo "{\"test\":\"$test_name\",\"response\":$response_min,\"request\":$request_escaped,\"timing\":{\"total_ms\":$time_total_ms,\"dns_ms\":$time_dns_ms,\"connect_ms\":$time_connect_ms,\"ttfb_ms\":$time_ttfb_ms}}"
            else
                echo "{\"test\":\"$test_name\",\"response\":$response_min,\"timing\":{\"total_ms\":$time_total_ms,\"dns_ms\":$time_dns_ms,\"connect_ms\":$time_connect_ms,\"ttfb_ms\":$time_ttfb_ms}}"
            fi
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

# Helper function for cleaner curl commands with heredoc JSON
# Usage: run_test_json "category" "test_name" 'JSON_PAYLOAD'
run_test_json() {
    local category="$1"
    local test_name="$2"
    local json_payload="$3"
    run_test "$category" "$test_name" "curl -s -X POST --data '$json_payload' -H 'Content-Type: application/json' $RPC_URL"
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
run_test "system" "rpc.discover" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"rpc.discover\",\"params\":[],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
# circles_tables MUST be last in system — its response is parsed for schema metadata below
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

# Consented Flow Tests
# When simulatedConsentedAvatars includes the source, transfers through untrusted intermediaries should be blocked
# Test 1: Without consented flow - should find path normally
run_test "trust" "circlesV2_findPath (no consented flow)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 2: With consented flow on source - if source doesn't trust intermediate, flow should be reduced
# Source has consented flow, intermediate doesn't have consented flow -> transfers blocked from source
run_test "trust" "circlesV2_findPath (simulated consented flow on source)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}],\"simulatedConsentedAvatars\":[\"$TEST_ADDR_1\"]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 3: Both source and sink have consented flow, and source trusts sink (via simulatedTrusts)
# This should allow transfer if source->sink direct edge exists
run_test "trust" "circlesV2_findPath (consented flow with mutual trust)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_2\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}],\"simulatedConsentedAvatars\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\"],\"simulatedTrusts\":[{\"Truster\":\"$TEST_ADDR_1\",\"Trustee\":\"$TEST_ADDR_2\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Quantized Mode Tests (Invitation Module)
# quantizedMode ensures all sink-bound transfers are exact multiples of 96 CRC for invitations.
# 96 CRC = 96000000000000000000 wei (96 * 10^18)

# Test 1: Without quantizedMode - baseline comparison (returns any valid flow)
run_test "trust" "circlesV2_findPath (no quantizedMode - baseline)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"192000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"500000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 2: With quantizedMode, request 1 invite (96 CRC) - should return exactly 96 CRC
run_test "trust" "circlesV2_findPath (quantizedMode=true, 1 invite)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"96000000000000000000\",\"quantizedMode\":true,\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"500000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 3: With quantizedMode, request 2 invites (192 CRC) - should return exactly 192 CRC
run_test "trust" "circlesV2_findPath (quantizedMode=true, 2 invites)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"192000000000000000000\",\"quantizedMode\":true,\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"500000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 4: With quantizedMode, request 5 invites (480 CRC) - should return up to 5 invites worth
run_test "trust" "circlesV2_findPath (quantizedMode=true, 5 invites)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"480000000000000000000\",\"quantizedMode\":true,\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"500000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 5: With quantizedMode, max discovery - find all possible invites
run_test "trust" "circlesV2_findPath (quantizedMode=true, max invites discovery)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"115792089237316195423570985008687907853269984665640564039457584007913129639935\",\"quantizedMode\":true,\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"500000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 6: With quantizedMode, insufficient balance for 1 invite - should return empty
run_test "trust" "circlesV2_findPath (quantizedMode=true, insufficient for invite)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"96000000000000000000\",\"quantizedMode\":true,\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"50000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 7: With quantizedMode, partial balance (100 CRC, enough for 1 but not 2) - should return 96 CRC
run_test "trust" "circlesV2_findPath (quantizedMode=true, partial balance rounds down)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"192000000000000000000\",\"quantizedMode\":true,\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"100000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 8: Without quantizedMode but same params - compare behavior
run_test "trust" "circlesV2_findPath (no quantizedMode, same params as test 7)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"192000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"$TOKEN_ADDR_1\",\"Amount\":\"100000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# B. Trust Method Coverage Enhancements

# B1. Verify limit=0 entries are NOT returned (revoked trusts)
# circles_getTrustRelations should exclude trusts with limit=0
run_test "trust" "circles_getTrustRelations (stopped avatar - revoked)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$STOPPED_AVATAR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Trust relations for group (groups trust members)
run_test "trust" "circles_getTrustRelations (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Trust relations for organization
run_test "trust" "circles_getTrustRelations (organization)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$ORG_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Trust for V1-only user
run_test "trust" "circles_getTrustRelations (v1 user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$V1_USER_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# B2. Query V2 trust relations and verify no limit=0 entries appear
run_test_json "trust" "circles_query (V_CrcV2_TrustRelations - verify no limit=0)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "TrustRelations",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "Equals", "Column": "truster", "Value": ["'"$STOPPED_AVATAR"'"]},
        {"Type": "FilterPredicate", "FilterType": "Equals", "Column": "expiryTime", "Value": 0}
      ]
    }],
    "Limit": 10
  }]
}'

# B3. Common trust with high-activity address
run_test "trust" "circles_getCommonTrust (high-activity pair)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",\"$HIGH_ACTIVITY_ADDR_2\"]}' -H \"Content-Type: application/json\" $RPC_URL"

# B4. Common trust with group and user
run_test "trust" "circles_getCommonTrust (group+user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$GROUP_ADDR_1\",\"$GROUPMINT_USER\"]}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getAggregatedTrustRelations (non-enriched version)
run_test "trust" "circles_getAggregatedTrustRelations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getAggregatedTrustRelations (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelations\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "trust" "circles_getAggregatedTrustRelations (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelations\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

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

# Invitation Origin (reconstructs how user was invited)
run_test "sdk" "circles_getInvitationOrigin (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getInvitationOrigin (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getInvitationOrigin (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Invitation Origin - specific invitation types (staging only - InvitationsAtScale tables)
run_test "sdk" "circles_getInvitationOrigin (v2_escrow)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$INVITE_ESCROW_USER\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getInvitationOrigin (v2_at_scale)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$INVITE_ATSCALE_USER\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getInvitationOrigin (v2_standard)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$INVITE_STANDARD_USER\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# All Invitations - get pending invitations by type (staging only - InvitationsAtScale tables)
run_test "sdk" "circles_getAllInvitations (pending escrow)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAllInvitations\",\"params\":[\"$INVITE_PENDING_ESCROW\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getAllInvitations (pending at-scale)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAllInvitations\",\"params\":[\"$INVITE_PENDING_ATSCALE\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getAllInvitations (with min balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAllInvitations\",\"params\":[\"$TEST_ADDR_1\",\"96\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Transaction History Enriched (server-side participant enrichment)
run_test "sdk" "circles_getTransactionHistoryEnriched (addr1, recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$TEST_ADDR_1\",30282299],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransactionHistoryEnriched (addr2, with limit)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$TEST_ADDR_2\",30282299,null,10],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransactionHistoryEnriched (addr3, block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$TEST_ADDR_3\",30282299,30283000,20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Search Profile by Address or Name (unified search with auto-detection)
run_test "sdk" "circles_searchProfileByAddressOrName (by address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_searchProfileByAddressOrName (by address prefix)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"0xde37\",10,0],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_searchProfileByAddressOrName (by text)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"berlin\",10,0,[\"CrcV2_RegisterHuman\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Transfer Data (calldata bytes from ERC-1155 transfers)
# params: [address, direction?, counterparty?, fromBlock?, toBlock?, limit?, cursor?]
run_test "sdk" "circles_getTransferData (addr only)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransferData (block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",null,null,43000000,44000000],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransferData (sent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",\"sent\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransferData (received)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",\"received\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "sdk" "circles_getTransferData (with counterparty)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",\"sent\",\"$HIGH_ACTIVITY_ADDR_2\",null,null,10],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# D. New Method Smoke Tests - extended entity type coverage

# circles_getAggregatedTrustRelationsEnriched - groups and orgs
run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (org)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$ORG_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (v1 user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$V1_USER_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getAggregatedTrustRelationsEnriched (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getAllInvitations - extended
run_test "sdk" "circles_getAllInvitations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAllInvitations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getAllInvitations (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAllInvitations\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getProfileView - extended entity types
run_test "sdk" "circles_getProfileView (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getProfileView (org)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$ORG_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getProfileView (v1 user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$V1_USER_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getProfileView (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getTrustNetworkSummary - extended
run_test "sdk" "circles_getTrustNetworkSummary (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustNetworkSummary\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getTrustNetworkSummary (org)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustNetworkSummary\",\"params\":[\"$ORG_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getTransactionHistoryEnriched - extended entity types
run_test "sdk" "circles_getTransactionHistoryEnriched (group, recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$GROUP_ADDR_1\",30282299],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getTransactionHistoryEnriched (high-activity, limited)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistoryEnriched\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",30282299,null,5],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getValidInviters - extended
run_test "sdk" "circles_getValidInviters (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getValidInviters\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_searchProfileByAddressOrName - extended
run_test "sdk" "circles_searchProfileByAddressOrName (by name prefix)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"Fruter\",5,0],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_searchProfileByAddressOrName (empty search)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_searchProfileByAddressOrName\",\"params\":[\"zzzznonexistent99999\",10,0],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# circles_getTransferData - extended
run_test "sdk" "circles_getTransferData (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getTransferData (with limit)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",null,null,null,null,5],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Invitation sub-type methods
run_test "sdk" "circles_getTrustInvitations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustInvitations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getTrustInvitations (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustInvitations\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getEscrowInvitations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getEscrowInvitations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getAtScaleInvitations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAtScaleInvitations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getInvitationsFrom (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationsFrom\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getInvitationsFrom (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationsFrom\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "sdk" "circles_getInvitationsFrom (accepted only)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationsFrom\",\"params\":[\"$TEST_ADDR_1\",true],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

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
# Query Tests - Views
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- View Query Tests ---${NC}\n"
fi

# V_CrcV2_TrustRelations
run_test "query" "circles_query (V_CrcV2_TrustRelations for groupmint user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$GROUPMINT_USER\"]}],\"Limit\":10,\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_CrcV2_Avatars
run_test "query" "circles_query (V_CrcV2_Avatars for group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"Avatars\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"$GROUP_ADDR_1\"]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_query (V_CrcV2_Avatars for organization)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"Avatars\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"$ORG_ADDR_1\"]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_CrcV2_BalancesByAccountAndToken
run_test "query" "circles_query (V_CrcV2_BalancesByAccountAndToken for user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"BalancesByAccountAndToken\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"account\",\"Value\":[\"$GROUPMINT_USER\"]}],\"Limit\":20}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_CrcV2_GroupMemberships
run_test "query" "circles_query (V_CrcV2_GroupMemberships for group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"GroupMemberships\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"group\",\"Value\":[\"$GROUP_ADDR_1\"]}],\"Limit\":20}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_CrcV2_Groups
run_test "query" "circles_query (V_CrcV2_Groups all)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"Groups\",\"Columns\":[],\"Limit\":10,\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_CrcV1_Avatars
run_test "query" "circles_query (V_CrcV1_Avatars for v1 user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV1\",\"Table\":\"Avatars\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"$V1_USER_1\"]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_Crc_Avatars (combined view)
run_test "query" "circles_query (V_Crc_Avatars combined for multiple)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Avatars\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"In\",\"Column\":\"avatar\",\"Value\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\",\"$GROUP_ADDR_1\"]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_Safe_Owners
run_test "query" "circles_query (V_Safe_Owners for safeAddress)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Safe\",\"Table\":\"Owners\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"safeAddress\",\"Value\":[\"$SAFE_PROXY_1\"]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# V_Crc_Transfers
run_test "query" "circles_query (V_Crc_Transfers for user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Transfers\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":[\"$TEST_ADDR_1\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"to\",\"Value\":[\"$TEST_ADDR_1\"]}]}],\"Limit\":20,\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Edge case: Empty results query test
run_test "query" "circles_query (empty result - nonexistent avatar)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"Avatars\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"0x0000000000000000000000000000000000000001\"]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Large number tests (attocircles - 18 decimals)
run_test "query" "circles_query (large numbers - balances with totalBalance filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"BalancesByAccountAndToken\",\"Columns\":[],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"totalBalance\",\"Value\":\"1000000000000000000\"}],\"Limit\":5}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Group discovery and membership methods
run_test "query" "circles_findGroups (default)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_findGroups\",\"params\":[10],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_findGroups (with name filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_findGroups\",\"params\":[10,{\"nameContains\":\"Circles\"}],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getGroupMembers (group1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getGroupMembers\",\"params\":[\"$GROUP_ADDR_1\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getGroupMembers (group3 Frutero)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getGroupMembers\",\"params\":[\"$GROUP_ADDR_3\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getGroupMemberships (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getGroupMemberships\",\"params\":[\"$TEST_ADDR_1\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getGroupMemberships (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getGroupMemberships\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getGroupMemberships (groupmint user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getGroupMemberships\",\"params\":[\"$GROUPMINT_USER\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Transaction history (non-enriched)
run_test "query" "circles_getTransactionHistory (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistory\",\"params\":[\"$TEST_ADDR_1\",10],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getTransactionHistory (high-activity, limited)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistory\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",5],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getTransactionHistory (group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransactionHistory\",\"params\":[\"$GROUP_ADDR_1\",10],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Token holders
run_test "query" "circles_getTokenHolders (token1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenHolders\",\"params\":[\"$TOKEN_ADDR_1\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "query" "circles_getTokenHolders (group token)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenHolders\",\"params\":[\"$GROUP_ADDR_1\",20],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# Paginated query (vs legacy circles_query)
run_test_json "query" "circles_paginated_query (V_CrcV2_TrustRelations)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_paginated_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "TrustRelations",
    "Columns": [],
    "Filter": [{"Type": "FilterPredicate", "FilterType": "Equals", "Column": "truster", "Value": ["'"$TEST_ADDR_1"'"]}],
    "Limit": 10,
    "Order": [{"Column": "blockNumber", "SortOrder": "DESC"}]
  }]
}'
run_test_json "query" "circles_paginated_query (CrcV2_TransferSingle)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_paginated_query",
  "params": [{
    "Namespace": "CrcV2",
    "Table": "TransferSingle",
    "Columns": [],
    "Filter": [{"Type": "FilterPredicate", "FilterType": "Equals", "Column": "from", "Value": ["'"$HIGH_ACTIVITY_ADDR_1"'"]}],
    "Limit": 5,
    "Order": [{"Column": "blockNumber", "SortOrder": "DESC"}]
  }]
}'

######################################################################
# Events Methods
######################################################################

run_test "events" "circles_events (basic)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (sort ascending)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],null,true]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events_paginated (basic)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events_paginated\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events_paginated (with limit)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events_paginated\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],null,false,10]}' -H \"Content-Type: application/json\" $RPC_URL"

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
run_test "events" "circles_events (filter: combined with test addr3 and block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$TEST_ADDR_3\",38000000,38100000,[\"CrcV1_Trust\",\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_Trust with expiry)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38500000,38501000,[\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"expiryTime\",\"Value\":0}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_TransferSingle with amount filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38500000,38501000,[\"CrcV2_TransferSingle\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"value\",\"Value\":0}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Diverse Event Type Tests - CrcV1
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- CrcV1 Event Tests ---${NC}\n"
fi

# CrcV1 Signup events
run_test "events" "circles_events (CrcV1_Signup recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43340000,43345000,[\"CrcV1_Signup\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV1_Signup for user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43430000,43435000,[\"CrcV1_Signup\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"user\",\"Value\":\"$V1_USER_1\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV1 Trust events
run_test "events" "circles_events (CrcV1_Trust recent block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43000000,43001000,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV1_Trust for user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$V1_USER_2\",null,null,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV1_Trust limit filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38050000,[\"CrcV1_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"limit\",\"Value\":0}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV1 HubTransfer events
run_test "events" "circles_events (CrcV1_HubTransfer recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43525000,43530000,[\"CrcV1_HubTransfer\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV1_HubTransfer from)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43525000,43530000,[\"CrcV1_HubTransfer\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":\"$V1_TRANSFER_FROM\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV1_HubTransfer large amount)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43500000,43550000,[\"CrcV1_HubTransfer\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"amount\",\"Value\":\"100000000000000000\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV1 Transfer events
run_test "events" "circles_events (CrcV1_Transfer for token)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43430000,43435000,[\"CrcV1_Transfer\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"tokenAddress\",\"Value\":\"$V1_TOKEN_1\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Diverse Event Type Tests - CrcV2
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- CrcV2 Event Tests ---${NC}\n"
fi

# CrcV2 RegisterHuman events
run_test "events" "circles_events (CrcV2_RegisterHuman recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_RegisterHuman\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_RegisterHuman for avatar)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43585000,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":\"0xb3393dd1d89dfec04ac3d5938525baf5c128c937\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 RegisterGroup events
run_test "events" "circles_events (CrcV2_RegisterGroup recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43400000,43470000,[\"CrcV2_RegisterGroup\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_RegisterGroup for group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43400000,43470000,[\"CrcV2_RegisterGroup\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"group\",\"Value\":\"$GROUP_ADDR_2\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 RegisterOrganization events
run_test "events" "circles_events (CrcV2_RegisterOrganization recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43540000,43590000,[\"CrcV2_RegisterOrganization\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_RegisterOrganization for org)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_RegisterOrganization\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"organization\",\"Value\":\"$ORG_ADDR_1\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 Trust events
run_test "events" "circles_events (CrcV2_Trust for high-activity user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$GROUPMINT_USER\",null,null,[\"CrcV2_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_Trust truster filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43190000,43200000,[\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":\"$GROUPMINT_USER\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 TransferSingle events
run_test "events" "circles_events (CrcV2_TransferSingle for high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\",43500000,43510000,[\"CrcV2_TransferSingle\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_TransferSingle from filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_TransferSingle\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":\"$GROUPMINT_USER\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_TransferSingle to filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_TransferSingle\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"to\",\"Value\":\"$GROUPMINT_USER\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 TransferBatch events
run_test "events" "circles_events (CrcV2_TransferBatch recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_TransferBatch\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 GroupMint events
run_test "events" "circles_events (CrcV2_GroupMint recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_GroupMint\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_GroupMint for group)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_GroupMint\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"group\",\"Value\":\"$GROUP_ADDR_1\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_GroupMint for sender)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_GroupMint\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"sender\",\"Value\":\"$GROUPMINT_USER\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 PersonalMint events
run_test "events" "circles_events (CrcV2_PersonalMint recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_PersonalMint\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_PersonalMint for human)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_PersonalMint\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"human\",\"Value\":\"$GROUPMINT_USER\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 Stopped events
run_test "events" "circles_events (CrcV2_Stopped all)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,[\"CrcV2_Stopped\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_Stopped for avatar)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$STOPPED_AVATAR\",null,null,[\"CrcV2_Stopped\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 ERC20WrapperDeployed events
run_test "events" "circles_events (CrcV2_ERC20WrapperDeployed recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_ERC20WrapperDeployed\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (CrcV2_ERC20WrapperDeployed for avatar)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43585000,43590000,[\"CrcV2_ERC20WrapperDeployed\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":\"$WRAPPER_ADDR_1\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 Erc20WrapperTransfer events
run_test "events" "circles_events (CrcV2_Erc20WrapperTransfer recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_Erc20WrapperTransfer\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 DiscountCost events
run_test "events" "circles_events (CrcV2_DiscountCost recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_DiscountCost\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 StreamCompleted events
run_test "events" "circles_events (CrcV2_StreamCompleted recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43500000,43590000,[\"CrcV2_StreamCompleted\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# CrcV2 ApprovalForAll events
run_test "events" "circles_events (CrcV2_ApprovalForAll recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_ApprovalForAll\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Safe Event Tests
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Safe Event Tests ---${NC}\n"
fi

run_test "events" "circles_events (Safe_ProxyCreation recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"Safe_ProxyCreation\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (Safe_ProxyCreation for proxy)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43589000,43590000,[\"Safe_ProxyCreation\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"proxy\",\"Value\":\"$SAFE_PROXY_1\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (Safe_SafeSetup recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"Safe_SafeSetup\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (Safe_AddedOwner recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43500000,43590000,[\"Safe_AddedOwner\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (Safe_RemovedOwner recent)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43500000,43590000,[\"Safe_RemovedOwner\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Edge Case Tests
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Edge Case Tests ---${NC}\n"
fi

# Empty results tests - events
run_test "events" "circles_events (empty result - nonexistent address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"0x0000000000000000000000000000000000000001\",null,null,[\"CrcV2_RegisterHuman\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (empty result - future block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,99999999,100000000,[\"CrcV2_TransferSingle\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Multiple event types in single request
run_test "events" "circles_events (multiple event types)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_RegisterHuman\",\"CrcV2_RegisterGroup\",\"CrcV2_RegisterOrganization\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "events" "circles_events (all v1 events)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Signup\",\"CrcV1_Trust\",\"CrcV1_HubTransfer\",\"CrcV1_Transfer\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Complex nested filter
run_test "events" "circles_events (complex nested filter)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43500000,43590000,[\"CrcV2_TransferSingle\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":43580000},{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":\"$GROUPMINT_USER\"},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"to\",\"Value\":\"$GROUPMINT_USER\"}]}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Pagination test with cursor
run_test "events" "circles_events (pagination - with limit)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43590000,[\"CrcV2_TransferSingle\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Data Consistency Tests
# These tests query specific block ranges to verify data integrity
# between environments. The regression script compares event counts
# and data structure to ensure staging matches production.
#
# Block ranges covered:
# - Early V1: ~12.5M-13M (first Circles events)
# - Mid V1:   ~20M-21M (established V1 activity)
# - V2 Start: ~35M-36M (V2 launch period)
# - Recent:   ~43M+ (current activity)
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Data Consistency Tests ---${NC}\n"
    echo -e "${CYAN}Testing data integrity across different blockchain eras${NC}\n"
fi

# =====================================================================
# EARLY V1 ERA (~12.5M - 13M blocks) - First Circles events
# Reduced block ranges to keep results under ~500 events
# =====================================================================

run_test_json "consistency" "consistency: CrcV1_Signup early era (12.5M-12.51M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 12500000, 12510000, ["CrcV1_Signup"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_Trust early era (12.5M-12.505M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 12500000, 12505000, ["CrcV1_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_HubTransfer early era (12.5M-12.55M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 12500000, 12550000, ["CrcV1_HubTransfer"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_Transfer early era (12.5M-12.502M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 12500000, 12502000, ["CrcV1_Transfer"], null, false]
}'

run_test_json "consistency" "consistency: all V1 events early era (12.5M-12.502M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 12500000, 12502000, ["CrcV1_Signup", "CrcV1_Trust", "CrcV1_HubTransfer", "CrcV1_Transfer"], null, false]
}'

# =====================================================================
# MID V1 ERA (~20M - 21M blocks) - Established V1 activity
# Reduced block ranges to keep results under ~500 events
# =====================================================================

run_test_json "consistency" "consistency: CrcV1_Signup mid era (20M-20.05M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 20000000, 20050000, ["CrcV1_Signup"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_Trust mid era (20M-20.02M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 20000000, 20020000, ["CrcV1_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_HubTransfer mid era (20M-20.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 20000000, 20100000, ["CrcV1_HubTransfer"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_Transfer mid era (20M-20.005M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 20000000, 20005000, ["CrcV1_Transfer"], null, false]
}'

# =====================================================================
# V2 LAUNCH ERA (~35M - 36M blocks) - Critical V2 start period
# Reduced block ranges to keep results under ~500 events
# =====================================================================

run_test_json "consistency" "consistency: CrcV2_RegisterHuman v2 launch (35M-35.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35100000, ["CrcV2_RegisterHuman"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_RegisterGroup v2 launch (35M-35.5M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35500000, ["CrcV2_RegisterGroup"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_Trust v2 launch (35M-35.05M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35050000, ["CrcV2_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_TransferSingle v2 launch (35M-35.02M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35020000, ["CrcV2_TransferSingle"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_PersonalMint v2 launch (35M-35.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35100000, ["CrcV2_PersonalMint"], null, false]
}'

run_test_json "consistency" "consistency: all V2 registration events v2 launch (35M-35.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35100000, ["CrcV2_RegisterHuman", "CrcV2_RegisterGroup", "CrcV2_RegisterOrganization"], null, false]
}'

run_test_json "consistency" "consistency: Safe events v2 launch (35M-35.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 35000000, 35100000, ["Safe_ProxyCreation", "Safe_SafeSetup"], null, false]
}'

# =====================================================================
# POST-V2 GROWTH ERA (~38M - 39M blocks) - V2 adoption period
# Reduced block ranges to keep results under ~500 events
# =====================================================================

run_test_json "consistency" "consistency: CrcV2_RegisterHuman growth era (38M-38.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 38000000, 38100000, ["CrcV2_RegisterHuman"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_Trust growth era (38M-38.05M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 38000000, 38050000, ["CrcV2_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_TransferSingle growth era (38M-38.02M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 38000000, 38020000, ["CrcV2_TransferSingle"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_GroupMint growth era (38M-38.5M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 38000000, 38500000, ["CrcV2_GroupMint"], null, false]
}'

run_test_json "consistency" "consistency: V1 ongoing activity during V2 (38M-38.1M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 38000000, 38100000, ["CrcV1_Trust", "CrcV1_HubTransfer"], null, false]
}'

# =====================================================================
# RECENT ERA (~43M+ blocks) - Current activity
# Reduced block ranges to keep results under ~500 events
# =====================================================================

run_test_json "consistency" "consistency: CrcV2_RegisterHuman recent (43.5M-43.55M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43550000, ["CrcV2_RegisterHuman"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_Trust recent (43.5M-43.52M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43520000, ["CrcV2_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_TransferSingle recent (43.5M-43.51M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43510000, ["CrcV2_TransferSingle"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_TransferBatch recent (43.5M-43.6M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43600000, ["CrcV2_TransferBatch"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_PersonalMint recent (43.5M-43.55M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43550000, ["CrcV2_PersonalMint"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_GroupMint recent (43.5M-43.6M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43600000, ["CrcV2_GroupMint"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_Stopped recent (43M-43.6M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43000000, 43600000, ["CrcV2_Stopped"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_ERC20WrapperDeployed recent (43.5M-43.6M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43600000, ["CrcV2_ERC20WrapperDeployed"], null, false]
}'

run_test_json "consistency" "consistency: CrcV2_Erc20WrapperTransfer recent (43.5M-43.6M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43600000, ["CrcV2_Erc20WrapperTransfer"], null, false]
}'

run_test_json "consistency" "consistency: Safe_ProxyCreation recent (43.5M-43.55M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43550000, ["Safe_ProxyCreation"], null, false]
}'

# =====================================================================
# QUERY-BASED CONSISTENCY TESTS - Views and aggregated data
# Reduced block ranges to keep results under ~500 rows
# =====================================================================

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Query-based Consistency Tests ---${NC}\n"
fi

# V_CrcV2_Avatars count in block ranges
run_test_json "consistency" "consistency: V_CrcV2_Avatars registered 35M-35.2M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "Avatars",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 35000000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 35200000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

run_test_json "consistency" "consistency: V_CrcV2_Avatars registered 38M-38.2M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "Avatars",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 38000000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 38200000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

# V_CrcV1_Avatars early
run_test_json "consistency" "consistency: V_CrcV1_Avatars registered 12.5M-12.6M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV1",
    "Table": "Avatars",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 12500000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 12600000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

# V_CrcV2_TrustRelations in block range
run_test_json "consistency" "consistency: V_CrcV2_TrustRelations 35M-35.1M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "TrustRelations",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 35000000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 35100000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

# V_CrcV2_Groups in block range
run_test_json "consistency" "consistency: V_CrcV2_Groups registered 35M-38M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "Groups",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 35000000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 38000000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

# V_Crc_Transfers in block range (combined V1+V2)
run_test_json "consistency" "consistency: V_Crc_Transfers 38M-38.02M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_Crc",
    "Table": "Transfers",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 38000000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 38020000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

# V_Safe_Owners in block range
run_test_json "consistency" "consistency: V_Safe_Owners created 35M-35.2M" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_Safe",
    "Table": "Owners",
    "Columns": [],
    "Filter": [{
      "Type": "Conjunction",
      "ConjunctionType": "And",
      "Predicates": [
        {"Type": "FilterPredicate", "FilterType": "GreaterThanOrEquals", "Column": "blockNumber", "Value": 35000000},
        {"Type": "FilterPredicate", "FilterType": "LessThan", "Column": "blockNumber", "Value": 35200000}
      ]
    }],
    "Limit": 500,
    "Order": [{"Column": "blockNumber", "SortOrder": "ASC"}]
  }]
}'

# =====================================================================
# CROSS-ERA CONSISTENCY - Same queries across different time periods
# =====================================================================

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Cross-Era Consistency Tests ---${NC}\n"
fi

# CrcV1_Trust across multiple eras (should have consistent schema)
run_test_json "consistency" "consistency: CrcV1_Trust schema check era1 (13M-13.02M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 13000000, 13020000, ["CrcV1_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_Trust schema check era2 (25M-25.02M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 25000000, 25020000, ["CrcV1_Trust"], null, false]
}'

run_test_json "consistency" "consistency: CrcV1_Trust schema check era3 (40M-40.02M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 40000000, 40020000, ["CrcV1_Trust"], null, false]
}'

# Combined V1+V2 events in recent blocks
run_test_json "consistency" "consistency: all registration events recent (43.5M-43.52M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43520000, ["CrcV1_Signup", "CrcV2_RegisterHuman", "CrcV2_RegisterGroup", "CrcV2_RegisterOrganization"], null, false]
}'

run_test_json "consistency" "consistency: all trust events recent (43.5M-43.52M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43520000, ["CrcV1_Trust", "CrcV2_Trust"], null, false]
}'

run_test_json "consistency" "consistency: all transfer events recent (43.5M-43.51M)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43510000, ["CrcV1_HubTransfer", "CrcV1_Transfer", "CrcV2_TransferSingle", "CrcV2_TransferBatch", "CrcV2_Erc20WrapperTransfer"], null, false]
}'

######################################################################
# A. Events Pagination Deep Comparison Tests
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Pagination Deep Comparison Tests ---${NC}\n"
fi

# A1. Paginated events - page 1 with small limit (tests cursor generation)
run_test_json "pagination" "pagination: events page1 (CrcV2_Trust, limit 10)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43520000, ["CrcV2_Trust"], null, false]
}'

# A2. Paginated events - different event types with pagination
run_test_json "pagination" "pagination: events page1 (CrcV2_TransferSingle, limit 10)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43580000, 43590000, ["CrcV2_TransferSingle"], null, false]
}'

# A3. Paginated events - ascending sort (tests cursor direction)
run_test_json "pagination" "pagination: events ascending (CrcV2_Trust)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43500000, 43520000, ["CrcV2_Trust"], null, true]
}'

# A4. Paginated events - single address filter
run_test_json "pagination" "pagination: events for address (CrcV2_TransferSingle)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": ["'"$HIGH_ACTIVITY_ADDR_1"'", 43500000, 43600000, ["CrcV2_TransferSingle"], null, false]
}'

# A5. Paginated query - V_CrcV2_TrustRelations small limit
run_test_json "pagination" "pagination: query V_CrcV2_TrustRelations (limit 5)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "TrustRelations",
    "Columns": [],
    "Filter": [{"Type": "FilterPredicate", "FilterType": "Equals", "Column": "truster", "Value": ["'"$TEST_ADDR_1"'"]}],
    "Limit": 5,
    "Order": [{"Column": "blockNumber", "SortOrder": "DESC"}]
  }]
}'

# A6. Paginated query - larger limit for count comparison
run_test_json "pagination" "pagination: query V_CrcV2_TrustRelations (limit 50)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "TrustRelations",
    "Columns": [],
    "Filter": [{"Type": "FilterPredicate", "FilterType": "Equals", "Column": "truster", "Value": ["'"$TEST_ADDR_1"'"]}],
    "Limit": 50,
    "Order": [{"Column": "blockNumber", "SortOrder": "DESC"}]
  }]
}'

# A7. Events for multiple types - tests combined pagination
run_test_json "pagination" "pagination: events multi-type (registration events)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43580000, 43590000, ["CrcV2_RegisterHuman", "CrcV2_RegisterGroup", "CrcV2_RegisterOrganization"], null, false]
}'

# A8. Events - narrow block range to test small result sets
run_test_json "pagination" "pagination: events narrow range (10 blocks)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_events",
  "params": [null, 43580000, 43580010, ["CrcV2_TransferSingle"], null, false]
}'

######################################################################
# C. Balance Precision Tests
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Balance Precision Tests ---${NC}\n"
fi

# C1. Token balances - compare staticAttoCircles (should be exact match between envs)
run_test "precision" "precision: getTokenBalances (addr1 - static values)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "precision" "precision: getTokenBalances (addr2 - static values)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "precision" "precision: getTokenBalances (high-activity - static values)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# C2. Raw (non-demurraged) balance should be exact match
run_test "precision" "precision: getTotalBalance raw (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "precision" "precision: getTotalBalance raw (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_2\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "precision" "precision: getTotalBalance raw (high-activity)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$HIGH_ACTIVITY_ADDR_1\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# C3. V2 raw balance should be exact
run_test "precision" "precision: V2 getTotalBalance raw (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "precision" "precision: V2 getTotalBalance raw (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_2\", false],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# C4. V_CrcV2_BalancesByAccountAndToken - query static balances directly
run_test_json "precision" "precision: V_CrcV2_BalancesByAccountAndToken (addr1)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "BalancesByAccountAndToken",
    "Columns": ["account", "tokenAddress", "tokenId", "totalBalance"],
    "Filter": [{"Type": "FilterPredicate", "FilterType": "Equals", "Column": "account", "Value": ["'"$TEST_ADDR_1"'"]}]
  }]
}'

run_test_json "precision" "precision: V_CrcV2_BalancesByAccountAndToken (high-activity)" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "V_CrcV2",
    "Table": "BalancesByAccountAndToken",
    "Columns": ["account", "tokenAddress", "tokenId", "totalBalance"],
    "Filter": [{"Type": "FilterPredicate", "FilterType": "Equals", "Column": "account", "Value": ["'"$HIGH_ACTIVITY_ADDR_1"'"]}]
  }]
}'

# C5. Group balance (groups hold collateral)
run_test "precision" "precision: getTokenBalances (group - collateral)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$GROUP_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# C6. Wrapper user balance
run_test "precision" "precision: getTokenBalances (wrapper user)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$WRAPPER_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# E. Edge Case & Error Handling Tests
######################################################################

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Edge Case & Error Handling Tests ---${NC}\n"
fi

# E1. Empty results - nonexistent addresses across all major methods
ZERO_ADDR="0x0000000000000000000000000000000000000001"

run_test "edgecases" "edgecase: getTotalBalance (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: V2 getTotalBalance (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getTokenBalances (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getAvatarInfo (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getTrustRelations (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getProfileView (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileView\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getTrustNetworkSummary (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustNetworkSummary\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getAggregatedTrustRelationsEnriched (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAggregatedTrustRelationsEnriched\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getInvitationOrigin (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getInvitationOrigin\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getAllInvitations (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAllInvitations\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getTransferData (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTransferData\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getProfileByAddress (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getProfileCid (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCid\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: searchProfiles (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$ZERO_ADDR\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getTokenInfo (zero address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfo\",\"params\":[\"$ZERO_ADDR\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getCommonTrust (zero+zero)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$ZERO_ADDR\",\"$ZERO_ADDR\"]}' -H \"Content-Type: application/json\" $RPC_URL"

# E2. Error handling - invalid params
run_test "edgecases" "edgecase: missing params" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: null address param" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[null],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: invalid address format" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"not_an_address\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: short address" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"0x1234\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# E3. Error handling - invalid JSON-RPC
run_test "edgecases" "edgecase: nonexistent method" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_nonExistentMethod\",\"params\":[],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# E4. Error handling - query with invalid namespace/table
run_test_json "edgecases" "edgecase: query invalid namespace" '{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "circles_query",
  "params": [{
    "Namespace": "NonExistent",
    "Table": "FakeTable",
    "Columns": [],
    "Limit": 1
  }]
}'

# E5. Batch requests - empty batch
run_test "edgecases" "edgecase: getAvatarInfoBatch (empty array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfoBatch\",\"params\":[[]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getTokenInfoBatch (empty array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenInfoBatch\",\"params\":[[]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getProfileCidBatch (empty array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileCidBatch\",\"params\":[[]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: getProfileByAddressBatch (empty array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddressBatch\",\"params\":[[]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

# E6. findPath edge cases
run_test "edgecases" "edgecase: findPath source=sink (self-transfer)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_1\",\"targetFlow\":\"1000000000000000000\"}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: findPath zero target flow" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_2\",\"targetFlow\":\"0\"}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: findPath nonexistent source" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$ZERO_ADDR\",\"sink\":\"$TEST_ADDR_2\",\"targetFlow\":\"1000000000000000000\"}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: findPath nonexistent sink" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$ZERO_ADDR\",\"targetFlow\":\"1000000000000000000\"}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: findPath max uint256 target flow" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"115792089237316195423570985008687907853269984665640564039457584007913129639935\"}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: findPath maxTransfers=1" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"maxTransfers\":1}]}' -H \"Content-Type: application/json\" $RPC_URL"

# E7. Events edge cases
run_test "edgecases" "edgecase: events inverted block range (from > to)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43590000,43580000,[\"CrcV2_TransferSingle\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: events null event types" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43580010,null,null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "edgecases" "edgecase: events empty event types array" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,43580000,43580010,[],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

######################################################################
# Gas Estimation Tests (eth_estimateGas for group-tms transaction types)
######################################################################
# Verifies that eth_estimateGas works for the same transaction types
# that group-tms submits on-chain. Catches RPC-level gas estimation
# regressions (e.g. "Missing or invalid parameters" errors).
#
# `from` addresses use the on-chain owner() of each contract (publicly
# readable via eth_call). No private keys or operational Safe addresses.
#
# Contract addresses (already public in this repo):
#   Gnosis Group:  GROUP_ADDR_1 (0xc19bc204...)
#   GP-CRC Group:  0xb629a1e86f3efada0f87c83494da8cc34c3f84ef
#   Router:        0xdc287474114cc0551a81ddc2eb51783fbf34802f
#
# Function selectors:
#   0x4141f954 = trustBatchWithConditions(address[],uint96)
#   0x2c3df1de = enableCRCForRouting(address,address[])

# Owner addresses (from on-chain owner() calls — publicly readable contract state)
GNOSIS_GROUP_OWNER="0x7add2c8d1f7ce98ca9a6a7c0122916787988071f"
GP_CRC_GROUP_OWNER="0x58e843c5caad0aa6d14609a365fdf6cebc80a1d2"  # owner() of gp-crc group
GP_CRC_GROUP="0xb629a1e86f3efada0f87c83494da8cc34c3f84ef"
ROUTER_CONTRACT="0xdc287474114cc0551a81ddc2eb51783fbf34802f"

# G1. Baseline: simple eth_estimateGas (ETH transfer, no calldata)
run_test_json "gas_estimation" "eth_estimateGas (baseline transfer)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$TEST_ADDR_2"'", "to": "'"$TEST_ADDR_2"'", "value": "0x0"}],
  "id": 1
}'

# G2. trustBatchWithConditions — trust (expiry=MAX_UINT96)
# Used by: gnosis-group, gp-crc, crc-backers
# from = group owner (on-chain public), to = Gnosis Group contract
run_test_json "gas_estimation" "eth_estimateGas (trustBatchWithConditions - trust)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$GNOSIS_GROUP_OWNER"'", "to": "'"$GROUP_ADDR_1"'", "data": "0x4141f95400000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000ffffffffffffffffffffffff0000000000000000000000000000000000000000000000000000000000000001000000000000000000000000'"${TEST_ADDR_2:2}"'"}],
  "id": 1
}'

# G3. trustBatchWithConditions — untrust (expiry=0)
# Used by: gnosis-group, gp-crc
run_test_json "gas_estimation" "eth_estimateGas (trustBatchWithConditions - untrust)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$GNOSIS_GROUP_OWNER"'", "to": "'"$GROUP_ADDR_1"'", "data": "0x4141f954000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001000000000000000000000000'"${TEST_ADDR_2:2}"'"}],
  "id": 1
}'

# G4. trustBatchWithConditions on GP-CRC group (different owner + group contract)
# from = gp-crc group owner (on-chain public), to = GP-CRC Group contract
run_test_json "gas_estimation" "eth_estimateGas (trustBatch - gp-crc group)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$GP_CRC_GROUP_OWNER"'", "to": "'"$GP_CRC_GROUP"'", "data": "0x4141f95400000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000ffffffffffffffffffffffff0000000000000000000000000000000000000000000000000000000000000001000000000000000000000000'"${TEST_ADDR_2:2}"'"}],
  "id": 1
}'

# G5. enableCRCForRouting on Router contract (no access control — any from works)
# Used by: router-tms
run_test_json "gas_estimation" "eth_estimateGas (enableCRCForRouting)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$TEST_ADDR_2"'", "to": "'"$ROUTER_CONTRACT"'", "data": "0x2c3df1de000000000000000000000000'"${GROUP_ADDR_1:2}"'0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000100000000000000000000000042cedde51198d1773590311e2a340dc06b24cb37"}],
  "id": 1
}'

# G6. eth_estimateGas with block parameter (latest) — tests param format compatibility
run_test_json "gas_estimation" "eth_estimateGas (with block param)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$GNOSIS_GROUP_OWNER"'", "to": "'"$GROUP_ADDR_1"'", "data": "0x4141f95400000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000ffffffffffffffffffffffff0000000000000000000000000000000000000000000000000000000000000001000000000000000000000000'"${TEST_ADDR_2:2}"'"}, "latest"],
  "id": 1
}'

# G7. eth_estimateGas with EIP-1559 gas params (what viem/Safe SDK actually sends)
# Without these, tests pass but production fails — the RPC may reject maxFeePerGas/maxPriorityFeePerGas.
# Uses enableCRCForRouting (no access control) with TEST_ADDR_2 (has xDAI for balance check).
run_test_json "gas_estimation" "eth_estimateGas (EIP-1559 gas params)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$TEST_ADDR_2"'", "to": "'"$ROUTER_CONTRACT"'", "data": "0x2c3df1de000000000000000000000000'"${GROUP_ADDR_1:2}"'0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000100000000000000000000000042cedde51198d1773590311e2a340dc06b24cb37", "maxFeePerGas": "0x1000", "maxPriorityFeePerGas": "0x36"}],
  "id": 1
}'

# G8. eth_estimateGas with full viem params (EIP-1559 + nonce) — exact match of Safe SDK behavior
run_test_json "gas_estimation" "eth_estimateGas (full viem params)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$TEST_ADDR_2"'", "to": "'"$ROUTER_CONTRACT"'", "data": "0x2c3df1de000000000000000000000000'"${GROUP_ADDR_1:2}"'0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000100000000000000000000000042cedde51198d1773590311e2a340dc06b24cb37", "maxFeePerGas": "0x1000", "maxPriorityFeePerGas": "0x36", "nonce": "0x1"}],
  "id": 1
}'

# G9. Safe execTransaction wrapper — full production call path (no EIP-1559)
# Uses HIGH_ACTIVITY_ADDR_1 as the Safe (has xDAI, threshold=1)
# Owner 0xd55072d9... pre-approved signature (v=1, no private key needed)
# Inner call: enableCRCForRouting via router
SAFE_EXEC_OWNER="0xd55072d9826c798698e058bb3211b14f3a787cbb"
SAFE_EXEC_TX="0x6a761202000000000000000000000000dc287474114cc0551a81ddc2eb51783fbf34802f00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000842c3df1de000000000000000000000000c19bc204eb1c1d5b3fe500e5e5dfabab625f286c0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000100000000000000000000000042cedde51198d1773590311e2a340dc06b24cb37000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000060000000000000000000000000d55072d9826c798698e058bb3211b14f3a787cbb00000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000"

run_test_json "gas_estimation" "eth_estimateGas (Safe execTransaction)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$SAFE_EXEC_OWNER"'", "to": "'"$HIGH_ACTIVITY_ADDR_1"'", "data": "'"$SAFE_EXEC_TX"'"}],
  "id": 1
}'

# G10. Safe execTransaction + EIP-1559 params — exact production replica
# This is what crashed gp-crc on 2026-03-24: Safe SDK sends execTransaction with maxFeePerGas/nonce
run_test_json "gas_estimation" "eth_estimateGas (Safe execTx + EIP-1559)" '{
  "jsonrpc": "2.0",
  "method": "eth_estimateGas",
  "params": [{"from": "'"$SAFE_EXEC_OWNER"'", "to": "'"$HIGH_ACTIVITY_ADDR_1"'", "data": "'"$SAFE_EXEC_TX"'", "maxFeePerGas": "0x1000", "maxPriorityFeePerGas": "0x36"}],
  "id": 1
}'

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}All tests completed.${NC}\n"
fi
