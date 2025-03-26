using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class TrustGraph : IGraph<TrustEdge>
{
    public IDictionary<string, Node> Nodes { get; } = new Dictionary<string, Node>();
    public IDictionary<string, AvatarNode> AvatarNodes { get; } = new Dictionary<string, AvatarNode>();
    public HashSet<TrustEdge> Edges { get; } = new();

    private readonly List<string>? _toTokens;
    private readonly string? _sinkAddress;

    private const string VIRTUAL_SINK_SUFFIX = "_virtual_sink";
    private string? _virtualSinkAddress;
    private string? _sourceAddress;

    // Method to set up virtual sink when needed
    public void SetupVirtualSinkIfNeeded(string sourceAddress)
    {
        _sourceAddress = sourceAddress.ToLower();
        
        // Create virtual sink if source and sink are the same
        if (_sourceAddress == _sinkAddress)
        {
            bool anyTrusted = false;
            _virtualSinkAddress = _sourceAddress + VIRTUAL_SINK_SUFFIX;
            AddAvatar(_virtualSinkAddress);
            
            // If toTokens specified, make virtual sink trust them ONLY if the real sink trusts them
            if (_toTokens.Any() && _toTokens.Any())
            {
                foreach (var token in _toTokens)
                {
                    // Only add trust edge if the source/sink already trusts this token
                    if (HasTrustEdge(_sourceAddress, token))
                    {
                        AddTrustEdge(_virtualSinkAddress, token);
                        anyTrusted = true;
                    }
                }
            }
            
            // If no tokens are trusted, don't use virtual sink
            if (!anyTrusted)
            {
                _virtualSinkAddress = null;
            }
        }
    }

    // Helper method to check if a trust edge exists
    private bool HasTrustEdge(string truster, string trustee)
    {
        truster = truster.ToLower();
        trustee = trustee.ToLower();
        
        return Edges.Any(edge => edge.From == truster && edge.To == trustee);
    }

    // Helper method to check if a node has any outgoing edges
    public bool HasAnyOutgoingEdges(string nodeAddress)
    {
        nodeAddress = nodeAddress.ToLower();
        return Edges.Any(edge => edge.From == nodeAddress);
    }

    // Method to get the virtual sink address if it exists
    public string? GetVirtualSinkAddress() => _virtualSinkAddress;

    public void AddAvatar(string avatarAddress)
    {
        avatarAddress = avatarAddress.ToLower();
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            var avatar = new AvatarNode(avatarAddress);
            AvatarNodes.Add(avatarAddress, avatar);
            Nodes.Add(avatarAddress, avatar);
        }
    }

    public void AddTrustEdge(string truster, string trustee)
    {
        truster = truster.ToLower();
        trustee = trustee.ToLower();

        if (!AvatarNodes.TryGetValue(truster, out var trusterNode))
        {
            trusterNode = new AvatarNode(truster);
            AvatarNodes[truster] = trusterNode;
        }

        if (!AvatarNodes.TryGetValue(trustee, out var trusteeNode))
        {
            trusteeNode = new AvatarNode(trustee);
            AvatarNodes[trustee] = trusteeNode;
        }

        trusterNode.OutEdges.Add(new TrustEdge(truster, trustee));
        AvatarNodes[trustee].InEdges.Add(new TrustEdge(truster, trustee));

        Edges.Add(new TrustEdge(truster, trustee));
    }
}