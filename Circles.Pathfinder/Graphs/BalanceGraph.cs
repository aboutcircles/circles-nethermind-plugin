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
    private readonly List<string>? _toTokens;
    private readonly string? _sinkAddress;

    public BalanceGraph(List<string>? fromTokens = null, string? sourceAddress = null, List<string>? toTokens = null, string? sinkAddress = null)
    {
        _fromTokens = fromTokens?.Select(t => t.ToLower()).ToList();
        _sourceAddress = sourceAddress?.ToLower();
        _toTokens = toTokens?.Select(t => t.ToLower()).ToList();
        _sinkAddress = sinkAddress?.ToLower();
    }

    public void AddAvatar(string avatarAddress)
    {
        Nodes.Add(avatarAddress, new AvatarNode(avatarAddress));
    }


    public void AddBalance(string address, string token, BigInteger balance)
    {
        address = address.ToLower();
        token = token.ToLower();

        // Case 1: When source and sink are the same, don't add balances for tokens that are in toTokens
        if (_sourceAddress != null && _sinkAddress != null && 
            _sourceAddress == _sinkAddress && address == _sourceAddress && 
            _toTokens.Any() && _toTokens.Contains(token))
        {
            return; 
        }

        // Case 2: When fromTokens is specified, only include specified tokens for source address
        if (_sourceAddress != null && address == _sourceAddress && 
            _fromTokens.Any() && !_fromTokens.Contains(token))
        {
            return;
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