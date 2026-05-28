using System.Text.RegularExpressions;

namespace Circles.Rpc.Host;

/// <summary>
/// JSON-RPC module for Circles protocol queries.
///
/// This class is split into multiple partial class files for maintainability:
/// - CirclesRpcModule.cs (this file) - Shared constants + class declaration
/// - RpcModule/CirclesRpcModule.Core.cs          - Constructor, fields, connection management
/// - RpcModule/CirclesRpcModule.Avatars.cs       - Avatar info
/// - RpcModule/CirclesRpcModule.Balances.cs      - Balance queries
/// - RpcModule/CirclesRpcModule.Tokens.cs        - Token info
/// - RpcModule/CirclesRpcModule.Profiles.cs      - Profile CIDs and content
/// - RpcModule/CirclesRpcModule.Profiles.Search.cs - Profile views + search
/// - RpcModule/CirclesRpcModule.Trust.cs         - Trust relations + network summaries
/// - RpcModule/CirclesRpcModule.Groups.cs        - Group operations
/// - RpcModule/CirclesRpcModule.Helpers.cs       - Utility methods
/// - RpcModule/CirclesRpcModule.ScoreGroup.cs    - Score group mint limits
/// - RpcModule/CirclesRpcModule.Transactions.cs  - Transaction history + enriched
/// - RpcModule/CirclesRpcModule.Events.cs        - Events query
/// - RpcModule/CirclesRpcModule.Pathfinding.cs   - FindPathV2 + GetNetworkSnapshot
/// - RpcModule/CirclesRpcModule.Invitations.cs   - Invitation queries
/// - RpcModule/CirclesRpcModule.Query.cs         - Generic query engine
/// </summary>
public partial class CirclesRpcModule : ICirclesRpcModule
{
    private const int MaxInFilterElements = 1000;

    // Namespaces whose address-bearing tables seed the avatar tx-hash CTE used to
    // address-filter address-less flow-scope tables in GetEvents. Flow-scope events
    // are emitted exclusively inside Hub.operateFlowMatrix calls, which always also
    // emit V2 transfer/mint events for participating avatars in the same tx — so V2
    // is the only namespace whose tx-hashes pull in legitimate flow-scope rows.
    // If a future protocol introduces flow-scope-style events, add its namespace here
    // or its address-filtered queries will silently return zero flow-scope rows.
    private static readonly HashSet<string> FlowScopeAddressFilterNamespaces =
        new(StringComparer.Ordinal) { "CrcV2" };
}
