using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class CapacityGraph : IGraph<CapacityEdge>
{
    public IDictionary<string, Node> Nodes { get; } = new Dictionary<string, Node>();
    public IDictionary<string, AvatarNode> AvatarNodes { get; } = new Dictionary<string, AvatarNode>();
    public IDictionary<string, BalanceNode> BalanceNodes { get; } = new Dictionary<string, BalanceNode>();
    public HashSet<CapacityEdge> Edges { get; } = new();

    public string? VirtualSinkAddress { get; set; }
    public string? SourceAddress { get; }

    public void AddAvatar(string avatarAddress)
    {
        avatarAddress = avatarAddress.ToLower();
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            AvatarNodes.Add(avatarAddress, new AvatarNode(avatarAddress));
            Nodes.Add(avatarAddress, AvatarNodes[avatarAddress]);
        }
    }

    public void AddBalanceNode(string address, string token, long amount, bool isWrapped, bool isStatic)
    {
        address = address.ToLower();
        token = token.ToLower();
        
        var balanceNode = new BalanceNode(address, token, amount, isWrapped, isStatic);
        balanceNode.Address = address;
        BalanceNodes.TryAdd(balanceNode.Address, balanceNode);
        Nodes.TryAdd(balanceNode.Address, balanceNode);
    }

    public void AddCapacityEdge(string from, string to, string token, long capacity)
    {
        from = from.ToLower();
        to = to.ToLower();
        token = token.ToLower();
        
        var edge = new CapacityEdge(from, to, token, capacity);
        Edges.Add(edge);

        // Manage adjacency lists
        if (AvatarNodes.TryGetValue(from, out var node))
        {
            node.OutEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balanceNode))
        {
            balanceNode.OutEdges.Add(edge);
        }

        if (AvatarNodes.TryGetValue(to, out var avatarNode))
        {
            avatarNode.InEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balanceNode))
        {
            balanceNode.InEdges.Add(edge);
        }
    }
}