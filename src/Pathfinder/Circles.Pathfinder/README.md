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
The virtual sink addresses scenarios where the source and sink nodes are the same address. This is essential for enabling token-to-token conversions within the same account. By creating a virtual sink in the graph, we are able to take advantage of the full implementation of the MaxFlow algorithm

### Implementation Details
- **Location**: Primarily implemented in `TrustGraph.cs`
- **Key Components**:
  ```csharp
  private const string VIRTUAL_SINK_SUFFIX = "_virtual_sink";
  private string? _virtualSinkAddress;
  private string? _sourceAddress;
  
  public void SetupVirtualSinkIfNeeded(string sourceAddress)
  public string? GetVirtualSinkAddress()
  private bool HasTrustEdge(string truster, string trustee)
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

#### Self-Loop Prevention
- Current Implementation:
  ```csharp
  // Case 1: When source and sink are the same, don't add balances for tokens that are in toTokens
  if (_sourceAddress != null && _sinkAddress != null && 
      _sourceAddress == _sinkAddress && address == _sourceAddress && 
      _toTokens.Any() && _toTokens.Contains(token))
  {
      return; 
  }
  ```
- This approach prevents self-loops by filtering out token balances for tokens in `toTokens`
- The rationale is that if the user already has Token B and wants to convert Token A to Token B, we shouldn't consider the existing Token B balance as a potential source for the conversion

#### Token Requirements
- `toTokens` must be specified when using virtual sink (critical requirement)
- Without destination tokens specified, the system cannot determine valid conversion targets
- `fromTokens` remains optional but helps optimize the process by limiting the search space


## 3. ERC20 Token Wrapping Support

### Balance/Trust Query Enhancement
- **Purpose**: Support wrapped CRC20 tokens
- **Implementation**: Added in `BalanceGraph.cs`
- **Behavior**: All accounts will now support both ERC1155 and CRC20 tokens. CRC20 tokens will retain their addresses and remain distinct from ERC1155 tokens. They will be introduced into the trust graph as nodes trusted only by the addresses that trust the avatar that deployed the wrap token. This setup enables the construction of a capacity graph, allowing the flow of CRC20 tokens from accounts holding them to accounts that trust the corresponding avatar.
```csharp
 // Case 3: Filter wrapped tokens, only isWrapped tokens for source address are kept
if ( isWrapped && (_withWrap == false || (_withWrap == true && address != _sourceAddress)) )
{
    return; 
}
```
`BalanceNode` will now have a new property IsWrapped


## 4. API Changes

### New Parameters
```csharp
public class FlowRequest
{
    public string? Source { get; set; }
    public string? Sink { get; set; }
    public string? TargetFlow { get; set; }
    public List<string>? FromTokens { get; set; }
    public List<string>? ToTokens { get; set; }
    public bool? WithWrap { get; set; }
}
```

### Response Format
- Remains unchanged
- Virtual sink paths are automatically transformed to use actual sink address


## 5. Group Minting Along a Path (Router Integration)

### Overview

The pathfinder supports minting group tokens as part of a transitive transfer path. This is implemented via a special `BaseGroupMintRouter` contract that acts as an intermediary between avatars and groups.

### How Group Minting Works

When a path includes group token acquisition, the flow is:

1. **Avatar → Router**: Source sends collateral (personal CRC) to the router
2. **Router → Group**: Router deposits collateral to the group's treasury
3. **Group → Avatar**: Group mints group tokens to the recipient

The router is necessary because `operateFlowMatrix` requires operator approval for transfers, and the router pre-approves all human CRCs.

### Edge Ordering Constraint (Critical)

**Contract Requirement**: The `operateFlowMatrix` smart contract processes edges sequentially. Groups can only transfer tokens they have received, so:

> All collateral edges (Router → Group) MUST appear before the group's outbound mint edge (Group → Avatar)

**Example of correct ordering**:

```text
1. Avatar1 → Router (Token A, 50 CRC)
2. Avatar2 → Router (Token B, 30 CRC)
3. Router → GroupX (Token A, 50 CRC)  ← collateral deposited
4. Router → GroupX (Token B, 30 CRC)  ← collateral deposited
5. GroupX → Sink (GroupX token, 80 CRC)  ← mint AFTER all collateral
```

**Example of incorrect ordering (causes ERC1155InsufficientBalance revert)**:

```text
1. Avatar1 → Router (Token A, 50 CRC)
2. Router → GroupX (Token A, 50 CRC)
3. GroupX → Sink (GroupX token, 80 CRC)  ← FAIL: only 50 CRC received!
4. Avatar2 → Router (Token B, 30 CRC)   ← too late
5. Router → GroupX (Token B, 30 CRC)
```

### Sorting and Validation

The pathfinder enforces correct ordering via two methods in `V2Pathfinder.cs`:

1. **`SortEdgesForMintDependencies()`**: Reorders edges after router insertion to ensure:
   - All Avatar → Router edges come first
   - For each group: all Router → Group edges before Group → Avatar edges
   - Other edges last

2. **`ValidateMintEdgeOrdering()`**: Runtime validation that throws `InvalidOperationException` if:
   - A Router → Group edge appears after a Group → Avatar edge for the same group
   - Cumulative outbound flow exceeds cumulative inbound flow at any point

### Router Configuration

The production router is: `0xdc287474114cc0551a81ddc2eb51783fbf34802f`

Configure via environment variable: `V2_BASE_GROUP_ROUTER`
