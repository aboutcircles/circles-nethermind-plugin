using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests.Utils;

/// <summary>
/// A test-specific version of GraphFactory that works with ILoadGraph instead of LoadGraph
/// </summary>
public class TestGraphFactory : GraphFactory
{
    public TrustGraph CreateTrustGraph(ILoadGraph loadGraph)
    {
        var graph = new TrustGraph();
        var trustEdges = loadGraph.LoadV2Trust().ToArray();

        foreach (var trustEdge in trustEdges)
        {
            if (!graph.AvatarNodes.ContainsKey(trustEdge.Truster))
            {
                graph.AddAvatar(trustEdge.Truster);
            }

            if (!graph.AvatarNodes.ContainsKey(trustEdge.Trustee))
            {
                graph.AddAvatar(trustEdge.Trustee);
            }

            graph.AddTrustEdge(trustEdge.Truster, trustEdge.Trustee);
        }

        return graph;
    }

    public BalanceGraph CreateBalanceGraph(ILoadGraph loadGraph)
    {
        var graph = new BalanceGraph();

        var balances = loadGraph.LoadV2Balances().ToArray();

        foreach (var balance in balances)
        {
            if (!graph.AvatarNodes.ContainsKey(balance.Account))
            {
                graph.AddAvatar(balance.Account);
            }

            graph.AddBalance(
                balance.Account,
                balance.TokenAddress,
                Circles.Index.Utils.ConversionUtils.TruncateToInt64(Nethermind.Int256.UInt256.Parse(balance.Balance)),
                balance.IsWrapped,
                balance.IsStatic);
        }

        return graph;
    }
}