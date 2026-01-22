using System.Text.Json.Serialization;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Embedded subgraph data for unit testing without external dependencies.
/// Contains the minimal trust/balance/group data needed for a specific source→sink path.
/// </summary>
public class FixtureSubgraph
{
    /// <summary>
    /// Trust relationships as [truster, trustee] pairs.
    /// </summary>
    [JsonPropertyName("trust")]
    public List<string[]>? Trust { get; set; }

    /// <summary>
    /// Balance entries as [holder, token, amount, isWrapped, isStatic] tuples.
    /// Amount is in WEI (18 decimals) as string.
    /// </summary>
    [JsonPropertyName("balances")]
    public List<BalanceEntry>? Balances { get; set; }

    /// <summary>
    /// List of group addresses in the subgraph.
    /// </summary>
    [JsonPropertyName("groups")]
    public List<string>? Groups { get; set; }

    /// <summary>
    /// Group trust relationships as [groupAddress, trustedToken] pairs.
    /// </summary>
    [JsonPropertyName("groupTrusts")]
    public List<string[]>? GroupTrusts { get; set; }

    /// <summary>
    /// Avatars with consented flow enabled.
    /// </summary>
    [JsonPropertyName("consentedAvatars")]
    public List<string>? ConsentedAvatars { get; set; }

    /// <summary>
    /// Stats about the subgraph extraction.
    /// </summary>
    [JsonPropertyName("stats")]
    public SubgraphStats? Stats { get; set; }
}

/// <summary>
/// Balance entry in the embedded subgraph.
/// </summary>
public class BalanceEntry
{
    [JsonPropertyName("holder")]
    public string Holder { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Balance amount in WEI (18 decimals) as string.
    /// </summary>
    [JsonPropertyName("amount")]
    public string Amount { get; set; } = "0";

    [JsonPropertyName("isWrapped")]
    public bool IsWrapped { get; set; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }
}

/// <summary>
/// Statistics about the extracted subgraph.
/// </summary>
public class SubgraphStats
{
    [JsonPropertyName("addressCount")]
    public int AddressCount { get; set; }

    [JsonPropertyName("trustEdges")]
    public int TrustEdges { get; set; }

    [JsonPropertyName("balanceEntries")]
    public int BalanceEntries { get; set; }

    [JsonPropertyName("groupCount")]
    public int GroupCount { get; set; }

    [JsonPropertyName("groupTrustCount")]
    public int GroupTrustCount { get; set; }

    [JsonPropertyName("consentedCount")]
    public int ConsentedCount { get; set; }
}

/// <summary>
/// Documentation for recreating the scenario if the original block becomes unavailable.
/// </summary>
public class ScenarioRequirements
{
    /// <summary>
    /// Description of what this scenario tests.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Requirements for the source address.
    /// </summary>
    [JsonPropertyName("sourceRequirements")]
    public List<string>? SourceRequirements { get; set; }

    /// <summary>
    /// Requirements for the sink address.
    /// </summary>
    [JsonPropertyName("sinkRequirements")]
    public List<string>? SinkRequirements { get; set; }

    /// <summary>
    /// Requirements for the path between source and sink.
    /// </summary>
    [JsonPropertyName("pathRequirements")]
    public List<string>? PathRequirements { get; set; }

    /// <summary>
    /// Instructions for recreating this scenario at a newer block.
    /// </summary>
    [JsonPropertyName("howToRecreate")]
    public string? HowToRecreate { get; set; }
}

/// <summary>
/// Expected properties of the path result (for validation).
/// </summary>
public class ExpectedPath
{
    [JsonPropertyName("minHops")]
    public int? MinHops { get; set; }

    [JsonPropertyName("maxHops")]
    public int? MaxHops { get; set; }

    [JsonPropertyName("routerInvolved")]
    public bool? RouterInvolved { get; set; }

    [JsonPropertyName("groupsMinted")]
    public List<string>? GroupsMinted { get; set; }
}
