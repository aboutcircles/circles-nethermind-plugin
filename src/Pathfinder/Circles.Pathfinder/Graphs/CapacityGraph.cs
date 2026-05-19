using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

/// <summary>
/// Cached group/consent data extracted from a full capacity graph build.
/// Passed to subsequent filtered builds to skip redundant DB queries.
/// </summary>
public sealed record CachedGroupData(
    HashSet<int> GroupNodes,
    HashSet<int> OrganizationNodes,
    Dictionary<int, HashSet<int>> GroupTrustedTokens,
    HashSet<int> ConsentedAvatars,
    HashSet<int> RegisteredAvatarIds,
    Dictionary<int, int> WrapperToAvatar,
    Dictionary<int, int>? GroupRouters = null,
    Dictionary<(int GroupAddress, int CollateralToken), long>? ScoreGroupMintLimits = null,
    Dictionary<int, HashSet<int>>? OperatorApprovals = null,
    HashSet<int>? ScoreRouterIds = null);

public class CapacityGraph : IGraph<CapacityEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();
    public IDictionary<int, TokenNode> TokenNodes { get; } = new Dictionary<int, TokenNode>();
    public List<CapacityEdge> Edges { get; } = new();

    public int? VirtualSinkAddress { get; set; }

    // Track special avatar types
    public HashSet<int> GroupNodes { get; } = new HashSet<int>();
    public HashSet<int> OrganizationNodes { get; } = new HashSet<int>();
    public int? RouterNode { get; set; }
    public HashSet<int> RouterNodes { get; } = new HashSet<int>();
    public Dictionary<int, int> GroupRouters { get; } = new Dictionary<int, int>();
    public Dictionary<(int GroupAddress, int CollateralToken), long> ScoreGroupMintLimits { get; } = new();

    /// <summary>
    /// ERC-1155 operator approvals from Hub.ApprovalForAll, keyed by the account (typically a
    /// ScoreGroupMintRouter) that granted the approval. The value set contains the operator
    /// node IDs that the account currently approves. Used to gate Avatar→Router edges:
    /// Hub.operateFlowMatrix reverts if the path includes a Router→Group hop and the caller
    /// (the operator) is not in this set for that router.
    /// </summary>
    public Dictionary<int, HashSet<int>> OperatorApprovals { get; } = new();

    /// <summary>
    /// Score-group mint routers, keyed by address ID. Source of truth: the
    /// CrcV2_ScoreGroup.GroupInitialized event's pathMintRouter field (immutable
    /// post-init). Used by IsScoreRouter and by AddTokenPoolOutEdges to recognize a
    /// freshly-initialized score group even when no mint-limit rows or operator
    /// approvals exist yet — both of which lag the initialize event in practice.
    /// </summary>
    public HashSet<int> ScoreRouterIds { get; } = new();

    // Track which tokens each group trusts
    public Dictionary<int, HashSet<int>> GroupTrustedTokens { get; } = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// Avatars with Hub.sol advancedUsageFlags enabled (consented flow).
    /// Set during graph construction, immutable after publication to CapacityGraphPool.
    /// </summary>
    public HashSet<int> ConsentedAvatars { get; internal set; } = new HashSet<int>();

    // Track all registered V2 avatar IDs (cached to avoid per-request DB queries)
    public HashSet<int> RegisteredAvatarIds { get; } = new HashSet<int>();

    // Reverse mapping: ERC20 wrapper contract address ID → underlying avatar address ID
    // Used at DTO output layer to resolve wrapper addresses to registered avatars
    public Dictionary<int, int> WrapperToAvatar { get; } = new Dictionary<int, int>();

    // Trust lookup for consented flow validation (truster -> set of trustees)
    public IReadOnlyDictionary<int, HashSet<int>>? TrustLookup { get; set; }

    // Router trust coverage metrics (set during graph build)
    public int TotalGroupTokenEdges { get; set; }
    public int RouterFilteredEdges { get; set; }

    // Block number of the snapshot this graph was built from (for replay logging)
    public long Block { get; set; }

    public void AddTokenNode(int tokenId, int? poolNodeId = null)
    {
        // Note: We deliberately create token pool ids via BalanceNodeIdOf(...) so they’re marked “balance‑ish”.
        //       That way the path‑collapse logic (which treats “non‑avatar nodes” as collapsible) will also fold Avatar → TokenPool → Avatar into a clean Avatar → Avatar hop.
        int id = poolNodeId ?? AddressIdPool.BalanceNodeIdOf($"tpool-{tokenId}");
        if (!TokenNodes.ContainsKey(id))
        {
            var tn = new TokenNode(id, tokenId);
            TokenNodes.Add(id, tn);
            Nodes.TryAdd(id, tn);
        }
    }

    public void AddAvatar(int avatarAddress)
    {
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            AvatarNodes.Add(avatarAddress, new AvatarNode(avatarAddress));
            Nodes.Add(avatarAddress, AvatarNodes[avatarAddress]);
        }
    }

    // Add a group
    public void AddGroup(int groupAddress)
    {
        AddAvatar(groupAddress);
        GroupNodes.Add(groupAddress);
    }

    public void SetGroupRouter(int groupAddress, int routerAddress)
    {
        AddGroup(groupAddress);
        SetRouter(routerAddress);
        GroupRouters[groupAddress] = routerAddress;
    }

    // Track the router node ID for post-processing.
    // Note: Router is added as an avatar node but has no edges in the capacity graph during construction.
    // It's only used during path post-processing to insert router steps between Avatar→Group transfers.
    public void SetRouter(int routerAddress)
    {
        AddAvatar(routerAddress);
        RouterNodes.Add(routerAddress);
        RouterNode ??= routerAddress;
    }

    // Helper methods
    public bool IsGroup(int nodeAddress) => GroupNodes.Contains(nodeAddress);
    public bool IsOrganization(int nodeAddress) => OrganizationNodes.Contains(nodeAddress);
    public bool IsRouter(int nodeAddress) => RouterNodes.Contains(nodeAddress);

    public int RouterForGroup(int groupAddress)
    {
        if (GroupRouters.TryGetValue(groupAddress, out var routerAddress))
            return routerAddress;

        if (RouterNode.HasValue)
            return RouterNode.Value;

        throw new InvalidOperationException("No router is configured for group minting.");
    }

    public void AddCapacityEdge(int from, int to, int token, long capacity)
    {
        var edge = new CapacityEdge(from, to, token, capacity);
        Edges.Add(edge);
    }
}
