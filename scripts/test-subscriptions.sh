#!/usr/bin/env bash
set -e
set -o pipefail

# WebSocket Subscription Testing Script
# Tests the circles_subscribe WebSocket endpoint for real-time event notifications
#
# Usage:
#   ./test-subscriptions.sh [WS_URL] [--duration SECONDS] [--min-events N] [--filter ADDRESS] [--json] [--json-file FILE]
#
# Environment Variables (when using --filter):
#   CIRCLES_SEED_PHRASE    - Required: 12/24 word mnemonic seed phrase for triggering test transactions
#
# Examples:
#   ./test-subscriptions.sh                                          # Test localhost:8081 (exits after 3 events or 60s)
#   ./test-subscriptions.sh ws://localhost:8081/subscribe            # Test custom URL
#   ./test-subscriptions.sh --duration 30 --min-events 5             # Custom thresholds
#   CIRCLES_SEED_PHRASE="..." ./test-subscriptions.sh --filter 0x... # Filter by address and auto-trigger transaction
#   ./test-subscriptions.sh --json --json-file results.json          # Output to JSON file

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
NC='\033[0m' # No Color

# Default configuration
WS_URL=""
MAX_DURATION=60
MIN_EVENTS=3
FILTER_ADDRESS=""
OUTPUT_MODE="pretty"
JSON_FILE=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --duration)
            shift
            if [[ -z "$1" ]]; then
                echo "Error: --duration requires a value" >&2
                exit 1
            fi
            MAX_DURATION="$1"
            ;;
        --min-events)
            shift
            if [[ -z "$1" ]]; then
                echo "Error: --min-events requires a value" >&2
                exit 1
            fi
            MIN_EVENTS="$1"
            ;;
        --filter)
            shift
            if [[ -z "$1" ]]; then
                echo "Error: --filter requires an address" >&2
                exit 1
            fi
            FILTER_ADDRESS="$1"
            ;;
        --json)
            OUTPUT_MODE="json"
            ;;
        --json-file)
            shift
            if [[ -z "$1" ]]; then
                echo "Error: --json-file requires a path" >&2
                exit 1
            fi
            JSON_FILE="$1"
            OUTPUT_MODE="json"
            ;;
        ws://*|wss://*)
            WS_URL="$1"
            ;;
        *)
            if [[ -z "$WS_URL" ]]; then
                WS_URL="$1"
            else
                echo "Unknown argument: $1" >&2
                exit 1
            fi
            ;;
    esac
    shift
done

# Determine WebSocket URL
if [[ -z "$WS_URL" ]]; then
    RPC_PORT="${RPC_PORT:-8081}"
    WS_URL="ws://localhost:${RPC_PORT}/subscribe"
fi

# Check if seed phrase is required for triggering transactions
if [[ -n "$FILTER_ADDRESS" && -z "$CIRCLES_SEED_PHRASE" ]]; then
    echo "Error: CIRCLES_SEED_PHRASE environment variable is required when using --filter (to trigger test transactions)" >&2
    exit 1
fi

# Check if curl supports WebSocket (--ws flag)
CURL_CMD="curl"
WS_SUPPORT=false

# Try system curl first
if curl --help all 2>/dev/null | grep -q -- '--ws'; then
    WS_SUPPORT=true
# Try Homebrew curl on macOS
elif [[ -f "/opt/homebrew/bin/curl" ]] && /opt/homebrew/bin/curl --help all 2>/dev/null | grep -q -- '--ws'; then
    CURL_CMD="/opt/homebrew/bin/curl"
    WS_SUPPORT=true
elif [[ -f "/usr/local/bin/curl" ]] && /usr/local/bin/curl --help all 2>/dev/null | grep -q -- '--ws'; then
    CURL_CMD="/usr/local/bin/curl"
    WS_SUPPORT=true
fi

# If no WebSocket support, check for websocat as fallback
if [[ "$WS_SUPPORT" == "false" ]]; then
    if command -v websocat &> /dev/null; then
        CURL_CMD="websocat"
        WS_SUPPORT=true
    fi
fi

if [[ "$WS_SUPPORT" == "false" ]]; then
    echo -e "${RED}Error: No WebSocket client found${NC}" >&2
    echo -e "${YELLOW}Current curl version does not support WebSocket${NC}" >&2
    echo -e "${YELLOW}Version: $(curl --version | head -1)${NC}" >&2
    echo -e "" >&2
    echo -e "${CYAN}Quick Fix (Recommended):${NC}" >&2
    echo -e "  ${GREEN}# Install websocat (simple WebSocket client)${NC}" >&2
    echo -e "  brew install websocat" >&2
    echo -e "" >&2
    echo -e "${CYAN}Alternative Options:${NC}" >&2
    echo -e "  ${GREEN}# Install curl with WebSocket support via Homebrew${NC}" >&2
    echo -e "  brew install curl" >&2
    echo -e "  # Then ensure Homebrew curl is in your PATH:" >&2
    echo -e "  export PATH=\"/opt/homebrew/bin:\$PATH\"" >&2
    echo -e "" >&2
    echo -e "  ${GREEN}# Or use Homebrew curl directly${NC}" >&2
    echo -e "  /opt/homebrew/bin/curl --version" >&2
    echo -e "" >&2
    echo -e "${CYAN}Linux:${NC}" >&2
    echo -e "  ${GREEN}# Ubuntu/Debian${NC}" >&2
    echo -e "  sudo apt install websocat" >&2
    echo -e "  ${GREEN}# Or build from source${NC}" >&2
    echo -e "  cargo install websocat" >&2
    exit 1
fi

# Check for required utilities
if ! command -v jq &> /dev/null; then
    echo -e "${RED}Error: jq is required but not found${NC}" >&2
    echo -e "${YELLOW}Installation:${NC}" >&2
    echo -e "  ${CYAN}macOS:${NC}    brew install jq" >&2
    echo -e "  ${CYAN}Ubuntu:${NC}   sudo apt install jq" >&2
    echo -e "  ${CYAN}Alpine:${NC}   apk add jq" >&2
    exit 1
fi

if [[ "$OUTPUT_MODE" != "json" ]]; then
    echo -e "${BLUE}=== WebSocket Subscription Test ===${NC}"
    echo -e "${CYAN}WebSocket URL:      $WS_URL${NC}"
    echo -e "${CYAN}Max Duration:       ${MAX_DURATION}s${NC}"
    echo -e "${CYAN}Min Events to Exit: $MIN_EVENTS${NC}"
    if [[ -n "$FILTER_ADDRESS" ]]; then
        echo -e "${CYAN}Filter Address:     $FILTER_ADDRESS${NC}"
    else
        echo -e "${CYAN}Filter Address:     (none - all events)${NC}"
    fi
    echo -e ""
fi

# Temporary files for WebSocket communication
TEMP_DIR=$(mktemp -d)
FIFO_IN="$TEMP_DIR/ws_in"
FIFO_OUT="$TEMP_DIR/ws_out"
EVENTS_FILE="$TEMP_DIR/events.jsonl"
STATE_FILE="$TEMP_DIR/state.json"

mkfifo "$FIFO_IN" "$FIFO_OUT"
touch "$EVENTS_FILE"

# Initialize state
jq -n '{
  subscription_id: null,
  events_received: 0,
  connection_time: null,
  first_event_time: null,
  last_event_time: null,
  errors: []
}' > "$STATE_FILE"

# Track whether we need to generate report in cleanup
REPORT_GENERATED=false

# Cleanup function
cleanup() {
    local exit_code=$?

    # Kill background processes (don't wait - can block indefinitely with FIFOs)
    [[ -n "$TIMEOUT_PID" ]] && kill "$TIMEOUT_PID" 2>/dev/null || true
    [[ -n "$WS_PID" ]] && kill "$WS_PID" 2>/dev/null || true
    [[ -n "$TRIGGER_PID" ]] && kill "$TRIGGER_PID" 2>/dev/null || true

    # Generate timeout report if we were killed before generating one
    if [[ "$REPORT_GENERATED" == "false" && "$OUTPUT_MODE" == "json" && -n "$JSON_FILE" ]]; then
        # Generate minimal error report for timeout case
        local report
        report=$(jq -n \
            --arg url "$WS_URL" \
            --argjson duration "$MAX_DURATION" \
            '{
                websocket_url: $url,
                max_duration_seconds: $duration,
                subscription_id: null,
                total_events_received: 0,
                error_count: 1,
                errors: ["Process was terminated (timeout or signal)"],
                success: false
            }')
        echo "$report" > "$JSON_FILE"
    fi

    # Now safe to remove temp directory
    rm -rf "$TEMP_DIR"
}
trap cleanup EXIT INT TERM

# Build subscription request
if [[ -n "$FILTER_ADDRESS" ]]; then
    SUBSCRIBE_REQUEST='{"jsonrpc":"2.0","method":"circles_subscribe","id":1,"params":{"address":"'"$FILTER_ADDRESS"'"}}'
else
    SUBSCRIBE_REQUEST='{"jsonrpc":"2.0","method":"circles_subscribe","id":1,"params":{}}'
fi

log_message() {
    local message="$1"
    local level="${2:-info}"

    if [[ "$OUTPUT_MODE" != "json" ]]; then
        case "$level" in
            success) echo -e "${GREEN}$message${NC}" ;;
            error)   echo -e "${RED}$message${NC}" ;;
            warning) echo -e "${YELLOW}$message${NC}" ;;
            info)    echo -e "${CYAN}$message${NC}" ;;
            *)       echo "$message" ;;
        esac
    fi
}

# Start WebSocket connection in background
log_message "Connecting to $WS_URL..."

# Record connection time
jq '.connection_time = now' "$STATE_FILE" > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"

# Start WebSocket connection (curl or websocat)
if [[ "$CURL_CMD" == "websocat" ]]; then
    # Use websocat
    (
        # Send subscription request immediately
        echo "$SUBSCRIBE_REQUEST"

        # Keep connection alive and read responses
        cat "$FIFO_IN"
    ) | websocat "$WS_URL" 2>"$TEMP_DIR/ws_stderr.log" > "$FIFO_OUT" &
    WS_PID=$!
else
    # Use curl with WebSocket support
    (
        # Send subscription request immediately
        echo "$SUBSCRIBE_REQUEST"

        # Keep connection alive and read responses
        cat "$FIFO_IN"
    ) | "$CURL_CMD" --no-buffer --ws "$WS_URL" 2>"$TEMP_DIR/ws_stderr.log" > "$FIFO_OUT" &
    WS_PID=$!
fi

# Give WebSocket client a moment to establish connection
sleep 0.5

# Check if WebSocket client is still running (connection successful)
if ! kill -0 "$WS_PID" 2>/dev/null; then
    log_message "✗ Failed to connect to WebSocket" "error"
    if [[ -f "$TEMP_DIR/ws_stderr.log" ]]; then
        cat "$TEMP_DIR/ws_stderr.log" >&2
    fi
    exit 1
fi

log_message "✓ WebSocket connection established" "success"
log_message "Sent subscription request"

# Open FIFO as file descriptor for read with timeout support
exec 3<"$FIFO_OUT"

# Process WebSocket messages
START_TIME=$(date +%s)
SUBSCRIPTION_ACK_RECEIVED=false
EVENT_COUNT=0
ACK_TIMEOUT=10  # Timeout for subscription acknowledgment in seconds

# Start a timeout for subscription acknowledgment
# Note: We use a simple approach - the timeout just kills processes if needed
# The parent process will handle cleanup
ACK_RECEIVED_FILE="$TEMP_DIR/ack_received"
(
    sleep "$ACK_TIMEOUT"
    # Check if acknowledgment was received (via file marker)
    if [[ ! -f "$ACK_RECEIVED_FILE" ]]; then
        echo "✗ Timeout waiting for subscription acknowledgment (${ACK_TIMEOUT}s)" >&2
        echo "No response received from server. Please verify:" >&2
        echo "  1. RPC service is running at $WS_URL" >&2
        echo "  2. Indexer is running (required for event generation)" >&2
        echo "  3. PostgreSQL NOTIFY is configured correctly" >&2
        # Signal timeout by creating a marker file
        touch "$TEMP_DIR/timeout_reached" 2>/dev/null || true
    fi
) &
TIMEOUT_PID=$!

while true; do
    # Check overall timeout FIRST (before blocking on read)
    CURRENT_TIME=$(date +%s)
    ELAPSED=$((CURRENT_TIME - START_TIME))
    if [[ "$ELAPSED" -ge "$MAX_DURATION" ]]; then
        log_message "⏱ Maximum duration (${MAX_DURATION}s) reached" "warning"
        if [[ -n "$TIMEOUT_PID" ]]; then
            kill "$TIMEOUT_PID" 2>/dev/null || true
        fi
        break
    fi

    # Read with 1-second timeout to allow periodic timeout checks
    # Use -u 3 to read from the file descriptor (required for timeout to work with FIFOs)
    # Reset line to detect empty reads
    line=""
    if ! IFS= read -r -t 1 -u 3 line; then
        # read failed - either timeout or EOF
        # If line is empty, it's likely a timeout; continue to re-check overall timeout
        # Note: bash 3.2 retains previous line value on timeout, so we reset it above
        if [[ -z "$line" ]]; then
            continue
        fi
        # If we got some data but read still failed, WebSocket probably closed
        break
    fi

    # Skip empty lines
    [[ -z "$line" ]] && continue

    # Try to parse as JSON
    if ! echo "$line" | jq -e . >/dev/null 2>&1; then
        continue
    fi

    # Check if it's subscription acknowledgment
    if echo "$line" | jq -e '.result' >/dev/null 2>&1 && ! echo "$line" | jq -e '.method' >/dev/null 2>&1; then
        if [[ "$SUBSCRIPTION_ACK_RECEIVED" == "false" ]]; then
            SUBSCRIPTION_ID=$(echo "$line" | jq -r '.result')
            jq --arg id "$SUBSCRIPTION_ID" '.subscription_id = $id' "$STATE_FILE" > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"
            log_message "✓ Subscription acknowledged. ID: $SUBSCRIPTION_ID" "success"
            log_message "Listening for events (max ${MAX_DURATION}s, exits after $MIN_EVENTS events)..."
            SUBSCRIPTION_ACK_RECEIVED=true
            
            # Trigger a test transaction if seed phrase and filter address are provided
            if [[ -n "$FILTER_ADDRESS" && -n "$CIRCLES_SEED_PHRASE" ]]; then
                log_message "Triggering Circles transaction to $FILTER_ADDRESS..."
                CIRCLES_RECIPIENT="$FILTER_ADDRESS" ./scripts/trigger-circles-tx.sh &
                TRIGGER_PID=$!
            fi
            
            # Mark acknowledgment received (for timeout subprocess)
            touch "$ACK_RECEIVED_FILE" 2>/dev/null || true

            # Cancel the acknowledgment timeout
            if [[ -n "$TIMEOUT_PID" ]]; then
                kill "$TIMEOUT_PID" 2>/dev/null || true
            fi
        fi
        continue
    fi

    # Check if it's an error response
    if echo "$line" | jq -e '.error' >/dev/null 2>&1; then
        ERROR_MSG=$(echo "$line" | jq -r '.error.message // "Unknown error"')
        log_message "✗ Subscription error: $ERROR_MSG" "error"
        jq --arg err "$ERROR_MSG" '.errors += [$err]' "$STATE_FILE" > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"
        # Cancel timeout before breaking
        if [[ -n "$TIMEOUT_PID" ]]; then
            kill "$TIMEOUT_PID" 2>/dev/null || true
        fi
        break
    fi

    # Check if it's a subscription event
    if echo "$line" | jq -e '.method == "circles_subscription"' >/dev/null 2>&1; then
        EVENTS=$(echo "$line" | jq '.params.result // []')
        NEW_EVENT_COUNT=$(echo "$EVENTS" | jq 'length')

        if [[ "$NEW_EVENT_COUNT" -gt 0 ]]; then
            # Record first event time
            if [[ "$EVENT_COUNT" -eq 0 ]]; then
                jq '.first_event_time = now' "$STATE_FILE" > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"
                CONN_TIME=$(jq -r '.connection_time' "$STATE_FILE")
                FIRST_TIME=$(jq -r '.first_event_time' "$STATE_FILE")
                TIME_TO_FIRST=$(echo "$FIRST_TIME - $CONN_TIME" | bc)
                log_message "✓ First event received (${TIME_TO_FIRST}s after connection)" "success"
            fi

            # Update last event time and count
            jq '.last_event_time = now' "$STATE_FILE" > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"
            EVENT_COUNT=$((EVENT_COUNT + NEW_EVENT_COUNT))
            jq --argjson count "$EVENT_COUNT" '.events_received = $count' "$STATE_FILE" > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"

            # Save events to file
            echo "$EVENTS" | jq -c '.[]' >> "$EVENTS_FILE"

            log_message "📨 Received $NEW_EVENT_COUNT event(s). Total: $EVENT_COUNT"

            # Pretty print events in non-JSON mode
            if [[ "$OUTPUT_MODE" != "json" ]]; then
                echo "$EVENTS" | jq -r '.[] | "   └─ \(.["$type"] // "Unknown") @ block \(.blockNumber // "?")"'
            fi

            # Check if we've reached minimum event count
            if [[ "$EVENT_COUNT" -ge "$MIN_EVENTS" ]]; then
                log_message "✓ Reached minimum event count ($MIN_EVENTS), exiting" "success"
                # Cancel timeout before breaking
                if [[ -n "$TIMEOUT_PID" ]]; then
                    kill "$TIMEOUT_PID" 2>/dev/null || true
                fi
                break
            fi
        fi
    fi

done
# Reader loop exited (either by break, timeout, or EOF)

# Close the file descriptor
exec 3<&- 2>/dev/null || true

# Ensure timeout process is cleaned up (don't wait, just kill)
if [[ -n "$TIMEOUT_PID" ]]; then
    kill "$TIMEOUT_PID" 2>/dev/null || true
fi

# Stop the WebSocket connection (don't wait, just kill - wait can block if pipe is open)
if [[ -n "$WS_PID" ]]; then
    kill "$WS_PID" 2>/dev/null || true
fi

# Brief pause to let processes clean up
sleep 0.2

# Check if timeout occurred (from the timeout subprocess)
if [[ -f "$TEMP_DIR/timeout_reached" ]]; then
    log_message "✗ Test timed out waiting for subscription acknowledgment" "error"
    # Generate minimal error report
    if [[ "$OUTPUT_MODE" == "json" ]]; then
        REPORT=$(jq -n \
            --arg url "$WS_URL" \
            --argjson duration "$MAX_DURATION" \
            '{
                websocket_url: $url,
                max_duration_seconds: $duration,
                subscription_id: null,
                total_events_received: 0,
                error_count: 1,
                errors: ["Timeout waiting for subscription acknowledgment"],
                success: false
            }')
        if [[ -n "$JSON_FILE" ]]; then
            echo "$REPORT" > "$JSON_FILE"
        else
            echo "$REPORT"
        fi
    fi
    exit 1
fi

# Verify state file exists before reading
if [[ ! -f "$STATE_FILE" ]]; then
    log_message "✗ State file not found - process may have been interrupted" "error"
    # Generate minimal error report for JSON mode
    if [[ "$OUTPUT_MODE" == "json" && -n "$JSON_FILE" ]]; then
        jq -n \
            --arg url "$WS_URL" \
            --argjson duration "$MAX_DURATION" \
            '{
                websocket_url: $url,
                max_duration_seconds: $duration,
                subscription_id: null,
                total_events_received: 0,
                error_count: 1,
                errors: ["State file not found - process was interrupted"],
                success: false
            }' > "$JSON_FILE"
        REPORT_GENERATED=true
    fi
    exit 1
fi

# Generate final report
SUBSCRIPTION_ID=$(jq -r '.subscription_id // "null"' "$STATE_FILE")
ERRORS=$(jq -r '.errors | length' "$STATE_FILE")
FINAL_EVENT_COUNT=$(jq -r '.events_received' "$STATE_FILE")
CONN_TIME=$(jq -r '.connection_time // "null"' "$STATE_FILE")
FIRST_EVENT=$(jq -r '.first_event_time // "null"' "$STATE_FILE")
LAST_EVENT=$(jq -r '.last_event_time // "null"' "$STATE_FILE")

# Calculate time to first event if available
TIME_TO_FIRST=""
if [[ "$FIRST_EVENT" != "null" && "$CONN_TIME" != "null" ]]; then
    TIME_TO_FIRST=$(echo "$FIRST_EVENT - $CONN_TIME" | bc -l)
fi

# Collect all events
ALL_EVENTS="[]"
if [[ -f "$EVENTS_FILE" && -s "$EVENTS_FILE" ]]; then
    ALL_EVENTS=$(jq -s '.' "$EVENTS_FILE")
fi

# Validate triggered transaction event if applicable
# When using --filter with CIRCLES_SEED_PHRASE, we trigger a 1 atto transfer and should receive it
TRIGGERED_TX_VALIDATED=false
TRIGGERED_TX_EXPECTED=false
if [[ -n "$FILTER_ADDRESS" && -n "$CIRCLES_SEED_PHRASE" && "$FINAL_EVENT_COUNT" -gt 0 ]]; then
    TRIGGERED_TX_EXPECTED=true
    # Look for CrcV2_TransferSingle with value="1" involving the filter address
    # The event should have the filter address as from, to, or tokenAddress
    MATCHING_EVENT=$(echo "$ALL_EVENTS" | jq -r --arg addr "${FILTER_ADDRESS,,}" '
        .[] | select(
            (."$type" == "CrcV2_TransferSingle") and
            (.value == "1" or .value == 1) and
            ((.from | ascii_downcase) == $addr or (.to | ascii_downcase) == $addr)
        ) | .transactionHash' 2>/dev/null | head -n 1)

    if [[ -n "$MATCHING_EVENT" && "$MATCHING_EVENT" != "null" ]]; then
        TRIGGERED_TX_VALIDATED=true
        log_message "✓ Validated triggered transaction (value=1 atto) in received events" "success"
    else
        log_message "⚠ Triggered transaction (value=1 atto) NOT found in received events" "warning"
    fi
fi

# Build final report
if [[ "$OUTPUT_MODE" == "json" ]]; then
    REPORT=$(jq -n \
        --arg url "$WS_URL" \
        --argjson duration "$MAX_DURATION" \
        --argjson min_events "$MIN_EVENTS" \
        --arg filter "${FILTER_ADDRESS:-null}" \
        --arg sub_id "$SUBSCRIPTION_ID" \
        --arg conn_time "$CONN_TIME" \
        --arg first_time "$FIRST_EVENT" \
        --arg last_time "$LAST_EVENT" \
        --argjson event_count "$FINAL_EVENT_COUNT" \
        --argjson events "$ALL_EVENTS" \
        --arg ttf "$TIME_TO_FIRST" \
        --argjson error_count "$ERRORS" \
        --argjson tx_expected "$TRIGGERED_TX_EXPECTED" \
        --argjson tx_validated "$TRIGGERED_TX_VALIDATED" \
        '{
            websocket_url: $url,
            max_duration_seconds: $duration,
            min_events_threshold: $min_events,
            filter_address: ($filter | if . == "null" then null else . end),
            subscription_id: ($sub_id | if . == "null" then null else . end),
            connection_time: ($conn_time | if . == "null" then null else . end),
            first_event_time: ($first_time | if . == "null" then null else . end),
            last_event_time: ($last_time | if . == "null" then null else . end),
            total_events_received: $event_count,
            time_to_first_event_seconds: ($ttf | if . == "" then null else (. | tonumber) end),
            triggered_tx_expected: $tx_expected,
            triggered_tx_validated: $tx_validated,
            events: $events,
            error_count: $error_count,
            success: ($sub_id != "null" and $error_count == 0)
        }')

    if [[ -n "$JSON_FILE" ]]; then
        echo "$REPORT" > "$JSON_FILE"
    else
        echo "$REPORT"
    fi
    REPORT_GENERATED=true
else
    # Pretty print summary
    echo ""
    echo "=============================================================="
    echo "SUBSCRIPTION TEST SUMMARY"
    echo "=============================================================="
    echo "WebSocket URL:           $WS_URL"
    echo "Subscription ID:         ${SUBSCRIPTION_ID}"
    echo "Max Duration:            ${MAX_DURATION}s"
    echo "Min Events Threshold:    $MIN_EVENTS"
    echo "Filter Address:          ${FILTER_ADDRESS:-(none)}"
    echo "Total Events Received:   $FINAL_EVENT_COUNT"

    if [[ -n "$TIME_TO_FIRST" && "$TIME_TO_FIRST" != "null" ]]; then
        printf "Time to First Event:     %.2fs\n" "$TIME_TO_FIRST"
    fi

    if [[ "$ERRORS" -gt 0 ]]; then
        echo ""
        echo -e "${RED}Errors: $ERRORS${NC}"
        jq -r '.errors[]' "$STATE_FILE" | while read -r err; do
            echo "  - $err"
        done
    fi

    if [[ "$FINAL_EVENT_COUNT" -gt 0 ]]; then
        echo ""
        echo "Event Types Breakdown:"
        jq -r '.[]["$type"] // "Unknown"' "$EVENTS_FILE" 2>/dev/null | sort | uniq -c | sort -rn | while read -r count type; do
            echo "  - $type: $count"
        done
    fi

    # Show transaction validation status if applicable
    if [[ "$TRIGGERED_TX_EXPECTED" == "true" ]]; then
        echo ""
        if [[ "$TRIGGERED_TX_VALIDATED" == "true" ]]; then
            echo -e "${GREEN}Triggered Tx Validation: ✓ Found 1-atto transfer in events${NC}"
        else
            echo -e "${YELLOW}Triggered Tx Validation: ⚠ 1-atto transfer NOT found in events${NC}"
        fi
    fi

    echo "=============================================================="
    echo ""

    if [[ "$SUBSCRIPTION_ID" != "null" && "$ERRORS" -eq 0 ]]; then
        echo -e "${GREEN}✓✓✓ Subscription test PASSED ✓✓✓${NC}"
    else
        echo -e "${RED}✗✗✗ Subscription test FAILED ✗✗✗${NC}"
    fi
    REPORT_GENERATED=true
fi

# Exit with appropriate code
if [[ "$SUBSCRIPTION_ID" != "null" && "$ERRORS" -eq 0 ]]; then
    exit 0
else
    exit 1
fi
