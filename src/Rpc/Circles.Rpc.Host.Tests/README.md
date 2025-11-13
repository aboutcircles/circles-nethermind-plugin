# Circles.Rpc.Host Tests

This project contains unit and integration tests for the Circles RPC Host implementation.

## Test Structure

### Unit Tests
- **CirclesConverterTests.cs**: Tests for the CirclesConverter utility class
  - Conversion between atto-circles and circles
  - Round-trip conversion tests
  - Placeholder method tests (Phase 1 limitations)

### Integration Tests
- **CirclesRpcModuleTests.cs**: Integration tests for CirclesRpcModule
  - Requires PostgreSQL database connection
  - Tests all Phase 1 features

## Running Tests

### Running Unit Tests Only

Unit tests don't require a database connection:

```bash
dotnet test src/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj --filter "FullyQualifiedName~CirclesConverterTests"
```

### Running Integration Tests

Integration tests require a PostgreSQL database with Circles event data. Set the environment variable first:

```bash
export CIRCLES_TEST_DB_CONNECTION="Host=localhost;Database=circles_test;Username=postgres;Password=password"
dotnet test src/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj --filter "FullyQualifiedName~CirclesRpcModuleTests"
```

### Running All Tests

```bash
export CIRCLES_TEST_DB_CONNECTION="Host=localhost;Database=circles_test;Username=postgres;Password=password"
dotnet test src/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj
```

## Test Coverage

### Phase 1 Features Tested

- ✅ CirclesConverter placeholder methods (database-only)
- ✅ GetAvatarInfo / GetAvatarInfoBatch (V1 + V2 support)
- ✅ GetTokenBalances (V1, V2 ERC-1155, V2 ERC-20 wrapped)
- ✅ GetEvents with advanced filter predicates
- ✅ GetProfileCid / GetProfileCidBatch (V1 + V2 fallback)
- ✅ GetProfileByAddress / GetProfileByAddressBatch (enrichment)
- ✅ SearchProfiles
- ✅ GetTokenInfo / GetTokenInfoBatch
- ✅ GetTrustRelations
- ✅ GetCommonTrust
- ✅ GetHealth

### What's Not Tested (Future Phases)

- ⏳ Time-based inflation/demurrage calculations (Phase 3)
- ⏳ Redis caching layer (Phase 2)
- ⏳ Blockchain connector integration (Phase 3)

## Writing New Tests

### Unit Test Example

```csharp
[Test]
public void MyUnitTest()
{
    // Arrange
    var input = BigInteger.Parse("1000000000000000000");

    // Act
    var result = CirclesConverter.AttoCirclesToCircles(input);

    // Assert
    Assert.That(result, Is.EqualTo(1m));
}
```

### Integration Test Example

```csharp
[Test]
public async Task MyIntegrationTest()
{
    RequireModule();

    // Act
    var result = await _module!.GetAvatarInfo("0x...");

    // Assert
    var json = JsonSerializer.Serialize(result);
    Assert.That(json, Does.Contain("avatar"));
}
```

## Continuous Integration

Add to CI pipeline:

```yaml
- name: Run Unit Tests
  run: dotnet test src/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj --filter "FullyQualifiedName~CirclesConverterTests"

- name: Run Integration Tests
  env:
    CIRCLES_TEST_DB_CONNECTION: ${{ secrets.TEST_DB_CONNECTION }}
  run: dotnet test src/Circles.Rpc.Host.Tests/Circles.Rpc.Host.Tests.csproj --filter "FullyQualifiedName~CirclesRpcModuleTests"
```

## Test Database Setup

For integration tests, you need a PostgreSQL database with the Circles schema. See the [database setup guide](../../docs/database-setup.md) for details.

Minimum required tables for Phase 1 tests:
- CrcV1_Signup
- CrcV1_Transfer
- CrcV1_Trust
- CrcV1_RegisterName
- V_CrcV2_Avatars
- CrcV2_RegisterHuman
- CrcV2_RegisterGroup
- CrcV2_RegisterName
- CrcV2_RegisterShortName
- CrcV2_TransferSingle
- CrcV2_TransferBatch
- CrcV2_Erc20WrapperTransfer
- CrcV2_ERC20WrapperDeployed
- ipfs_files
