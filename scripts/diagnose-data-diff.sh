#!/usr/bin/env bash
set -e

# Data Difference Diagnostic Script
# Compares indexed data between two Circles RPC endpoints to identify
# which events/blocks/tables are missing or different.
#
# Usage:
#   ./scripts/diagnose-data-diff.sh <staging_url> <production_url> [address]
#
# Example:
#   ./scripts/diagnose-data-diff.sh http://135.181.238.49:8081 https://rpc.aboutcircles.com

GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m'

STAGING_URL="${1:-http://135.181.238.49:8081}"
PROD_URL="${2:-https://rpc.aboutcircles.com}"
TEST_ADDRESS="${3:-0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0}"

echo -e "${BLUE}=== Circles Data Difference Diagnostic ===${NC}"
echo -e "${CYAN}Staging:    $STAGING_URL${NC}"
echo -e "${CYAN}Production: $PROD_URL${NC}"
echo -e "${CYAN}Test Addr:  $TEST_ADDRESS${NC}"
echo ""

# Helper function to make RPC calls
rpc_call() {
    local url="$1"
    local method="$2"
    local params="$3"
    curl -s -X POST --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$method\",\"params\":$params}" \
        -H "Content-Type: application/json" "$url"
}

# 1. Compare indexer health and sync status
echo -e "${YELLOW}[1/8] Checking indexer health...${NC}"
echo "Staging:"
rpc_call "$STAGING_URL" "circles_health" "[]" | jq -r '.result // .error'
echo "Production:"
rpc_call "$PROD_URL" "circles_health" "[]" | jq -r '.result // .error'
echo ""

# 2. Compare latest indexed block
echo -e "${YELLOW}[2/8] Checking latest indexed block...${NC}"

# Query the most recent event to find the latest block
STAGING_LATEST=$(rpc_call "$STAGING_URL" "circles_query" '[{"Namespace":"V_Crc","Table":"Transfers","Limit":1,"Columns":["blockNumber"],"Filter":[],"Order":[{"Column":"blockNumber","SortOrder":"DESC"}]}]' | jq -r '.result.Rows[0][0] // "N/A"')
PROD_LATEST=$(rpc_call "$PROD_URL" "circles_query" '[{"Namespace":"V_Crc","Table":"Transfers","Limit":1,"Columns":["blockNumber"],"Filter":[],"Order":[{"Column":"blockNumber","SortOrder":"DESC"}]}]' | jq -r '.result.Rows[0][0] // "N/A"')

echo "Staging latest block:    $STAGING_LATEST"
echo "Production latest block: $PROD_LATEST"

if [[ "$STAGING_LATEST" != "$PROD_LATEST" ]]; then
    echo -e "${RED}⚠ Block difference detected!${NC}"
    if [[ "$STAGING_LATEST" != "N/A" && "$PROD_LATEST" != "N/A" ]]; then
        DIFF=$((PROD_LATEST - STAGING_LATEST))
        echo -e "${RED}  Staging is $DIFF blocks behind production${NC}"
    fi
fi
echo ""

# 3. Compare row counts for key tables
echo -e "${YELLOW}[3/8] Comparing table row counts...${NC}"

TABLES=(
    "CrcV1:Signup"
    "CrcV1:Trust"
    "CrcV1:Transfer"
    "CrcV2:RegisterHuman"
    "CrcV2:RegisterGroup"
    "CrcV2:RegisterOrganization"
    "CrcV2:Trust"
    "CrcV2:TransferSingle"
    "CrcV2:TransferBatch"
    "CrcV2:PersonalMint"
    "CrcV2:Erc20WrapperDeployed"
    "CrcV2:Erc20WrapperTransfer"
)

printf "%-40s %15s %15s %15s\n" "Table" "Staging" "Production" "Difference"
printf "%-40s %15s %15s %15s\n" "-----" "-------" "----------" "----------"

DIFF_FOUND=false

for table_spec in "${TABLES[@]}"; do
    IFS=':' read -r namespace table <<< "$table_spec"
    
    # Get count from staging
    STAGING_COUNT=$(rpc_call "$STAGING_URL" "circles_query" "[{\"Namespace\":\"$namespace\",\"Table\":\"$table\",\"Limit\":1,\"Columns\":[\"blockNumber\"],\"Filter\":[],\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"}]}]" 2>/dev/null | jq -r '.result.Rows | length // 0')
    
    # Actually we need to count all rows - let's use a different approach
    # Query with high limit to estimate
    STAGING_RESULT=$(rpc_call "$STAGING_URL" "circles_query" "[{\"Namespace\":\"$namespace\",\"Table\":\"$table\",\"Limit\":100000,\"Columns\":[\"blockNumber\"],\"Filter\":[],\"Order\":[]}]" 2>/dev/null)
    STAGING_COUNT=$(echo "$STAGING_RESULT" | jq -r '.result.Rows | length // 0')
    
    PROD_RESULT=$(rpc_call "$PROD_URL" "circles_query" "[{\"Namespace\":\"$namespace\",\"Table\":\"$table\",\"Limit\":100000,\"Columns\":[\"blockNumber\"],\"Filter\":[],\"Order\":[]}]" 2>/dev/null)
    PROD_COUNT=$(echo "$PROD_RESULT" | jq -r '.result.Rows | length // 0')
    
    DIFF=$((PROD_COUNT - STAGING_COUNT))
    
    if [[ $DIFF -ne 0 ]]; then
        DIFF_FOUND=true
        printf "%-40s %15s %15s ${RED}%15s${NC}\n" "${namespace}_${table}" "$STAGING_COUNT" "$PROD_COUNT" "$DIFF"
    else
        printf "%-40s %15s %15s %15s\n" "${namespace}_${table}" "$STAGING_COUNT" "$PROD_COUNT" "0"
    fi
done

echo ""

# 4. Check specific address events
echo -e "${YELLOW}[4/8] Comparing events for test address: $TEST_ADDRESS${NC}"

# Get all events for the address from both endpoints
STAGING_EVENTS=$(rpc_call "$STAGING_URL" "circles_events" "[\"$TEST_ADDRESS\", 0, null]" | jq -r '.result | length // 0')
PROD_EVENTS=$(rpc_call "$PROD_URL" "circles_events" "[\"$TEST_ADDRESS\", 0, null]" | jq -r '.result | length // 0')

echo "Staging events:    $STAGING_EVENTS"
echo "Production events: $PROD_EVENTS"

if [[ "$STAGING_EVENTS" != "$PROD_EVENTS" ]]; then
    echo -e "${RED}⚠ Event count difference: $((PROD_EVENTS - STAGING_EVENTS)) events missing on staging${NC}"
fi
echo ""

# 5. Compare avatar info
echo -e "${YELLOW}[5/8] Comparing avatar info...${NC}"
echo "Staging avatar info:"
rpc_call "$STAGING_URL" "circles_getAvatarInfo" "[\"$TEST_ADDRESS\"]" | jq '.result'
echo ""
echo "Production avatar info:"
rpc_call "$PROD_URL" "circles_getAvatarInfo" "[\"$TEST_ADDRESS\"]" | jq '.result'
echo ""

# 6. Compare token balances
echo -e "${YELLOW}[6/8] Comparing token balances...${NC}"
echo "Staging balances:"
STAGING_BALANCES=$(rpc_call "$STAGING_URL" "circles_getTokenBalances" "[\"$TEST_ADDRESS\"]")
echo "$STAGING_BALANCES" | jq '.result | length'
echo "$STAGING_BALANCES" | jq -r '.result[] | "  - \(.tokenAddress): \(.attoCircles) (\(.tokenType))"' 2>/dev/null || echo "  (no balances)"

echo ""
echo "Production balances:"
PROD_BALANCES=$(rpc_call "$PROD_URL" "circles_getTokenBalances" "[\"$TEST_ADDRESS\"]")
echo "$PROD_BALANCES" | jq '.result | length'
echo "$PROD_BALANCES" | jq -r '.result[] | "  - \(.tokenAddress): \(.attoCircles) (\(.tokenType))"' 2>/dev/null || echo "  (no balances)"
echo ""

# 7. Find first missing event
echo -e "${YELLOW}[7/8] Finding first event difference...${NC}"

# Get events from production and check if they exist in staging
PROD_EVENT_DATA=$(rpc_call "$PROD_URL" "circles_events" "[\"$TEST_ADDRESS\", 0, null]")
STAGING_EVENT_DATA=$(rpc_call "$STAGING_URL" "circles_events" "[\"$TEST_ADDRESS\", 0, null]")

# Extract event types and blocks
echo "Production event types:"
echo "$PROD_EVENT_DATA" | jq -r '.result[] | "\(.blockNumber): \(.["$type"] // .event)"' 2>/dev/null | sort -n | head -20

echo ""
echo "Staging event types:"
echo "$STAGING_EVENT_DATA" | jq -r '.result[] | "\(.blockNumber): \(.["$type"] // .event)"' 2>/dev/null | sort -n | head -20

echo ""

# 8. Check for specific transfer events
echo -e "${YELLOW}[8/8] Checking transfer events in detail...${NC}"

# Get recent transfers involving the test address
echo "Recent transfers (staging):"
rpc_call "$STAGING_URL" "circles_query" "[{\"Namespace\":\"V_Crc\",\"Table\":\"Transfers\",\"Limit\":5,\"Columns\":[\"blockNumber\",\"from\",\"to\",\"amount\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":\"$TEST_ADDRESS\"}],\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"}]}]" | jq '.result.Rows'

echo ""
echo "Recent transfers (production):"
rpc_call "$PROD_URL" "circles_query" "[{\"Namespace\":\"V_Crc\",\"Table\":\"Transfers\",\"Limit\":5,\"Columns\":[\"blockNumber\",\"from\",\"to\",\"amount\"],\"Filter\":[{\"Type\":\"FilterPredicate\",\"FilterType\":\"Equals\",\"Column\":\"from\",\"Value\":\"$TEST_ADDRESS\"}],\"Order\":[{\"Column\":\"blockNumber\",\"SortOrder\":\"DESC\"}]}]" | jq '.result.Rows'

echo ""
echo -e "${BLUE}=== Diagnostic Complete ===${NC}"
echo ""
echo "Summary:"
echo "- Compare the latest indexed blocks to see if staging is behind"
echo "- Check table row counts for missing data"
echo "- Event differences indicate indexing gaps"
echo ""
echo "If staging has fewer events but the same latest block,"
echo "there may be a parsing/indexing bug. Check:"
echo "1. Log parser configuration"
echo "2. Contract addresses in settings"
echo "3. Database migrations"
