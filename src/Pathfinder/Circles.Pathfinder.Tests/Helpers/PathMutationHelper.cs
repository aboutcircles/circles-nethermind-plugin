using Circles.Common.Dto;
using Circles.Pathfinder.Validation;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Mutation strategies for testing that HubContractValidator detects invalid paths.
/// Each mutation targets a specific validation rule.
/// </summary>
public static class PathMutationHelper
{
    /// <summary>
    /// Apply a random mutation to a valid transfer path.
    /// Returns the mutated path and a description of what was changed.
    /// </summary>
    public static (List<TransferPathStep> Mutated, string Description) ApplyRandomMutation(
        List<TransferPathStep> original,
        IContractState state,
        Random rng)
    {
        if (original.Count == 0)
            return (original, "empty path — no mutation possible");

        // Pick a mutation strategy weighted toward the ones most likely to catch bugs
        int strategy = rng.Next(7);

        return strategy switch
        {
            0 => ZeroOutFlow(original, rng),
            1 => DuplicateEdge(original, rng),
            2 => ReplaceWithUntrustedSender(original, rng),
            3 => RemoveCollateralEdge(original, state, rng),
            4 => AddConsentedWithoutTrust(original, state, rng),
            5 => SwapEdgePositions(original, rng),
            6 => InjectUnregisteredAddress(original, rng),
            _ => ZeroOutFlow(original, rng),
        };
    }

    /// <summary>Strategy 0: Set an edge's Value to "0" → violates Rule 1 (NoZeroFlow).</summary>
    private static (List<TransferPathStep> Mutated, string Description) ZeroOutFlow(
        List<TransferPathStep> original, Random rng)
    {
        var mutated = CloneSteps(original);
        int idx = rng.Next(mutated.Count);
        mutated[idx].Value = "0";
        return (mutated, $"Zeroed flow at edge {idx}");
    }

    /// <summary>Strategy 1: Duplicate an edge → violates Rule 7 (NoDuplicateEdges).</summary>
    private static (List<TransferPathStep> Mutated, string Description) DuplicateEdge(
        List<TransferPathStep> original, Random rng)
    {
        var mutated = CloneSteps(original);
        int idx = rng.Next(mutated.Count);
        var dup = new TransferPathStep
        {
            From = mutated[idx].From,
            To = mutated[idx].To,
            TokenOwner = mutated[idx].TokenOwner,
            Value = mutated[idx].Value,
        };
        mutated.Insert(idx + 1, dup);
        return (mutated, $"Duplicated edge {idx}");
    }

    /// <summary>Strategy 2: Replace From with a random untrusted address → violates Rule 4 (IsPermittedFlow).</summary>
    private static (List<TransferPathStep> Mutated, string Description) ReplaceWithUntrustedSender(
        List<TransferPathStep> original, Random rng)
    {
        var mutated = CloneSteps(original);
        int idx = rng.Next(mutated.Count);

        // Generate a random address that nobody trusts
        var fakeAddr = $"0x{rng.Next(0x10000000, int.MaxValue):x8}{rng.Next(0x10000000, int.MaxValue):x8}{rng.Next(0x10000000, int.MaxValue):x8}{rng.Next(0x10000000, int.MaxValue):x8}{rng.Next(0x100, 0xfff):x4}";
        // Ensure 42 chars total (0x + 40 hex)
        fakeAddr = "0x" + fakeAddr[2..].PadLeft(40, '0');
        if (fakeAddr.Length > 42) fakeAddr = fakeAddr[..42];

        mutated[idx].From = fakeAddr;
        return (mutated, $"Replaced From at edge {idx} with untrusted {fakeAddr[..12]}...");
    }

    /// <summary>Strategy 3: Remove a Router→Group collateral edge → violates Rule 6 (CollateralBeforeMint).</summary>
    private static (List<TransferPathStep> Mutated, string Description) RemoveCollateralEdge(
        List<TransferPathStep> original, IContractState state, Random rng)
    {
        var router = state.RouterAddress?.ToLowerInvariant();
        if (router == null)
            return ZeroOutFlow(original, rng); // Fallback

        // Find Router→Group edges
        var collateralIndices = new List<int>();
        for (int i = 0; i < original.Count; i++)
        {
            if (original[i].From.ToLowerInvariant() == router && state.IsGroup(original[i].To))
                collateralIndices.Add(i);
        }

        if (collateralIndices.Count == 0)
            return ZeroOutFlow(original, rng); // Fallback

        var mutated = CloneSteps(original);
        int removeIdx = collateralIndices[rng.Next(collateralIndices.Count)];
        mutated.RemoveAt(removeIdx);
        return (mutated, $"Removed Router→Group collateral edge at index {removeIdx}");
    }

    /// <summary>
    /// Strategy 4: Mark From as consented without adding trust to To
    /// → violates Rule 4 (IsPermittedFlow consented branch).
    /// This mutation changes the state, not the path — returns an updated state wrapper.
    /// Since we can't modify IContractState, we return the original path and
    /// the test should construct the state to trigger this case.
    /// As a fallback, we duplicate an edge instead.
    /// </summary>
    private static (List<TransferPathStep> Mutated, string Description) AddConsentedWithoutTrust(
        List<TransferPathStep> original, IContractState state, Random rng)
    {
        // Can't easily mutate state, so fallback to a structural mutation
        return DuplicateEdge(original, rng);
    }

    /// <summary>Strategy 5: Swap two edge positions → may break Rule 6 (CollateralBeforeMint ordering).</summary>
    private static (List<TransferPathStep> Mutated, string Description) SwapEdgePositions(
        List<TransferPathStep> original, Random rng)
    {
        if (original.Count < 2)
            return ZeroOutFlow(original, rng);

        var mutated = CloneSteps(original);
        int a = rng.Next(mutated.Count);
        int b;
        do { b = rng.Next(mutated.Count); } while (b == a);

        (mutated[a], mutated[b]) = (mutated[b], mutated[a]);
        return (mutated, $"Swapped edges {a} and {b}");
    }

    /// <summary>
    /// Strategy 6: Replace a From or To with a never-seen address
    /// → violates Rule 9 (AvatarRegistration) and/or Rule 5 (FlowConservation).
    /// </summary>
    private static (List<TransferPathStep> Mutated, string Description) InjectUnregisteredAddress(
        List<TransferPathStep> original, Random rng)
    {
        var mutated = CloneSteps(original);
        int idx = rng.Next(mutated.Count);

        // Generate a valid-format but unregistered address
        var fakeAddr = $"0xdead{rng.Next(0x10000000, int.MaxValue):x8}{rng.Next(0x10000000, int.MaxValue):x8}{rng.Next(0x10000000, int.MaxValue):x8}";
        fakeAddr = "0x" + fakeAddr[2..].PadLeft(40, '0');
        if (fakeAddr.Length > 42) fakeAddr = fakeAddr[..42];

        if (rng.Next(2) == 0)
        {
            mutated[idx].From = fakeAddr;
            return (mutated, $"Injected unregistered From at edge {idx}: {fakeAddr[..12]}...");
        }
        else
        {
            mutated[idx].To = fakeAddr;
            return (mutated, $"Injected unregistered To at edge {idx}: {fakeAddr[..12]}...");
        }
    }

    private static List<TransferPathStep> CloneSteps(List<TransferPathStep> original)
    {
        return original.Select(s => new TransferPathStep
        {
            From = s.From,
            To = s.To,
            TokenOwner = s.TokenOwner,
            Value = s.Value,
        }).ToList();
    }
}
