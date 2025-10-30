using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Host;

public sealed class NetworkSnapshot
{
    public long BlockNumber { get; init; }
    public required List<string> Addresses { get; init; }
    public required Dictionary<int, HashSet<int>> Trust { get; init; }
    public required Dictionary<int, List<BalanceNode>> Balance { get; init; }
}