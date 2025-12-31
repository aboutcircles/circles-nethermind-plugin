# Circles.Common

Core shared library for the Circles Nethermind plugin ecosystem. This library provides foundational types, interfaces, and utilities used across all Circles services (Index, Pathfinder, RPC Host, Cache).

## Key Components

### Interfaces

- **`IIndexEvent`** - Base interface for all indexed blockchain events
- **`ILogParser`** - Contract for parsing transaction receipt logs into events
- **`IDatabase`** - Database abstraction for event storage and querying
- **`IDatabaseSchema`** - Defines table structures and column mappings

### Database Abstractions

- **`DatabaseSchema`** - Base implementation for schema definitions
- **`EventSchema`** - Maps event types to database tables
- **`SchemaPropertyMap`** - Property-to-column mapping utilities
- **`InsertBuffer`** - Batched insert operations for performance
- **`RollbackCache`** - Tracks block data for chain reorganization handling

### Value Converters

- **`CirclesConverter`** - Demurrage/inflation calculations for Circles v2 tokens
- **`V1Converter`** - Inflation calculations for Circles v1 tokens
- **`AddressConverter`** - Ethereum address normalization
- **`UInt256AsStringConverter`** - Large integer serialization

### Utilities

- **`LogDataParsingHelper`** - Extracts typed data from EVM log topics/data
- **`KeccakHelper`** - Keccak-256 hashing utilities
- **`CidHelper`** - IPFS CID encoding/decoding
- **`Demurrage`** - Time-based token value calculations

### DTOs

Located in `Dto/`:
- **`FlowRequest`** - Pathfinder flow computation request
- **`MaxFlowResponse`** - Pathfinder result with transfer path
- **`TransferPathStep`** - Individual step in a transfer path

### Configuration

- **`Settings`** - Environment-based configuration (DB connections, contract addresses)

## Usage

This library is referenced by:
- All `Circles.Index.*` protocol implementations
- `Circles.Pathfinder` and `Circles.Pathfinder.Host`
- `Circles.Rpc.Host`
- `Circles.Cache.Service`
- `Circles.Metrics.Exporter`

## NuGet Package

Published as `Gnosis.Circles.Common` for internal distribution.
