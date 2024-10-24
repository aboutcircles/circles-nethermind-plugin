using System.Numerics;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class FlowGraph : IGraph<FlowEdge>
{
    public IDictionary<string, Node> Nodes { get; } = new Dictionary<string, Node>();
    public IDictionary<string, AvatarNode> AvatarNodes { get; } = new Dictionary<string, AvatarNode>();
    public IDictionary<string, BalanceNode> BalanceNodes { get; } = new Dictionary<string, BalanceNode>();
    public HashSet<FlowEdge> Edges { get; } = new();

    public void AddAvatar(string avatarAddress)
    {
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            AvatarNodes.Add(avatarAddress, new AvatarNode(avatarAddress));
            Nodes.Add(avatarAddress, AvatarNodes[avatarAddress]);
        }
    }

    public void AddBalanceNode(string address, string token, BigInteger amount)
    {
        var balanceNode = new BalanceNode(address, token, amount);
        balanceNode.Address = address;
        BalanceNodes.TryAdd(balanceNode.Address, balanceNode);
        Nodes.TryAdd(balanceNode.Address, balanceNode);
    }

    public void AddCapacity(CapacityGraph capacityGraph)
    {
        foreach (var capacityEdge in capacityGraph.Edges)
        {
            AddCapacityEdge(capacityGraph, capacityEdge);
        }
    }

    public void AddFlowEdge(FlowGraph flowGraph, FlowEdge flowEdge)
    {
        var fromNode = flowGraph.Nodes[flowEdge.From];
        if (!Nodes.TryGetValue(fromNode.Address, out var from))
        {
            if (fromNode is AvatarNode)
            {
                AddAvatar(fromNode.Address);
            }
            else if (fromNode is BalanceNode fromBalance)
            {
                AddBalanceNode(fromBalance.Address, fromBalance.Token, fromBalance.Amount);
            }

            from = Nodes[fromNode.Address];
        }

        var toNode = flowGraph.Nodes[flowEdge.To];
        if (!Nodes.TryGetValue(toNode.Address, out var to))
        {
            if (toNode is AvatarNode)
            {
                AddAvatar(toNode.Address);
            }
            else if (toNode is BalanceNode toBalance)
            {
                AddBalanceNode(toBalance.Address, toBalance.Token, toBalance.Amount);
            }

            to = Nodes[toNode.Address];
        }

        if (from.OutEdges.Any(o => o.To == to.Address && (o as FlowEdge)?.Flow == flowEdge.Flow))
        {
            return;
        }

        if (to.InEdges.Any(o => o.From == from.Address && (o as FlowEdge)?.Flow == flowEdge.Flow))
        {
            return;
        }

        var newFlowEdge = new FlowEdge(from.Address, to.Address, flowEdge.Token, flowEdge.CurrentCapacity);
        newFlowEdge.Flow = flowEdge.Flow;

        var newReverseEdge = new FlowEdge(to.Address, from.Address, flowEdge.Token,
            flowEdge.ReverseEdge?.CurrentCapacity ?? BigInteger.Zero);
        newReverseEdge.Flow = flowEdge.ReverseEdge?.Flow ?? BigInteger.Zero;

        newFlowEdge.ReverseEdge = newReverseEdge;
        newReverseEdge.ReverseEdge = newFlowEdge;

        Edges.Add(newFlowEdge);
        Edges.Add(newReverseEdge);

        if (AvatarNodes.TryGetValue(from.Address, out var fromAvatarNode))
        {
            fromAvatarNode.OutEdges.Add(newFlowEdge);
        }
        else if (BalanceNodes.TryGetValue(from.Address, out var fromBalanceNode))
        {
            fromBalanceNode.OutEdges.Add(newFlowEdge);
        }

        if (AvatarNodes.TryGetValue(to.Address, out var toAvatarNode))
        {
            toAvatarNode.InEdges.Add(newFlowEdge);
        }
        else if (BalanceNodes.TryGetValue(to.Address, out var toBalanceNode))
        {
            toBalanceNode.InEdges.Add(newFlowEdge);
        }
    }

    public void AddCapacityEdge(CapacityGraph capacityGraph, CapacityEdge capacityEdge)
    {
        var from = capacityEdge.From;
        var to = capacityEdge.To;
        var token = capacityEdge.Token;
        var capacity = capacityEdge.InitialCapacity;

        // Create edge and reverse edge
        var edge = new FlowEdge(from, to, token, capacity);
        Edges.Add(edge);

        var reverseEdge = new FlowEdge(to, from, token, 0);
        Edges.Add(reverseEdge);

        edge.ReverseEdge = reverseEdge;
        reverseEdge.ReverseEdge = edge;

        // Create nodes if they don't exist
        var fromNode = capacityGraph.Nodes[from];
        if (fromNode is AvatarNode)
        {
            AddAvatar(fromNode.Address);
        }
        else if (fromNode is BalanceNode fromBalance)
        {
            AddBalanceNode(fromBalance.Address, fromBalance.Token, fromBalance.Amount);
        }

        var toNode = capacityGraph.Nodes[to];
        if (toNode is AvatarNode)
        {
            AddAvatar(toNode.Address);
        }
        else if (toNode is BalanceNode toBalance)
        {
            AddBalanceNode(toBalance.Address, toBalance.Token, toBalance.Amount);
        }

        // manage adjacency lists
        if (AvatarNodes.TryGetValue(from, out var node))
        {
            node.OutEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balanceNode))
        {
            balanceNode.OutEdges.Add(edge);
        }

        if (AvatarNodes.TryGetValue(to, out var avatarNode))
        {
            avatarNode.InEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balanceNode))
        {
            balanceNode.InEdges.Add(edge);
        }

        if (AvatarNodes.TryGetValue(to, out var avatarNode2))
        {
            avatarNode2.OutEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balanceNode2))
        {
            balanceNode2.OutEdges.Add(reverseEdge);
        }

        if (AvatarNodes.TryGetValue(from, out var node2))
        {
            node2.InEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balanceNode2))
        {
            balanceNode2.InEdges.Add(reverseEdge);
        }
    }

    public List<List<FlowEdge>> ExtractPathsWithFlow(string sourceNode, string sinkNode)
    {
        var resultPaths = new List<List<FlowEdge>>();
        var visited = new HashSet<string>();

        // A helper method to perform DFS and collect paths with positive flow
        void Dfs(string currentNode, List<FlowEdge> currentPath)
        {
            if (currentNode == sinkNode)
            {
                resultPaths.Add(new List<FlowEdge>(currentPath)); // Store a copy of the path
                return;
            }

            if (!Nodes.TryGetValue(currentNode, out var node)) return;

            visited.Add(currentNode);

            foreach (var edge in node.OutEdges.OfType<FlowEdge>())
            {
                if (edge.Flow > 0 && !visited.Contains(edge.To))
                {
                    currentPath.Add(edge); // Add edge to the current path
                    Dfs(edge.To, currentPath); // Recursively go deeper
                    currentPath.Remove(edge); // Backtrack
                }
            }

            visited.Remove(currentNode);
        }

        // Start DFS from the source node
        Dfs(sourceNode, new List<FlowEdge>());

        return resultPaths;
    }
}