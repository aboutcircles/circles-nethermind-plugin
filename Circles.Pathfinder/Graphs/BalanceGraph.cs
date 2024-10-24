using System.Numerics;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class BalanceGraph : IGraph<CapacityEdge>
{
    public IDictionary<string, Node> Nodes { get; } = new Dictionary<string, Node>();
    public HashSet<CapacityEdge> Edges { get; } = new();
    public IDictionary<string, BalanceNode> BalanceNodes { get; } = new Dictionary<string, BalanceNode>();
    public IDictionary<string, AvatarNode> AvatarNodes { get; } = new Dictionary<string, AvatarNode>();

    public void AddAvatar(string avatarAddress)
    {
        Nodes.Add(avatarAddress, new AvatarNode(avatarAddress));
    }

    public void AddBalance(string address, string token, BigInteger balance)
    {
        address = address.ToLower();
        token = token.ToLower();
        if (!AvatarNodes.ContainsKey(address))
        {
            AvatarNodes.Add(address, new AvatarNode(address));
        }

        var balanceNode = new BalanceNode(address, token, balance);
        Nodes.Add(balanceNode.Address, balanceNode);
        BalanceNodes.Add(balanceNode.Address, balanceNode);

        var capacityEdge = new CapacityEdge(address, balanceNode.Address, token, balance);
        Edges.Add(capacityEdge);

        AvatarNodes[address].OutEdges.Add(capacityEdge);
        BalanceNodes[balanceNode.Address].InEdges.Add(capacityEdge);
    }
}