.PHONY: help build test test-unit test-snapshot test-regression test-e2e test-all docker docker-clean docker-index docker-rpc docker-pathfinder pack push clean run-pathfinder run-rpc run-postgres test-rpc test-rpc-prod test-rpc-regression test-subscriptions test-http docker-up docker-down docker-logs docker-logs-nethermind call-rpc call-http all release setup-hooks lint

# Default test environment URL for snapshot/regression/e2e tests
TEST_ENV_URL ?= https://staging.circlesubi.network/test-env

# Default target
help:
	@echo "Circles Nethermind Plugin - Build Targets"
		@echo ""
	@echo "Build & Test:"
	@echo "  make build             Build solution"
	@echo "  make test              Run unit tests (fast, no external deps)"
	@echo "  make test-coverage     Run unit tests with coverage"
	@echo "  make test-unit         Run unit tests only"
	@echo "  make test-snapshot     Run snapshot tests (default: staging)"
	@echo "  make test-snapshot TEST_ENV_URL=<url>  Run snapshot tests against custom test-env"
	@echo "  make test-regression   Run regression tests (default: staging)"
	@echo "  make test-regression TEST_ENV_URL=<url>  Run regression tests against custom test-env"
	@echo "  make test-e2e          Run E2E tests with Anvil (default: staging)"
	@echo "  make test-e2e TEST_ENV_URL=<url>  Run E2E tests against custom test-env"
	@echo "  make test-all          Run ALL tests (unit + snapshot + regression + e2e)"
	@echo "  make test-all TEST_ENV_URL=<url>  Run all tests against custom test-env"
	@echo "  make clean             Clean build artifacts"
	@echo "  make clean-cache       Clear blockchain cache (DESTRUCTIVE)"
		@echo ""
	@echo "Docker:"
	@echo "  make docker            Build all Docker images"
	@echo "  make docker-clean      Build all Docker images (no cache, pull latest)"
	@echo "  make docker-index      Build Index plugin image"
	@echo "  make docker-pathfinder Build Pathfinder image"
	@echo "  make docker-rpc        Build RPC image"
	@echo "  make docker-up         Start services (Gnosis)"
	@echo "  make docker-down       Stop services"
	@echo "  make docker-logs       View logs (all services)"
	@echo "  make docker-logs SERVICE=<name>  View logs for specific service"
	@echo "  make docker-logs-nethermind      View filtered nethermind logs (circles, sync, errors)"
	@echo "  make clean-docker      Clean Docker cache and unused images"
		@echo ""
	@echo "NuGet:"
	@echo "  make pack              Create NuGet packages"
	@echo "  make pack-clean        Clean and create packages"
	@echo "  make push              Push packages to NuGet.org"
	@echo "  make push SOURCE=<url> Push packages to custom repository"
	@echo "  make push SOURCE=<url|path> Push packages to custom repository or local directory"
		@echo ""
	@echo "Development:"
	@echo "  make run-index         Run Nethermind with Index plugin (Gnosis)"
	@echo "  make run-cache-service Run Cache Service"
	@echo "  make run-pathfinder    Run Pathfinder service"
	@echo "  make run-rpc           Run RPC service"
	@echo "  make run-postgres      Run PostgreSQL database (Gnosis)"
	@echo "  make call-rpc          Call RPC interactively"
	@echo "  make call-rpc ARGS='<args>'  Call RPC with custom arguments"
	@echo "  make call-http         Call HTTP endpoints interactively (profiles, pathfinder)"
	@echo "  make call-http ARGS='<args>' Call HTTP with custom arguments"
	@echo ""
	@echo "Testing:"
	@echo "  make test-rpc                              Run RPC tests (default: localhost:8081)"
	@echo "  make test-rpc URL=<url>                    Run RPC tests against custom URL"
	@echo "  make test-rpc ARGS='--json'                Run RPC tests with JSON output"
	@echo "  make test-rpc-prod                         Run RPC tests against production"
	@echo "  make test-rpc-prod ARGS='--json'           Run production tests with JSON output"
	@echo "  make test-rpc-regression                   Compare staging vs production (default)"
	@echo "  make test-rpc-regression ENV_A=<url> ENV_B=<url>  Compare custom environments"
	@echo "  make test-cache                            Test cache service endpoints"
	@echo "  make test-cache URL=<url>                  Test cache service against custom URL"
	@echo "  make test-cache ARGS='--performance'       Run cache tests with performance benchmarks"
	@echo "  make test-http                             Test HTTP endpoints (profiles, pathfinder)"
	@echo "  make test-http URL=<url>                   Test HTTP endpoints against custom URL"
	@echo "  make test-http ARGS='-v'                   Test HTTP endpoints with verbose output"
	@echo "  make test-subscriptions                    Test WebSocket subscriptions (default: localhost:8081)"
	@echo "  make test-subscriptions URL=<ws_url>       Test subscriptions against custom WebSocket URL"
	@echo "  make test-subscriptions ARGS='--duration 60 --filter 0x...' Test with custom options"
		@echo ""
	@echo "Setup:"
	@echo "  make setup-hooks       Install git hooks (run after cloning)"
	@echo "  make lint              Check code formatting"
	@echo "  make lint-fix          Fix code formatting issues"
		@echo ""
	@echo "Complete Workflows:"
	@echo "  make all               Build, test, and pack"
	@echo "  make release           Build, test, pack, and push"

# Build solution
build:
	dotnet build -c Release

# Run unit tests (no external dependencies)
test:
	./scripts/test.sh

# Alias for test
test-unit:
	./scripts/test.sh

# Run tests with coverage
test-coverage:
	./scripts/test.sh --coverage

# Run snapshot tests (requires TEST_ENV_URL)
test-snapshot:
	@echo "Running snapshot tests against $(TEST_ENV_URL)..."
	TEST_ENV_URL=$(TEST_ENV_URL) dotnet test --filter "Category=Snapshot" -v minimal

# Run regression tests (requires TEST_ENV_URL)
test-regression:
	@echo "Running regression tests against $(TEST_ENV_URL)..."
	TEST_ENV_URL=$(TEST_ENV_URL) dotnet test --filter "Category=Regression" -v minimal

# Run E2E tests with Anvil (requires TEST_ENV_URL)
test-e2e:
	@echo "Running E2E tests against $(TEST_ENV_URL)..."
	TEST_ENV_URL=$(TEST_ENV_URL) dotnet test --filter "Category=E2E" -v minimal

# Run ALL tests (unit + snapshot + regression + e2e)
test-all:
	@echo "Running ALL tests against $(TEST_ENV_URL)..."
	@echo ""
	@echo "=== Unit Tests ==="
	./scripts/test.sh
	@echo ""
	@echo "=== Snapshot Tests ==="
	TEST_ENV_URL=$(TEST_ENV_URL) dotnet test --filter "Category=Snapshot" -v minimal || true
	@echo ""
	@echo "=== Regression Tests ==="
	TEST_ENV_URL=$(TEST_ENV_URL) dotnet test --filter "Category=Regression" -v minimal || true
	@echo ""
	@echo "=== E2E Tests ==="
	TEST_ENV_URL=$(TEST_ENV_URL) dotnet test --filter "Category=E2E" -v minimal || true

# Clean build artifacts
clean:
	dotnet clean
	rm -rf src/*/bin src/*/obj
	rm -rf nupkgs/
	rm -rf TestResults/

# Clean Docker cache and images
clean-docker:
	docker system prune -f
	docker image prune -f

# Clean Docker state and caches (use with caution - will delete all indexed data!)
clean-cache:
	@echo "WARNING: This will delete all cached blockchain data!"
	@echo "Press Ctrl+C to cancel, or wait 5 seconds to continue..."
	@sleep 5
	rm -rf .state/nethermind-gnosis/circles
	@echo "Cache cleared successfully"

# Build all Docker images
docker:
	./scripts/docker-build.sh

# Build all Docker images (no cache)
docker-clean:
	docker compose --env-file .env -f docker/docker-compose.gnosis.yml build --no-cache --pull

# Build specific Docker images
docker-index:
	./scripts/docker-build.sh index

docker-pathfinder:
	./scripts/docker-build.sh pathfinder

docker-rpc:
	./scripts/docker-build.sh rpc

# Docker Compose operations
docker-up:
	./scripts/docker-run.sh gnosis up -d

docker-down:
	./scripts/docker-run.sh gnosis down

docker-logs:
	@if [ -n "$(SERVICE)" ]; then \
		./scripts/docker-run.sh gnosis logs -f $(SERVICE); \
	else \
		./scripts/docker-run.sh gnosis logs -f; \
	fi

# Filtered logs for nethermind (circles, sync, errors)
docker-logs-nethermind:
	@docker compose --env-file .env -f docker/docker-compose.gnosis.yml logs -f --tail 10000 nethermind-gnosis 2>&1 | \grep -E -i "circles|changing sync|receipt|ancient|barrier|header|snap|fast|warn|err"

# NuGet package operations
pack:
	./scripts/nuget-pack.sh

pack-clean:
	./scripts/nuget-pack.sh --clean

push:
	@if [ -n "$(SOURCE)" ]; then \
		if [[ "$(SOURCE)" == http* ]]; then \
			NUGET_SOURCE="$(SOURCE)" ./scripts/nuget-push.sh; \
		else \
			NUGET_SOURCE="$(realpath $(SOURCE))" ./scripts/nuget-push.sh; \
		fi; \
	else \
		./scripts/nuget-push.sh; \
	fi

push-local: push CirclesLocalFeed

# Run development services
run-index:
	./scripts/run-index.sh

run-cache-service:
	./scripts/run-cache-service.sh

run-pathfinder:
	./scripts/run-pathfinder.sh

run-rpc:
	./scripts/run-rpc.sh

run-postgres:
	./scripts/run-postgres.sh

call-rpc:
	./scripts/call_rpc.sh $(ARGS)

call-http:
	./scripts/call_http.sh $(ARGS)

test-rpc:
	@if [ -n "$(URL)" ]; then \
		./scripts/test-rpc.sh "$(URL)" $(ARGS); \
	else \
		./scripts/test-rpc.sh $(ARGS); \
	fi

test-rpc-prod:
	./scripts/test-rpc.sh https://rpc.aboutcircles.com $(ARGS)

test-rpc-regression:
	@if [ -n "$(ENV_A)" ] && [ -n "$(ENV_B)" ]; then \
		./scripts/rpc-regression.sh "$(ENV_A)" "$(ENV_B)" $(ARGS); \
	else \
		./scripts/rpc-regression.sh --label-a "Staging" --label-b "Production" $(ARGS); \
	fi

test-subscriptions:
	@if [ -n "$(URL)" ]; then \
		./scripts/test-subscriptions.sh "$(URL)" $(ARGS); \
	else \
		./scripts/test-subscriptions.sh $(ARGS); \
	fi

test-cache:
	@if [ -n "$(URL)" ]; then \
		./scripts/test-cache.sh "$(URL)" $(ARGS); \
	else \
		./scripts/test-cache.sh $(ARGS); \
	fi

test-http:
	@if [ -n "$(URL)" ]; then \
		./scripts/test-http.sh "$(URL)" $(ARGS); \
	else \
		./scripts/test-http.sh $(ARGS); \
	fi

# Git hooks
setup-hooks:
	./scripts/setup-hooks.sh

# Linting
lint:
	dotnet format --verify-no-changes

lint-fix format:
	dotnet format

# Complete workflows
all: build test pack

release: build test pack push
	@echo "Release complete!"
