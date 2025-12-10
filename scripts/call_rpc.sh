#!/usr/bin/env bash

# Default URL for RPC calls
DEFAULT_URL="https://rpc.aboutcircles.com/"

# Ask user for RPC endpoint
echo "Choose RPC endpoint:"
echo "1) Remote mainnet (rpc.aboutcircles.com)"
echo "2) Remote testnet (chiado-rpc.aboutcircles.com)"
echo "3) Local (localhost:8081)"
echo "4) Staging (135.181.238.49:8081)"
printf "Enter choice (1-3): "
read choice
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
  4)
    RPC_URL="http://135.181.238.49:8081/"
    ;;
  *)
    echo "Invalid choice, using remote mainnet (default)"
    RPC_URL="$DEFAULT_URL"
    ;;
esac

# List of available RPC methods
methods=(
# Balance Methods
"circles_getTotalBalance"
"circles_getTokenBalances"
# Token Methods
"circles_getTokenInfo"
"circles_getTokenHolders"
# Avatar Methods
"circles_getAvatarInfo"
"circles_getAvatarInfoBatch"
# Profile Methods
"circles_getProfileCid"
"circles_getProfileByCid"
"circles_getProfileByAddress"
"circles_searchProfiles"
"circles_searchProfileByAddressOrName"
# Trust Methods
"circles_getTrustRelations"
"circles_getAggregatedTrustRelations"
"circles_getAggregatedTrustRelationsEnriched"
"circles_getTrustNetworkSummary"
"circles_getCommonTrust"
# Group Methods
"circles_findGroups"
"circles_getGroupMembers"
"circles_getGroupMemberships"
# Invitation Methods
"circles_getValidInviters"
"circles_getInvitationOrigin"
# Transaction Methods
"circles_getTransactionHistory"
"circles_getTransactionHistoryEnriched"
# SDK Enablement Methods
"circles_getProfileView"
# Query & Events
"circles_query"
"circles_events"
# System
"circles_health"
"circles_tables"
"circles_getNetworkSnapshot"
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
  # Balance Methods
  circles_getTotalBalance)
    printf "Enter address: "
    read address
    printf "Enter version (1 or 2): "
    read version
    json='{"jsonrpc":"2.0","method":"circles_getTotalBalance","params":["'$address'",'$version'],"id":1}'
    ;;
  circles_getTokenBalances)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getTokenBalances","params":["'$address'"],"id":1}'
    ;;

  # Token Methods
  circles_getTokenInfo)
    printf "Enter token address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getTokenInfo","params":["'$address'"],"id":1}'
    ;;
  circles_getTokenHolders)
    printf "Enter token address: "
    read address
    printf "Enter limit (default 100): "
    read limit
    limit=${limit:-100}
    json='{"jsonrpc":"2.0","method":"circles_getTokenHolders","params":["'$address'",'$limit',null],"id":1}'
    ;;

  # Avatar Methods
  circles_getAvatarInfo)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getAvatarInfo","params":["'$address'"],"id":1}'
    ;;
  circles_getAvatarInfoBatch)
    printf "Enter addresses (comma-separated): "
    read addresses
    # Convert comma-separated to JSON array
    json_addresses=$(echo "$addresses" | sed 's/,/","/g' | sed 's/^/["/' | sed 's/$/"]/')
    json='{"jsonrpc":"2.0","method":"circles_getAvatarInfoBatch","params":['$json_addresses'],"id":1}'
    ;;

  # Profile Methods
  circles_getProfileCid)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getProfileCid","params":["'$address'"],"id":1}'
    ;;
  circles_getProfileByCid)
    printf "Enter CID: "
    read cid
    json='{"jsonrpc":"2.0","method":"circles_getProfileByCid","params":["'$cid'"],"id":1}'
    ;;
  circles_getProfileByAddress)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getProfileByAddress","params":["'$address'"],"id":1}'
    ;;
  circles_searchProfiles)
    printf "Enter search text: "
    read text
    printf "Enter limit (default 20): "
    read limit
    limit=${limit:-20}
    json='{"jsonrpc":"2.0","method":"circles_searchProfiles","params":["'$text'",'$limit',0],"id":1}'
    ;;
  circles_searchProfileByAddressOrName)
    printf "Enter query (address prefix or name): "
    read query
    printf "Enter limit (default 10): "
    read limit
    limit=${limit:-10}
    json='{"jsonrpc":"2.0","method":"circles_searchProfileByAddressOrName","params":["'$query'",'$limit',0],"id":1}'
    ;;

  # Trust Methods
  circles_getTrustRelations)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getTrustRelations","params":["'$address'"],"id":1}'
    ;;
  circles_getAggregatedTrustRelations)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getAggregatedTrustRelations","params":["'$address'"],"id":1}'
    ;;
  circles_getAggregatedTrustRelationsEnriched)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getAggregatedTrustRelationsEnriched","params":["'$address'"],"id":1}'
    ;;
  circles_getTrustNetworkSummary)
    printf "Enter address: "
    read address
    printf "Enter max depth (default 2): "
    read depth
    depth=${depth:-2}
    json='{"jsonrpc":"2.0","method":"circles_getTrustNetworkSummary","params":["'$address'",'$depth'],"id":1}'
    ;;
  circles_getCommonTrust)
    printf "Enter first address: "
    read address1
    printf "Enter second address: "
    read address2
    json='{"jsonrpc":"2.0","method":"circles_getCommonTrust","params":["'$address1'","'$address2'"],"id":1}'
    ;;

  # Group Methods
  circles_findGroups)
    printf "Enter limit (default 50): "
    read limit
    limit=${limit:-50}
    printf "Enter name prefix filter (or press enter to skip): "
    read namePrefix
    if [ -n "$namePrefix" ]; then
      json='{"jsonrpc":"2.0","method":"circles_findGroups","params":['$limit',{"nameStartsWith":"'$namePrefix'"},null],"id":1}'
    else
      json='{"jsonrpc":"2.0","method":"circles_findGroups","params":['$limit',null,null],"id":1}'
    fi
    ;;
  circles_getGroupMembers)
    printf "Enter group address: "
    read address
    printf "Enter limit (default 100): "
    read limit
    limit=${limit:-100}
    json='{"jsonrpc":"2.0","method":"circles_getGroupMembers","params":["'$address'",'$limit',null],"id":1}'
    ;;
  circles_getGroupMemberships)
    printf "Enter member address: "
    read address
    printf "Enter limit (default 50): "
    read limit
    limit=${limit:-50}
    json='{"jsonrpc":"2.0","method":"circles_getGroupMemberships","params":["'$address'",'$limit',null],"id":1}'
    ;;

  # Invitation Methods
  circles_getValidInviters)
    printf "Enter address: "
    read address
    printf "Enter minimum balance (default 96): "
    read minBalance
    minBalance=${minBalance:-96}
    json='{"jsonrpc":"2.0","method":"circles_getValidInviters","params":["'$address'","'$minBalance'"],"id":1}'
    ;;
  circles_getInvitationOrigin)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getInvitationOrigin","params":["'$address'"],"id":1}'
    ;;

  # Transaction Methods
  circles_getTransactionHistory)
    printf "Enter address: "
    read address
    printf "Enter limit (default 50): "
    read limit
    limit=${limit:-50}
    json='{"jsonrpc":"2.0","method":"circles_getTransactionHistory","params":["'$address'",'$limit',null],"id":1}'
    ;;
  circles_getTransactionHistoryEnriched)
    printf "Enter address: "
    read address
    printf "Enter from block: "
    read fromBlock
    printf "Enter limit (default 20): "
    read limit
    limit=${limit:-20}
    json='{"jsonrpc":"2.0","method":"circles_getTransactionHistoryEnriched","params":["'$address'",'$fromBlock',null,'$limit',null],"id":1}'
    ;;

  # SDK Enablement Methods
  circles_getProfileView)
    printf "Enter address: "
    read address
    json='{"jsonrpc":"2.0","method":"circles_getProfileView","params":["'$address'"],"id":1}'
    ;;

  # System Methods
  circles_health)
    json='{"jsonrpc":"2.0","method":"circles_health","params":[],"id":1}'
    ;;
  circles_tables)
    json='{"jsonrpc":"2.0","method":"circles_tables","params":[],"id":1}'
    ;;
  circles_getNetworkSnapshot)
    json='{"jsonrpc":"2.0","method":"circles_getNetworkSnapshot","params":[],"id":1}'
    ;;

  # Fallback for any other method
  *)
    printf "Enter the params as a JSON array (e.g., [\"param1\", \"param2\"]) or [] for no params: "
    read params
    json='{"jsonrpc":"2.0","method":"'$method'","params":'$params',"id":1}'
    ;;
esac

# Set URL based on method and choice
url="$RPC_URL"

# Execute the curl command
echo "Executing RPC call..."

curl -X POST --data "$json" -H "Content-Type: application/json" "$url"
