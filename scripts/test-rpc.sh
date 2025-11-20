#!/usr/bin/env bash
set -e
set -o pipefail

# Test script for RPC and Pathfinder hosts
# Runs all curl commands from the documentation against the local services.
#
# Usage:
#   ./test-rpc.sh [RPC_URL] [--json]
#
# Examples:
#   ./test-rpc.sh                                          # Test localhost:8081
#   ./test-rpc.sh http://localhost:8081                    # Test custom local URL
#   ./test-rpc.sh https://rpc.aboutcircles.com             # Test production
#   ./test-rpc.sh https://rpc.aboutcircles.com --json      # Output only JSON (for regression testing)

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
TOKEN_ADDR_4="0x1de1C49E7a623Cb3D1114bA0D40063F243ceeA"

# Parse arguments
RPC_URL="${1:-http://localhost:${RPC_PORT:-8081}}"
OUTPUT_MODE="${2:-pretty}"

# Check if --json flag is present
if [[ "$1" == "--json" ]] || [[ "$2" == "--json" ]]; then
    OUTPUT_MODE="json"
    if [[ "$1" == "--json" ]]; then
        RPC_URL="http://localhost:${RPC_PORT:-8081}"
    fi
fi

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}Running tests against RPC host at $RPC_URL${NC}\n"
    echo -e "${YELLOW}Test addresses:${NC}"
    echo -e "  1: $TEST_ADDR_1"
    echo -e "  2: $TEST_ADDR_2"
    echo -e "  3: $TEST_ADDR_3"
    echo -e ""
fi

# Function to execute and print curl commands
run_test() {
    local test_name="$1"
    local curl_cmd="$2"

    if [[ "$OUTPUT_MODE" == "json" ]]; then
        # JSON output mode for regression testing
        echo "{\"test\":\"$test_name\",\"response\":"
        eval "$curl_cmd" | jq -c '.'
        echo "}"
    else
        # Pretty output mode for interactive use
        echo -e "${YELLOW}Testing: $test_name${NC}"
        echo -e "${GREEN}Request:${NC}"
        echo "$curl_cmd"
        echo -e "${GREEN}Response:${NC}"

        if ! response=$(eval "$curl_cmd" 2>&1); then
            echo -e "${RED}Error executing request: $response${NC}"
            echo -e "\n"
            return 1
        fi

        if ! echo "$response" | jq '.' 2>/dev/null; then
            echo -e "${RED}Invalid JSON response: $response${NC}"
            echo -e "\n"
            return 1
        fi

        echo -e "\n"
    fi
}

# ============================================================================
# V1 tests
# ============================================================================

run_test "circles_getTotalBalance (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTotalBalance (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTotalBalance (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_getTokenBalances (v1, addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTokenBalances (v1, addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTokenBalances (v1, addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_getTrustRelations (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTrustRelations (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTrustRelations (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_query (trust relations)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"CrcV2\",\"Table\":\"Stopped\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37\"]}]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (transaction history)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"CrcV1\",\"Table\":\"HubTransfer\",\"Limit\":10,\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":[\"0xc5d6c75087780e0c18820883cf5a580bb3a4d834\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"to\",\"Value\":[\"0xc5d6c75087780e0c18820883cf5a580bb3a4d834\"]}]}],\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"},{\"Column\":\"transactionIndex\",\"SortOrder\":\"DESC\"},{\"Column\":\"logIndex\",\"SortOrder\":\"DESC\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_health" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_health\",\"params\":[]}' -H \"Content-Type: application/json\" $RPC_URL"

# ============================================================================
# V2 tests
# ============================================================================

run_test "circlesV2_getTotalBalance (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_getTotalBalance (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_getTotalBalance (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_query (v2 trust relations for addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_1\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"$TEST_ADDR_1\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (v2 trust relations for addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_2\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"$TEST_ADDR_2\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (v2 trust relations for addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"$TEST_ADDR_3\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"$TEST_ADDR_3\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Pathfinder tests - FIXED: Use lowercase property names (source/sink/targetFlow) and add SimulatedBalances with actual token addresses
run_test "circlesV2_findPath (addr1->addr3, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_findPath (addr2->addr3, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_2\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_2\",\"Token\":\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_findPath (addr1->addr2, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_2\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

# Additional working findPath examples with actual token addresses
run_test "circlesV2_findPath (addr3->addr1, with token balance)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_3\",\"sink\":\"$TEST_ADDR_1\",\"targetFlow\":\"1000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_3\",\"Token\":\"0x1de1C49E7a623Cb3D1114bA0D40063F243ceeA\",\"Amount\":\"1000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_findPath (circular path with token swap)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_3\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"100000000000000000000\",\"fromTokens\":[\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\"],\"toTokens\":[\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\"],\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_3\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"100000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_findPath (multiple token balances)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"source\":\"$TEST_ADDR_1\",\"sink\":\"$TEST_ADDR_3\",\"targetFlow\":\"500000000000000000000\",\"simulatedBalances\":[{\"Holder\":\"$TEST_ADDR_1\",\"Token\":\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\",\"Amount\":\"300000000000000000000\",\"IsWrapped\":false},{\"Holder\":\"$TEST_ADDR_2\",\"Token\":\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\",\"Amount\":\"200000000000000000000\",\"IsWrapped\":false}]}]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_tables" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circles_tables\",\"params\":[]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_events (basic)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,null,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_getCommonTrust (addr1+addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getCommonTrust (addr2+addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_2\",\"$TEST_ADDR_3\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getCommonTrust (addr1+addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"$TEST_ADDR_1\",\"$TEST_ADDR_3\"]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_getTokenBalances (v2, addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_1\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTokenBalances (v2, addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_2\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTokenBalances (v2, addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"$TEST_ADDR_3\"]}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_getAvatarInfo (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getAvatarInfo (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getAvatarInfo (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_query (token info for addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0x6D5e20F62C177765f73aee343a307D949c08B9DC\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (token info for addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0xa0f8904eC48a2775B8a88b40e9c171F05F7d7673\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (token info for addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0x1de1C49E7a623Cb3D1114bA0D40063F243ceeA\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getNetworkSnapshot" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getNetworkSnapshot\",\"params\":[],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByCid" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByCid\",\"params\":[\"Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByCidBatch" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByCidBatch\",\"params\":[[\"Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W\",null,\"QmZuR1Jkhs9RLXVY28eTTRSnqbxLTBSoggp18Yde858xCM\",\"QmanRNbDjbiSFdxcYT9S9wpk3gaCVnM81MVAHkmJj6AqE5\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByAddress (addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$TEST_ADDR_1\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByAddress (addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$TEST_ADDR_2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByAddress (addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"$TEST_ADDR_3\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_getProfileByAddressBatch (with test addresses)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddressBatch\",\"params\":[[\"$TEST_ADDR_1\",\"$TEST_ADDR_2\",\"$TEST_ADDR_3\",null]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"

run_test "circles_searchProfiles (using addr1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_1\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_searchProfiles (using addr2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_2\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_searchProfiles (using addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"$TEST_ADDR_3\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"

# ============================================================================
# Advanced Filter Predicate Tests
# ============================================================================

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Advanced Filter Predicate Tests ---${NC}\n"
fi

# Test 1: GreaterThan filter
run_test "circles_events (filter: blockNumber > 38000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 2: LessThanOrEquals filter
run_test "circles_events (filter: blockNumber <= 39000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,39000000,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThanOrEquals\",\"Column\":\"blockNumber\",\"Value\":39000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 3: In filter (array values)
run_test "circles_events (filter: blockNumber IN array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38000100,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"In\",\"Column\":\"blockNumber\",\"Value\":[38000000,38000001,38000002]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 4: NotEquals filter
run_test "circles_events (filter: avatar != address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"NotEquals\",\"Column\":\"avatar\",\"Value\":\"0x0000000000000000000000000000000000000000\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 5: IsNotNull filter
run_test "circles_events (filter: transactionHash IS NOT NULL)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 6: Conjunction with AND
run_test "circles_events (filter: blockNumber > 38000000 AND blockNumber < 39000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,39000000,null,[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000},{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThan\",\"Column\":\"blockNumber\",\"Value\":39000000}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 7: Conjunction with OR
run_test "circles_events (filter: blockNumber < 100 OR blockNumber > 40000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,[\"CrcV1_Signup\",\"CrcV2_RegisterHuman\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThan\",\"Column\":\"blockNumber\",\"Value\":38000100},{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000900}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 8: Nested Conjunction (complex logic) - Using test addresses
run_test "circles_events (filter: nested AND/OR with test addr3)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38100000,[\"CrcV1_Trust\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000},{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"canSendTo\",\"Value\":\"$TEST_ADDR_3\"},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"user\",\"Value\":\"$TEST_ADDR_3\"}]}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 9: Like filter (text pattern matching)
run_test "circles_events (filter: transactionHash LIKE pattern)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Like\",\"Column\":\"transactionHash\",\"Value\":\"0x%\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 10: NotIn filter
run_test "circles_events (filter: blockNumber NOT IN array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"NotIn\",\"Column\":\"blockNumber\",\"Value\":[1,2,3,4,5]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 11: GreaterThanOrEquals filter
run_test "circles_events (filter: blockNumber >= 38000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThanOrEquals\",\"Column\":\"blockNumber\",\"Value\":38000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 12: Multiple filters combined with basic parameters - Using test addresses
run_test "circles_events (filter: combined with test addr3 and block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"$TEST_ADDR_3\",38000000,39000000,[\"CrcV1_Trust\",\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}All tests completed.${NC}\n"
fi
