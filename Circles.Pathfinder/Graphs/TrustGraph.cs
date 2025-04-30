using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class TrustGraph : IGraph<TrustEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();
    public HashSet<TrustEdge> Edges { get; } = new();

    public void AddAvatar(int avatarAddress)
    {
        var avatar = new AvatarNode(avatarAddress);
        AvatarNodes.Add(avatarAddress, avatar);
        Nodes.Add(avatarAddress, avatar);
    }

    public void AddTrustEdge(int truster, int trustee)
    {
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
        // AvatarNodes[trustee].InEdges.Add(new TrustEdge(truster, trustee));

        Edges.Add(new TrustEdge(truster, trustee));
    }
}