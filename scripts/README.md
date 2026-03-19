# Circles Build and Development Scripts

This directory contains scripts to build, test, package, and run the Circles Nethermind Plugin project.

## Quick Start

```bash
# Build everything (Docker images, NuGet packages, and run tests)
./scripts/build-all.sh --all

# Or individually:
./scripts/docker-build.sh        # Build Docker images
./scripts/nuget-pack.sh           # Pack NuGet packages
./scripts/test.sh                 # Run tests
./scripts/nuget-push.sh           # Push packages to NuGet.org
```

## Available Scripts

### `build-all.sh` - Master Build Script

Orchestrates all build steps.

**Usage:**

```bash
./scripts/build-all.sh [options]

Options:
  --docker    Build all Docker images
  --pack      Pack NuGet packages
  --test      Run all tests
  --push      Push NuGet packages to nuget.org (requires --pack)
  --all       Run all build steps (docker, pack, test)
  --help      Show help message
```

**Examples:**

```bash
./scripts/build-all.sh --all                    # Build everything
./scripts/build-all.sh --pack --push            # Pack and push NuGet packages
./scripts/build-all.sh --docker --test          # Build Docker images and run tests
```

---

### `docker-build.sh` - Docker Image Builder

Builds Docker images for all Circles components.

**Usage:**

```bash
./scripts/docker-build.sh [image]

Images:
  index         Build Nethermind plugin (Index.Dockerfile)
  pathfinder    Build Pathfinder host (pathfinder-host.Dockerfile)
  rpc           Build RPC host (rpc-host.Dockerfile)
```

**Examples:**

```bash
./scripts/docker-build.sh              # Build all images
./scripts/docker-build.sh pathfinder   # Build only pathfinder image
./scripts/docker-build.sh index        # Build only index plugin image
```

**Output Images:**

- `circles-index:latest` - Nethermind with Circles plugin
- `circles-pathfinder-host:latest` - Pathfinder service
- `circles-rpc-host:latest` - RPC service

**Running Docker Images:**

After building, you can run the images using docker-compose:

```bash
# Start all services (Gnosis mainnet)
docker compose -f docker/docker-compose.gnosis.yml up -d

# View logs
docker compose -f docker/docker-compose.gnosis.yml logs -f

# Stop services
docker compose -f docker/docker-compose.gnosis.yml down
```

---

### `nuget-pack.sh` - NuGet Package Builder

Creates NuGet packages for distribution.

**Usage:**

```bash
./scripts/nuget-pack.sh [--clean]

Options:
  --clean     Remove previous packages before packing
```

**Environment Variables:**

- `BUILD_CONFIGURATION` - Build configuration (default: Release)

**Examples:**

```bash
./scripts/nuget-pack.sh              # Pack all packages
./scripts/nuget-pack.sh --clean      # Clean and pack
BUILD_CONFIGURATION=Debug ./scripts/nuget-pack.sh  # Pack debug builds
```

**Output:**
Packages are created in `nupkgs/` directory:

- `Gnosis.Circles.Nethermind.Plugin.<version>.nupkg` - Main plugin package
- `Gnosis.Circles.Pathfinder.<version>.nupkg` - Pathfinder library
- `*.snupkg` - Symbol packages for debugging

---

### `nuget-push.sh` - NuGet Package Publisher

Pushes NuGet packages to nuget.org.

**Usage:**

```bash
./scripts/nuget-push.sh [options]

Options:
  -y, --yes   Skip confirmation prompt
```

**Environment Variables:**

- `NUGET_API_KEY` - NuGet.org API key (required)
- `NUGET_SOURCE` - NuGet source URL (default: https://api.nuget.org/v3/index.json)

**Examples:**

```bash
# Set API key and push
export NUGET_API_KEY='your-api-key-here'
./scripts/nuget-push.sh

# Push without confirmation
NUGET_API_KEY='your-key' ./scripts/nuget-push.sh --yes

# Push to custom source
export NUGET_API_KEY='your-key'
export NUGET_SOURCE='https://custom-nuget-server.com/v3/index.json'
./scripts/nuget-push.sh
```

**Getting a NuGet API Key:**

1. Go to https://www.nuget.org/
2. Sign in with your account
3. Go to your account settings
4. Navigate to "API Keys"
5. Create a new API key with "Push" permissions
6. Copy the key and set it as `NUGET_API_KEY`

---

### `run-pathfinder.sh` - Pathfinder Development Server

Runs the Pathfinder host application locally for development.

**Usage:**

```bash
./scripts/run-pathfinder.sh [dotnet-run-args]
```

**Environment Variables:**

- `BUILD_CONFIGURATION` - Build configuration (default: Debug)
- `ASPNETCORE_ENVIRONMENT` - ASP.NET Core environment (default: Development)
- `ASPNETCORE_URLS` - Listen URLs (default: http://localhost:8080)
- `POSTGRES_CONNECTION_STRING` - PostgreSQL connection string
- `Logging__LogLevel__Default` - Log level (default: Information)

**Examples:**

```bash
# Run with defaults
./scripts/run-pathfinder.sh

# Run with custom database
export POSTGRES_READONLY_CONNECTION_STRING="Host=localhost;Port=5432;Database=postgres;Username=dev;Password=dev123"
./scripts/run-pathfinder.sh

# Run on different port
ASPNETCORE_URLS="http://localhost:8001" ./scripts/run-pathfinder.sh

# Run in release mode
BUILD_CONFIGURATION=Release ./scripts/run-pathfinder.sh
```

**Default Configuration:**

- URL: http://localhost:8080
- Database: localhost:5432/postgres (user: postgres, pass: postgres)
- Environment: Development
- Build: Debug

---

### `run-rpc.sh` - RPC Development Server

Runs the RPC host application locally for development.

**Usage:**

```bash
./scripts/run-rpc.sh
```

**Environment Variables:**

- `BUILD_CONFIGURATION` - Build configuration (default: Debug)
- `ASPNETCORE_ENVIRONMENT` - ASP.NET Core environment (default: Development)
- `ASPNETCORE_URLS` - Listen URLs (default: http://localhost:8081)
- `POSTGRES_CONNECTION_STRING` - PostgreSQL connection string
- `EXTERNAL_PATHFINDER_URL` - Pathfinder service URL (default: http://localhost:8080)
- `Logging__LogLevel__Default` - Log level (default: Information)

**Examples:**

```bash
# Run with defaults
./scripts/run-rpc.sh

# Run with custom configuration
export POSTGRES_READONLY_CONNECTION_STRING="Host=localhost;Port=5432;Database=circles_dev;Username=dev;Password=dev123"
export ExternalPathfinderUrl="http://localhost:8080"
./scripts/run-rpc.sh

# Run on different port
RPC_PORT="8002" ./scripts/run-rpc.sh
```

**Default Configuration:**

- URL: http://localhost:8081
- Database: localhost:5432/postgres (user: postgres, pass: postgres)
- Pathfinder: http://localhost:8080
- Environment: Development
- Build: Debug

---

### `test-rpc.sh` - RPC Endpoint Testing

Tests RPC endpoints by running all documented API calls against a running RPC service.

**Usage:**

```bash
./scripts/test-rpc.sh [RPC_URL] [--json]

Options:
  RPC_URL     URL of the RPC endpoint (default: http://localhost:8081)
  --json      Output JSON format for regression testing (default: pretty format)
```

**Examples:**

```bash
# Direct script usage
./scripts/test-rpc.sh                              # Test local instance
./scripts/test-rpc.sh http://localhost:8081        # Test custom URL
./scripts/test-rpc.sh https://rpc.aboutcircles.com # Test production
./scripts/test-rpc.sh https://rpc.aboutcircles.com --json # JSON output

# Via Makefile
make test-rpc                                      # Test localhost
make test-rpc URL=https://rpc.aboutcircles.com     # Test custom URL
make test-rpc ARGS='--json'                        # JSON output for localhost
make test-rpc-prod                                 # Test production (pretty output)
make test-rpc-prod ARGS='--json'                   # Test production (JSON output)
```

**Tested Methods:**

The script tests the following RPC methods:

- V1 Methods: `circles_getTotalBalance`, `circles_getTokenBalances`, `circles_getTrustRelations`
- V2 Methods: `circlesV2_getTotalBalance`, `circlesV2_findPath`
- Query Methods: `circles_query` (various table queries)
- Info Methods: `circles_health`, `circles_tables`, `circles_events`, `circles_getAvatarInfo`
- Profile Methods: `circles_getProfileByCid`, `circles_getProfileByAddress`, `circles_searchProfiles`
- Network Methods: `circles_getCommonTrust`, `circles_getNetworkSnapshot`

---

### `rpc-regression.sh` - RPC Regression Testing

Compares RPC responses between two endpoints (typically local vs production) to identify discrepancies.

**Usage:**

```bash
./scripts/rpc-regression.sh [LOCAL_URL] [REMOTE_URL]

Arguments:
  LOCAL_URL   URL of local RPC endpoint (default: http://localhost:8081)
  REMOTE_URL  URL of remote RPC endpoint (default: https://rpc.aboutcircles.com)
```

**Examples:**

```bash
# Compare local vs production
./scripts/rpc-regression.sh

# Compare custom endpoints
./scripts/rpc-regression.sh http://localhost:8081 https://staging.aboutcircles.com

# Compare two remote instances
./scripts/rpc-regression.sh https://rpc1.aboutcircles.com https://rpc2.aboutcircles.com
```

**Output:**

Results are saved to `RegressionTestResults/TIMESTAMP/`:

- `local.json` - Raw JSON responses from local endpoint
- `remote.json` - Raw JSON responses from remote endpoint
- `diff.txt` - Detailed comparison showing differences
- `summary.txt` - Summary report with statistics

**Exit Codes:**

- `0` - All tests match
- `1` - Discrepancies found or errors occurred

**Example Output:**

```
=== RPC Regression Testing ===
Local URL:  http://localhost:8081
Remote URL: https://rpc.aboutcircles.com
Results:    RegressionTestResults/20250114_143022

[1/4] Running tests against local endpoint...
✓ Local tests completed

[2/4] Running tests against remote endpoint...
✓ Remote tests completed

[3/4] Analyzing differences...
✓ Analysis completed

[4/4] Generating summary...

=== Summary ===
Total tests compared: 24
Matching responses:   22
Different responses:  2
Missing in remote:    0
Missing in local:     0

⚠ Discrepancies found!
Review detailed diff: RegressionTestResults/20250114_143022/diff.txt
```

---

### `test.sh` - Test Runner

Runs all or specific test projects.

**Usage:**

```bash
./scripts/test.sh [project] [options]

Projects:
  pathfinder    Run only Pathfinder tests
  query         Run only Query tests
  common        Run only Common tests

Options:
  --coverage            Collect code coverage
  --filter=<expression> Filter tests by expression
```

**Environment Variables:**

- `BUILD_CONFIGURATION` - Build configuration (default: Debug)
- `TEST_VERBOSITY` - Test verbosity (default: normal)

**Examples:**

```bash
# Run all tests
./scripts/test.sh

# Run specific test project
./scripts/test.sh pathfinder

# Run with coverage
./scripts/test.sh --coverage

# Run specific test
./scripts/test.sh --filter=PathfinderTests

# Run with custom verbosity
TEST_VERBOSITY=detailed ./scripts/test.sh
```

**Test Projects:**

- **Circles.Pathfinder.Tests** - Pathfinder algorithm tests
- **Circles.Index.Query.Tests** - Query system tests
- **Circles.Index.Common.Tests** - Common utilities tests

**Coverage Reports:**
When using `--coverage`, results are saved to `TestResults/` directory.

---

### `bump-index-versions.sh` - Version Bumper for Index Packages

Bumps version numbers in .csproj files for Index sub-packages.

**Usage:**

```bash
./scripts/bump-index-versions.sh
```

**Description:**

- Lists all .csproj files in `src/Index/`
- Lets you select a package
- Shows current version
- Prompts for new version
- Updates the file in-place

**When to use:**

- When releasing new versions of Index sub-packages
- Before creating NuGet packages with updated versions

---

### `call_rpc.sh` - Interactive RPC Method Caller

Interactive script to call Circles RPC methods against different endpoints.

**Usage:**

```bash
./scripts/call_rpc.sh
```

**Description:**

- Prompts for RPC endpoint (remote mainnet, testnet, or local)
- Lists available Circles RPC methods
- Prompts for method selection and parameters
- Makes the RPC call and displays results

**When to use:**

- Manual testing of RPC endpoints
- Debugging RPC method responses
- Quick verification of deployed services

---

### `diagnose-data-diff.sh` - Data Difference Diagnostic

Compares indexed data between two Circles RPC endpoints to identify missing or different data.

**Usage:**

```bash
./scripts/diagnose-data-diff.sh [staging_url] [production_url] [test_address]
```

**Examples:**

```bash
./scripts/diagnose-data-diff.sh http://135.181.238.49:8081 https://rpc.aboutcircles.com
./scripts/diagnose-data-diff.sh http://localhost:8081 https://rpc.aboutcircles.com 0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0
```

**When to use:**

- Debugging indexing issues between environments
- Verifying data consistency after deployments
- Identifying missing blocks or events

---

### `docker-run.sh` - Docker Compose Runner

Convenient wrapper for running Docker Compose configurations for different networks.

**Usage:**

```bash
./scripts/docker-run.sh <network> [command]
```

**Networks:**

- `gnosis` - Gnosis mainnet

**Commands:**

- `up` - Start services (default: detached mode)
- `down` - Stop and remove services
- `logs` - View logs (use `-f` to follow)
- `ps` - List running services
- `restart` - Restart services
- `stop` - Stop services
- `start` - Start stopped services

**Examples:**

```bash
./scripts/docker-run.sh gnosis                    # Start Gnosis mainnet
./scripts/docker-run.sh gnosis logs -f            # Follow logs
./scripts/docker-run.sh gnosis down               # Stop services
```

**When to use:**

- Running full stack in Docker for testing or production
- When you don't need to modify plugin code locally

---

### `load-test-rpc.sh` - RPC Load Testing

Load testing script for RPC endpoints with cache performance comparison.

**Usage:**

```bash
./scripts/load-test-rpc.sh [options]
```

**Options:**

- `--rpc-url URL` - RPC service URL (default: `http://localhost:8081`)
- `--cache-url URL` - Cache service URL (default: `http://localhost:3001`)
- `--duration SECONDS` - Duration of each test phase (default: 60)
- `--concurrency NUM` - Number of concurrent requests (default: 10,50,100)
- `--output-dir DIR` - Directory for results (default: LoadTestResults/TIMESTAMP)
- `--skip-cache-test` - Skip cache-enabled tests
- `--skip-nocache-test` - Skip cache-disabled tests

**Examples:**

```bash
./scripts/load-test-rpc.sh                           # Full test with defaults
./scripts/load-test-rpc.sh --duration 30             # Shorter test
./scripts/load-test-rpc.sh --concurrency 10,20,50    # Custom concurrency levels
```

**When to use:**

- Performance testing of RPC services
- Comparing cache vs non-cache performance
- Load testing before production deployment

---

### `run-cache-service.sh` - Cache Service Development Server

Runs the Circles Cache Service locally for development.

**Usage:**

```bash
./scripts/run-cache-service.sh [dotnet-run-args]
```

**Environment Variables:**

- `BUILD_CONFIGURATION` - Build configuration (default: Debug)
- `ASPNETCORE_ENVIRONMENT` - ASP.NET Core environment (default: Development)
- `ASPNETCORE_URLS` - Listen URLs (default: `http://localhost:3001`)
- `POSTGRES_CONNECTION_STRING` - PostgreSQL connection string
- `PG_NOTIFY_CHANNEL` - PostgreSQL notification channel (default: circles_index_events)
- `ROLLBACK_CAPACITY` - Rollback capacity (default: 12)
- `MAX_CATCHUP_LAG` - Max catchup lag (default: 10)
- `Logging__LogLevel__Default` - Log level (default: Information)

**Examples:**

```bash
./scripts/run-cache-service.sh
```

**Default Configuration:**

- URL: `http://localhost:3001`
- Database: localhost:5432/postgres (user: postgres, pass: postgres)
- Environment: Development
- Build: Debug

**When to use:**

- Developing or testing the cache service locally
- Running cache service alongside other local services

---

### `run-index.sh` - Index Plugin Development Server

Builds and runs Nethermind with the Circles Index plugin for local development.

**Usage:**

```bash
./scripts/run-index.sh
```

**Environment Variables:**

- `NETHERMIND_SOURCE` - Path to Nethermind source code (default: src/nethermind)

**Prerequisites:**

- Nethermind source code cloned to `src/nethermind/` or set `NETHERMIND_SOURCE`
- PostgreSQL running (via Docker or locally)
- .NET 10.0 SDK

**When to use:**

- Developing the Circles Index plugin
- Testing plugin integration with Nethermind
- When you need fastest iteration on plugin code

---

### `run-postgres.sh` - PostgreSQL Development Server

Starts PostgreSQL database using Docker Compose for different networks.

**Usage:**

```bash
./scripts/run-postgres.sh [network]
```

**Networks:**

- `gnosis` - Gnosis mainnet (default)
- `spaceneth` - Spaceneth testnet

**Examples:**

```bash
./scripts/run-postgres.sh              # Start Gnosis PostgreSQL
./scripts/run-postgres.sh spaceneth    # Start Spaceneth PostgreSQL
```

**When to use:**

- Starting database for local development
- When running services locally without full Docker Compose stack

---

### `test-cache.sh` - Cache Service Tester

Comprehensive test script for Cache Service endpoints, validation, performance, and reorg handling.

**Usage:**

```bash
./scripts/test-cache.sh [CACHE_URL] [options]
```

**Options:**

- `--json` - Output JSON format
- `--json-dir <dir>` - Directory for JSON output
- `--skip-warmup` - Skip warmup checks
- `--performance` - Run performance benchmarks

**Examples:**

```bash
./scripts/test-cache.sh                                    # Test localhost:3001
./scripts/test-cache.sh http://localhost:3001              # Test custom URL
./scripts/test-cache.sh --json                             # JSON output
./scripts/test-cache.sh --performance                      # Performance benchmarks
```

**When to use:**

- Testing cache service functionality
- Validating cache performance and correctness
- Debugging cache issues

---

### `test-subscriptions.sh` - WebSocket Subscription Tester

Tests the circles_subscribe WebSocket endpoint for real-time event notifications.

**Usage:**

```bash
./scripts/test-subscriptions.sh [WS_URL] [options]
```

**Options:**

- `--duration SECONDS` - Max test duration (default: 60)
- `--min-events N` - Minimum events to receive (default: 3)
- `--filter ADDRESS` - Filter by address (requires CIRCLES_SEED_PHRASE)
- `--json` - Output JSON format
- `--json-file FILE` - Output to JSON file

**Environment Variables:**

- `CIRCLES_SEED_PHRASE` - 12/24-word circles.garden key phrase (store it in `.env.local`). When `--filter` is provided the script automatically calls `trigger-circles-tx.sh`, which derives the first garden account from this phrase and emits a micro transaction to produce subscription events.

**Examples:**

```bash
./scripts/test-subscriptions.sh                                          # Test localhost:8081
./scripts/test-subscriptions.sh ws://localhost:8081/subscribe            # Custom URL
CIRCLES_SEED_PHRASE="..." ./test-subscriptions.sh --filter 0x...         # Filter and auto-trigger
./scripts/test-subscriptions.sh --json --json-file results.json          # JSON output
```

**When to use:**

- Testing WebSocket subscriptions
- Validating real-time event notifications
- Debugging subscription issues

---

### `trigger-circles-tx.sh` - Transaction Trigger

Triggers Circles protocol transactions for testing purposes.

**Usage:**

```bash
./scripts/trigger-circles-tx.sh
```

**Description:**

- Ensures Node.js and ethers.js are installed
- Runs the TypeScript transaction trigger script
- Uses the same circles.garden entropy → private key derivation that 5ecret-garden employs
- Can be used to generate test transactions on testnets

**Environment Variables:**

- `CIRCLES_SEED_PHRASE` (required) - Copy your circles.garden key phrase into `.env.local`. The script derives the first garden account from this phrase and signs the helper transfer with it.
- `CIRCLES_RPC_URL` (optional) - Override the JSON-RPC endpoint (defaults to `https://rpc.gnosis.gateway.fm`).
- `CIRCLES_HUB_ADDRESS` (optional) - Override the Hub V2 contract address.
- `CIRCLES_AMOUNT` / `CIRCLES_RECIPIENT` (optional) - Control transfer size and recipient for custom testing flows.

**When to use:**

- Generating test data for development
- Testing indexer with new transactions
- Validating subscription endpoints

---

## Development Workflow

### Local Development

1. **Start required services** (PostgreSQL):

   ```bash
   docker run -d --name circles-postgres \
     -e POSTGRES_DB=postgres \
     -e POSTGRES_USER=postgres \
     -e POSTGRES_PASSWORD=postgres \
     -p 5432:5432 \
     postgres:15
   ```

2. **Run Pathfinder service**:

   ```bash
   ./scripts/run-pathfinder.sh
   ```

3. **Run RPC service** (in another terminal):

   ```bash
   ./scripts/run-rpc.sh
   ```

4. **Run tests** (in another terminal):

   ```bash
   ./scripts/test.sh

   # Test RPC endpoints
   ./scripts/test-rpc.sh
   ```

5. **Compare with production** (optional):

   ```bash
   # Run regression tests
   ./scripts/rpc-regression.sh
   ```

### Building for Production

1. **Build and test**:

   ```bash
   ./scripts/build-all.sh --all
   ```

2. **Push packages to NuGet.org**:

   ```bash
   export NUGET_API_KEY='your-api-key'
   ./scripts/nuget-push.sh
   ```

3. **Deploy using Docker**:

   ```bash
   cd docker
   docker-compose -f docker-compose.gnosis.yml up -d
   ```

### CI/CD Integration

These scripts are designed to work in CI/CD pipelines:

```bash
# Example GitHub Actions workflow
- name: Build and Test
  run: ./scripts/build-all.sh --all

- name: Push NuGet Packages
  env:
    NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
  run: ./scripts/nuget-push.sh --yes
```

---

## Troubleshooting

### Docker Build Fails

**Issue:** Docker build fails with "context too large"
**Solution:** Ensure `.dockerignore` is properly configured to exclude `bin/`, `obj/`, `nupkgs/`, etc.

**Issue:** Cannot connect to Docker daemon
**Solution:** Start Docker Desktop or Docker daemon

### NuGet Push Fails

**Issue:** "API key is not set"
**Solution:** Set the `NUGET_API_KEY` environment variable

**Issue:** "Package already exists"
**Solution:** The script uses `--skip-duplicate` flag, but you may need to increment version numbers

### Development Server Issues

**Issue:** Port already in use
**Solution:** Change port using environment variables:

```bash
ASPNETCORE_URLS="http://localhost:8001" ./scripts/run-pathfinder.sh
```

**Issue:** Cannot connect to database
**Solution:** Verify PostgreSQL is running and connection string is correct:

```bash
docker ps | grep postgres
psql -h localhost -U postgres -d postgres -c "SELECT 1"
```

### Test Failures

**Issue:** Tests fail with database connection errors
**Solution:** Ensure test database is available or tests are configured to use in-memory database

**Issue:** Coverage collection fails
**Solution:** Ensure coverlet.collector package is installed in test projects

---

## Script Customization

All scripts support environment variables for customization.

### For Local Development (scripts/run-\*.sh)

Create a `.env.local` file from the example:

```bash
cp .env.example .env.local
# Edit .env.local with your settings
```

Load it before running scripts:

```bash
source .env.local
./scripts/run-pathfinder.sh
```

### For Docker Compose

Docker-compose files use `docker/.env`:

```bash
cp docker/.env.example docker/.env
# Edit docker/.env with your settings
cd docker
docker-compose -f docker-compose.gnosis.yml up -d
```

---

## Project Structure

```text
scripts/
├── build-all.sh           # Master build orchestrator
├── bump-index-versions.sh # Version bumper for Index packages
├── call_rpc.sh            # Interactive RPC method caller
├── diagnose-data-diff.sh  # Data difference diagnostic
├── docker-build.sh        # Docker image builder
├── docker-run.sh          # Docker Compose runner
├── load-test-rpc.sh       # RPC load testing
├── nuget-pack.sh          # NuGet package creator
├── nuget-push.sh          # NuGet package publisher
├── README.md              # This file
├── rpc-regression.sh      # RPC regression testing
├── run-cache-service.sh   # Cache service development server
├── run-index.sh           # Index plugin development server
├── run-pathfinder.sh      # Pathfinder development server
├── run-postgres.sh        # PostgreSQL development server
├── run-rpc.sh             # RPC development server
├── test-cache.sh          # Cache service tester
├── test-rpc.sh            # RPC endpoint tester
├── test.sh                # Test runner
├── test-subscriptions.sh  # WebSocket subscription tester
├── trigger-circles-tx.sh  # Transaction trigger
└── trigger-circles-tx.ts  # TypeScript transaction trigger
nupkgs/                    # NuGet packages output (created by nuget-pack.sh)
TestResults/               # Test coverage results (created by test.sh --coverage)
RegressionTestResults/     # Regression test results (created by rpc-regression.sh)
```

---

## Additional Resources

- [Circles Protocol Documentation](https://docs.aboutcircles.com/)
- [Nethermind Documentation](https://docs.nethermind.io/)
- [.NET 10 Documentation](https://docs.microsoft.com/en-us/dotnet/)
- [Docker Documentation](https://docs.docker.com/)
- [NuGet Documentation](https://docs.microsoft.com/en-us/nuget/)

---

## License

See the main project LICENSE file for details.
```
