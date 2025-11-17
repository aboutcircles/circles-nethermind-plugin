#!/usr/bin/env bash
set -e

# Test script for RPC and Pathfinder hosts
# Runs all curl commands from the documentation against the local services.
#
# Usage:
#   ./test-rpc.sh [RPC_URL] [--json]
#
# Examples:
#   ./test-rpc.sh                                          # Test localhost:8082
#   ./test-rpc.sh http://localhost:8082                    # Test custom local URL
#   ./test-rpc.sh https://rpc.aboutcircles.com             # Test production
#   ./test-rpc.sh https://rpc.aboutcircles.com --json      # Output only JSON (for regression testing)

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Parse arguments
RPC_URL="${1:-http://localhost:${RPC_PORT:-8082}}"
OUTPUT_MODE="${2:-pretty}"

# Check if --json flag is present
if [[ "$1" == "--json" ]] || [[ "$2" == "--json" ]]; then
    OUTPUT_MODE="json"
    if [[ "$1" == "--json" ]]; then
        RPC_URL="http://localhost:${RPC_PORT:-8082}"
    fi
fi

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}Running tests against RPC host at $RPC_URL${NC}\n"
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

# v1 examples
run_test "circles_getTotalBalance" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTotalBalance\",\"params\":[\"0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTokenBalances (v1)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTokenBalances\",\"params\":[\"0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTrustRelations" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getTrustRelations\",\"params\":[\"0x2091e2fb4dcfed050adcdd518e57fbfea7e32e5c\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (trust relations)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"CrcV2\",\"Table\":\"Stopped\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"avatar\",\"Value\":[\"0xf3dbe5f4b9bae6038a44e2cd01c49bd5d5544a37\"]}]}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (transaction history)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"CrcV1\",\"Table\":\"HubTransfer\",\"Limit\":10,\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":[\"0xc5d6c75087780e0c18820883cf5a580bb3a4d834\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"to\",\"Value\":[\"0xc5d6c75087780e0c18820883cf5a580bb3a4d834\"]}]}],\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"},{\"Column\":\"transactionIndex\",\"SortOrder\":\"DESC\"},{\"Column\":\"logIndex\",\"SortOrder\":\"DESC\"}]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_health" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_health\",\"params\":[]}' -H \"Content-Type: application/json\" $RPC_URL"

# v2 examples
run_test "circlesV2_getTotalBalance" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circlesV2_getTotalBalance\",\"params\":[\"0xcadd4ea3bcc361fc4af2387937d7417be8d7dfc2\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (v2 trust relations)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_CrcV2\",\"Table\":\"TrustRelations\",\"Columns\":[],\"Filter\":[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"truster\",\"Value\":[\"0xae3a29a9ff24d0e936a5579bae5c4179c4dff565\"]},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"trustee\",\"Value\":[\"0xae3a29a9ff24d0e936a5579bae5c4179c4dff565\"]}]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circlesV2_findPath" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circlesV2_findPath\",\"params\":[{\"Source\":\"0x749c930256b47049cb65adcd7c25e72d5de44b3b\",\"Sink\":\"0xde374ece6fa50e781e81aac78e811b33d16912c7\",\"TargetFlow\":\"99999999999999999999999999999999999\"}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_tables" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circles_tables\",\"params\":[]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_events (basic)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,null,[\"CrcV1_Trust\"],null,false]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getCommonTrust" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getCommonTrust\",\"params\":[\"0xde374ece6fa50e781e81aac78e811b33d16912c7\",\"0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getTokenBalances (v2)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_getTokenBalances\",\"params\":[\"0x7cadf434b692ca029d950607a4b3f139c30d4e98\"]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getAvatarInfo" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getAvatarInfo\",\"params\":[\"0xde374ece6fa50e781e81aac78e811b33d16912c7\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_query (token info)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_query\",\"params\":[{\"Namespace\":\"V_Crc\",\"Table\":\"Tokens\",\"Columns\":[\"blockNumber\",\"timestamp\",\"transactionIndex\",\"logIndex\",\"transactionHash\",\"version\",\"type\",\"token\",\"tokenOwner\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"token\",\"Value\":[\"0x0d8c4901dd270fe101b8014a5dbecc4e4432eb1e\"]}],\"Order\":[]}]}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getNetworkSnapshot" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getNetworkSnapshot\",\"params\":[],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByCid" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByCid\",\"params\":[\"Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByCidBatch" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByCidBatch\",\"params\":[[\"Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W\",null,\"QmZuR1Jkhs9RLXVY28eTTRSnqbxLTBSoggp18Yde858xCM\",\"QmanRNbDjbiSFdxcYT9S9wpk3gaCVnM81MVAHkmJj6AqE5\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByAddress" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddress\",\"params\":[\"0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7\"],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_getProfileByAddressBatch" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"method\":\"circles_getProfileByAddressBatch\",\"params\":[[\"0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7\",\"0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7\",\"0xf712d3b31de494b5c0ea51a6a407460ca66b12e8\",null,\"0xde374ece6fa50e781e81aac78e811b33D16912C7\",\"0xde374ece6fa50e781e81aac78e811b33D16912C7\"]],\"id\":1}' -H \"Content-Type: application/json\" $RPC_URL"
run_test "circles_searchProfiles" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_searchProfiles\",\"params\":[\"0xc3a1428c04c426cdf513c6fc8e09f55ddaf50cd7\",10,0]}' -H \"Content-Type: application/json\" $RPC_URL"

# Advanced Filter Predicate Tests
if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}--- Advanced Filter Predicate Tests ---${NC}\n"
fi

# Test 1: GreaterThan filter
run_test "circles_events (filter: blockNumber > 38000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,[\"CrcV1_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 2: LessThanOrEquals filter
run_test "circles_events (filter: blockNumber <= 39000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThanOrEquals\",\"Column\":\"blockNumber\",\"Value\":39000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 3: In filter (array values)
run_test "circles_events (filter: blockNumber IN array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"In\",\"Column\":\"blockNumber\",\"Value\":[38000000,38000001,38000002]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 4: NotEquals filter
run_test "circles_events (filter: avatar != address)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,[\"CrcV2_RegisterHuman\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"NotEquals\",\"Column\":\"avatar\",\"Value\":\"0x0000000000000000000000000000000000000000\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 5: IsNotNull filter
run_test "circles_events (filter: transactionHash IS NOT NULL)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 6: Conjunction with AND
run_test "circles_events (filter: blockNumber > 38000000 AND blockNumber < 39000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,null,[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000},{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThan\",\"Column\":\"blockNumber\",\"Value\":39000000}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 7: Conjunction with OR
run_test "circles_events (filter: blockNumber < 100 OR blockNumber > 40000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,[\"CrcV1_Signup\",\"CrcV2_RegisterHuman\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"LessThan\",\"Column\":\"blockNumber\",\"Value\":100},{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":40000000}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 8: Nested Conjunction (complex logic)
run_test "circles_events (filter: nested AND/OR)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,[\"CrcV1_Trust\"],[{\"Type\":\"Conjunction\",\"ConjunctionType\":\"And\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThan\",\"Column\":\"blockNumber\",\"Value\":38000000},{\"Type\":\"Conjunction\",\"ConjunctionType\":\"Or\",\"Predicates\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"canSendTo\",\"Value\":\"0xde374ece6fa50e781e81aac78e811b33d16912c7\"},{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"user\",\"Value\":\"0xde374ece6fa50e781e81aac78e811b33d16912c7\"}]}]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 9: Like filter (text pattern matching)
run_test "circles_events (filter: transactionHash LIKE pattern)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,38000000,38001000,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Like\",\"Column\":\"transactionHash\",\"Value\":\"0x%\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 10: NotIn filter
run_test "circles_events (filter: blockNumber NOT IN array)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"NotIn\",\"Column\":\"blockNumber\",\"Value\":[1,2,3,4,5]}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 11: GreaterThanOrEquals filter
run_test "circles_events (filter: blockNumber >= 38000000)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[null,null,null,null,[{\"Type\":\"FilterPredicate\",\"FilterType\":\"GreaterThanOrEquals\",\"Column\":\"blockNumber\",\"Value\":38000000}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

# Test 12: Multiple filters combined with basic parameters
run_test "circles_events (filter: combined with address and block range)" "curl -s -X POST --data '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"circles_events\",\"params\":[\"0xde374ece6fa50e781e81aac78e811b33d16912c7\",38000000,39000000,[\"CrcV1_Trust\",\"CrcV2_Trust\"],[{\"Type\":\"FilterPredicate\",\"FilterType\":\"IsNotNull\",\"Column\":\"transactionHash\"}],false]}' -H \"Content-Type: application/json\" $RPC_URL"

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}All tests completed.${NC}\n"
fi
