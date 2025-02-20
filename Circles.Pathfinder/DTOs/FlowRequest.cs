namespace Circles.Pathfinder.DTOs;

public class FlowRequest
{
    public string? Source { get; set; }
    public string? Sink { get; set; }
    public string? TargetFlow { get; set; }
    public List<string>? FromTokens { get; set; }
    public List<string>? ToTokens { get; set; }
}