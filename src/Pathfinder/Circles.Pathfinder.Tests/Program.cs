using BenchmarkDotNet.Running;
using Circles.Pathfinder.Tests.Benchmarks;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Entry point for BenchmarkDotNet runner (Release builds only).
/// Uses a named class to avoid shadowing the Host's Program class,
/// which WebApplicationFactory&lt;Program&gt; needs to resolve.
/// </summary>
internal class BenchmarkEntryPoint
{
    static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(GraphLoadBenchmarks).Assembly).Run(args);
}
