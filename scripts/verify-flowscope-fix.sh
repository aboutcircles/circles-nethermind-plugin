#!/usr/bin/env bash
set -euo pipefail

# verify-flowscope-fix.sh
#
# Differential verification for PR #347-fix (V2-narrowed MATERIALIZED CTE).
# Compares prod (pre-fix, has over-return bug) vs staging-direct (post-fix)
# for `circles_events` with an `address` filter. Asserts:
#
#   1. Latency improvement: staging wall time ≤ ~2x prod wall time
#   2. Correctness improvement: staging returns ≤ prod's flow-scope row count
#   3. Bug invariant on prod: at least one flow-scope row whose txHash
#      doesn't share with any address-bearing row in the same response
#      (proves the bug existed; if 0, the chosen avatar isn't a useful repro).
#   4. Fix invariant on staging: every flow-scope row's txHash appears in
#      at least one address-bearing row in the same response (proves the
#      fix works on real data).
#
# Usage:
#   ./verify-flowscope-fix.sh <prod-url> <staging-url> [avatar1] [avatar2] ...
#
# Examples:
#   ./verify-flowscope-fix.sh https://rpc.aboutcircles.com https://staging1-direct/ \
#     0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0
#
# Defaults to the blackbox-rpc-functional probe avatar if none given.

PROD_URL="${1:-}"
STAGING_URL="${2:-}"
shift 2 2>/dev/null || true

if [[ -z "$PROD_URL" || -z "$STAGING_URL" ]]; then
    echo "Usage: $0 <prod-url> <staging-url> [avatar1] [avatar2] ..." >&2
    exit 2
fi

AVATARS=("$@")
if [[ ${#AVATARS[@]} -eq 0 ]]; then
    AVATARS=("0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0")
fi

# Colors (fall back to none if not a tty)
if [[ -t 1 ]]; then
    BOLD=$'\033[1m'; GREEN=$'\033[0;32m'; RED=$'\033[0;31m'
    YELLOW=$'\033[0;33m'; CYAN=$'\033[0;36m'; NC=$'\033[0m'
else
    BOLD=''; GREEN=''; RED=''; YELLOW=''; CYAN=''; NC=''
fi

PASS=0
FAIL=0
SUMMARY=""

# Run circles_events for an avatar against an endpoint.
# Echo: "<wall_seconds> <total_rows> <flowscope_rows> <leaked_flowscope_rows>"
# Exit non-zero on transport error.
probe() {
    local url="$1" avatar="$2"
    local payload
    payload=$(printf '{"jsonrpc":"2.0","method":"circles_events","params":["%s",null,null,null,null,true],"id":1}' "$avatar")

    local start_ns end_ns body http_code
    start_ns=$(python3 -c 'import time;print(time.time_ns())')

    # Capture body and HTTP status. 30s ceiling.
    local tmpfile
    tmpfile=$(mktemp)
    http_code=$(curl -sS --max-time 30 -o "$tmpfile" -w '%{http_code}' \
        -H 'Content-Type: application/json' \
        -d "$payload" \
        "$url" 2>&1) || {
        echo "ERROR: $http_code" >&2
        rm -f "$tmpfile"
        return 1
    }
    end_ns=$(python3 -c 'import time;print(time.time_ns())')
    body=$(cat "$tmpfile")
    rm -f "$tmpfile"

    if [[ "$http_code" != "200" ]]; then
        echo "ERROR: HTTP $http_code from $url" >&2
        return 1
    fi

    local wall_seconds total flow leaked
    wall_seconds=$(python3 -c "print(($end_ns - $start_ns) / 1e9)")

    # Compute counts via jq:
    #   total: number of result entries
    #   flow: count of CrcV2_FlowEdgesScope* entries
    #   leaked: count of CrcV2_FlowEdgesScope* entries whose transactionHash
    #     does NOT appear in any non-flow-scope entry in the same response
    read -r total flow leaked < <(jq -r '
        (.result // []) as $r
        | ($r | map(select(.event | test("^CrcV2_FlowEdgesScope")) | .values.transactionHash)) as $flow_txs
        | ($r | map(select(.event | test("^CrcV2_FlowEdgesScope") | not) | .values.transactionHash)) as $addr_txs
        | ($r | length) as $total
        | ($flow_txs | length) as $flow_count
        | ($flow_txs - $addr_txs | length) as $leaked
        | "\($total) \($flow_count) \($leaked)"
    ' <<<"$body")

    printf '%s %s %s %s\n' "$wall_seconds" "$total" "$flow" "$leaked"
}

# Format a number to 2 decimals if it's a float
fmt() {
    awk -v n="$1" 'BEGIN { if (n ~ /\./) printf "%.2f", n; else printf "%s", n }'
}

assert() {
    local label="$1" cond="$2" detail="$3"
    if [[ "$cond" == "true" ]]; then
        printf "  ${GREEN}✓${NC} %s — %s\n" "$label" "$detail"
        PASS=$((PASS+1))
    else
        printf "  ${RED}✗${NC} %s — %s\n" "$label" "$detail"
        FAIL=$((FAIL+1))
        SUMMARY+=$'\n  - '"$label: $detail"
    fi
}

echo "${BOLD}verify-flowscope-fix${NC}"
echo "  prod    = $PROD_URL"
echo "  staging = $STAGING_URL"
echo "  avatars = ${AVATARS[*]}"
echo

for avatar in "${AVATARS[@]}"; do
    echo "${BOLD}${CYAN}avatar ${avatar}${NC}"

    set +e
    prod_out=$(probe "$PROD_URL" "$avatar")
    prod_rc=$?
    staging_out=$(probe "$STAGING_URL" "$avatar")
    staging_rc=$?
    set -e

    if [[ $prod_rc -ne 0 || $staging_rc -ne 0 ]]; then
        printf "  ${RED}✗${NC} probe failed (prod_rc=%s staging_rc=%s)\n" "$prod_rc" "$staging_rc"
        FAIL=$((FAIL+1))
        continue
    fi

    read -r prod_wall prod_total prod_flow prod_leaked <<<"$prod_out"
    read -r staging_wall staging_total staging_flow staging_leaked <<<"$staging_out"

    printf "  %-12s %-10s %-10s %-12s %-12s\n" "endpoint" "wall(s)" "total" "flow-scope" "leaked"
    printf "  %-12s ${YELLOW}%-10s${NC} %-10s %-12s %-12s\n" \
        "prod" "$(fmt "$prod_wall")" "$prod_total" "$prod_flow" "$prod_leaked"
    printf "  %-12s ${GREEN}%-10s${NC} %-10s %-12s %-12s\n" \
        "staging" "$(fmt "$staging_wall")" "$staging_total" "$staging_flow" "$staging_leaked"
    echo

    # Assertion 1: staging latency ≤ 2× prod
    if (( $(awk -v s="$staging_wall" -v p="$prod_wall" 'BEGIN { print (s <= p * 2 + 0.5) }') )); then
        assert "latency" "true" "staging $(fmt "$staging_wall")s ≤ 2× prod $(fmt "$prod_wall")s + 0.5s headroom"
    else
        assert "latency" "false" "staging $(fmt "$staging_wall")s > 2× prod $(fmt "$prod_wall")s + 0.5s headroom"
    fi

    # Assertion 2: staging returns ≤ prod flow-scope rows (fix is more restrictive)
    if [[ "$staging_flow" -le "$prod_flow" ]]; then
        assert "correctness" "true" "staging flow-scope rows ($staging_flow) ≤ prod ($prod_flow)"
    else
        assert "correctness" "false" "staging returned MORE flow-scope rows than prod"
    fi

    # Assertion 3: prod has the bug (≥1 leaked row) — informative, not blocking
    if [[ "$prod_leaked" -gt 0 ]]; then
        printf "  ${YELLOW}i${NC} prod over-return repro: $prod_leaked flow-scope rows leak (txHash not in address-bearing rows of same response)\n"
    else
        printf "  ${YELLOW}i${NC} prod over-return NOT reproduced for this avatar — pick a more active one to confirm bug\n"
    fi

    # Assertion 4: staging has zero leaks (fix invariant)
    if [[ "$staging_leaked" -eq 0 ]]; then
        assert "fix invariant" "true" "staging flow-scope rows all share txHash with address-bearing rows"
    else
        assert "fix invariant" "false" "staging leaked $staging_leaked flow-scope rows — fix did NOT close over-return"
    fi

    echo
done

echo "${BOLD}summary${NC}: ${GREEN}${PASS} passed${NC}, ${RED}${FAIL} failed${NC}"
if [[ $FAIL -gt 0 ]]; then
    echo "${RED}failures:${NC}${SUMMARY}"
    exit 1
fi
