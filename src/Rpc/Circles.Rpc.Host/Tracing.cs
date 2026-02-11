using System.Diagnostics;

namespace Circles.Rpc.Host;

public static class Tracing
{
    public const string Name = "Circles.Rpc.Host";
    public static readonly ActivitySource Source = new(Name);
}
