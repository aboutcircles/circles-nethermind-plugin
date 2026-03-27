namespace Circles.Pathfinder.Simulation;

/// <summary>
/// Classifies eth_call revert reasons into actionable categories.
/// "bug" = pathfinder produced invalid output (graph stale, logic error).
/// "input" = user request cannot succeed (sink can't receive ERC1155, etc.).
/// "unknown" = needs manual investigation.
/// </summary>
public static class RevertClassifier
{
    // Hub.sol Circles-specific error selectors (from circles-contracts-v2/src/errors/Errors.sol)
    // CirclesErrorAddressUintArgs(address,uint256,uint8) — type 1 (0x20-0x3F)
    private const string FlowEdgeIsNotPermitted = "0x5e418dba";
    // CirclesErrorOneAddressArg(address,uint8) — type 1 (0x20-0x3F)
    private const string AvatarMustBeRegistered = "0xc14c0700";
    // Hub.sol flow matrix error
    private const string NettedFlowMismatch = "0xb8c358de";
    // OpenZeppelin ERC1155 errors
    private const string ERC1155InsufficientBalance = "0x03dee4c5";
    private const string ERC1155InvalidReceiver = "0x57f447ce";

    public static (string Category, string Label) Classify(string? revertData)
    {
        if (string.IsNullOrEmpty(revertData))
            return ("unknown", "empty_revert");

        var lower = revertData.ToLowerInvariant();

        // Check for known error selectors in the revert data
        if (Contains(lower, FlowEdgeIsNotPermitted))
            return ("bug", "flow_edge_not_permitted");

        if (Contains(lower, AvatarMustBeRegistered))
            return ("bug", "avatar_not_registered");

        if (Contains(lower, NettedFlowMismatch))
            return ("bug", "netted_flow_mismatch");

        if (Contains(lower, ERC1155InsufficientBalance))
            return ("bug", "insufficient_balance");

        if (Contains(lower, ERC1155InvalidReceiver))
            return ("input", "invalid_receiver");

        // Generic revert string patterns
        if (lower.Contains("execution reverted"))
            return ("unknown", "generic_revert");

        return ("unknown", "unrecognized");
    }

    private static bool Contains(string data, string selector)
        => data.Contains(selector[2..]); // strip 0x prefix for matching
}
