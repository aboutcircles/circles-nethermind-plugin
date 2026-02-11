using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

/// <summary>
/// Cached group/consent data extracted from a full capacity graph build.
/// Passed to subsequent filtered builds to skip redundant DB queries.
/// </summary>
public sealed record CachedGroupData(
    HashSet<int> GroupNodes,
    Dictionary<int, HashSet<int>> GroupTrustedTokens,
    HashSet<int> ConsentedAvatars);

public class CapacityGraph : IGraph<CapacityEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();
    public IDictionary<int, TokenNode> TokenNodes { get; } = new Dictionary<int, TokenNode>();
    public List<CapacityEdge> Edges { get; } = new();

    public int? VirtualSinkAddress { get; set; }

    // Track special avatar types
    public HashSet<int> GroupNodes { get; } = new HashSet<int>();
    public int? RouterNode { get; set; }

    // Track which tokens each group trusts
    public Dictionary<int, HashSet<int>> GroupTrustedTokens { get; } = new Dictionary<int, HashSet<int>>();

    // Track avatars that have enabled consented flow
    public HashSet<int> ConsentedAvatars { get; set; } = new HashSet<int>();

    // Trust lookup for consented flow validation (truster -> set of trustees)
    public IReadOnlyDictionary<int, HashSet<int>>? TrustLookup { get; set; }

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

    // Track the router node ID for post-processing.
    // Note: Router is added as an avatar node but has no edges in the capacity graph during construction.
    // It's only used during path post-processing to insert router steps between Avatar→Group transfers.
    public void SetRouter(int routerAddress)
    {
        AddAvatar(routerAddress);
        RouterNode = routerAddress;
    }

    // Helper methods
    public bool IsGroup(int nodeAddress) => GroupNodes.Contains(nodeAddress);
    public bool IsRouter(int nodeAddress) => RouterNode.HasValue && RouterNode.Value == nodeAddress;

    public void AddCapacityEdge(int from, int to, int token, long capacity)
    {
        var edge = new CapacityEdge(from, to, token, capacity);
        Edges.Add(edge);
    }
}