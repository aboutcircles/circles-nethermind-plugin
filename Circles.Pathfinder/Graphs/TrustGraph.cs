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

    public TrustGraph(List<string>? toTokens = null, string? sinkAddress = null)
    {
        _toTokens = toTokens?.Select(t => t.ToLowerInvariant()).ToList();
        _sinkAddress = sinkAddress?.ToLowerInvariant();
    }

    // Method to set up virtual sink when needed
    public void SetupVirtualSinkIfNeeded(string sourceAddress)
    {
        _sourceAddress = sourceAddress.ToLowerInvariant();
        
        // Create virtual sink if source and sink are the same
        if (_sourceAddress == _sinkAddress)
        {
            _virtualSinkAddress = _sourceAddress + VIRTUAL_SINK_SUFFIX;
            AddAvatar(_virtualSinkAddress);
            
            // If toTokens specified, make virtual sink trust them
            if (_toTokens != null && _toTokens.Any())
            {
                foreach (var token in _toTokens)
                {
                    AddTrustEdge(_virtualSinkAddress, token);
                }
            }
        }
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

        // If this trust relation involves the sink trusting
        if (_sinkAddress != null && truster == _sinkAddress)
        {
            // If we have toTokens filter and the token (trustee) is not in toTokens, skip
            if (_toTokens != null && !_toTokens.Contains(trustee))
            {
                return;
            }
        }

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