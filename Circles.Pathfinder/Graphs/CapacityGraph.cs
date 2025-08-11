using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class CapacityGraph : IGraph<CapacityEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();
    public IDictionary<int, TokenNode> TokenNodes { get; } = new Dictionary<int, TokenNode>();
    public List<CapacityEdge> Edges { get; } = new();

    public int? VirtualSinkAddress { get; set; }
    
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

    public void AddCapacityEdge(int from, int to, int token, long capacity)
    {
        var edge = new CapacityEdge(from, to, token, capacity);
        Edges.Add(edge);
    }
}