using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Host;

public sealed class NetworkSnapshot
{
    public long BlockNumber { get; init; }
    public required List<string> Addresses { get; init; }
    public required Dictionary<int, HashSet<int>> Trust { get; init; }
    public required BalanceGraphDto Balance { get; init; }
}

public sealed class BalanceGraphDto
{
    public required IDictionary<int, AvatarNode> AvatarNodes { get; init; }
    public required IDictionary<int, BalanceNode> BalanceNodes { get; init; }
    public required List<CapacityEdge> Edges { get; init; }
}