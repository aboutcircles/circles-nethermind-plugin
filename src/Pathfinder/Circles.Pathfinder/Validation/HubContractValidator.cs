using System.Globalization;
using System.Numerics;
using Circles.Common.Dto;

namespace Circles.Pathfinder.Validation;

/// <summary>
/// Validates a pathfinder-produced TransferPathStep[] against Hub.sol rules.
/// Each rule is independently derived from the Solidity source — NOT copied from V2Pathfinder.
/// This is the "second reader" that catches bugs in the pathfinder's own rule implementation.
/// </summary>
public static class HubContractValidator
{
    /// <summary>
    /// Run all 11 validation rules against the transfer path.
    /// </summary>
    /// <param name="steps">The transfer steps produced by the pathfinder.</param>
    /// <param name="source">The sender address.</param>
    /// <param name="sink">The receiver address.</param>
    /// <param name="state">Contract state (trust, consent, groups).</param>
    /// <returns>Validation result with all violations found.</returns>
    public static ValidationResult Validate(
        IReadOnlyList<TransferPathStep> steps,
        string source,
        string sink,
        IContractState state)
    {
        // Filter out display-only self-loops (e.g., quantized sink aggregation Sink→Sink)
        var filtered = FilterDisplayOnlyEdges(steps, sink);

        var violations = new List<ValidationViolation>();

        ValidateNoZeroFlowEdges(filtered, violations);
        ValidateAddressFormat(filtered, violations);
        ValidateVertexOrdering(filtered, source, sink, violations);
        ValidateAvatarRegistration(filtered, source, sink, state, violations);
        ValidateGroupRegistration(filtered, state, violations);
        ValidateTokenIdValidity(filtered, state, violations);
        ValidateIsPermittedFlow(filtered, state, violations);
        ValidateFlowConservation(filtered, source, sink, violations);
        ValidateCollateralBeforeMint(filtered, state, violations);
        ValidateNoDuplicateEdges(filtered, violations);
        ValidateNoSelfTransfers(filtered, sink, violations);

        bool isValid = !violations.Any(v => v.Severity == "error");
        return new ValidationResult(isValid, violations);
    }

    /// <summary>
    /// Filter out Sink→Sink self-loop edges which are display-only aggregation
    /// added by AddSinkSelfLoopAggregation() in quantized mode.
    /// These are NOT sent to the contract.
    /// </summary>
    private static IReadOnlyList<TransferPathStep> FilterDisplayOnlyEdges(
        IReadOnlyList<TransferPathStep> steps, string sink)
    {
        var sinkLower = sink.ToLowerInvariant();
        var result = new List<TransferPathStep>(steps.Count);
        foreach (var step in steps)
        {
            bool isSelfLoop = step.From.ToLowerInvariant() == sinkLower
                              && step.To.ToLowerInvariant() == sinkLower;
            if (!isSelfLoop)
                result.Add(step);
        }
        return result;
    }

    // ────────────────────────────────────────────
    // Rule 1: No zero-flow edges
    // Hub.sol rejects zero amounts in the flow matrix.
    // ────────────────────────────────────────────
    internal static void ValidateNoZeroFlowEdges(
        IReadOnlyList<TransferPathStep> steps,
        List<ValidationViolation> violations)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var val = steps[i].Value;
            if (string.IsNullOrEmpty(val) || val == "0")
            {
                violations.Add(new ValidationViolation(
                    "NoZeroFlow",
                    $"Edge {i} has zero or empty flow value",
                    i, "error"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 2: Valid Ethereum address format
    // Invalid addresses would cause revert on uint160 cast.
    // ────────────────────────────────────────────
    internal static void ValidateAddressFormat(
        IReadOnlyList<TransferPathStep> steps,
        List<ValidationViolation> violations)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            CheckAddress(step.From, "From", i, violations);
            CheckAddress(step.To, "To", i, violations);
            CheckAddress(step.TokenOwner, "TokenOwner", i, violations);
        }
    }

    private static void CheckAddress(string addr, string field, int index, List<ValidationViolation> violations)
    {
        if (string.IsNullOrEmpty(addr))
        {
            violations.Add(new ValidationViolation(
                "AddressFormat", $"Edge {index}: {field} is null or empty", index, "error"));
            return;
        }

        if (!addr.StartsWith("0x") || addr.Length != 42)
        {
            violations.Add(new ValidationViolation(
                "AddressFormat", $"Edge {index}: {field} '{addr}' is not a valid 20-byte hex address", index, "error"));
            return;
        }

        // Verify all characters after 0x are valid hex
        if (!IsValidHex(addr.AsSpan(2)))
        {
            violations.Add(new ValidationViolation(
                "AddressFormat", $"Edge {index}: {field} '{addr}' contains non-hex characters", index, "error"));
        }
    }

    private static bool IsValidHex(ReadOnlySpan<char> hex)
    {
        foreach (char c in hex)
        {
            bool valid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!valid) return false;
        }
        return true;
    }

    // ────────────────────────────────────────────
    // Rule 3: Vertex ordering (uint160-sorted)
    // Hub.sol:_flowVertices must be sorted ascending by uint160 value.
    // The pathfinder's BuildOperateFlowMatrixCall sorts vertices, but
    // we check that the transfer path is consistent with such ordering.
    // ────────────────────────────────────────────
    internal static void ValidateVertexOrdering(
        IReadOnlyList<TransferPathStep> steps,
        string source,
        string sink,
        List<ValidationViolation> violations)
    {
        // Collect all unique addresses
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            source.ToLowerInvariant(),
            sink.ToLowerInvariant()
        };

        foreach (var step in steps)
        {
            addresses.Add(step.From.ToLowerInvariant());
            addresses.Add(step.To.ToLowerInvariant());
            addresses.Add(step.TokenOwner.ToLowerInvariant());
        }

        // Verify they can all be parsed as uint160 and sorted.
        // Addresses that fail hex parsing are already caught by Rule 2 (AddressFormat).
        var parsed = new List<(string Address, BigInteger Value)>();
        foreach (var addr in addresses)
        {
            if (!addr.StartsWith("0x") || addr.Length != 42) continue;
            if (BigInteger.TryParse("0" + addr[2..], NumberStyles.HexNumber, null, out var val))
            {
                parsed.Add((addr, val));
            }
            // else: non-hex chars already reported by ValidateAddressFormat
        }

        // Check for duplicate addresses (same uint160 value, different casing shouldn't happen
        // since we lowercased, but check for actual duplicates)
        var sorted = parsed.OrderBy(p => p.Value).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Value == sorted[i - 1].Value && sorted[i].Address != sorted[i - 1].Address)
            {
                violations.Add(new ValidationViolation(
                    "VertexOrdering",
                    $"Duplicate uint160 value for different addresses: {sorted[i].Address} and {sorted[i - 1].Address}",
                    null, "error"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 9: Avatar registration
    // Hub.sol:794-805 checks avatars[addr] != address(0) for ALL
    // flow vertices. Error codes 0x24 (non-last) and 0x25 (last).
    // The pathfinder only adds registered avatars to the graph, but
    // this rule independently verifies the output.
    // ────────────────────────────────────────────
    internal static void ValidateAvatarRegistration(
        IReadOnlyList<TransferPathStep> steps,
        string source,
        string sink,
        IContractState state,
        List<ValidationViolation> violations)
    {
        var router = state.RouterAddress?.ToLowerInvariant();

        // Collect all unique vertex addresses (From and To only — TokenOwner checked by Rule 11)
        var vertices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            source.ToLowerInvariant(),
            sink.ToLowerInvariant()
        };

        foreach (var step in steps)
        {
            vertices.Add(step.From.ToLowerInvariant());
            vertices.Add(step.To.ToLowerInvariant());
        }

        foreach (var vertex in vertices)
        {
            // Router is a contract address — always registered on-chain
            if (router != null && vertex == router)
                continue;

            // Skip malformed addresses (already caught by Rule 2)
            if (!vertex.StartsWith("0x") || vertex.Length != 42)
                continue;

            if (!state.IsRegistered(vertex))
            {
                violations.Add(new ValidationViolation(
                    "AvatarRegistration",
                    $"Vertex '{vertex[..Math.Min(10, vertex.Length)]}' is not a registered avatar (Hub.sol error 0x24/0x25)",
                    null, "error"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 10: Group registration for mint edges
    // Hub.sol:707 checks isGroup(_group) inside _groupMint.
    // Error code 0x40 (CirclesHubGroupIsNotRegistered).
    // When _effectPathTransfers sees an edge where To is a group,
    // it calls _groupMint — which reverts if isGroup(to) is false.
    // This rule catches edges where From is expected to be a group
    // (because it's minting its own token) but isn't registered.
    // ────────────────────────────────────────────
    internal static void ValidateGroupRegistration(
        IReadOnlyList<TransferPathStep> steps,
        IContractState state,
        List<ValidationViolation> violations)
    {
        var router = state.RouterAddress?.ToLowerInvariant();
        if (router == null) return; // No router means no group minting in this path

        // Detect group-mint pattern: an address X is in a group mint flow when:
        //   1. Router→X edge exists (collateral deposit)
        //   2. X→Avatar edge exists where TokenOwner == X (X mints its own token)
        // Hub.sol:893 calls _groupMint(sender, to, to, ...) when isGroup(to).
        // Hub.sol:707 reverts with 0x40 if !isGroup(_group).

        var receivesFromRouter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (step.From.ToLowerInvariant() == router)
                receivesFromRouter.Add(step.To.ToLowerInvariant());
        }

        var mintCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var from = step.From.ToLowerInvariant();
            if (step.TokenOwner.ToLowerInvariant() == from && from != router)
                mintCandidates.Add(from);
        }

        // Addresses in BOTH sets are in the group-mint pattern — verify IsGroup
        foreach (var candidate in mintCandidates)
        {
            if (!receivesFromRouter.Contains(candidate))
                continue;

            if (!state.IsGroup(candidate))
            {
                violations.Add(new ValidationViolation(
                    "GroupRegistration",
                    $"Address '{candidate[..Math.Min(10, candidate.Length)]}' is in group-mint pattern (Router→X, X→Avatar with own token) but is not a registered group (Hub.sol error 0x40)",
                    null, "error"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 11: Token ID validity
    // Hub.sol:718 _validateAddressFromId(_collateral[i], 1)
    // checks that collateral token IDs encode valid registered
    // avatar addresses. For personal Circles, tokenId == avatarAddress,
    // so TokenOwner must be a registered avatar.
    // ────────────────────────────────────────────
    internal static void ValidateTokenIdValidity(
        IReadOnlyList<TransferPathStep> steps,
        IContractState state,
        List<ValidationViolation> violations)
    {
        var router = state.RouterAddress?.ToLowerInvariant();
        var checked_ = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < steps.Count; i++)
        {
            var tokenOwner = steps[i].TokenOwner.ToLowerInvariant();

            // Skip already checked, malformed (caught by Rule 2), and router
            if (!checked_.Add(tokenOwner))
                continue;
            if (!tokenOwner.StartsWith("0x") || tokenOwner.Length != 42)
                continue;
            if (router != null && tokenOwner == router)
                continue;

            if (!state.IsRegistered(tokenOwner))
            {
                violations.Add(new ValidationViolation(
                    "TokenIdValidity",
                    $"Edge {i}: TokenOwner '{tokenOwner[..Math.Min(10, tokenOwner.Length)]}' is not a registered avatar — invalid token ID (Hub.sol:718 _validateAddressFromId)",
                    i, "error"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 4: isPermittedFlow — Hub.sol:668-676
    //
    //   function isPermittedFlow(address _from, address _to, address _circlesAvatar)
    //       returns (bool) {
    //       if (!advancedUsageFlags[_from]) return isTrusted(_to, _circlesAvatar);
    //       return isTrusted(_from, _to) && advancedUsageFlags[_to];
    //   }
    //
    // Router edge handling (Hub.sol:665, Hub.sol:723):
    //   Router→Group: internal group mint — Router is _sender, isPermittedFlow N/A.
    //   Avatar→Router: Hub.sol:665 checks trustMarkers[Router][tokenOwner].expiry.
    //   Router→NonGroup: fall through to normal validation (fail-closed).
    // ────────────────────────────────────────────
    internal static void ValidateIsPermittedFlow(
        IReadOnlyList<TransferPathStep> steps,
        IContractState state,
        List<ValidationViolation> violations)
    {
        var router = state.RouterAddress?.ToLowerInvariant();

        for (int i = 0; i < steps.Count; i++)
        {
            var from = steps[i].From.ToLowerInvariant();
            var to = steps[i].To.ToLowerInvariant();
            var tokenOwner = steps[i].TokenOwner.ToLowerInvariant();

            // Router→Group: internal group mint — Hub.sol uses Router as _sender,
            // isPermittedFlow does not apply (Hub.sol:723). Safe to skip.
            if (router != null && from == router && state.IsGroup(to))
                continue;

            // Avatar→Router: Hub.sol:665 checks trustMarkers[Router][tokenOwner].expiry
            // Router must trust the token owner for this transfer to succeed.
            if (router != null && to == router)
            {
                if (!state.IsTrusted(router, tokenOwner))
                {
                    violations.Add(new ValidationViolation(
                        "IsPermittedFlow",
                        $"Edge {i}: Avatar→Router — Router does not trust token owner '{tokenOwner[..Math.Min(10, tokenOwner.Length)]}'",
                        i, "error"));
                }
                continue;
            }

            if (!state.HasAdvancedUsageFlags(from))
            {
                // Standard mode: check isTrusted(_to, _circlesAvatar)
                // "To" must trust the token owner's token
                if (!state.IsTrusted(to, tokenOwner))
                {
                    violations.Add(new ValidationViolation(
                        "IsPermittedFlow",
                        $"Edge {i}: standard flow — '{to[..Math.Min(10, to.Length)]}' does not trust token owner '{tokenOwner[..Math.Min(10, tokenOwner.Length)]}'",
                        i, "error"));
                }
            }
            else
            {
                // Consented mode: check isTrusted(_from, _to) && advancedUsageFlags[_to]
                if (!state.IsTrusted(from, to))
                {
                    violations.Add(new ValidationViolation(
                        "IsPermittedFlow",
                        $"Edge {i}: consented flow — '{from[..Math.Min(10, from.Length)]}' does not trust '{to[..Math.Min(10, to.Length)]}'",
                        i, "error"));
                }

                if (!state.HasAdvancedUsageFlags(to))
                {
                    violations.Add(new ValidationViolation(
                        "IsPermittedFlow",
                        $"Edge {i}: consented flow — '{to[..Math.Min(10, to.Length)]}' does not have advancedUsageFlags enabled",
                        i, "error"));
                }
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 5: Flow conservation
    // At every intermediate vertex (not source, not sink),
    // total inbound flow must equal total outbound flow.
    // ────────────────────────────────────────────
    internal static void ValidateFlowConservation(
        IReadOnlyList<TransferPathStep> steps,
        string source,
        string sink,
        List<ValidationViolation> violations)
    {
        var src = source.ToLowerInvariant();
        var snk = sink.ToLowerInvariant();
        var netFlow = new Dictionary<string, BigInteger>();

        foreach (var step in steps)
        {
            var from = step.From.ToLowerInvariant();
            var to = step.To.ToLowerInvariant();

            if (!BigInteger.TryParse(step.Value, out var flow) || flow <= 0)
                continue; // Already caught by Rule 1

            netFlow.TryGetValue(from, out var fromNet);
            netFlow[from] = fromNet - flow;

            netFlow.TryGetValue(to, out var toNet);
            netFlow[to] = toNet + flow;
        }

        foreach (var (vertex, net) in netFlow)
        {
            if (vertex == src || vertex == snk)
                continue;

            if (net != 0)
            {
                violations.Add(new ValidationViolation(
                    "FlowConservation",
                    $"Intermediate vertex '{vertex[..Math.Min(10, vertex.Length)]}' has net flow {net} (should be 0)",
                    null, "error"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 6: Collateral before mint
    // Hub.sol:_groupMint requires all collateral deposited to a group
    // before any minting can occur. In the operateFlowMatrix call,
    // edges are processed sequentially, so:
    //   - Router→Group (collateral deposit) MUST precede Group→Avatar (mint)
    //   - At each Group→Avatar edge, cumulative inbound >= cumulative outbound
    // ────────────────────────────────────────────
    internal static void ValidateCollateralBeforeMint(
        IReadOnlyList<TransferPathStep> steps,
        IContractState state,
        List<ValidationViolation> violations)
    {
        var router = state.RouterAddress?.ToLowerInvariant();
        if (router == null) return; // No router means no group minting in this path

        // Track per-group: cumulative inbound from router, cumulative outbound to avatars
        var groupInbound = new Dictionary<string, BigInteger>();
        var groupOutbound = new Dictionary<string, BigInteger>();
        var groupsWithOutboundSeen = new HashSet<string>();

        for (int i = 0; i < steps.Count; i++)
        {
            var from = steps[i].From.ToLowerInvariant();
            var to = steps[i].To.ToLowerInvariant();

            if (!BigInteger.TryParse(steps[i].Value, out var flow) || flow <= 0)
                continue;

            // Router → Group (collateral deposit)
            if (from == router && state.IsGroup(to))
            {
                // Ordering check: no outbound should have been seen for this group yet
                if (groupsWithOutboundSeen.Contains(to))
                {
                    violations.Add(new ValidationViolation(
                        "CollateralBeforeMint",
                        $"Edge {i}: Router→Group collateral for '{to[..Math.Min(10, to.Length)]}' appears after Group→Avatar mint",
                        i, "error"));
                }

                groupInbound.TryGetValue(to, out var current);
                groupInbound[to] = current + flow;
            }
            // Group → Avatar (mint)
            else if (state.IsGroup(from) && from != router && to != router && !state.IsGroup(to))
            {
                groupsWithOutboundSeen.Add(from);

                groupOutbound.TryGetValue(from, out var currentOut);
                groupOutbound[from] = currentOut + flow;

                groupInbound.TryGetValue(from, out var inbound);
                if (inbound < groupOutbound[from])
                {
                    violations.Add(new ValidationViolation(
                        "CollateralBeforeMint",
                        $"Edge {i}: Group '{from[..Math.Min(10, from.Length)]}' has insufficient collateral. Inbound: {inbound}, required: {groupOutbound[from]}",
                        i, "error"));
                }
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 7: No duplicate edges (warning)
    // Duplicate (From, To, TokenOwner) tuples indicate an aggregation
    // bug in the pathfinder. The contract may still accept them but
    // they waste gas and indicate a logic error.
    // ────────────────────────────────────────────
    internal static void ValidateNoDuplicateEdges(
        IReadOnlyList<TransferPathStep> steps,
        List<ValidationViolation> violations)
    {
        var seen = new HashSet<(string, string, string)>();

        for (int i = 0; i < steps.Count; i++)
        {
            var key = (
                steps[i].From.ToLowerInvariant(),
                steps[i].To.ToLowerInvariant(),
                steps[i].TokenOwner.ToLowerInvariant());

            if (!seen.Add(key))
            {
                violations.Add(new ValidationViolation(
                    "NoDuplicateEdges",
                    $"Edge {i}: duplicate (From={key.Item1[..Math.Min(10, key.Item1.Length)]}, To={key.Item2[..Math.Min(10, key.Item2.Length)]}, Token={key.Item3[..Math.Min(10, key.Item3.Length)]})",
                    i, "warning"));
            }
        }
    }

    // ────────────────────────────────────────────
    // Rule 8: No self-transfers (warning)
    // An edge where From == To is meaningless (transfer to yourself).
    // Exception: quantized mode sink self-loops are already filtered
    // out before validation, so any remaining self-transfers are bugs.
    // ────────────────────────────────────────────
    internal static void ValidateNoSelfTransfers(
        IReadOnlyList<TransferPathStep> steps,
        string sink,
        List<ValidationViolation> violations)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var from = steps[i].From.ToLowerInvariant();
            var to = steps[i].To.ToLowerInvariant();

            if (from == to)
            {
                violations.Add(new ValidationViolation(
                    "NoSelfTransfers",
                    $"Edge {i}: self-transfer at '{from[..Math.Min(10, from.Length)]}'",
                    i, "warning"));
            }
        }
    }
}
