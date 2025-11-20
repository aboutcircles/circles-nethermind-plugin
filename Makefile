.PHONY: help build test docker pack push clean run-pathfinder run-rpc run-postgres test-rpc test-rpc-prod test-rpc-regression docker-up docker-down docker-logs call-rpc all release

# Default target
help:
	@echo "Circles Nethermind Plugin - Build Targets"
		@echo ""
	@echo "Build & Test:"
	@echo "  make build             Build solution"
	@echo "  make test              Run all tests"
	@echo "  make test-coverage     Run tests with coverage"
	@echo "  make clean             Clean build artifacts"
	@echo "  make clean-cache       Clear blockchain cache (DESTRUCTIVE)"
		@echo ""
	@echo "Docker:"
	@echo "  make docker            Build all Docker images"
	@echo "  make docker-index      Build Index plugin image"
	@echo "  make docker-pathfinder Build Pathfinder image"
	@echo "  make docker-rpc        Build RPC image"
	@echo "  make docker-up         Start services (Gnosis)"
	@echo "  make docker-down       Stop services"
	@echo "  make docker-logs       View logs (all services)"
	@echo "  make docker-logs SERVICE=<name>  View logs for specific service"
		@echo ""
	@echo "NuGet:"
	@echo "  make pack              Create NuGet packages"
	@echo "  make pack-clean        Clean and create packages"
	@echo "  make push              Push packages to NuGet.org"
	@echo "  make push SOURCE=<url> Push packages to custom repository"
	@echo "  make push <local-path> Push packages to local directory"
		@echo ""
	@echo "Development:"
	@echo "  make run-index         Run Nethermind with Index plugin (Gnosis)"
	@echo "  make run-pathfinder    Run Pathfinder service"
	@echo "  make run-rpc           Run RPC service"
	@echo "  make run-postgres      Run PostgreSQL database (Gnosis)"
	@echo "  make call-rpc          Call RPC interactively"
	@echo ""
	@echo "Testing:"
	@echo "  make test-rpc                    Run RPC tests (default: localhost:8081)"
	@echo "  make test-rpc URL=<url>          Run RPC tests against custom URL"
	@echo "  make test-rpc ARGS='--json'      Run RPC tests with JSON output"
	@echo "  make test-rpc-prod               Run RPC tests against production"
	@echo "  make test-rpc-prod ARGS='--json' Run production tests with JSON output"
	@echo "  make test-rpc-regression         Compare local vs production responses"
		@echo ""
	@echo "Complete Workflows:"
	@echo "  make all               Build, test, and pack"
	@echo "  make release           Build, test, pack, and push"

# Build solution
build:
	dotnet build -c Release

# Run all tests
test:
	./scripts/test.sh

# Run tests with coverage
test-coverage:
	./scripts/test.sh --coverage

# Clean build artifacts
clean:
	dotnet clean
	rm -rf src/*/bin src/*/obj
	rm -rf nupkgs/
	rm -rf TestResults/

# Clean Docker state and caches (use with caution - will delete all indexed data!)
clean-cache:
	@echo "WARNING: This will delete all cached blockchain data!"
	@echo "Press Ctrl+C to cancel, or wait 5 seconds to continue..."
	@sleep 5
	rm -rf .state/nethermind-gnosis/circles
	rm -rf .state/nethermind-chiado/circles
	@echo "Cache cleared successfully"

# Build all Docker images
docker:
	./scripts/docker-build.sh

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

# NuGet package operations
pack:
	./scripts/nuget-pack.sh

pack-clean:
	./scripts/nuget-pack.sh --clean

push:
	@TARGET=$(firstword $(MAKECMDGOALS)) && \
	if [ "$$TARGET" != "push" ]; then \
		if [[ "$$TARGET" == http* ]]; then \
			NUGET_SOURCE="$$TARGET" ./scripts/nuget-push.sh; \
		else \
			NUGET_SOURCE="$$(realpath $$TARGET)" ./scripts/nuget-push.sh; \
		fi; \
	elif [ -n "$(SOURCE)" ]; then \
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

run-pathfinder:
	./scripts/run-pathfinder.sh

run-rpc:
	./scripts/run-rpc.sh

run-postgres:
	./scripts/run-postgres.sh

call-rpc:
	./scripts/call_rpc.sh $(ARGS)

test-rpc:
	@if [ -n "$(URL)" ]; then \
		./scripts/test-rpc.sh "$(URL)" $(ARGS); \
	else \
		./scripts/test-rpc.sh $(ARGS); \
	fi

test-rpc-prod:
	./scripts/test-rpc.sh https://rpc.aboutcircles.com $(ARGS)

test-rpc-regression:
	./scripts/rpc-regression.sh

# Complete workflows
all: build test pack

release: build test pack push
	@echo "Release complete!"
