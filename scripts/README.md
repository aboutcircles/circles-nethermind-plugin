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
psql -h localhost -U postgres -d circles -c "SELECT 1"
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
cp .env.local.example .env.local
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

```
scripts/
├── build-all.sh           # Master build orchestrator
├── docker-build.sh        # Docker image builder
├── docker-run.sh          # Docker Compose runner
├── nuget-pack.sh          # NuGet package creator
├── nuget-push.sh          # NuGet package publisher
├── run-index.sh           # Nethermind with Index plugin dev server
├── run-pathfinder.sh      # Pathfinder dev server
├── run-postgres.sh        # PostgreSQL dev server
├── run-rpc.sh             # RPC dev server
├── test.sh                # Test runner
├── test-rpc.sh            # RPC endpoint tester
├── rpc-regression.sh      # RPC regression testing (local vs production)
└── README.md              # This file

nupkgs/                    # NuGet packages output (created by nuget-pack.sh)
TestResults/               # Test coverage results (created by test.sh --coverage)
RegressionTestResults/       # Regression test results (created by rpc-regression.sh)
```

---

## Additional Resources

- [Circles Protocol Documentation](https://docs.circles.garden/)
- [Nethermind Documentation](https://docs.nethermind.io/)
- [.NET 9 Documentation](https://docs.microsoft.com/en-us/dotnet/)
- [Docker Documentation](https://docs.docker.com/)
- [NuGet Documentation](https://docs.microsoft.com/en-us/nuget/)

---

## License

See the main project LICENSE file for details.
