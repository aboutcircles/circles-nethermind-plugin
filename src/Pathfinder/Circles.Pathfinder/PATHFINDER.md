# Circles Pathfinder: Node Types & Graph Construction

## Node Types in Detail

### 1. Avatar Node
**When Created**: During graph initialization from trust/balance data  
**Used In**: Graph construction & pathfinding  
**Properties**:
- Represents a user account address
- Can hold token balances
- Can establish trust relationships
- Can be source/sink (except Router & Groups)

### 2. TokenPool Node  
**When Created**: Dynamically during graph construction  
**Used In**: Graph construction & pathfinding (removed in post-processing)  
**Properties**:
- Virtual node (not in database)
- One per unique token in the system
- ID format: `tpool-{tokenId}`
- Aggregates all holders of a specific token
- Acts as distribution hub

### 3. Group Node
**When Created**: Loaded from database during graph initialization  
**Used In**: Graph construction & pathfinding  
**Properties**:
- Special avatar that mints tokens
- Group address = Group token address
- Cannot be source or sink
- Stored in `CrcV2_RegisterGroup` table
- Can trust other tokens (receive them)
- Can mint unlimited own tokens

### 4. Router Node
**When Created**: Tracked during graph init, used in post-processing only  
**Used In**: NOT in capacity graph, only in final output  
**Properties**:
- Address: `0xdc287474114cc0551a81ddc2eb51783fbf34802f`
- No edges during graph construction
- Inserted between Avatar→Group transfers after pathfinding
- Preserves token identity (no transformation)

### 5. Virtual Sink Node
**When Created**: Only when `source == sink && toTokens.length > 0`  
**Used In**: Graph construction & pathfinding (replaced in output)  
**Properties**:
- ID format: `{sourceId}_virtual_sink`
- Enables token-to-token conversion
- Trusts only tokens in `toTokens` that source also trusts
- Replaced with real sink address in final output

## Graph Construction Flow

```mermaid
graph TD
    subgraph "Phase 1: Initialize Nodes"
        LoadAvatars[Load all Avatars from trust/balance data]
        LoadGroups[Load Groups from CrcV2_RegisterGroup]
        TrackRouter[Track Router address]
        CheckVirtual{source == sink?}
        CreateVirtual[Create Virtual Sink]
        
        LoadAvatars --> LoadGroups
        LoadGroups --> TrackRouter
        TrackRouter --> CheckVirtual
        CheckVirtual -->|Yes + toTokens| CreateVirtual
        CheckVirtual -->|No| Phase2
        CreateVirtual --> Phase2[Continue to Phase 2]
    end
```

## Edge Construction in Detail

### Phase 1: Avatar → TokenPool Edges

```mermaid
graph LR
    subgraph "Balance Edges"
        A1[Avatar1<br/>Balance: 100 T1<br/>50 T2] 
        A2[Avatar2<br/>Balance: 75 T1]
        TP1[TokenPool T1<br/>Created on-demand]
        TP2[TokenPool T2<br/>Created on-demand]
        
        A1 -->|"100 T1"| TP1
        A1 -->|"50 T2"| TP2
        A2 -->|"75 T1"| TP1
    end
```

**Node Creation**:
```csharp
foreach (balance in balances) {
    // Skip Router and Groups - they don't use pools
    if (IsRouter(balance.Holder) || IsGroup(balance.Holder)) continue;
    
    // Create TokenPool node if doesn't exist
    int poolId = AddressIdPool.TokenPoolIdOf(balance.Token);
    graph.AddTokenNode(balance.Token, poolId);
    
    // Add edge with balance as capacity
    graph.AddCapacityEdge(
        from: balance.Holder,
        to: poolId,
        token: balance.Token,
        capacity: balance.Amount
    );
}
```

### Phase 2: TokenPool → Avatar/Group Edges

```mermaid
graph LR
    subgraph "Trust-Based Distribution"
        TP1[TokenPool T1]
        TP2[TokenPool T2]
        A1[Avatar1<br/>trusts: T1,T2]
        A2[Avatar2<br/>trusts: T1]
        G1[Group1<br/>trusts: T1,T2]
        R[Router<br/>NO EDGES]
        
        TP1 -->|"∞"| A1
        TP1 -->|"∞"| A2
        TP1 -->|"∞"| G1
        TP2 -->|"∞"| A1
        TP2 -->|"∞"| G1
        TP1 -.->|"NO!"| R
    end
```

**Key Points**:
- Router NEVER receives from TokenPools
- Groups CAN receive from TokenPools (for tokens they trust)
- Capacity always ∞ (unlimited)

### Phase 3: Group → Avatar Minting Edges

```mermaid
graph LR
    subgraph "Minting Edges"
        G1[Group1<br/>Token: G1]
        G2[Group2<br/>Token: G2]
        A1[Avatar1<br/>trusts: G1]
        A2[Avatar2<br/>trusts: G1,G2]
        A3[Avatar3<br/>trusts: G2]
        R[Router<br/>NO EDGES]
        G3[Group3<br/>NO EDGES]
        
        G1 -->|"∞ G1 token"| A1
        G1 -->|"∞ G1 token"| A2
        G2 -->|"∞ G2 token"| A2
        G2 -->|"∞ G2 token"| A3
        G1 -.->|"NO!"| R
        G1 -.->|"NO!"| G3
    end
```

**Implementation**:
```csharp
foreach (group in groups) {
    int groupToken = group; // Group IS its token
    
    foreach (avatar in avatars) {
        // Skip Router and other Groups
        if (IsRouter(avatar) || IsGroup(avatar)) continue;
        
        if (trustsToken(avatar, groupToken)) {
            graph.AddCapacityEdge(
                from: group,
                to: avatar,
                token: groupToken,
                capacity: long.MaxValue
            );
        }
    }
}
```

## Complete Flow Example

```mermaid
graph TD
    subgraph sub1["1. Graph Construction"]
        direction LR
        A[Avatar A<br/>Has: 100 AT]
        TPat[TokenPool AT]
        G[Group G<br/>Trusts: AT<br/>Mints: GT]
        B[Avatar B<br/>Trusts: GT]
        
        A -->|"100 AT"| TPat
        TPat -->|"∞ AT"| G
        G -->|"∞ GT"| B
    end
    
    subgraph sub2["2. Path Found by MaxFlow"]
        direction LR
        A2[A] -->|"50 AT"| TP2[Pool AT]
        TP2 -->|"50 AT"| G2[G]
        G2 -->|"50 GT"| B2[B]
    end
    
    subgraph sub3["3. After Collapse"]
        direction LR
        A3[A] -->|"50 AT"| G3[G]
        G3 -->|"50 GT"| B3[B]
    end
    
    subgraph sub4["4. After Router Insertion"]
        direction LR
        A4[A] -->|"50 AT"| R[Router]
        R -->|"50 AT"| G4[G]
        G4 -->|"50 GT"| B4[B]
    end
    
    sub1 ~~~ sub2
    sub2 ~~~ sub3
    sub3 ~~~ sub4
```

## Node Presence by Phase

| Node Type | Graph Construction | MaxFlow | Path Collapse | Post-Process | Final Output |
|-----------|-------------------|---------|---------------|--------------|--------------|
| Avatar | ✓ | ✓ | ✓ | ✓ | ✓ |
| TokenPool | ✓ | ✓ | Removed | - | - |
| Group | ✓ | ✓ | ✓ | ✓ | ✓ |
| Router | Tracked only | - | - | Inserted | ✓ |
| Virtual Sink | ✓ (conditional) | ✓ | ✓ | Replaced | - |

## Critical Implementation Rules

1. **TokenPool Creation**: Created on-demand when first balance references the token
2. **Router Isolation**: Router node exists but has ZERO edges during graph construction
3. **Group Direct Access**: Groups receive directly from TokenPools (no Router needed here)
4. **Minting Restriction**: Groups only send their own token, only to Avatars
5. **Post-Process Router**: Router inserted ONLY between Avatar→Group, preserving token identity