namespace Circles.Pathfinder.DTOs;

public class FlowRequest
{
    public string? Source { get; set; }
    public string? Sink { get; set; }
    public string? TargetFlow { get; set; }
    public List<string>? ToTokens { get; set; }
    public List<string>? FromTokens { get; set; }
    public List<string>? ExcludedFromTokens { get; set; }
    public List<string>? ExcludedToTokens { get; set; }
    public bool? WithWrap { get; set; }
    public List<SimulatedBalance>? SimulatedBalances { get; set; }
    public List<SimulatedTrust>? SimulatedTrusts { get; set; }
    public int? MaxTransfers { get; set; }

    /// <summary>
    /// When true, enforces 96 CRC quantization for sink-bound transfers (invitation module).
    /// Each sink-bound transfer will be exactly N × 96 CRC.
    /// The number of invites is derived from targetFlow: invites = targetFlow / 96 CRC.
    /// Use max uint256 targetFlow to discover all possible invites.
    /// </summary>
    public bool? QuantizedMode { get; set; }
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