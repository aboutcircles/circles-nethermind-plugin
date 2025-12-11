using System.Text.Json.Serialization;

namespace Circles.Index.Common.Dto;

public class FlowRequest
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sink")]
    public string? Sink { get; set; }

    [JsonPropertyName("targetFlow")]
    public string? TargetFlow { get; set; }

    [JsonPropertyName("toTokens")]
    public List<string>? ToTokens { get; set; }

    [JsonPropertyName("fromTokens")]
    public List<string>? FromTokens { get; set; }

    [JsonPropertyName("excludedFromTokens")]
    public List<string>? ExcludedFromTokens { get; set; }

    [JsonPropertyName("excludedToTokens")]
    public List<string>? ExcludedToTokens { get; set; }

    [JsonPropertyName("withWrap")]
    public bool? WithWrap { get; set; }

    [JsonPropertyName("simulatedBalances")]
    public List<SimulatedBalance>? SimulatedBalances { get; set; }

    [JsonPropertyName("simulatedTrusts")]
    public List<SimulatedTrust>? SimulatedTrusts { get; set; }

    [JsonPropertyName("maxTransfers")]
    public int? MaxTransfers { get; set; }
}

public class SimulatedBalance
{
    public string Holder { get; set; } = string.Empty; // avatar address (lower/any case accepted)
    public string Token { get; set; } = string.Empty; // token-owner avatar (or wrapper) address
    public string Amount { get; set; } = "0"; // uint256 as string
    public bool? IsWrapped { get; set; } // optional, default false
    public bool? IsStatic { get; set; } // optional, default false
}

public class SimulatedTrust
{
    public string Truster { get; set; } = string.Empty;
    public string Trustee { get; set; } = string.Empty;
}
