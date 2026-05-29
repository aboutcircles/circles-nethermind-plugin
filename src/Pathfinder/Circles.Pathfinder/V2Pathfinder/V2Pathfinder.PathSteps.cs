using Circles.Common;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder;

public partial class V2Pathfinder
{
    // Count how many (From,To,Token) transfer steps remain after collapsing
    internal static int CountCollapsedTransferSteps(IReadOnlyList<List<SimpleEdge>> paths, CapacityGraph capacityGraph)
    {
        var unique = new HashSet<(int From, int To, int Token)>();

        for (int i = 0; i < paths.Count; i++)
        {
            var triples = CollapsePathToTransfers(paths[i], capacityGraph);
            for (int j = 0; j < triples.Count; j++)
            {
                unique.Add(triples[j]);
            }
        }

        return unique.Count;
    }

    // Greedy pruning: pick paths that give the highest flow per *marginal* step
    private static List<List<SimpleEdge>> PrunePathsByStepLimit(
        IReadOnlyList<List<SimpleEdge>> original,
        int stepCap,
        CapacityGraph capacityGraph)
    {
        // Precompute collapsed triples for each path + path flow.
        var metas = new List<(int Index, long Flow, HashSet<(int F, int T, int K)> Triples)>(original.Count);
        for (int i = 0; i < original.Count; i++)
        {
            var path = original[i];
            long flow = 0;
            if (path.Count > 0)
            {
                flow = path[0].Flow;
            }

            var triples = new HashSet<(int F, int T, int K)>(CollapsePathToTransfers(path, capacityGraph)
                .Select(t => (t.From, t.To, t.Token)));

            metas.Add((i, flow, triples));
        }

        var picked = new bool[original.Count];
        var selectedEdges = new HashSet<(int F, int T, int K)>();
        int stepsLeft = stepCap;

        while (stepsLeft > 0)
        {
            int bestIdx = -1;
            long bestFlow = 0;
            int bestDelta = 0;

            for (int i = 0; i < metas.Count; i++)
            {
                if (picked[i])
                {
                    continue;
                }

                // How many *new* steps would this path introduce?
                int delta = 0;
                foreach (var tr in metas[i].Triples)
                {
                    bool isNew = !selectedEdges.Contains(tr);
                    if (isNew)
                    {
                        delta++;
                    }
                }

                bool fitsBudget = delta <= stepsLeft;
                if (!fitsBudget)
                {
                    continue;
                }

                // Prefer zero-delta (free) additions first.
                if (bestIdx == -1)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = delta;
                    continue;
                }

                if (delta == 0 && bestDelta != 0)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = 0;
                    continue;
                }

                if (delta == 0 && bestDelta == 0)
                {
                    bool betterFlow = metas[i].Flow > bestFlow;
                    if (betterFlow)
                    {
                        bestIdx = i;
                        bestFlow = metas[i].Flow;
                        bestDelta = 0;
                    }

                    continue;
                }

                if (bestDelta == 0)
                {
                    // Current best is free; keep it.
                    continue;
                }

                // Compare flow/delta without floating point: a/b > c/d  <=>  a*d > c*b
                // Use Math.BigMul (returns Int128) to prevent overflow for large flows
                long a = metas[i].Flow;
                long b = delta;
                long c = bestFlow;
                long d = bestDelta;

                var ad = Math.BigMul(a, d);
                var cb = Math.BigMul(c, b);
                bool betterRatio = ad > cb;
                bool tieBreakFewerSteps = ad == cb && delta < bestDelta;
                bool better = betterRatio || tieBreakFewerSteps;

                if (better)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = delta;
                }
            }

            if (bestIdx == -1)
            {
                // Nothing else fits in the remaining budget.
                break;
            }

            // Commit the chosen path
            picked[bestIdx] = true;
            foreach (var tr in metas[bestIdx].Triples)
            {
                bool added = selectedEdges.Add(tr);
                if (added)
                {
                    stepsLeft--;
                    if (stepsLeft == 0)
                    {
                        break;
                    }
                }
            }
        }

        // Preserve original order for stability.
        var pruned = new List<List<SimpleEdge>>();
        for (int i = 0; i < original.Count; i++)
        {
            if (picked[i])
            {
                pruned.Add(original[i]);
            }
        }

        return pruned;
    }

    // Collapse ONE peeled path into transfer triples (FromAvatar, ToAvatar, Token).
    internal static List<(int From, int To, int Token)> CollapsePathToTransfers(
        List<SimpleEdge> path,
        CapacityGraph capacityGraph)
    {
        var triples = new List<(int From, int To, int Token)>(Math.Max(1, path.Count));

        int i = 0;
        while (i < path.Count)
        {
            var e = path[i];

            // Check if this is a pool node
            bool eToIsPool = AddressIdPool.IsBalanceNode(e.To) &&
                            AddressIdPool.StringOf(e.To).StartsWith("tpool-");

            // Standard pool collapse: Avatar → TokenPool → Next
            if (eToIsPool)
            {
                bool hasNext = (i + 1) < path.Count;
                if (hasNext && path[i + 1].From == e.To)
                {
                    var next = path[i + 1];
                    // Collapse to Avatar → Next
                    triples.Add((e.From, next.To, e.Token));
                    i += 2;
                    continue;
                }
            }

            // Direct edges (including Group → Avatar minting)
            triples.Add((e.From, e.To, e.Token));
            i += 1;
        }

        return triples;
    }
}
