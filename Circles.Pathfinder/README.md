# Pathfinder Extensions Documentation

## Overview
The Pathfinder service has been extended with new filtering capabilities and virtual sink functionality to support more complex token transfer scenarios. Here are the key changes:

## 1. Token Filtering Capabilities

### Starting Token Filter (`fromTokens`)
- **Purpose**: Restricts which tokens can be used as source tokens in the transfer path
- **Implementation**: Added in `BalanceGraph.cs`
- **Behavior**: 
  - When `fromTokens` is specified, only balances of those tokens are loaded for the source node
  - Filtering happens during graph construction in the `AddBalance` method
  - All other node balances remain unaffected
  - If `fromTokens` is not specified, all balances for the source node are loaded

### Ending Token Filter (`toTokens`)
- **Purpose**: Restricts which tokens can be accepted as final tokens in the transfer path
- **Implementation**: Added in `TrustGraph.cs`
- **Behavior**:
  - When `toTokens` is specified, only trust relationships to those tokens are loaded for the sink node
  - Filtering occurs in the `AddTrustEdge` method
  - If sink trusts a token not in `toTokens`, that trust relationship is skipped
  - All other trust relationships remain unaffected

## 2. Virtual Sink Functionality

### Purpose
Handles scenarios where source and sink nodes are the same, enabling token-to-token conversions within the same account.

### Implementation Details
- **Location**: Implemented in `TrustGraph.cs`
- **Key Components**:
  ```csharp
  private const string VIRTUAL_SINK_SUFFIX = "_virtual_sink";
  private string? _virtualSinkAddress;
  public void SetupVirtualSinkIfNeeded(string sourceAddress)
  ```

### Behavior
1. **Virtual Sink Creation**:
   - Created when source and sink addresses match
   - Virtual sink address = source address + "_virtual_sink"
   - Added as a new avatar node in the graph

2. **Trust Relationships**:
   - Virtual sink trusts those tokens trusted by source and in `toTokens`
   
3. **Path Resolution**:
   - Uses virtual sink as the effective sink during path finding
   - Final paths are transformed to replace virtual sink with actual sink address

### Edge Cases and Limitations

#### Self-Loop Prevention
- Current Implementation:
  ```csharp
  if (source == sink && edge.From == source && edge.To == source)
  {
      continue;
  }
  ```
- **Known Issue**: While this prevents self-loops, it might remove potentially valid flows that could be pushed through other paths
- **Future Enhancement**: Consider preventing balances of `toTokens` in source node when `source = sink` during graph construction

#### Token Requirements
- `toTokens` must be specified when using virtual sink
- `fromTokens` remains optional
- Without `toTokens`, conversion targets are undefined

## 3. ERC20 Token Wrapping Support

### Trust Query Enhancement
- **Purpose**: Support wrapped ERC20 tokens
- **Implementation**: Modified trust query to include ERC20 wrapper relationships
- **SQL Changes**:
  ```sql
  SELECT truster, trustee FROM "V_CrcV2_TrustRelations"
  UNION ALL
  SELECT t1.truster, t2."erc20Wrapper" AS trustee 
  FROM "V_CrcV2_TrustRelations" t1
  INNER JOIN "CrcV2_ERC20WrapperDeployed" t2
  ON t2.avatar = t1.trustee
  ```

### Balance Query Enhancement
- **Purpose**: Handle wrapped token balances
- **Implementation**: Updated balance query to consider ERC20 wrapper accounts
- **Impact**: Enables pathfinding through wrapped token paths

## 4. Performance Considerations

### Graph Construction
- Token filtering happens during graph construction
- No runtime performance impact during pathfinding
- Memory usage optimized by loading only necessary edges

### Virtual Sink Impact
- Minimal overhead from additional node
- Path transformation cost is negligible
- No significant impact on pathfinding algorithm performance

## 5. API Changes

### New Parameters
```csharp
public class FlowRequest
{
    public string? Source { get; set; }
    public string? Sink { get; set; }
    public string? TargetFlow { get; set; }
    public List<string>? FromTokens { get; set; }
    public List<string>? ToTokens { get; set; }
}
```

### Response Format
- Remains unchanged
- Virtual sink paths are automatically transformed to use actual sink address