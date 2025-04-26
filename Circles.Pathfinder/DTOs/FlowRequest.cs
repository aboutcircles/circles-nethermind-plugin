using System.Collections;

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
}