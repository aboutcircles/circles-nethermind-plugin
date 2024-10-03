using System.Numerics;
using System.Text.RegularExpressions;

namespace Circles.Pathfinder;

public class Solver
{
    private readonly Dictionary<string, int> _addressToId = new();
    private readonly List<string> _idToAddress = new();
    private readonly Dictionary<int, HashSet<int>> _trusts = new();

    private readonly Dictionary<int, Dictionary<int, BigInteger>> _balances = new();

    private Graph? _graph;
    private Graph? _transformedGraph;

    public PathResult FindPath(string source, string sink)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sink))
        {
            throw new Exception("Please provide both source and sink parameters.");
        }

        if (!IsValidEthereumAddress(source) || !IsValidEthereumAddress(sink))
        {
            throw new Exception("Invalid Ethereum address provided.");
        }

        if (!_addressToId.TryGetValue(source, out int sourceId) || !_addressToId.TryGetValue(sink, out int sinkId))
        {
            throw new Exception("Either source or sink address not found.");
        }

        // Implement path finding and max flow computation
        BigInteger maxFlow = EdmondsKarp.MaxFlow(_transformedGraph, sourceId, sinkId, out var parentMap);

        if (maxFlow.IsZero)
        {
            throw new Exception($"No transferable amount from {source} to {sink}.");
        }

        // Extract transfers
        var transfers = ExtractTransfers(sourceId, sinkId);

        var result = new PathResult
        {
            Source = source,
            Sink = sink,
            MaxTransferableAmount = maxFlow,
            Transfers = transfers
        };

        return result;
    }

    public void LoadData(TrustRelation[] trustRelations, Balance[] balances)
    {
        // Map addresses to IDs
        int MapAddress(string address)
        {
            if (!_addressToId.ContainsKey(address))
            {
                int id = _idToAddress.Count;
                _addressToId[address] = id;
                _idToAddress.Add(address);
            }

            return _addressToId[address];
        }

        // Load trust relationships
        foreach (var tr in trustRelations)
        {
            tr.TrusterId = MapAddress(tr.Truster);
            tr.TrusteeId = MapAddress(tr.Trustee);
        }

        // Build trusts mapping
        foreach (var tr in trustRelations)
        {
            if (!_trusts.ContainsKey(tr.TrusterId))
            {
                _trusts[tr.TrusterId] = new HashSet<int>();
            }

            _trusts[tr.TrusterId].Add(tr.TrusteeId);
        }

        // Load balances
        foreach (var bal in balances)
        {
            bal.AccountId = MapAddress(bal.Account);
            bal.TokenId = MapAddress(bal.TokenAddress);
        }

        // Build balances mapping
        foreach (var bal in balances)
        {
            if (!_balances.ContainsKey(bal.AccountId))
            {
                _balances[bal.AccountId] = new Dictionary<int, BigInteger>();
            }

            _balances[bal.AccountId][bal.TokenId] = bal.DemurragedTotalBalance;
        }

        // Build graph
        int nodeCount = _idToAddress.Count;
        _graph = new Graph(nodeCount);

        foreach (var sender in _balances.Keys)
        {
            var senderTokens = _balances[sender];
            foreach (var receiver in _trusts.Keys)
            {
                if (receiver == sender)
                    continue;
                var trustedTokens = _trusts[receiver];
                var commonTokens = senderTokens.Keys.Intersect(trustedTokens);
                foreach (var token in commonTokens)
                {
                    var tokenBalance = senderTokens[token];
                    if (tokenBalance > 0)
                    {
                        _graph.AddEdge(sender, receiver, tokenBalance, token);
                    }
                }
            }
        }

        // Transform the graph if needed
        _transformedGraph = _graph; // Simplification for now
    }

    private List<Transfer> ExtractTransfers(int sourceId, int sinkId)
    {
        var transfers = new List<Transfer>();
        var visited = new bool[_graph.NodeCount];
        var stack = new Stack<int>();
        var pathEdges = new Dictionary<int, Edge>();

        stack.Push(sourceId);
        visited[sourceId] = true;

        while (stack.Count > 0)
        {
            int current = stack.Pop();
            if (current == sinkId)
                break;

            foreach (var edge in _transformedGraph.AdjacencyList[current])
            {
                if (edge.Flow > 0 && !visited[edge.To])
                {
                    visited[edge.To] = true;
                    pathEdges[edge.To] = edge;
                    stack.Push(edge.To);
                }
            }
        }

        // Backtrack from sink to source
        int node = sinkId;
        while (node != sourceId)
        {
            if (!pathEdges.ContainsKey(node))
                break;

            var edge = pathEdges[node];
            transfers.Add(new Transfer
            {
                From = _idToAddress[edge.From],
                To = _idToAddress[edge.To],
                Amount = edge.Flow,
                Token = _idToAddress[edge.TokenId]
            });

            node = edge.From;
        }

        transfers.Reverse();
        return transfers;
    }

    private bool IsValidEthereumAddress(string address)
    {
        return Regex.IsMatch(address, "^0x[a-fA-F0-9]{40}$");
    }
}