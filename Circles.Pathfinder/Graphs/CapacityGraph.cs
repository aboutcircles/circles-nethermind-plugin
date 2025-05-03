using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class CapacityGraph : IGraph<CapacityEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();
    public IDictionary<int, BalanceNode> BalanceNodes { get; } = new Dictionary<int, BalanceNode>();
    public List<CapacityEdge> Edges { get; } = new();

    public int? VirtualSinkAddress { get; set; }

    public void AddAvatar(int avatarAddress)
    {
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            AvatarNodes.Add(avatarAddress, new AvatarNode(avatarAddress));
            Nodes.Add(avatarAddress, AvatarNodes[avatarAddress]);
        }
    }

    public void AddBalanceNode(int holder, int token, long amount, bool isWrapped, bool isStatic, int? balanceNodeId)
    {
        var _balanceNodeId = balanceNodeId ?? AddressIdPool.BalanceNodeIdOf($"{holder}-{token}");
        var balanceNode = new BalanceNode(_balanceNodeId, holder, token, amount, isWrapped, isStatic);
        BalanceNodes.TryAdd(balanceNode.Address, balanceNode);
        Nodes.TryAdd(balanceNode.Address, balanceNode);
    }

    public void AddCapacityEdge(int from, int to, int token, long capacity)
    {
        var edge = new CapacityEdge(from, to, token, capacity);
        Edges.Add(edge);
    }
}