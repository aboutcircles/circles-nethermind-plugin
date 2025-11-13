# Circles RPC Host

This implementation provides a comprehensive JSON-RPC 2.0 service for the Circles protocol, exposing all public methods from the `CirclesRpcModule` as HTTP endpoints.

## Implemented RPC Methods

### Balance & Token Methods

- **`circlesV2_getTotalBalance`** - Get total balance for an address
- **`circles_getTokenBalances`** - Get token balances for an address
- **`circles_getTokenInfo`** - Get information about a specific token
- **`circles_getTokenInfoBatch`** - Get information about multiple tokens

### Avatar & Profile Methods

- **`circles_getAvatarInfo`** - Get avatar information for an address
- **`circles_getAvatarInfoBatch`** - Get avatar information for multiple addresses
- **`circles_getProfileCid`** - Get profile CID for an address
- **`circles_getProfileCidBatch`** - Get profile information for multiple CIDs
- **`circles_getProfileByAddress`** - Get profile by address
- **`circles_getProfileByAddressBatch`** - Get profiles for multiple addresses
- **`circles_searchProfiles`** - Search profiles by text

### Trust & Network Methods

- **`circles_getTrustRelations`** - Get trust relations for an address
- **`circles_getCommonTrust`** - Get common trust between two addresses
- **`circles_getNetworkSnapshot`** - Get current network snapshot
- **`circlesV2_findPath`** - Find payment path between addresses

### System & Query Methods

- **`circles_events`** - Query events with filtering
- **`circles_health`** - Health check endpoint
- **`circles_tables`** - Get database table information
- **`circles_query`** - Generic query interface

## Architecture

### Key Features

1. **Comprehensive Parameter Validation** - All endpoints validate input parameters
2. **Error Handling** - Consistent error responses following JSON-RPC 2.0 specification
3. **Type Safety** - Strong typing for all parameters and return values
4. **Async/Await Pattern** - All handlers are properly asynchronous
5. **Consistent API** - All endpoints follow the same request/response pattern

### Request Format

All RPC requests should be JSON-RPC 2.0 compliant:

```json
{
  "jsonrpc": "2.0",
  "method": "circles_getTotalBalance",
  "params": ["0x1234567890abcdef"],
  "id": 1
}
```

### Response Format

Successful responses:

```json
{
  "jsonrpc": "2.0",
  "result": {...},
  "id": 1
}
```

Error responses:

```json
{
  "jsonrpc": "2.0",
  "error": {
    "code": -32602,
    "message": "Invalid params: Missing 'address' parameter."
  },
  "id": 1
}
```

## Usage

The service is built as a standard ASP.NET Core application. Once started, it will:

1. Listen on the configured port (default: 5000 for HTTP, 5001 for HTTPS)
2. Accept JSON-RPC 2.0 requests at the root endpoint `/`
3. Return proper JSON-RPC 2.0 responses
4. Provide health check endpoints at `/live` and `/ready`
5. Expose metrics at `/metrics` (if Prometheus is enabled)

## Configuration

The service is configured through the `Settings` class, which includes:

- Database connection strings
- RPC endpoints
- Performance settings
- Logging configuration

## Development

The implementation compiles successfully with .NET 9.0 and includes comprehensive warning handling for nullability and async patterns.
