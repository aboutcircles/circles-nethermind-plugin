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

    private readonly List<string>? _fromTokens;
    private readonly string? _sourceAddress;

    public BalanceGraph(List<string>? fromTokens = null, string? sourceAddress = null)
    {
        _fromTokens = fromTokens?.Select(t => t.ToLowerInvariant()).ToList();
        _sourceAddress = sourceAddress?.ToLowerInvariant();
    }

    public void AddAvatar(string avatarAddress)
    {
        Nodes.Add(avatarAddress, new AvatarNode(avatarAddress));
    }

    public void AddBalance(string address, string token, BigInteger balance)
    {
        address = address.ToLower();
        token = token.ToLower();

        // For source address, only add balances of specified tokens
        if (_sourceAddress != null && address == _sourceAddress)
        {
            if (_fromTokens != null && !_fromTokens.Contains(token))
            {
                return;
            }
        }

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