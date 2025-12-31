# Circles.Common.Tests

Unit tests for the `Circles.Common` library.

## Test Coverage

### CirclesConverterTests
Validates demurrage and inflation calculations:
- Round-trip conversions between static and inflationary values
- Precision guarantees for large token amounts
- Comparison with on-chain `CirclesConverter` contract

### RollbackCacheTests
Tests block reorganization handling:
- Event tracking and retrieval by block
- Cache eviction for old blocks
- Rollback data integrity

### ValueConverterTests
Tests value type conversions:
- Address normalization
- UInt256 string serialization
- Edge cases and boundary values

### LogDataParsingHelperTests
Tests EVM log parsing:
- Topic extraction
- Data field decoding
- Multi-value parsing

### NumericPrecisionTests
Validates numeric precision:
- BigInteger operations
- Fixed-point arithmetic
- Overflow handling

## Running Tests

```bash
# Run all Circles.Common tests
dotnet test src/Common/Circles.Common.Tests

# Run with coverage
dotnet test src/Common/Circles.Common.Tests --collect:"XPlat Code Coverage"
```
