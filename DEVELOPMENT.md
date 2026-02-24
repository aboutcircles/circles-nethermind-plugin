# Circles Development Guide

Quick reference for building, testing, and running the Circles Nethermind Plugin.

## Two Ways to Run

1. **Full Stack with Docker Compose** (recommended for most use cases)
   - Run everything: Nethermind, PostgreSQL, Pathfinder, RPC, Consensus
   - See [Running with Docker Compose](#running-with-docker-compose)

2. **Local Development with dotnet run** (for active development)
   - Run only services you're working on locally
   - PostgreSQL via Docker, everything else via `dotnet run`
   - See [Local Development](#local-development)

## Initial Setup

After cloning the repository, install git hooks:

```bash
make setup-hooks
```

This installs pre-push hooks that run linting and build checks before pushing.

## Quick Start Scripts

All scripts are located in the [`scripts/`](scripts/) directory.

### Build Everything
```bash
./scripts/build-all.sh --all
```

### Docker Operations
```bash
# Build all Docker images
./scripts/docker-build.sh

# Build specific image
./scripts/docker-build.sh pathfinder

# Run with docker compose
./scripts/docker-run.sh gnosis          # Start Gnosis mainnet
./scripts/docker-run.sh gnosis logs -f  # Follow logs
./scripts/docker-run.sh gnosis down     # Stop services
```

### NuGet Packages
```bash
# Create packages
./scripts/nuget-pack.sh

# Publish to NuGet.org
export NUGET_API_KEY='your-api-key'
./scripts/nuget-push.sh
```

### Development Servers (Local)
```bash
# Run Nethermind with Circles Index plugin (requires Nethermind source)
./scripts/run-index.sh

# Run Pathfinder service (http://localhost:8080)
./scripts/run-pathfinder.sh

# Run RPC service (http://localhost:8081)
./scripts/run-rpc.sh
```

**Note:** `run-index.sh` requires Nethermind source in `src/nethermind/` directory (or set `NETHERMIND_SOURCE` env var). Currently only supports Gnosis network.

### Testing
```bash
# Run all tests
./scripts/test.sh

# Run specific test project
./scripts/test.sh pathfinder

# Run with coverage
./scripts/test.sh --coverage
```

## Project Structure

```
src/
├── Index/                           # Main plugin (24 projects)
│   ├── Circles.Index/              # Gnosis.Circles.Nethermind.Plugin
│   ├── Circles.Index.CirclesV1/    # V1 contract indexing
│   ├── Circles.Index.CirclesV2/    # V2 contract indexing
│   ├── Circles.Index.Common/       # Shared utilities
│   ├── Circles.Index.Query/        # Query abstractions
│   └── Circles.Index.Postgres/     # Database layer
├── Pathfinder/                      # Pathfinding (3 projects)
│   ├── Circles.Pathfinder/         # Gnosis.Circles.Pathfinder
│   ├── Circles.Pathfinder.Host/    # REST API service
│   └── Circles.Pathfinder.Tests/   # Tests
├── Rpc/                             # JSON-RPC (2 projects)
│   ├── Circles.Rpc.Host/           # RPC service
│   └── Circles.Rpc.Host.Tests/     # Tests
└── Cache/                           # Caching layer (2 projects)
    ├── Circles.Cache.Service/       # Cache service
    └── Circles.Cache.Service.Tests/ # Tests

docker/
├── Index.Dockerfile                 # Nethermind plugin
├── pathfinder-host.Dockerfile       # Pathfinder service
├── rpc-host.Dockerfile              # RPC service
├── cache-service.Dockerfile         # Cache service
├── metrics-exporter.Dockerfile      # Metrics exporter
├── backfill.Dockerfile              # Backfill job
├── trust-missing-avatars.Dockerfile # Trust repair job
└── docker-compose.*.yml             # Compose configurations

scripts/
├── build-all.sh                     # Master build script
├── docker-build.sh                  # Build Docker images
├── docker-run.sh                    # Run Docker Compose
├── nuget-pack.sh                    # Create NuGet packages
├── nuget-push.sh                    # Publish packages
├── run-index.sh                     # Run Index plugin (Nethermind)
├── run-pathfinder.sh                # Run Pathfinder locally
├── run-rpc.sh                       # Run RPC locally
└── test.sh                          # Run tests
```

## NuGet Packages

### Published Packages

1. **Gnosis.Circles.Nethermind.Plugin** (v0.0.6)
   - Main Nethermind plugin for Circles indexing
   - Includes all sub-packages embedded

2. **Gnosis.Circles.Pathfinder** (v0.0.1)
   - Standalone pathfinding library
   - Uses Google OR-Tools for optimization

### Building Packages Locally

```bash
./scripts/nuget-pack.sh
# Output: nupkgs/Gnosis.Circles.*.nupkg
```

### Installing Packages

```bash
dotnet add package Gnosis.Circles.Nethermind.Plugin
dotnet add package Gnosis.Circles.Pathfinder
```

## Docker Images

### Available Images

After building with `./scripts/docker-build.sh`:

- **circles-index:latest** - Nethermind with Circles plugin
- **circles-pathfinder-host:latest** - Pathfinder service
- **circles-rpc-host:latest** - RPC service
- **circles-cache-service:latest** - Cache service

### Running with Docker Compose

```bash
# Gnosis mainnet
docker compose -f docker/docker-compose.gnosis.yml up -d
```

Or use the convenience script:
```bash
./scripts/docker-run.sh gnosis          # Start
./scripts/docker-run.sh gnosis logs -f  # View logs
./scripts/docker-run.sh gnosis down     # Stop
```

### Running Specific Services

To run only specific services:

```bash
# Run only PostgreSQL
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis

# Run only Pathfinder (requires postgres)
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis pathfinder

# Run only RPC (requires postgres)
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis rpc
```

## Local Development

This section is for developing services locally with `dotnet run` (not Docker).

**For running the full stack with Docker, use docker-compose instead** - see [Running with Docker Compose](#running-with-docker-compose).

### Prerequisites

- .NET 10.0 SDK
- PostgreSQL 15+ (database only)
- Docker (optional, for PostgreSQL container)

### Setup Database

For local development, you need PostgreSQL running:

```bash
# Option 1: Use docker compose (PostgreSQL only)
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis

# Option 2: Install PostgreSQL locally
# brew install postgresql@15  # macOS
# sudo apt install postgresql-15  # Ubuntu
# Then create database:
createdb circles
```

### Developing the Circles.Index Plugin

The Circles.Index plugin runs inside Nethermind. There are two approaches:

#### Option 1: Local Development with dotnet (Recommended for Plugin Development)

The fastest way to iterate on plugin code:

```bash
# Start PostgreSQL (required)
docker compose -f docker/docker-compose.gnosis.yml up -d postgres-gnosis

# Build and run Nethermind with the Circles plugin (Gnosis network)
./scripts/run-index.sh

# Optional: Set custom Nethermind source location
NETHERMIND_SOURCE=~/path/to/nethermind ./scripts/run-index.sh
```

This script will:

1. Build Nethermind from source (if not already built)
2. Publish the Circles.Index plugin to Nethermind's plugins directory
3. Start Nethermind with the plugin loaded
4. Connect to local PostgreSQL

**Prerequisites:**

- Nethermind source code in `src/nethermind/` directory (clone from <https://github.com/NethermindEth/nethermind>):
    ```bash
    git clone https://github.com/NethermindEth/nethermind.git src/nethermind
    ```
- PostgreSQL running (via Docker or locally)
- ~700GB+ free disk space for blockchain data
- .NET 10.0 SDK

**Advantages:**

- Fast rebuild cycle (only rebuilds the plugin, not Nethermind)
- Easy debugging with IDE
- Direct access to Nethermind logs

#### Option 2: Docker Compose (For Full Stack Testing)

Use Docker Compose to run the complete stack:

```bash
# Gnosis mainnet
docker compose -f docker/docker-compose.gnosis.yml up -d

# Or use the convenience script
./scripts/docker-run.sh gnosis
```

This includes:

- Nethermind with the plugin loaded
- PostgreSQL database
- Consensus layer (Lighthouse)
- Full blockchain sync

**Best for:**

- Testing the complete system
- Production-like environment
- When you don't need to modify plugin code frequently

### Running Pathfinder and RPC Services

Start services in separate terminals:

**Terminal 1 - Pathfinder:**
```bash
./scripts/run-pathfinder.sh
# Listening on http://localhost:8080
```

**Terminal 2 - RPC:**
```bash
./scripts/run-rpc.sh
# Listening on http://localhost:8081
```

**Terminal 3 - Tests:**
```bash
./scripts/test.sh --coverage
```

### Environment Configuration

#### For Local Development (dotnet run)

Copy and customize environment variables for local development:
```bash
cp .env.example .env.local
# Edit .env.local with your settings
source .env.local
./scripts/run-pathfinder.sh
```

#### For Docker Compose

Docker Compose loads `.env` from the repository root:
```bash
cp .env.example .env
# Edit .env with your settings
docker compose -f docker/docker-compose.gnosis.yml up -d
```

#### Reindexing

The Circles.Index plugin supports reindexing through the `REINDEX_FROM_BLOCK` environment variable.

**Environment Variable:**

| Variable | Description | Example |
|----------|-------------|---------|
| `REINDEX_FROM_BLOCK` | Reindex ALL tables from specified block number | `12000000` |

##### Usage

Reindex everything from a specific block:
```yaml
environment:
  - REINDEX_FROM_BLOCK=12000000
```

This will:

1. Delete data from ALL tables from block 12,000,000 onwards
2. Reinitialize caches from the remaining data
3. Resync all blocks from 12,000,000 to current head

##### Important Notes

1. **Remove after reindex**: This is a one-time operation. Remove the env var after reindexing completes to avoid re-deleting data on next restart.
2. **Full reindex only**: Partial table reindexing is not supported because it creates data inconsistency between tables and caches. All tables must be at the same block height.
3. **Backup first**: Consider backing up your database before large reindex operations.
4. **Time estimate**: A full reindex from genesis takes approximately 4-5 hours.
5. **Verify completion**: After reindexing, check with `SELECT MAX("blockNumber") FROM "System_Block"` to verify sync progress.

### Test Projects

- **Circles.Pathfinder.Tests** - Pathfinding algorithm tests
- **Circles.Index.Query.Tests** - Query system tests
- **Circles.Index.CirclesV2.Tests** - V2 contract indexing tests
- **Circles.Common.Tests** - Common utilities tests
- **Circles.Rpc.Host.Tests** - RPC endpoint tests
- **Circles.Cache.Service.Tests** - Cache service tests

### Running Tests

```bash
# All tests
./scripts/test.sh

# Specific project
./scripts/test.sh pathfinder

# With coverage
./scripts/test.sh --coverage

# Specific test
./scripts/test.sh --filter=PathfinderTests

# Custom verbosity
TEST_VERBOSITY=detailed ./scripts/test.sh
```

### Test Configuration

Environment variables:
- `BUILD_CONFIGURATION` - Debug or Release (default: Debug)
- `TEST_VERBOSITY` - quiet, minimal, normal, detailed (default: normal)

## CI/CD

### GitHub Actions Example

```yaml
name: Build and Test

on: [push, pull_request]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Build and Test
        run: ./scripts/build-all.sh --all

      - name: Publish Packages
        if: github.ref == 'refs/heads/main'
        env:
          NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
        run: ./scripts/nuget-push.sh --yes
```

## Common Tasks

### Update Package Versions

Edit version in project files:
- [src/Index/Circles.Index/Circles.Index.csproj](src/Index/Circles.Index/Circles.Index.csproj)
- [src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj](src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj)

Then rebuild:
```bash
./scripts/nuget-pack.sh --clean
```

### Debug in Development

```bash
# Run with debug logging
export Logging__LogLevel__Default=Debug
./scripts/run-pathfinder.sh

# Or use IDE (VS Code, Rider, Visual Studio)
# Open solution: Circles.sln
# Set startup project: Circles.Pathfinder.Host or Circles.Rpc.Host
```

### Clean Build

```bash
# Clean .NET artifacts
dotnet clean
rm -rf src/*/bin src/*/obj

# Clean Docker
docker system prune -a

# Clean packages
rm -rf nupkgs/
```

## Troubleshooting

### Port Already in Use

```bash
# Change port
ASPNETCORE_URLS="http://localhost:8001" ./scripts/run-pathfinder.sh
```

### Database Connection Issues

```bash
# Check PostgreSQL is running
docker ps | grep postgres

# Test connection
psql -h localhost -U postgres -d postgres -c "SELECT 1"
```

### Docker Build Fails

```bash
# Check Docker is running
docker info

# Clean Docker cache
docker builder prune
```

### NuGet Push Fails

```bash
# Verify API key
echo $NUGET_API_KEY

# Check package exists
ls -la nupkgs/

# Use --skip-duplicate flag (already in script)
```

## Additional Resources

- **Full Script Documentation**: [scripts/README.md](scripts/README.md)
- **Circles Protocol**: https://docs.circles.garden/
- **Nethermind**: https://docs.nethermind.io/
- **.NET 10**: https://docs.microsoft.com/en-us/dotnet/

## Getting Help

- Check [scripts/README.md](scripts/README.md) for detailed script documentation
- Review individual script help: `./scripts/build-all.sh --help`
- Open an issue on GitHub for bugs or feature requests
