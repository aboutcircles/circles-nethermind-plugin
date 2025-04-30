using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class BalanceGraph : IGraph<CapacityEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public HashSet<CapacityEdge> Edges { get; } = new();
    public IDictionary<int, BalanceNode> BalanceNodes { get; } = new Dictionary<int, BalanceNode>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();

    public void AddAvatar(int avatarAddress)
    {
        var avatar = new AvatarNode(avatarAddress);
        Nodes.Add(avatarAddress, avatar);
        AvatarNodes.Add(avatarAddress, avatar);
    }

    public void AddBalance(int address, int token, long balance, bool isWrapped, bool isStatic)
    {
        if (!AvatarNodes.ContainsKey(address))
        {
            AvatarNodes.Add(address, new AvatarNode(address));
        }

        var balanceNodeId = AddressIdPool.BalanceNodeIdOf($"{address}-{token}");
        var balanceNode = new BalanceNode(balanceNodeId, address, token, balance, isWrapped, isStatic);
        Nodes.Add(balanceNode.Address, balanceNode);
        BalanceNodes.Add(balanceNode.Address, balanceNode);

        var capacityEdge = new CapacityEdge(address, balanceNode.Address, token, balance);
        Edges.Add(capacityEdge);

        AvatarNodes[address].OutEdges.Add(capacityEdge);
        BalanceNodes[balanceNode.Address].InEdges.Add(capacityEdge);
    }
}