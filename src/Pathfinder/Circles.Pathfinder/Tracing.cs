using System.Diagnostics;

namespace Circles.Pathfinder;

/// <summary>
/// Central ActivitySource for custom spans.
/// </summary>
public static class Tracing
{
    public const string Name = "Circles.Pathfinder";
    public static readonly ActivitySource Source = new(Name);
}
