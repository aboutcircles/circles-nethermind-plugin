#!/bin/bash

# Default URL for RPC calls
DEFAULT_URL="https://rpc.aboutcircles.com/"

# Ask user for RPC endpoint
echo "Choose RPC endpoint:"
echo "1) Remote mainnet (rpc.aboutcircles.com)"
echo "2) Remote testnet (chiado-rpc.aboutcircles.com)"
echo "3) Local (localhost:8081)"
read -p "Enter choice (1-3): " choice
case $choice in
  1)
    RPC_URL="$DEFAULT_URL"
    ;;
  2)
    RPC_URL="https://chiado-rpc.aboutcircles.com/"
    ;;
  3)
    RPC_URL="http://localhost:8081/"
    ;;
  *)
    echo "Invalid choice, using remote mainnet (default)"
    RPC_URL="$DEFAULT_URL"
    ;;
esac

# List of available RPC methods
methods=(
"circles_getTotalBalance"
"circles_getTokenBalances"
"circles_getTrustRelations"
"circles_query"
"circles_health"
"circlesV2_getTotalBalance"
"circlesV2_findPath"
"circles_tables"
"circles_events"
"circles_getCommonTrust"
"circles_getAvatarInfo"
"circles_getNetworkSnapshot"
"circles_getProfileByCid"
"circles_getProfileByCidBatch"
"circles_getProfileByAddress"
"circles_getProfileByAddressBatch"
"circles_searchProfiles"
)

# Display menu and get user selection
echo "Available RPC methods:"
select method in "${methods[@]}"; do
  if [ -n "$method" ]; then
    echo "Selected: $method"
    break
  else
    echo "Invalid selection. Please choose a number from 1 to ${#methods[@]}."
  fi
done

# Handle method-specific parameter input and execution
case $method in
  circles_getTotalBalance)
    echo "Enter address to query total balance:"
    read address
    json='{"jsonrpc":"2.0","method":"circles_getTotalBalance","params":["'$address'"],"id":1}'
    ;;
  circles_getTokenBalances)
    echo "Enter address to query token balances:"
    read address
    json='{"jsonrpc":"2.0","method":"circles_getTokenBalances","params":["'$address'"],"id":1}'
    ;;
  circles_getTrustRelations)
    echo "Enter address to query trust relations:"
    read address
    json='{"jsonrpc":"2.0","method":"circles_getTrustRelations","params":["'$address'"],"id":1}'
    ;;
  circles_health)
    json='{"jsonrpc":"2.0","method":"circles_health","params":[],"id":1}'
    ;;
  circlesV2_getTotalBalance)
    echo "Enter address to query V2 total balance:"
    read address
    json='{"jsonrpc":"2.0","method":"circlesV2_getTotalBalance","params":["'$address'"],"id":1}'
    ;;
  circles_getAvatarInfo)
    echo "Enter address to get avatar info:"
    read address
    json='{"jsonrpc":"2.0","method":"circles_getAvatarInfo","params":["'$address'"],"id":1}'
    ;;
  circles_getProfileByCid)
    echo "Enter CID to get profile:"
    read cid
    json='{"jsonrpc":"2.0","method":"circles_getProfileByCid","params":["'$cid'"],"id":1}'
    ;;
  circles_getProfileByAddress)
    echo "Enter address to get profile:"
    read address
    json='{"jsonrpc":"2.0","method":"circles_getProfileByAddress","params":["'$address'"],"id":1}'
    ;;
  circles_tables)
    json='{"jsonrpc":"2.0","method":"circles_tables","params":[],"id":0}'
    ;;
  circles_getNetworkSnapshot)
    json='{"jsonrpc":"2.0","method":"circles_getNetworkSnapshot","params":[],"id":1}'
    ;;
  *)
    echo "Enter the params as a JSON array (e.g., [\"param1\", \"param2\"]) or [] for no params:"
    read params
    json='{"jsonrpc":"2.0","method":"'$method'","params":'$params',"id":1}'
    ;;
esac

# Set URL based on method and choice
url="$RPC_URL"

# Execute the curl command
echo "Executing RPC call..."

curl -X POST --data "$json" -H "Content-Type: application/json" "$url"
