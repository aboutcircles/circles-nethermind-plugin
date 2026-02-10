using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Extracts a minimal subgraph around source/sink addresses using BFS expansion.
/// The resulting FixtureSubgraph contains only trust/balance/group data within N hops
/// of the relevant addresses, enabling fast offline unit tests.
///
/// The subgraph must produce the same pathfinder result as the full graph for the
/// given source/sink pair — this is validated by SubgraphEquivalenceTests.
/// </summary>
public static class SubgraphExtractor
{
    /// <summary>
    /// Extracts a subgraph containing trust/balance data within <paramref name="maxHops"/>
    /// of the source, sink, and any addresses that appeared in the computed path.
    /// </summary>
    /// <param name="fullData">The full cached graph data from SharedGraphCache.</param>
    /// <param name="source">Source address (lowercase hex).</param>
    /// <param name="sink">Sink address (lowercase hex).</param>
    /// <param name="pathAddresses">Addresses that appeared in the computed path (from/to/token).</param>
    /// <param name="maxHops">BFS expansion depth from seed addresses. Default: 3.</param>
    public static FixtureSubgraph Extract(
        CachedGraphData fullData,
        string source,
        string sink,
        IEnumerable<string>? pathAddresses = null,
        int maxHops = 3)
    {
        // Step 1: Build adjacency list from trust edges for BFS
        var adjacency = BuildTrustAdjacency(fullData.Trust);

        // Step 2: Seed addresses = source + sink + path addresses
        var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source, sink };
        if (pathAddresses != null)
        {
            foreach (var addr in pathAddresses)
            {
                seeds.Add(addr);
            }
        }

        // Step 3: BFS expansion — find all addresses within maxHops of seeds
        var reachable = BfsExpand(adjacency, seeds, maxHops);

        // Step 4: Filter trust edges — both endpoints must be in reachable set
        var filteredTrust = fullData.Trust
            .Where(t =>
            {
                var truster = AddressIdPool.StringOf(
                    AddressIdPool.IdOf(t.Truster));
                var trustee = AddressIdPool.StringOf(
                    AddressIdPool.IdOf(t.Trustee));
                return reachable.Contains(truster) && reachable.Contains(trustee);
            })
            .Select(t => new[] { t.Truster, t.Trustee })
            .ToList();

        // Step 5: Filter balances — holder must be in reachable set
        var filteredBalances = fullData.Balances
            .Where(b => reachable.Contains(
                AddressIdPool.StringOf(b.Account)))
            .Select(b => new BalanceEntry
            {
                Holder = AddressIdPool.StringOf(b.Account),
                Token = AddressIdPool.StringOf(b.TokenAddress),
                Amount = b.Balance,
                IsWrapped = b.IsWrapped,
                IsStatic = b.IsStatic
            })
            .ToList();

        // Step 6: Filter groups — only groups in reachable set
        var filteredGroups = fullData.Groups
            .Where(g => reachable.Contains(g.ToLowerInvariant()))
            .ToList();

        // Step 7: Filter group trusts — group must be in reachable set
        var filteredGroupTrusts = fullData.GroupTrusts
            .Where(gt => reachable.Contains(gt.GroupAddress.ToLowerInvariant()))
            .Select(gt => new[] { gt.GroupAddress, gt.TrustedToken })
            .ToList();

        // Step 8: Filter consented flags — avatar must be in reachable set
        var filteredConsented = fullData.ConsentedFlags
            .Where(cf => cf.HasConsentedFlow && reachable.Contains(cf.Avatar.ToLowerInvariant()))
            .Select(cf => cf.Avatar)
            .ToList();

        return new FixtureSubgraph
        {
            Trust = filteredTrust,
            Balances = filteredBalances,
            Groups = filteredGroups,
            GroupTrusts = filteredGroupTrusts,
            ConsentedAvatars = filteredConsented,
            Stats = new SubgraphStats
            {
                AddressCount = reachable.Count,
                TrustEdges = filteredTrust.Count,
                BalanceEntries = filteredBalances.Count,
                GroupCount = filteredGroups.Count,
                GroupTrustCount = filteredGroupTrusts.Count,
                ConsentedCount = filteredConsented.Count
            }
        };
    }

    /// <summary>
    /// Extracts path addresses from a list of transfer steps (from, to, tokenAddress).
    /// </summary>
    public static HashSet<string> GetPathAddresses(IEnumerable<Circles.Common.Dto.TransferPathStep> transfers)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in transfers)
        {
            if (!string.IsNullOrEmpty(step.From)) addresses.Add(step.From);
            if (!string.IsNullOrEmpty(step.To)) addresses.Add(step.To);
            if (!string.IsNullOrEmpty(step.TokenOwner)) addresses.Add(step.TokenOwner);
        }
        return addresses;
    }

    /// <summary>
    /// Builds a bidirectional adjacency list from trust edges for BFS.
    /// Uses lowercase address strings as keys.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildTrustAdjacency(
        List<(string Truster, string Trustee, int Limit)> trust)
    {
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (truster, trustee, _) in trust)
        {
            var a = truster.ToLowerInvariant();
            var b = trustee.ToLowerInvariant();

            if (!adj.TryGetValue(a, out var setA))
            {
                setA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adj[a] = setA;
            }
            setA.Add(b);

            // Bidirectional for BFS (trust is directional, but for subgraph
            // extraction we want to discover the neighborhood in both directions)
            if (!adj.TryGetValue(b, out var setB))
            {
                setB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adj[b] = setB;
            }
            setB.Add(a);
        }

        return adj;
    }

    /// <summary>
    /// BFS expansion from seed addresses up to maxHops.
    /// Returns all reachable addresses (lowercase).
    /// </summary>
    private static HashSet<string> BfsExpand(
        Dictionary<string, HashSet<string>> adjacency,
        HashSet<string> seeds,
        int maxHops)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<(string Address, int Depth)>();

        foreach (var seed in seeds)
        {
            var lower = seed.ToLowerInvariant();
            if (visited.Add(lower))
            {
                frontier.Enqueue((lower, 0));
            }
        }

        while (frontier.Count > 0)
        {
            var (addr, depth) = frontier.Dequeue();

            if (depth >= maxHops) continue;

            if (adjacency.TryGetValue(addr, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (visited.Add(neighbor))
                    {
                        frontier.Enqueue((neighbor, depth + 1));
                    }
                }
            }
        }

        return visited;
    }
}
