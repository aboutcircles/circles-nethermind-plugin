#!/bin/bash
# Run snapshot-based integration tests against the Circles Test Environment
#
# Usage:
#   ./scripts/test-snapshot.sh                    # Local (localhost:5200)
#   ./scripts/test-snapshot.sh staging            # Staging environment
#   ./scripts/test-snapshot.sh http://custom:5200 # Custom URL
#
# Environment variables:
#   TEST_ENV_URL - Override the test environment URL
#
# Prerequisites:
#   - Test environment must be running and accessible
#   - For staging: https://staging.circlesubi.network/test-env
#   - For local: docker compose -f docker/docker-compose.test-environment.yml up -d

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Determine test environment URL
if [ -n "$TEST_ENV_URL" ]; then
    URL="$TEST_ENV_URL"
elif [ "$1" == "staging" ]; then
    URL="https://staging.circlesubi.network/test-env"
elif [ -n "$1" ]; then
    URL="$1"
else
    URL="http://localhost:5200"
fi

echo "=============================================="
echo "Circles Snapshot Integration Tests"
echo "=============================================="
echo "Test Environment: $URL"
echo ""

# Health check
echo "Checking test environment health..."
HEALTH_RESPONSE=$(curl -s -f "$URL/health" 2>/dev/null || echo '{"status":"unreachable"}')
HEALTH_STATUS=$(echo "$HEALTH_RESPONSE" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)

if [ "$HEALTH_STATUS" != "healthy" ]; then
    echo "ERROR: Test environment is not healthy or unreachable"
    echo "Response: $HEALTH_RESPONSE"
    echo ""
    echo "Make sure the test environment is running:"
    echo "  Local:   docker compose -f docker/docker-compose.test-environment.yml up -d"
    echo "  Staging: Ensure TEST_ENV_ENABLED=true in Ansible deployment"
    exit 1
fi

ACTIVE_SESSIONS=$(echo "$HEALTH_RESPONSE" | grep -o '"activeSessions":[0-9]*' | cut -d':' -f2)
echo "Status: healthy (active sessions: ${ACTIVE_SESSIONS:-0})"
echo ""

# Get current block
echo "Getting latest indexed block..."
CURRENT_BLOCK=$(curl -s "$URL/api/v1/blocks/current" | grep -o '"blockNumber":[0-9]*' | cut -d':' -f2)
echo "Latest block: $CURRENT_BLOCK"
echo ""

# Run snapshot tests
echo "Running snapshot integration tests..."
echo ""

cd "$PROJECT_ROOT"

TEST_ENV_URL="$URL" dotnet test \
    --filter "Category=Snapshot" \
    --logger "console;verbosity=normal" \
    --no-restore \
    src/Pathfinder/Circles.Pathfinder.Tests \
    src/Rpc/Circles.Rpc.Host.Tests \
    2>&1 || TEST_EXIT_CODE=$?

echo ""
echo "=============================================="
if [ "${TEST_EXIT_CODE:-0}" -eq 0 ]; then
    echo "All snapshot tests passed!"
else
    echo "Some tests failed (exit code: $TEST_EXIT_CODE)"
fi
echo "=============================================="

exit ${TEST_EXIT_CODE:-0}
