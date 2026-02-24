#!/usr/bin/env bash
#
# Interactive HTTP endpoint caller for Circles services
# Tests: /profiles (profile-service), /pathfinder (pathfinder service)
#

# Default URL
DEFAULT_URL="https://staging.circlesubi.network"

# Ask user for endpoint
echo "Choose HTTP endpoint:"
echo "1) Staging (staging.circlesubi.network)"
echo "2) Local (localhost)"
echo "3) Custom URL"
printf "Enter choice (1-3): "
read choice
case $choice in
  1)
    BASE_URL="$DEFAULT_URL"
    ;;
  2)
    BASE_URL="http://localhost"
    ;;
  3)
    printf "Enter custom URL (e.g., https://example.com): "
    read BASE_URL
    ;;
  *)
    echo "Invalid choice, using staging (default)"
    BASE_URL="$DEFAULT_URL"
    ;;
esac

# Remove trailing slash
BASE_URL="${BASE_URL%/}"

echo ""
echo "Using: $BASE_URL"
echo ""

# List of available HTTP endpoints
endpoints=(
# Profile Service
"profiles/health"
"profiles/get"
"profiles/getBatch"
"profiles/search"
"profiles/search/addresses"
# Pathfinder Service
"pathfinder/snapshot"
"pathfinder/findMaxFlow"
"pathfinder/findPath"
)

# Display menu and get user selection
echo "Available HTTP endpoints:"
select endpoint in "${endpoints[@]}"; do
  if [ -n "$endpoint" ]; then
    echo "Selected: $endpoint"
    break
  else
    echo "Invalid selection. Please choose a number from 1 to ${#endpoints[@]}."
  fi
done

# Handle endpoint-specific parameter input and execution
case $endpoint in
  # ─────────────────────────────────────────────────────────────
  # Profile Service Endpoints
  # ─────────────────────────────────────────────────────────────
  profiles/health)
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint"
    curl -s "$BASE_URL/$endpoint" | jq . 2>/dev/null || cat
    ;;

  profiles/get)
    printf "Enter CID (e.g., QmXxx...): "
    read cid
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint?cid=$cid"
    curl -s "$BASE_URL/$endpoint?cid=$cid" | jq . 2>/dev/null || cat
    ;;

  profiles/getBatch)
    printf "Enter CIDs (comma-separated, e.g., QmXxx,QmYyy): "
    read cids
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint?cids=$cids"
    curl -s "$BASE_URL/$endpoint?cids=$cids" | jq . 2>/dev/null || cat
    ;;

  profiles/search)
    printf "Enter search query: "
    read query
    printf "Enter limit (default 20): "
    read limit
    limit=${limit:-20}
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint?q=$query&limit=$limit"
    curl -s "$BASE_URL/$endpoint?q=$query&limit=$limit" | jq . 2>/dev/null || cat
    ;;

  profiles/search/addresses)
    printf "Enter addresses (comma-separated): "
    read addresses
    # Convert comma-separated to JSON array
    json_addresses=$(echo "$addresses" | sed 's/,/","/g' | sed 's/^/["/' | sed 's/$/"]/')
    json="{\"addresses\":$json_addresses}"
    echo ""
    echo "Executing: POST $BASE_URL/$endpoint"
    echo "Body: $json"
    curl -s -X POST "$BASE_URL/$endpoint" \
      -H "Content-Type: application/json" \
      -d "$json" | jq . 2>/dev/null || cat
    ;;

  # ─────────────────────────────────────────────────────────────
  # Pathfinder Service Endpoints
  # ─────────────────────────────────────────────────────────────
  pathfinder/snapshot)
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint"
    echo "(This may return a large response...)"
    curl -s "$BASE_URL/$endpoint" | head -c 2000
    echo ""
    echo "... (truncated)"
    ;;

  pathfinder/findMaxFlow)
    printf "Enter source address (from): "
    read from_addr
    printf "Enter destination address (to): "
    read to_addr
    printf "Enter amount in wei (default 1000000000000000000 = 1 CRC): "
    read amount
    amount=${amount:-1000000000000000000}
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint?from=$from_addr&to=$to_addr&amount=$amount"
    curl -s "$BASE_URL/$endpoint?from=$from_addr&to=$to_addr&amount=$amount" | jq . 2>/dev/null || cat
    ;;

  pathfinder/findPath)
    printf "Enter source address (from): "
    read from_addr
    printf "Enter destination address (to): "
    read to_addr
    printf "Enter amount in wei (default 1000000000000000000 = 1 CRC): "
    read amount
    amount=${amount:-1000000000000000000}
    echo ""
    echo "Executing: GET $BASE_URL/$endpoint?from=$from_addr&to=$to_addr&amount=$amount"
    curl -s "$BASE_URL/$endpoint?from=$from_addr&to=$to_addr&amount=$amount" | jq . 2>/dev/null || cat
    ;;

  *)
    echo "Unknown endpoint: $endpoint"
    exit 1
    ;;
esac

echo ""
