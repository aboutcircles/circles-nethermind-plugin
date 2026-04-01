namespace Circles.Pathfinder.Validation;

/// <summary>
/// Abstraction over the on-chain state needed by HubContractValidator.
/// Each method mirrors a Hub.sol view/mapping so the validator can check
/// contract rules without depending on CapacityGraph internals.
/// </summary>
public interface IContractState
{
    /// <summary>Hub.isTrusted(truster, circlesId) — does truster accept circlesId's token?</summary>
    bool IsTrusted(string truster, string circlesId);

    /// <summary>Hub.advancedUsageFlags(address) — has the address opted into consented flow?</summary>
    bool HasAdvancedUsageFlags(string address);

    /// <summary>Is the address a registered group in the Hub?</summary>
    bool IsGroup(string address);

    /// <summary>The StandardRouter contract address (null if not set).</summary>
    string? RouterAddress { get; }

    /// <summary>
    /// Hub.avatars(address) != address(0) — is the address a registered Circles avatar?
    /// Hub.sol:794-805 checks this for ALL flow vertices; error codes 0x24/0x25.
    /// </summary>
    bool IsRegistered(string address);
}
