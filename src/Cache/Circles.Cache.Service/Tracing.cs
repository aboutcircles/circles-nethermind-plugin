using System.Diagnostics;

namespace Circles.Cache.Service;

public static class Tracing
{
    public const string Name = "Circles.Cache.Service";
    public static readonly ActivitySource Source = new(Name);
}
