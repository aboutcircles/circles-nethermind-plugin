namespace Circles.Pathfinder.Graphs;

/// <summary>Read-only graph snapshot produced once per new block.</summary>
public sealed class FlowGraphSnapshot
{
    public long BlockNumber { get; }
    public FlowGraph BaseFlowGraph { get; }

    public FlowGraphSnapshot(long blockNumber, FlowGraph baseFlowGraph)
    {
        BlockNumber = blockNumber;
        BaseFlowGraph = baseFlowGraph ?? throw new ArgumentNullException(nameof(baseFlowGraph));
    }
}
