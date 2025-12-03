#!/usr/bin/env bash
set -euo pipefail

# Script to run the Cache Service locally for development
# Usage: ./scripts/run-cache-service.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_PATH="$PROJECT_ROOT/src/Cache/Circles.Cache.Service/Circles.Cache.Service.csproj"

# Configuration defaults (can be overridden by environment)
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:3001}"

# Database configuration
POSTGRES_CONNECTION_STRING="${POSTGRES_CONNECTION_STRING:-Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres}"
POSTGRES_READONLY_CONNECTION_STRING="${POSTGRES_READONLY_CONNECTION_STRING:-$POSTGRES_CONNECTION_STRING}"

# Cache Service configuration
PG_NOTIFY_CHANNEL="${PG_NOTIFY_CHANNEL:-circles_index_events}"
ROLLBACK_CAPACITY="${ROLLBACK_CAPACITY:-12}"
MAX_CATCHUP_LAG="${MAX_CATCHUP_LAG:-10}"
CACHE_SERVICE_PORT="${CACHE_SERVICE_PORT:-3001}"

# Logging
Logging__LogLevel__Default="${Logging__LogLevel__Default:-Information}"

echo "=== Running Circles Cache Service ==="
echo "Configuration: $BUILD_CONFIGURATION"
echo "Environment: $ASPNETCORE_ENVIRONMENT"
echo "URL: $ASPNETCORE_URLS"
echo "Database: ${POSTGRES_CONNECTION_STRING%%Password=*}Password=***"
echo "PG Notify Channel: $PG_NOTIFY_CHANNEL"
echo "======================================"

cd "$PROJECT_ROOT"

export ASPNETCORE_ENVIRONMENT
export ASPNETCORE_URLS
export POSTGRES_CONNECTION_STRING
export POSTGRES_READONLY_CONNECTION_STRING
export PG_NOTIFY_CHANNEL
export ROLLBACK_CAPACITY
export MAX_CATCHUP_LAG
export CACHE_SERVICE_PORT
export Logging__LogLevel__Default

dotnet run --project "$PROJECT_PATH" --configuration "$BUILD_CONFIGURATION" -- "$@"
