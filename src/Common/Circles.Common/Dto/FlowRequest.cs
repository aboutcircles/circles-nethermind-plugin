using System.Text.Json.Serialization;

namespace Circles.Common.Dto;

/// <summary>
/// Request body for path computation through the Circles trust network.
/// Contains source/sink addresses, target amount, and optional filters for tokens, wrapping, quantization, simulation, and debugging.
/// </summary>
public class FlowRequest
{
    /// <summary>
    /// Sender address (0x-prefixed, 40 hex chars). Must be a registered Circles V2 avatar.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Receiver address (0x-prefixed, 40 hex chars). Must be a registered Circles V2 avatar.
    /// </summary>
    [JsonPropertyName("sink")]
    public string? Sink { get; set; }

    /// <summary>
    /// Amount to transfer in CRC wei (1 CRC = 10^18 wei). Use max uint256 ("115792089237316195423570985008687907853269984665640564039457584007913129639935") to discover maximum possible flow.
    /// </summary>
    [JsonPropertyName("targetFlow")]
    public string? TargetFlow { get; set; }

    /// <summary>
    /// Restrict which tokens the sink can receive. Array of token-owner addresses. If omitted, all trusted tokens are accepted.
    /// </summary>
    [JsonPropertyName("toTokens")]
    public List<string>? ToTokens { get; set; }

    /// <summary>
    /// Restrict which tokens the source can send. Array of token-owner addresses. If omitted, all held tokens are used.
    /// </summary>
    [JsonPropertyName("fromTokens")]
    public List<string>? FromTokens { get; set; }

    /// <summary>
    /// Exclude specific tokens from the source side. Array of token-owner addresses.
    /// </summary>
    [JsonPropertyName("excludedFromTokens")]
    public List<string>? ExcludedFromTokens { get; set; }

    /// <summary>
    /// Exclude specific tokens from the sink side. Array of token-owner addresses.
    /// </summary>
    [JsonPropertyName("excludedToTokens")]
    public List<string>? ExcludedToTokens { get; set; }

    /// <summary>
    /// When true, includes ERC-20 wrapper token paths in addition to native ERC-1155 paths.
    /// </summary>
    [JsonPropertyName("withWrap")]
    public bool? WithWrap { get; set; }

    /// <summary>
    /// Hypothetical token balances to inject into the graph before path computation.
    /// Useful for testing "what if" scenarios without on-chain state changes.
    /// </summary>
    [JsonPropertyName("simulatedBalances")]
    public List<SimulatedBalance>? SimulatedBalances { get; set; }

    /// <summary>
    /// Hypothetical trust relations to inject into the graph before path computation.
    /// </summary>
    [JsonPropertyName("simulatedTrusts")]
    public List<SimulatedTrust>? SimulatedTrusts { get; set; }

    /// <summary>
    /// Addresses to treat as having consented to advanced usage (CRC-1155 operator approval).
    /// Affects which intermediate transfer paths are valid.
    /// </summary>
    [JsonPropertyName("simulatedConsentedAvatars")]
    public List<string>? SimulatedConsentedAvatars { get; set; }

    /// <summary>
    /// Maximum number of transfer steps in the result. Limits path complexity for on-chain gas cost control.
    /// </summary>
    [JsonPropertyName("maxTransfers")]
    public int? MaxTransfers { get; set; }

    /// <summary>
    /// When true, enforces 96 CRC quantization for sink-bound transfers (invitation module).
    /// Each sink-bound transfer will be exactly N × 96 CRC.
    /// The number of invites is derived from targetFlow: invites = targetFlow / 96 CRC.
    /// Use max uint256 targetFlow to discover all possible invites.
    /// </summary>
    [JsonPropertyName("quantizedMode")]
    public bool? QuantizedMode { get; set; }

    /// <summary>
    /// When true, includes debug information showing all transformation stages:
    /// rawPaths (from solver), collapsed (pools removed), routerInserted (group mints), sorted (final order).
    /// </summary>
    [JsonPropertyName("debugShowIntermediateSteps")]
    public bool? DebugShowIntermediateSteps { get; set; }
}

/// <summary>
/// A hypothetical token balance to inject into the trust graph for simulation.
/// </summary>
public class SimulatedBalance
{
    /// <summary>
    /// Holder address — the avatar that holds the tokens (0x-prefixed, any case accepted).
    /// </summary>
    public string Holder { get; set; } = string.Empty;

    /// <summary>
    /// Token identifier — the token-owner avatar address, or ERC-20 wrapper address.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Balance amount as uint256 string in CRC wei (1 CRC = 10^18 wei). Example: "96000000000000000000" = 96 CRC.
    /// </summary>
    public string Amount { get; set; } = "0";

    /// <summary>
    /// When true, treat this as an ERC-20 wrapped token balance instead of native ERC-1155.
    /// </summary>
    public bool? IsWrapped { get; set; }

    /// <summary>
    /// When true, this balance is not subject to demurrage decay — it remains constant regardless of time.
    /// </summary>
    public bool? IsStatic { get; set; }
}

/// <summary>
/// A hypothetical trust relation to inject into the trust graph for simulation.
/// </summary>
public class SimulatedTrust
{
    /// <summary>
    /// The address that grants trust (0x-prefixed, 40 hex chars).
    /// </summary>
    public string Truster { get; set; } = string.Empty;

    /// <summary>
    /// The address that receives trust (0x-prefixed, 40 hex chars).
    /// </summary>
    public string Trustee { get; set; } = string.Empty;
}
