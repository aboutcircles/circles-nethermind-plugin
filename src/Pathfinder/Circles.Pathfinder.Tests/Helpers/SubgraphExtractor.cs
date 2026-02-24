using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Extracts a minimal subgraph from the full graph data, targeted at reproducing
/// a specific pathfinder result. Instead of BFS (which reaches 96% of nodes in
/// the dense Circles trust graph), this extracts only:
///
/// 1. Trust edges: truster is core, trustee is core or a token owner in core balances
/// 2. Balances held by path participants
/// 3. Groups, group trusts, and consented flags for involved addresses
///
/// The resulting FixtureSubgraph enables fast offline unit tests (~10-100 KB per
/// scenario vs ~100 MB for BFS-based extraction).
///
/// Validated by SubgraphEquivalenceTests.
/// </summary>
public static class SubgraphExtractor
{
    /// <summary>
    /// Extracts a minimal subgraph containing only the trust/balance data needed
    /// to reproduce the pathfinder result for the given source→sink transfer.
    ///
    /// Trust filter: truster must be a core address AND trustee must be a token
    /// owner present in core balances (or another core address). This gives the
    /// solver exactly the capacity edges it needs without pulling in unrelated trust.
    /// Validated by SubgraphEquivalenceTests.
    /// </summary>
    public static FixtureSubgraph Extract(
        CachedGraphData fullData,
        string source,
        string sink,
        IEnumerable<string>? pathAddresses = null)
    {
        // Core addresses: source + sink + all addresses from computed path
        var coreAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source, sink };
        if (pathAddresses != null)
        {
            foreach (var addr in pathAddresses)
            {
                coreAddresses.Add(addr);
            }
        }

        // Step 1a: Collect token owners from core balances.
        // Capacity edges are: holder→receiver with token T, where receiver trusts T_owner.
        // We need trust edges (receiver, T_owner) for all token owners in core balances.
        var filteredBalances = fullData.Balances
            .Where(b => coreAddresses.Contains(
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

        // Token owners = addresses whose personal tokens are held by core addresses
        var tokenOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in filteredBalances)
        {
            tokenOwners.Add(b.Token!);
        }

        // Step 1b: Collect trust edges where truster is core AND trustee is either
        // core or a token owner. This gives the solver the capacity edges it needs:
        // - core→core trust: direct transfer capacity between path participants
        // - core→tokenOwner trust: capacity for tokens held by core addresses
        var filteredTrust = new List<string[]>();

        foreach (var (truster, trustee, _) in fullData.Trust)
        {
            var trusterLower = truster.ToLowerInvariant();

            if (!coreAddresses.Contains(trusterLower))
                continue;

            var trusteeLower = trustee.ToLowerInvariant();
            if (coreAddresses.Contains(trusteeLower) || tokenOwners.Contains(trusteeLower))
            {
                filteredTrust.Add(new[] { truster, trustee });
            }
        }

        // Step 3: Groups that appear in the core address set
        var filteredGroups = fullData.Groups
            .Where(g => coreAddresses.Contains(g.ToLowerInvariant()))
            .ToList();

        // Step 4: Group trusts for involved groups
        var groupSet = new HashSet<string>(filteredGroups, StringComparer.OrdinalIgnoreCase);
        var filteredGroupTrusts = fullData.GroupTrusts
            .Where(gt => groupSet.Contains(gt.GroupAddress.ToLowerInvariant()))
            .Select(gt => new[] { gt.GroupAddress, gt.TrustedToken })
            .ToList();

        // Step 5: Consented flags for core addresses
        var filteredConsented = fullData.ConsentedFlags
            .Where(cf => cf.HasConsentedFlow && coreAddresses.Contains(cf.Avatar.ToLowerInvariant()))
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
                AddressCount = coreAddresses.Count,
                TrustEdges = filteredTrust.Count,
                BalanceEntries = filteredBalances.Count,
                GroupCount = filteredGroups.Count,
                GroupTrustCount = filteredGroupTrusts.Count,
                ConsentedCount = filteredConsented.Count
            }
        };
    }

    /// <summary>
    /// Extracts path addresses from a list of transfer steps (from, to, tokenOwner).
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
}
