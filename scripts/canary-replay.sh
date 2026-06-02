#!/usr/bin/env bash
set -euo pipefail

# Circles Canary Replay
# Catches pathfinder 200s that would revert on-chain.
#
# Default flow:
#   1. Fetch structured request log entries from production pathfinder
#   2. Replay each request on staging to get full transfer steps
#   3. Build operateFlowMatrix calldata → eth_call simulation
#   4. Report: which production-served results would revert on-chain
#
# Usage:
#   circles-canary-replay [OPTIONS]
#
# Options:
#   --node prod1|prod2        Production node to fetch from (default: prod1)
#   --container <name>        Docker container name (default: pathfinder)
#   --staging <url>           Staging pathfinder URL (default: http://localhost:8080)
#   --since 1h|24h|7d         How far back to fetch logs (default: 1h)
#   --limit N                 Max requests to check (default: 50)
#   --gnosis-rpc <url>        Gnosis RPC for eth_call (default: https://rpc.gnosischain.com)
#   --rpc <url>               Circles RPC for wrapper resolution (default: https://rpc.aboutcircles.com/)
#   --no-simulate             Skip eth_call simulation (just compare maxFlow)
#   --tolerance PCT           maxFlow divergence threshold (default: 1.0)
#   --local <file>            Parse local log file instead of SSH
#   --no-fetch                Replay from previously fetched data
#   --json                    JSON output (for CI)
#   --no-color                Disable color output
#   --verbose                 Show full details per request
#   --help                    Show this help

# --- Infrastructure error exit code (distinct from exit 1=diverged, 2=revert) ---
EXIT_INFRA=3

trap 'log_err "Unexpected failure at line $LINENO"; exit $EXIT_INFRA' ERR

# --- Colors ---
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
BOLD='\033[1m'
DIM='\033[2m'
NC='\033[0m'

# --- Defaults ---
PROD_NODE="prod1"
CONTAINER="pathfinder"
STAGING_URL="http://localhost:8080"
SINCE="1h"
LIMIT=50
GNOSIS_RPC_URL="https://rpc.gnosischain.com"
CIRCLES_RPC_URL="https://rpc.aboutcircles.com/"
SIMULATE=true
TOLERANCE="1.0"
LOCAL_FILE=""
NO_FETCH=false
JSON_OUTPUT=false
NO_COLOR=false
VERBOSE=false

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Per-user cache path. A fixed /tmp/circles-canary is owned by whoever mkdir's it first; if a
# root operator runs the script, the dir becomes root-owned and the unprivileged service account
# running the scheduled timer then fails with "Permission denied". Namespacing by numeric uid keeps
# the operator and service dirs separate. `id -u` is used (not `id -un`): it always exits 0 with a
# single numeric line and needs no /etc/passwd entry, so it can't yield a multiline/empty suffix.
# Override with CANARY_CACHE_DIR if a fixed path is needed.
CACHE_DIR="${CANARY_CACHE_DIR:-/tmp/circles-canary-$(id -u)}"
HUB_ADDR="0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8"

# --- Known revert selectors ---
declare -A REVERT_NAMES=(
    ["0x5e418dba"]="FlowEdgeIsNotPermitted"
    ["0xc14c0700"]="AvatarMustBeRegistered"
    ["0x03dee4c5"]="ERC1155InsufficientBalance"
    ["0x57f447ce"]="ERC1155InvalidReceiver"
    ["0xfb8f41b2"]="ERC1155MissingApprovalForAll"
)

# --- Arg parsing ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --node)         PROD_NODE="$2"; shift 2 ;;
        --container)    CONTAINER="$2"; shift 2 ;;
        --staging)      STAGING_URL="$2"; shift 2 ;;
        --since)        SINCE="$2"; shift 2 ;;
        --limit)        LIMIT="$2"; shift 2 ;;
        --gnosis-rpc)   GNOSIS_RPC_URL="$2"; shift 2 ;;
        --rpc)          CIRCLES_RPC_URL="$2"; shift 2 ;;
        --no-simulate)  SIMULATE=false; shift ;;
        --tolerance)    TOLERANCE="$2"; shift 2 ;;
        --local)        LOCAL_FILE="$2"; shift 2 ;;
        --no-fetch)     NO_FETCH=true; shift ;;
        --json)         JSON_OUTPUT=true; shift ;;
        --no-color)     NO_COLOR=true; shift ;;
        --verbose)      VERBOSE=true; shift ;;
        --help)
            awk '/^# Circles Canary/,/^$/' "$0" | grep '^#' | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        -*)
            echo "Unknown option: $1" >&2
            exit 1
            ;;
        *)
            echo "Unexpected argument: $1" >&2
            exit 1
            ;;
    esac
done

# --- Input validation (H8: prevent SSH command injection) ---
if [[ ! "$SINCE" =~ ^[0-9]+[smhd]$ ]]; then
    echo "Error: --since must be a number followed by s/m/h/d (e.g., 1h, 24h, 7d)" >&2
    exit 1
fi

if [[ ! "$LIMIT" =~ ^[0-9]+$ ]]; then
    echo "Error: --limit must be a positive integer" >&2
    exit 1
fi

if [[ "$NO_COLOR" == true ]]; then
    GREEN='' BLUE='' YELLOW='' RED='' CYAN='' BOLD='' DIM='' NC=''
fi

# --- Pre-flight checks ---
if [[ "$SIMULATE" == true ]]; then
    if ! command -v node &>/dev/null; then
        echo "Error: node is required for eth_call simulation (calldata encoding). Install Node.js or use --no-simulate." >&2
        exit $EXIT_INFRA
    fi
    if [[ ! -f "$SCRIPT_DIR/diagnose-swap-calldata.mjs" ]]; then
        echo "Error: $SCRIPT_DIR/diagnose-swap-calldata.mjs not found." >&2
        exit $EXIT_INFRA
    fi
fi

# --- Helpers ---
log_info()  { [[ "$JSON_OUTPUT" == true ]] || echo -e "$@"; }
log_dim()   { [[ "$JSON_OUTPUT" == true ]] || echo -e "      ${DIM}$*${NC}"; }
log_ok()    { [[ "$JSON_OUTPUT" == true ]] || echo -e "  ${GREEN}✓${NC} $*"; }
log_warn()  { [[ "$JSON_OUTPUT" == true ]] || echo -e "  ${YELLOW}~${NC} $*"; }
log_fail()  { [[ "$JSON_OUTPUT" == true ]] || echo -e "  ${RED}✗${NC} $*"; }
log_err()   { echo -e "${RED}Error: $*${NC}" >&2; }

format_crc() {
    printf '%s' "$1" | awk '{ printf "%.2f", $1 / 1000000000000000000 }'
}

short_addr() {
    printf '%s' "${1:0:6}…${1:38:4}"
}

decode_revert() {
    local data="$1"
    if [[ ${#data} -ge 10 ]]; then
        local sel="${data:0:10}"
        printf '%s' "${REVERT_NAMES[$sel]:-$sel}"
    else
        printf '%s' "unknown"
    fi
}

# Resolve wrapper addresses to underlying avatars via circles RPC
resolve_wrappers() {
    local transfers_json="$1"
    local owners
    owners=$(printf '%s' "$transfers_json" | jq -r '.[].tokenOwner' | sort -u)

    local wrapper_map="{}"
    while IFS= read -r addr; do
        [[ -z "$addr" ]] && continue

        # (M7: safe jq --arg, no string interpolation)
        local payload
        payload=$(jq -n --arg a "$addr" '{jsonrpc:"2.0",id:1,method:"circles_getTokenInfo",params:[$a]}')

        local resp
        resp=$(curl -s -X POST --max-time 5 "$CIRCLES_RPC_URL" \
            -H "Content-Type: application/json" \
            -d "$payload" 2>/dev/null || echo '{}')

        local token_type
        token_type=$(printf '%s' "$resp" | jq -r '.result.type // empty' 2>/dev/null)

        if [[ "$token_type" == "CrcV2_ERC20WrapperDeployed"* ]] || [[ "$token_type" == *"Wrapper"* ]]; then
            local underlying
            underlying=$(printf '%s' "$resp" | jq -r '.result.tokenOwner // .result.avatar // empty' 2>/dev/null)
            if [[ -n "$underlying" ]]; then
                wrapper_map=$(printf '%s' "$wrapper_map" | jq --arg w "$addr" --arg a "$underlying" '. + {($w): $a}')
            fi
        fi
    done <<< "$owners"

    printf '%s' "$wrapper_map"
}

mkdir -p "$CACHE_DIR"

# Parse structured log line into JSON.
# Input:  "findPath source=0x... sink=0x... targetFlow=... maxFlow=... transfers=5 graphBlock=123 ..."
# Output: one JSON object per line (JSONL)
parse_request_log() {
    awk '
    # Convert a comma-separated token list (e.g. "0xaaa,0xbbb" or "") to a JSON array.
    function jarr(s,    n, parts, i, out) {
        if (s == "") return "[]"
        n = split(s, parts, ",")
        out = "["
        for (i = 1; i <= n; i++) {
            gsub(/"/, "\\\"", parts[i])
            out = out (i > 1 ? "," : "") "\"" parts[i] "\""
        }
        return out "]"
    }
    /source=.*sink=.*targetFlow=/ {
        route=""; source=""; sink=""; tf=""; mf="0"; xfers=0; mt="null"; gb=0; ms=0; st=0; wrap="false"; qm="false"; err=""
        ft=""; tt=""; eft=""; ett=""; rid=""
        for (i=1; i<=NF; i++) {
            if ($i ~ /^(findPath|findMaxFlow)$/) route=$i
            else if ($i ~ /^source=/) source=substr($i, 8)
            else if ($i ~ /^sink=/) sink=substr($i, 6)
            else if ($i ~ /^targetFlow=/) tf=substr($i, 12)
            else if ($i ~ /^maxFlow=/) { v=substr($i, 9); mf=(v=="" ? "0" : v) }
            else if ($i ~ /^transfers=/) xfers=substr($i, 11)
            else if ($i ~ /^maxTransfers=/) { v=substr($i, 14); mt=(v=="-1")?"null":v }
            else if ($i ~ /^graphBlock=/) gb=substr($i, 12)
            else if ($i ~ /^durationMs=/) ms=substr($i, 12)
            else if ($i ~ /^status=/) st=substr($i, 8)
            else if ($i ~ /^withWrap=/) { v=substr($i, 10); wrap=(tolower(v)=="true")?"true":"false" }
            else if ($i ~ /^quantizedMode=/) { v=substr($i, 15); qm=(tolower(v)=="true")?"true":"false" }
            else if ($i ~ /^fromTokens=/) ft=substr($i, 12)
            else if ($i ~ /^toTokens=/) tt=substr($i, 10)
            else if ($i ~ /^excludedFromTokens=/) eft=substr($i, 20)
            else if ($i ~ /^excludedToTokens=/) ett=substr($i, 18)
            else if ($i ~ /^reqId=/) rid=substr($i, 7)
            else if ($i ~ /^error=/) err=substr($i, 7)
        }
        if (source != "" && sink != "") {
            # Escape quotes in values to produce valid JSON
            gsub(/"/, "\\\"", source)
            gsub(/"/, "\\\"", sink)
            gsub(/"/, "\\\"", tf)
            gsub(/"/, "\\\"", mf)
            gsub(/"/, "\\\"", err)
            gsub(/"/, "\\\"", rid)
            printf "{\"route\":\"%s\",\"source\":\"%s\",\"sink\":\"%s\",\"targetFlow\":\"%s\",\"maxFlow\":\"%s\",\"transfers\":%s,\"maxTransfers\":%s,\"graphBlock\":%s,\"durationMs\":%s,\"status\":%s,\"withWrap\":%s,\"quantizedMode\":%s,\"fromTokens\":%s,\"toTokens\":%s,\"excludedFromTokens\":%s,\"excludedToTokens\":%s,\"reqId\":\"%s\",\"error\":\"%s\"}\n", route, source, sink, tf, mf, xfers, mt, gb, ms, st, wrap, qm, jarr(ft), jarr(tt), jarr(eft), jarr(ett), rid, err
        }
    }'
}

# ============================================================
# Phase 1: Fetch request logs from production
# ============================================================
RAW_LOG="$CACHE_DIR/raw-logs.txt"
CANARY_FILE="$CACHE_DIR/canary-entries.jsonl"

if [[ -n "$LOCAL_FILE" ]]; then
    log_info "${YELLOW}[1/3] Parsing local file: $LOCAL_FILE${NC}"
    parse_request_log < "$LOCAL_FILE" > "$CANARY_FILE"
elif [[ "$NO_FETCH" == true ]]; then
    log_info "${YELLOW}[1/3] Using cached data${NC}"
    if [[ ! -f "$CANARY_FILE" ]]; then
        log_err "No cached data found at $CANARY_FILE. Run without --no-fetch first."
        exit $EXIT_INFRA
    fi
else
    log_info "${YELLOW}[1/3] Fetching request logs from ${BOLD}$PROD_NODE${NC}${YELLOW} (last $SINCE)...${NC}"

    for attempt in 1 2 3; do
        if ssh -o ConnectTimeout=10 "$PROD_NODE" \
            "docker logs '$CONTAINER' --since '$SINCE' 2>&1" \
            > "$RAW_LOG" 2>/dev/null; then
            break
        fi
        if [[ $attempt -lt 3 ]]; then
            log_dim "SSH attempt $attempt failed, retrying..."
            sleep 2
        else
            log_err "Failed to fetch logs from $PROD_NODE after 3 attempts"
            exit $EXIT_INFRA
        fi
    done

    parse_request_log < "$RAW_LOG" > "$CANARY_FILE"
fi

TOTAL_ENTRIES=$(wc -l < "$CANARY_FILE" | tr -d ' ')

if [[ "$TOTAL_ENTRIES" -eq 0 ]]; then
    log_info "      ${DIM}No pathfinder request log entries found.${NC}"
    log_info "      ${DIM}Check that the pathfinder on $PROD_NODE is running the version with request logging.${NC}"
    exit 0
fi

# Filter to status=200 with transfers > 0, deduplicate by request shape.
# The token filters are part of the shape (they change which path is built), so distinct
# constrained requests are not collapsed together.
DEDUPED_FILE="$CACHE_DIR/canary-deduped.jsonl"
if ! jq -s '
    [ .[] | select(.status == 200 and .transfers > 0) ]
    | group_by(.source + "|" + .sink + "|" + .targetFlow + "|" + (.withWrap|tostring) + "|" + (.quantizedMode|tostring)
        + "|" + ((.toTokens // [])|tostring) + "|" + ((.fromTokens // [])|tostring)
        + "|" + ((.excludedFromTokens // [])|tostring) + "|" + ((.excludedToTokens // [])|tostring))
    | map(last)
' "$CANARY_FILE" 2>/dev/null | jq -c '.[]' > "$DEDUPED_FILE" 2>/dev/null; then
    log_err "Failed to parse canary entries (corrupted log data?)"
    exit $EXIT_INFRA
fi

UNIQUE_COUNT=$(wc -l < "$DEDUPED_FILE" | tr -d ' ')
REPLAY_COUNT=$((UNIQUE_COUNT < LIMIT ? UNIQUE_COUNT : LIMIT))

log_info "      Found ${BOLD}$TOTAL_ENTRIES${NC} entries, ${BOLD}$UNIQUE_COUNT${NC} with transfers, replaying ${BOLD}$REPLAY_COUNT${NC}"
echo ""

if [[ "$REPLAY_COUNT" -eq 0 ]]; then
    log_info "      ${DIM}No requests with transfers to validate.${NC}"
    exit 0
fi

# ============================================================
# Phase 2: Replay on staging + simulate on-chain
# ============================================================
if [[ "$SIMULATE" == true ]]; then
    log_info "${YELLOW}[2/3] Replay on staging → eth_call simulation...${NC}"
else
    log_info "${YELLOW}[2/3] Replay on staging (comparison only, --no-simulate)...${NC}"
fi

RESULTS_FILE="$CACHE_DIR/canary-results.jsonl"
> "$RESULTS_FILE"

# Counters
MATCH=0; DRIFT=0; DIVERGED=0; REPLAY_ERR=0
SIM_PASS=0; SIM_REVERT=0; SIM_SKIP=0
REVERT_REASONS=()
IDX=0

while IFS= read -r entry; do
    IDX=$((IDX + 1))
    [[ $IDX -gt $LIMIT ]] && break

    source_addr=$(printf '%s' "$entry" | jq -r '.source')
    sink_addr=$(printf '%s' "$entry" | jq -r '.sink')
    target_flow=$(printf '%s' "$entry" | jq -r '.targetFlow')
    prod_max_flow=$(printf '%s' "$entry" | jq -r '.maxFlow // "0"')
    prod_transfers=$(printf '%s' "$entry" | jq -r '.transfers // 0')
    with_wrap=$(printf '%s' "$entry" | jq -r '.withWrap // false')
    quantized_mode=$(printf '%s' "$entry" | jq -r '.quantizedMode // false')
    max_transfers=$(printf '%s' "$entry" | jq -r '.maxTransfers // null')
    prod_graph_block=$(printf '%s' "$entry" | jq -r '.graphBlock // 0')
    to_tokens=$(printf '%s' "$entry" | jq -c '.toTokens // []')
    from_tokens=$(printf '%s' "$entry" | jq -c '.fromTokens // []')
    excluded_from_tokens=$(printf '%s' "$entry" | jq -c '.excludedFromTokens // []')
    excluded_to_tokens=$(printf '%s' "$entry" | jq -c '.excludedToTokens // []')
    req_id=$(printf '%s' "$entry" | jq -r '.reqId // ""')

    # Guard against empty maxFlow from parse failures
    [[ -z "$prod_max_flow" ]] && prod_max_flow="0"

    label="$(short_addr "$source_addr")→$(short_addr "$sink_addr")"
    prod_crc=$(format_crc "$prod_max_flow")

    # --- Replay on staging ---
    POST_BODY=$(jq -n \
        --arg source "$source_addr" \
        --arg sink "$sink_addr" \
        --arg targetFlow "$target_flow" \
        --argjson withWrap "$with_wrap" \
        --argjson quantizedMode "$quantized_mode" \
        '{source: $source, sink: $sink, targetFlow: $targetFlow,
          withWrap: $withWrap, quantizedMode: $quantizedMode}')

    if [[ "$max_transfers" != "null" ]]; then
        POST_BODY=$(printf '%s' "$POST_BODY" | jq --argjson mt "$max_transfers" '. + {maxTransfers: $mt}')
    fi

    # Forward token filters (only when non-empty) so the replay reconstructs the exact path shape
    # — group-targeted / score-group payments depend on these and are the #74-prone requests.
    [[ "$to_tokens" != "[]" ]]            && POST_BODY=$(printf '%s' "$POST_BODY" | jq --argjson v "$to_tokens" '. + {toTokens: $v}')
    [[ "$from_tokens" != "[]" ]]          && POST_BODY=$(printf '%s' "$POST_BODY" | jq --argjson v "$from_tokens" '. + {fromTokens: $v}')
    [[ "$excluded_from_tokens" != "[]" ]] && POST_BODY=$(printf '%s' "$POST_BODY" | jq --argjson v "$excluded_from_tokens" '. + {excludedFromTokens: $v}')
    [[ "$excluded_to_tokens" != "[]" ]]   && POST_BODY=$(printf '%s' "$POST_BODY" | jq --argjson v "$excluded_to_tokens" '. + {excludedToTokens: $v}')

    STAGING_RESP=$(curl -s -X POST "${STAGING_URL%/}/findPath" \
        -H "Content-Type: application/json" \
        -d "$POST_BODY" \
        --max-time 60 2>/dev/null || echo '{"error":"timeout"}')

    staging_err=$(printf '%s' "$STAGING_RESP" | jq -r 'if type == "string" then . elif .error then (.error | if type == "object" then .message else . end) else empty end' 2>/dev/null)
    staging_max_flow=$(printf '%s' "$STAGING_RESP" | jq -r '.maxFlow // "0"' 2>/dev/null)
    staging_transfers_json=$(printf '%s' "$STAGING_RESP" | jq '.transfers // []' 2>/dev/null)
    staging_transfer_count=$(printf '%s' "$staging_transfers_json" | jq 'length' 2>/dev/null || echo "0")
    staging_crc=$(format_crc "$staging_max_flow")

    # Guard against empty values
    [[ -z "$staging_max_flow" ]] && staging_max_flow="0"

    # --- maxFlow comparison (H3: use awk -v to prevent injection) ---
    if [[ -n "$staging_err" ]] && [[ "$staging_err" != "null" ]]; then
        comparison="ERROR"
        REPLAY_ERR=$((REPLAY_ERR + 1))
    elif [[ "$prod_max_flow" == "$staging_max_flow" ]]; then
        comparison="MATCH"
        MATCH=$((MATCH + 1))
    else
        pct_diff=$(awk -v pm="$prod_max_flow" -v sm="$staging_max_flow" 'BEGIN { if (pm+0 == 0) print 100; else { d=(sm-pm)/pm*100; print (d<0?-d:d) } }')
        is_over=$(awk -v pd="$pct_diff" -v tol="$TOLERANCE" 'BEGIN { print (pd+0 > tol+0) ? 1 : 0 }')
        if [[ "$is_over" == "0" ]]; then
            comparison="DRIFT"
            DRIFT=$((DRIFT + 1))
        else
            comparison="DIVERGED"
            DIVERGED=$((DIVERGED + 1))
        fi
    fi

    # --- On-chain simulation ---
    sim_verdict="skip"
    sim_revert_name=""

    if [[ "$SIMULATE" == true ]] && [[ "$comparison" != "ERROR" ]] && [[ "$staging_transfer_count" -gt 0 ]]; then

        # Resolve wrappers if withWrap was used
        WRAPPER_MAP="{}"
        if [[ "$with_wrap" == "true" ]]; then
            WRAPPER_MAP=$(resolve_wrappers "$staging_transfers_json")
        fi

        CALLDATA_INPUT=$(printf '%s' "$STAGING_RESP" | jq --arg from "$source_addr" --arg to "$sink_addr" --argjson wmap "$WRAPPER_MAP" \
            '{from: $from, to: $to, transfers: .transfers, wrapperMap: $wmap}')

        CALLDATA_ERR="$CACHE_DIR/calldata-err.txt"
        CALLDATA=$(printf '%s' "$CALLDATA_INPUT" | node "$SCRIPT_DIR/diagnose-swap-calldata.mjs" 2>"$CALLDATA_ERR") || CALLDATA=""

        if [[ -z "$CALLDATA" ]]; then
            # Empty calldata = nothing to simulate. Distinguish a real encoder/node failure
            # (non-empty stderr) from a legitimately empty result, so a systematic calldata-build
            # break is surfaced as a warning instead of being silently counted as a benign skip
            # (which would mask on-chain reverts — the exact thing this canary exists to catch).
            if [[ -s "$CALLDATA_ERR" ]]; then
                log_warn "$label  calldata build failed — sim skipped (encoder error)"
                [[ "$VERBOSE" == true ]] && log_dim "$(cat "$CALLDATA_ERR")"
            fi
            sim_verdict="skip"
            SIM_SKIP=$((SIM_SKIP + 1))
        else
            # Use graphBlock for simulation if available, else "latest"
            local_block="\"latest\""
            if [[ "$prod_graph_block" -gt 0 ]] 2>/dev/null; then
                local_block="\"0x$(printf '%x' "$prod_graph_block")\""
            fi

            ETH_CALL=$(jq -n \
                --arg from "$source_addr" \
                --arg to "$HUB_ADDR" \
                --arg data "$CALLDATA" \
                --argjson block "$local_block" \
                '{jsonrpc:"2.0",id:1,method:"eth_call",params:[{from:$from,to:$to,data:$data},$block]}')

            SIM_RESP=$(curl -s -X POST "$GNOSIS_RPC_URL" \
                -H "Content-Type: application/json" \
                -d "$ETH_CALL" \
                --max-time 30 2>/dev/null || echo '{"error":{"message":"timeout"}}')

            SIM_ERROR=$(printf '%s' "$SIM_RESP" | jq -r '.error.message // empty' 2>/dev/null)

            if [[ -z "$SIM_ERROR" ]]; then
                sim_verdict="pass"
                SIM_PASS=$((SIM_PASS + 1))
            else
                # Extract revert data
                REVERT_DATA=$(printf '%s' "$SIM_RESP" | jq -r '.error.data // empty' 2>/dev/null)
                [[ -z "$REVERT_DATA" || "$REVERT_DATA" == "null" ]] && \
                    REVERT_DATA=$(printf '%s' "$SIM_RESP" | jq -r '.error.data.data // empty' 2>/dev/null)

                if [[ -n "$REVERT_DATA" ]] && [[ "$REVERT_DATA" != "null" ]]; then
                    sim_revert_name=$(decode_revert "$REVERT_DATA")
                else
                    sim_revert_name="$SIM_ERROR"
                fi

                sim_verdict="revert"
                SIM_REVERT=$((SIM_REVERT + 1))
                REVERT_REASONS+=("$sim_revert_name")
            fi
        fi
    elif [[ "$SIMULATE" == true ]]; then
        SIM_SKIP=$((SIM_SKIP + 1))
    fi

    # --- Output line ---
    if [[ "$SIMULATE" == true ]]; then
        case "$sim_verdict" in
            pass)
                log_ok "$label  ${prod_crc} CRC  ${GREEN}on-chain OK${NC}  ${DIM}($comparison)${NC}"
                ;;
            revert)
                log_fail "$label  ${prod_crc} CRC  ${RED}REVERT: $sim_revert_name${NC}  ${DIM}($comparison)${NC}"
                [[ "$VERBOSE" == true ]] && log_dim "reqId=$req_id source=$source_addr sink=$sink_addr block=$prod_graph_block"
                ;;
            skip)
                if [[ "$comparison" == "ERROR" ]]; then
                    log_fail "$label  ${prod_crc} CRC  ${RED}staging error${NC}"
                    [[ "$VERBOSE" == true ]] && log_dim "$staging_err"
                else
                    log_warn "$label  ${prod_crc} CRC  ${DIM}sim skipped${NC}  ${DIM}($comparison)${NC}"
                fi
                ;;
        esac
    else
        case "$comparison" in
            MATCH)    log_ok   "$label  ${prod_crc} CRC  ${GREEN}MATCH${NC}" ;;
            DRIFT)    log_warn "$label  prod=${prod_crc}  staging=${staging_crc}  ${YELLOW}DRIFT${NC}" ;;
            DIVERGED) log_fail "$label  prod=${prod_crc}  staging=${staging_crc}  ${RED}DIVERGED${NC}" ;;
            ERROR)    log_fail "$label  ${prod_crc} CRC  ${RED}staging error${NC}" ;;
        esac
    fi

    # Save result
    jq -n -c \
        --arg reqId "$req_id" \
        --arg source "$source_addr" --arg sink "$sink_addr" \
        --arg targetFlow "$target_flow" \
        --arg prodMaxFlow "$prod_max_flow" --arg stagingMaxFlow "$staging_max_flow" \
        --arg comparison "$comparison" --arg simVerdict "$sim_verdict" \
        --arg simRevertName "$sim_revert_name" \
        --argjson prodGraphBlock "$prod_graph_block" \
        '{reqId, source, sink, targetFlow, prodMaxFlow, stagingMaxFlow,
          comparison, simVerdict, simRevertName, prodGraphBlock}' \
        >> "$RESULTS_FILE"

done < "$DEDUPED_FILE"

echo ""

# ============================================================
# Phase 3: Summary
# ============================================================
# Disable ERR trap for summary — all critical work is done
trap - ERR

log_info "${YELLOW}[3/3] Summary${NC}"

TOTAL=$((MATCH + DRIFT + DIVERGED + REPLAY_ERR))

if [[ "$JSON_OUTPUT" == true ]]; then
    # Aggregate revert reasons
    REVERT_AGG="{}"
    for r in "${REVERT_REASONS[@]+"${REVERT_REASONS[@]}"}"; do
        REVERT_AGG=$(printf '%s' "$REVERT_AGG" | jq --arg r "$r" '.[$r] = ((.[$r] // 0) + 1)')
    done

    jq -n \
        --arg src "$PROD_NODE" --arg since "$SINCE" \
        --argjson total "$TOTAL" \
        --argjson match "$MATCH" --argjson drift "$DRIFT" \
        --argjson diverged "$DIVERGED" --argjson replayErr "$REPLAY_ERR" \
        --argjson simPass "$SIM_PASS" --argjson simRevert "$SIM_REVERT" \
        --argjson simSkip "$SIM_SKIP" \
        --argjson revertReasons "$REVERT_AGG" \
        '{
            source: $src, since: $since, total: $total,
            comparison: {match: $match, drift: $drift, diverged: $diverged, error: $replayErr},
            simulation: {pass: $simPass, revert: $simRevert, skip: $simSkip, revertReasons: $revertReasons}
        }'
else
    pct() { [[ $TOTAL -gt 0 ]] && awk -v n="$1" -v t="$TOTAL" 'BEGIN{printf "%.0f", n/t*100}' || echo 0; }

    echo -e "      Source:       ${BOLD}$PROD_NODE${NC} (last $SINCE)"
    echo -e "      Checked:      ${BOLD}$TOTAL${NC} unique requests"
    echo ""

    if [[ "$SIMULATE" == true ]]; then
        echo -e "      ${BOLD}On-chain simulation:${NC}"
        echo -e "        Pass:       ${GREEN}$SIM_PASS${NC}"
        echo -e "        ${RED}Revert:     $SIM_REVERT${NC}"
        echo -e "        Skip:       ${DIM}$SIM_SKIP${NC}"

        if [[ ${#REVERT_REASONS[@]} -gt 0 ]]; then
            echo ""
            echo -e "      ${BOLD}Revert breakdown:${NC}"
            printf '%s\n' "${REVERT_REASONS[@]}" | sort | uniq -c | sort -rn | while read -r count reason; do
                echo -e "        ${RED}${count}x${NC} $reason"
            done
        fi

        echo ""
    fi

    echo -e "      ${BOLD}maxFlow comparison:${NC}"
    echo -e "        Match:      ${GREEN}$MATCH${NC} ($(pct $MATCH)%)"
    echo -e "        Drift:      ${YELLOW}$DRIFT${NC} (<${TOLERANCE}%)"
    echo -e "        Diverged:   ${RED}$DIVERGED${NC} (>${TOLERANCE}%)"
    echo -e "        Error:      ${RED}$REPLAY_ERR${NC}"
fi

# Exit non-zero if any reverts detected — this is the primary signal
if [[ $SIM_REVERT -gt 0 ]]; then
    exit 2
fi

# Exit 1 for maxFlow divergences (secondary concern)
if [[ $DIVERGED -gt 0 ]]; then
    exit 1
fi
