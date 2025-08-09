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
}

public class SimulatedBalance
{
    public string Holder { get; set; } = string.Empty; // avatar address (lower/any case accepted)
    public string Token { get; set; } = string.Empty;  // token-owner avatar (or wrapper) address
    public string Amount { get; set; } = "0";          // uint256 as string
    public bool? IsWrapped { get; set; }               // optional, default false
    public bool? IsStatic { get; set; }                // optional, default false
}