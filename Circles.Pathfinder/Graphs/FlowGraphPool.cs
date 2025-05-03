using System.Collections.Concurrent;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;

namespace Circles.Pathfinder.Graphs;

public sealed class FlowGraphPool
{
    private readonly ConcurrentDictionary<FlowGraphSnapshot, int> _refCounts = new();
    private volatile FlowGraphSnapshot? _current;
    private readonly string _connectionString;

    public FlowGraphPool(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<FlowGraphHandle> Rent(FlowRequest request)
    {
        if (_current == null)
        {
            throw new InvalidOperationException("No flow graph available.");
        }

        bool hasFilters = RequestNeedsFiltering(request);
        if (hasFilters)
        {
            Console.WriteLine("Has filters...");
            var flowGraph = await CreateFlowGraph(_connectionString, request);
            return new FlowGraphHandle(flowGraph, null, this);
        }

        FlowGraphSnapshot snapshot = _current;
        int _ = _refCounts.AddOrUpdate(
            snapshot,
            addValueFactory: _ => 1,
            updateValueFactory: (_, count) => count + 1);

        FlowGraph clone = snapshot.BaseFlowGraph.CloneShallow();
        return new FlowGraphHandle(clone, snapshot, this);
    }

    public static async Task<FlowGraph> CreateFlowGraph(string connectionString, FlowRequest request)
    {
        var loadGraph = new LoadGraph(connectionString);
        var graphFactory = new GraphFactory();
        var balanceTask = Task.Run(() => graphFactory.V2BalanceGraph(loadGraph));
        var trustTask = Task.Run(() =>
        {
            var graph = graphFactory.V2TrustGraph(loadGraph);
            return GraphFactory.BuildTrustLookup(graph);
        });

        await Task.WhenAll(trustTask, balanceTask);

        var capacityGraph = graphFactory.CreateCapacityGraph(balanceTask.Result, trustTask.Result, request);
        var flowGraph = graphFactory.CreateFlowGraph(capacityGraph);
        return flowGraph;
    }

    public void UpdateSnapshot(FlowGraphSnapshot newSnapshot)
    {
        FlowGraphSnapshot? oldSnapshot = _current;
        _current = newSnapshot;

        _refCounts.TryAdd(newSnapshot, 0);
        // If nobody is using the old snapshot any more, drop the entry.
        if (oldSnapshot != null && _refCounts.TryGetValue(oldSnapshot, out int count) && count == 0)
        {
            _refCounts.TryRemove(oldSnapshot, out _);
        }
    }

    internal void Release(FlowGraphSnapshot snapshot)
    {
        bool found = _refCounts.TryGetValue(snapshot, out int count);
        if (!found)
        {
            return; // already discarded
        }

        int newCount = _refCounts.AddOrUpdate(snapshot, _ => 0, (_, c) => c - 1);
        if (newCount == 0 && snapshot != _current)
        {
            _refCounts.TryRemove(snapshot, out _);
        }
        else
        {
            _refCounts[snapshot] = newCount;
        }
    }

    public static bool RequestNeedsFiltering(FlowRequest r)
    {
        bool hasIncludeFilters =
            (r.FromTokens?.Any() ?? false) ||
            (r.ToTokens?.Any() ?? false);

        bool hasExcludeFilters =
            (r.ExcludedFromTokens?.Any() ?? false) ||
            (r.ExcludedToTokens?.Any() ?? false);

        bool hasWrap = r.WithWrap == true;

        bool needsFiltering = hasIncludeFilters || hasExcludeFilters || hasWrap;
        return needsFiltering;
    }
}