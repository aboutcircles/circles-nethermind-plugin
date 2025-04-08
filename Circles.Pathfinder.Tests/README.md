# Circles Pathfinder Test Suite

This test suite provides comprehensive testing for the Circles Pathfinder service, which implements the MaxFlow algorithm to find optimal token transfer paths in the Circles trust graph.

## Test Structure

The test suite is organized as follows:

- **Unit Tests**: Test individual components of the system
  - `BalanceGraphTests.cs`: Tests for balance graph construction and filtering
  - `TrustGraphTests.cs`: Tests for trust graph construction and filtering
  - `CapacityGraphTests.cs`: Tests for capacity graph creation
  - `FlowGraphTests.cs`: Tests for flow graph creation and max flow computation
  - `PathfinderCollapseTests.cs`: Tests for the path collapsing functionality

- **Integration Tests**:
  - `PathfinderIntegrationTests.cs`: End-to-end tests of the pathfinding process

- **Path Verification Tests**:
  - `PathVerificationTests.cs`: Tests that verify exact path results
  - `PathVerificationRunner.cs`: Runner for path verification tests
  - `JsonRpcTestExtractor.cs`: Utility for extracting tests from JSON RPC calls
  - `PathTestGenerator.cs`: Utility for generating path test cases

- **Snapshot Tests**:
  - `SnapshotTests.cs`: Tests using recorded network states for regression testing

## Setup and Requirements

### .NET Requirements

The project targets .NET 9.0, so you'll need the appropriate SDK:

```bash
# Install .NET 9.0 preview using the installation script
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 9.0 --quality preview --install-dir ~/.dotnet

# Add .NET to your PATH
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet:$HOME/.dotnet/tools

# Add these lines to your shell configuration file (~/.bashrc or ~/.zshrc)
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
echo 'export PATH=$PATH:$HOME/.dotnet:$HOME/.dotnet/tools' >> ~/.bashrc
source ~/.bashrc
```

### Project Structure

The test project uses a special testing infrastructure to isolate tests from the database:

- `TestUtils.cs`: Utility methods for creating and validating test data
- `MockLoadGraph.cs`: A mock implementation of ILoadGraph for testing
- `TestGraphFactory.cs`: Test-specific version of GraphFactory that works with MockLoadGraph
- `PathfinderTestFixture.cs`: Test fixture for setting up graph data

## Test Data

The test suite can work with:

1. **In-memory test networks**: Generated on-the-fly for testing
2. **CSV snapshots**: Saved network states for regression testing
3. **JSON test cases**: Path verification test cases with expected results

### Test Data Storage

Test data is stored in the `TestData` directory, which is automatically created by the test framework. The following files are used:

- `sample_balances.csv`: Sample balance data for basic tests
- `sample_trust.csv`: Sample trust relation data for basic tests
- `snapshot_balances.csv`: Saved balance data for snapshot tests
- `snapshot_trust.csv`: Saved trust relation data for snapshot tests
- `regression_balances.csv`: Balance data for regression tests
- `regression_trust.csv`: Trust relation data for regression tests
- `path_test_*.json`: Path verification test case definitions
- `balances_*.csv`: Network snapshots for path verification tests
- `trust_*.csv`: Trust snapshots for path verification tests

The TestData directory is located in the build output directory of the test project:

```
/path/to/project/Circles.Pathfinder.Tests/bin/Debug/net9.0/TestData/
```

## Running Tests

You can run all tests or specific test categories:

```bash
# Run all Pathfinder tests
dotnet test Circles.Pathfinder.Tests/Circles.Pathfinder.Tests.csproj

# Run only unit tests
dotnet test --filter "Category=UnitTest"

# Run only integration tests
dotnet test --filter "Category=Integration"

# Run only path verification tests
dotnet test --filter "FullyQualifiedName~PathVerification"

# Run only snapshot tests
dotnet test --filter "Category=SnapshotTests"

# Run specific test class
dotnet test --filter "ClassName=Circles.Pathfinder.Tests.PathVerificationRunner"

# Run specific test method
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.CreateExampleTestCase"

# Combine filters
dotnet test --filter "ClassName=Circles.Pathfinder.Tests.BalanceGraphTests|ClassName=Circles.Pathfinder.Tests.TrustGraphTests"
```

## Path Verification Tests

The path verification tests ensure that the pathfinder produces correct and consistent results for specific test cases. This is particularly important for ensuring that algorithmic changes don't cause regressions in the quality of path solutions.

### Creating Path Verification Test Cases

#### From JSON RPC Examples

If you have real-world JSON RPC request/response pairs, you can create test cases from them:

1. Open `PathVerificationRunner.cs`
2. Modify the `CreateExampleTestCase` method with your JSON RPC example
3. Run the test to create the test case file:

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.CreateExampleTestCase"
```

Example code:

```csharp
[Test]
public void CreateExampleTestCase()
{
    string jsonRpcRequest = @"{
        ""jsonrpc"":""2.0"",
        ""id"":0,
        ""method"":""circlesV2_findPath"",
        ""params"":[{
            ""Source"":""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
            ""Sink"":""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
            ""TargetFlow"":""1000000000000000000000"",
            ""WithWrap"":false
        }]
    }";

    string jsonRpcResponse = @"{
        ""jsonrpc"": ""2.0"",
        ""result"": {
            ""maxFlow"": ""1000000000000000000000"",
            ""transfers"": [
                {
                    ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                    ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""tokenOwner"": ""0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"",
                    ""value"": ""83211305000000000000""
                },
                {
                    ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                    ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""tokenOwner"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""value"": ""431913977000000000000""
                },
                {
                    ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                    ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""tokenOwner"": ""0x4a9affa9249f36fd0629f342c182a4e94a13c2e0"",
                    ""value"": ""484874718000000000000""
                }
            ]
        },
        ""id"": 0
    }";

    PathTestGenerator.CreateFromJsonRpcExample("Example JSON RPC Test", jsonRpcRequest, jsonRpcResponse);
}
```

#### From Manual Parameters

You can also create test cases by specifying the parameters and expected results directly:

1. Open `PathVerificationRunner.cs`
2. Modify the `CreateTestCaseFromParams` method with your test parameters
3. Run the test to create the test case file:

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.CreateTestCaseFromParams"
```

Example code:

```csharp
[Test]
public void CreateTestCaseFromParams()
{
    var source = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
    var sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e";
    var targetFlow = "1000000000000000000000";
    
    var expectedTransfers = new List<TransferPathStep>
    {
        new TransferPathStep
        {
            From = source,
            To = sink,
            TokenOwner = "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
            Value = "83211305000000000000"
        },
        new TransferPathStep
        {
            From = source,
            To = sink,
            TokenOwner = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
            Value = "431913977000000000000"
        },
        new TransferPathStep
        {
            From = source,
            To = sink,
            TokenOwner = "0x4a9affa9249f36fd0629f342c182a4e94a13c2e0",
            Value = "484874718000000000000"
        }
    };
    
    PathTestGenerator.CreateFromInputs(
        "Manual Test Case",
        source,
        sink,
        targetFlow,
        null,  // fromTokens
        null,  // toTokens
        false, // withWrap
        expectedTransfers,
        targetFlow // expectedMaxFlow
    );
}
```

#### Creating Network Snapshots

When you create a test case, the current network state is automatically saved as CSV files. The files are named:

- `balances_<test_name>.csv`
- `trust_<test_name>.csv`

To create a network snapshot for a specific test case:

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationTests.CreateNetworkSnapshotForTestCase"
```

This will save both the test case definition and the network snapshot files.

### Running Path Verification Tests

#### Running All Tests

To run all path verification tests:

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.RunAllVerificationTests"
```

This will run all test cases found in the `TestData` directory.

#### Running a Specific Test

To run a specific test with hardcoded values:

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.VerifyPathFromExample"
```

You can modify the `VerifyPathFromExample` method in `PathVerificationRunner.cs` to test specific parameters.

## Step-by-Step Example

Here's a complete example of how to create and run a path verification test:

### 1. Create a test case from a real-world example

Extract the request and response from your logs or API calls:

```
Request: {"jsonrpc":"2.0","id":0,"method":"circlesV2_findPath","params":[{"Source":"0xde374ece6fa50e781e81aac78e811b33d16912c7","Sink":"0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e","TargetFlow":"1000000000000000000000","WithWrap":false}]}

Response: {"jsonrpc":"2.0","result":{"maxFlow":"1000000000000000000000","transfers":[{"from":"0xde374ece6fa50e781e81aac78e811b33d16912c7","to":"0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e","tokenOwner":"0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c","value":"83211305000000000000"},{"from":"0xde374ece6fa50e781e81aac78e811b33d16912c7","to":"0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e","tokenOwner":"0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e","value":"431913977000000000000"},{"from":"0xde374ece6fa50e781e81aac78e811b33d16912c7","to":"0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e","tokenOwner":"0x4a9affa9249f36fd0629f342c182a4e94a13c2e0","value":"484874718000000000000"}]},"id":0}
```

### 2. Update the `CreateExampleTestCase` method

Open `PathVerificationRunner.cs` and update the `CreateExampleTestCase` method with your JSON RPC example:

```csharp
[Test]
public void CreateExampleTestCase()
{
    string jsonRpcRequest = @"{
        ""jsonrpc"":""2.0"",
        ""id"":0,
        ""method"":""circlesV2_findPath"",
        ""params"":[{
            ""Source"":""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
            ""Sink"":""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
            ""TargetFlow"":""1000000000000000000000"",
            ""WithWrap"":false
        }]
    }";

    string jsonRpcResponse = @"{
        ""jsonrpc"": ""2.0"",
        ""result"": {
            ""maxFlow"": ""1000000000000000000000"",
            ""transfers"": [
                {
                    ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                    ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""tokenOwner"": ""0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"",
                    ""value"": ""83211305000000000000""
                },
                {
                    ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                    ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""tokenOwner"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""value"": ""431913977000000000000""
                },
                {
                    ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                    ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                    ""tokenOwner"": ""0x4a9affa9249f36fd0629f342c182a4e94a13c2e0"",
                    ""value"": ""484874718000000000000""
                }
            ]
        },
        ""id"": 0
    }";

    PathTestGenerator.CreateFromJsonRpcExample("Example JSON RPC Test", jsonRpcRequest, jsonRpcResponse);
}
```

### 3. Run the test to create the test case

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.CreateExampleTestCase"
```

This will create:
- A test case file: `TestData/path_test_Example_JSON_RPC_Test.json`
- Network snapshot files:
  - `TestData/balances_Example_JSON_RPC_Test.csv`
  - `TestData/trust_Example_JSON_RPC_Test.csv`

### 4. Run all verification tests

```bash
dotnet test --filter "FullyQualifiedName=Circles.Pathfinder.Tests.PathVerificationRunner.RunAllVerificationTests"
```

This will run all test cases found in the `TestData` directory, including the one you just created.

### 5. Verify the results

The test output will show whether the pathfinder produced the expected results for each test case.

## Test Infrastructure

### The Mock ILoadGraph Pattern

Since the project's `GraphFactory` methods expect a concrete `LoadGraph` type rather than the interface, we've implemented a workaround:

1. Created a `TestGraphFactory` class that replicates the crucial functionality but accepts `ILoadGraph`
2. Modified the `PathfinderTestFixture` to use this test-specific factory
3. Our tests use the fixture to get properly initialized graphs with test data

```csharp
// MockLoadGraph.cs
public class MockLoadGraph : ILoadGraph
{
    private readonly IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> _balances;
    private readonly IEnumerable<(string Truster, string Trustee, int Limit)> _trustRelations;

    public MockLoadGraph(
        IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> balances,
        IEnumerable<(string Truster, string Trustee, int Limit)> trustRelations)
    {
        _balances = balances;
        _trustRelations = trustRelations;
    }

    public IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        return _balances;
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        return _trustRelations;
    }
}
```

## Troubleshooting

### Binary File Errors

If you see errors like:
```
error CS2015: '._filename.cs' is a binary file instead of a text file
```

These are macOS metadata files that can be removed with:
```bash
find ~/repos/circles-nethermind-plugin/Circles.Pathfinder -name "._*" -delete
```

### Network Snapshot Missing

If a test fails with "No specific network snapshot found", you need to create the network snapshot for the test case:

1. Run `CreateNetworkSnapshotForTestCase` with the specific parameters
2. Run the verification test again

### JSON Serialization Errors

If you see errors related to JSON serialization, make sure your JSON RPC examples are properly formatted:

1. Check for missing commas, quotes, or braces
2. Make sure all string values are properly quoted
3. Ensure numeric values don't have leading zeros

### Path Format

If the pathfinder produces a different path than expected, it might be due to:

1. Different network data
2. Changes in the pathfinder algorithm
3. Non-deterministic behavior in the algorithm

In these cases, you can:
1. Update the expected result to match the new behavior
2. Add assertions that verify important properties rather than exact paths
3. Create multiple acceptable expected results

## Tips for Effective Testing

1. **Use real-world examples**: Always base your tests on real examples from production to catch real issues.

2. **Create multiple tests**: Test different scenarios (e.g., with different `FromTokens`, `ToTokens`, `WithWrap` settings).

3. **Verify edge cases**: Test source equals sink, disconnected nodes, very small/large amounts.

4. **Keep network snapshots**: Always make sure to save the network state that produced a test case.

5. **Update tests when code changes**: If you make changes to the pathfinder algorithm, update the expected results.

6. **Automate test case creation**: If you find issues in production, automatically create test cases from them.

## Future Improvements

For long-term maintainability, consider:

1. Modifying the original GraphFactory to accept ILoadGraph
2. Adding proper dependency injection to the Pathfinder service
3. Creating more targeted tests for complex edge cases
4. Adding more real-world test scenarios with known good results
5. Implementing fuzzing tests to find edge cases automatically
6. Adding property-based testing for algorithm invariants