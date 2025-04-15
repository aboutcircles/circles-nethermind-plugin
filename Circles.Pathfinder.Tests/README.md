# Circles Pathfinder Test Suite

This test suite provides testing for the Circles Pathfinder service, which implements the MaxFlow algorithm to find optimal token transfer paths in the Circles trust graph.

## Test Structure

The test suite has been simplified and now consists of a single integration test approach:

- **NetworkPathfinderTests.cs**: End-to-end tests that validate the pathfinder service by:
  - Making direct API calls to the running pathfinder service
  - Testing various filter combinations (fromTokens, toTokens)
  - Testing self-conversion scenarios (source = sink)
  - Testing wrapped token handling (withWrap parameter)
  - Validating flow conservation, trust relationships, and balance sufficiency

## Test Cases

All test cases are defined in the JSON file `pathfinder-test-cases.json`. Each test case specifies:

- `name`: Unique name for the test
- `description`: Brief description of what the test validates
- `source`: Source address for the token transfer
- `sink`: Destination address for the token transfer
- `targetFlow`: Amount of tokens to transfer
- `fromTokens`: Optional array of tokens that can be used from the source
- `toTokens`: Optional array of tokens that can be accepted by the sink
- `withWrap`: Boolean flag to enable/disable wrapped token support

Example test case:
```json
{
  "name": "BasicPath-1",
  "description": "Basic path with no filters",
  "source": "0xde374ece6fa50e781e81aac78e811b33d16912c7",
  "sink": "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
  "targetFlow": "1000000000000000000000",
  "fromTokens": [],
  "toTokens": [],
  "withWrap": false
}
```

## Validation Approach

The test suite performs comprehensive validation on pathfinder responses:

1. **Flow Conservation**: Ensures the flow is balanced at each node
   - Total outflow from source equals the max flow
   - Total inflow to sink equals the max flow
   - For all intermediate nodes, inflow equals outflow

2. **Token Filter Validation**: Verifies that filter constraints are respected
   - When `fromTokens` is specified, only those tokens are used by the source
   - When `toTokens` is specified, only those tokens flow into the sink

3. **Trust Relationship Validation**: Ensures trust relationships are respected
   - Every receiver trusts the token they're receiving
   - Creates a full trust graph from database for validation

4. **Balance Validation**: Verifies addresses have sufficient token balances
   - Every sender has enough balance of the token they're sending
   - Creates a full balance graph from database for validation

5. **Virtual Sink / Self-Conversion Validation**: Tests for token-to-token conversion
   - Prevents creation of circular flows
   - Validates that tokens in `toTokens` aren't being used as source tokens

6. **Wrapped Token Validation**: Verifies wrapped token handling
   - For `withWrap=false`, no wrapped tokens should be used
   - For `withWrap=true`, only source can send wrapped tokens, and never to sink

## Setup and Requirements

### Environment Variables

The test suite requires the following environment variables:

- `PATHFINDER_URL`: URL of the pathfinder service to test (default: `http://localhost:8547`)
- `POSTGRES_READONLY_CONNECTION_STRING`: Connection string for the database (for trust and balance validation)

### Running Tests

You can run all tests or specific test cases. To avoid warnings, always specify the project path:

```bash
# Run all tests in the pathfinder tests project
dotnet test Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj

# Run a specific test by name
dotnet test Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj --filter "Name=BasicPath-1"

# Run all tests with a specific pattern in their name
dotnet test Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj --filter "Name~Path"

# Run all tests in a specific test class
dotnet test Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj --filter "FullyQualifiedName=Circles.Pathfinder.Tests.NetworkPathfinderTests"
```

## Test Output

The test output includes detailed validation information with color-coded results:

- 🟢 **GREEN**: Test passed
- 🔴 **RED**: Test failed
- 🟡 **YELLOW**: Test skipped or inconclusive

For each test, a comprehensive report is provided showing:
- Flow validation
- Intermediate node flow validation
- Token filter validation
- Trust relationship validation
- Balance validation
- Wrapped token validation

## Adding New Test Cases

To add new test cases, simply extend the `pathfinder-test-cases.json` file with additional entries. Each test case will be automatically picked up and executed by the test framework.

```json
{
  "name": "MyNewTestCase",
  "description": "Description of what my test checks",
  "source": "0xsourceaddress",
  "sink": "0xsinkaddress",
  "targetFlow": "1000000000000000000",
  "fromTokens": ["0xtoken1", "0xtoken2"],
  "toTokens": ["0xtoken3", "0xtoken4"],
  "withWrap": false
}
```

## Network Validation

The test suite can validate responses against the actual network state by loading trust and balance data from the database. This allows the tests to verify that:

1. All token transfers respect trust relationships
2. All senders have sufficient balances
3. All wrapped token constraints are enforced

This validation is optional and depends on the `POSTGRES_READONLY_CONNECTION_STRING` environment variable being set. If not available, the tests will still run but will skip these validation checks.