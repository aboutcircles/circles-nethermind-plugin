using BenchmarkDotNet.Attributes;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests.Benchmarks;

/// <summary>
/// BenchmarkDotNet performance baselines for pathfinder graph construction.
/// Run: dotnet run -c Release -- --filter "*GraphLoad*"
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class GraphLoadBenchmarks
{
    private const string RouterAddr = "0xf1ff000000000000000000000000000000ffffff";

    private MockLoadGraph _mock10K = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_mock10K, _) = LargeGraphGenerator.Generate(avatarCount: 10_000, seed: 100);
    }

    [Benchmark(Description = "V2TrustGraph: 10K avatars, ~50K edges")]
    public TrustGraph V2TrustGraph_10K()
    {
        var factory = new GraphFactory(RouterAddr, _mock10K);
        return factory.V2TrustGraph();
    }

    [Benchmark(Description = "V2BalanceGraph: 10K avatars, ~30K balances")]
    public BalanceGraph V2BalanceGraph_10K()
    {
        var factory = new GraphFactory(RouterAddr, _mock10K);
        return factory.V2BalanceGraph();
    }

    [Benchmark(Description = "CreateCapacityGraph: 10K avatars")]
    public CapacityGraph CreateCapacityGraph_10K()
    {
        var factory = new GraphFactory(RouterAddr, _mock10K);
        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trust);
        return factory.CreateCapacityGraph(balance, trustLookup);
    }

    // 50K benchmarks commented out — too slow for local runs (~30 min).
    // Re-enable for CI on beefier hardware.
    //
    // private MockLoadGraph _mock50K = null!;
    // // Add to GlobalSetup: (_mock50K, _) = LargeGraphGenerator.Generate(avatarCount: 50_000, seed: 200);
    //
    // [Benchmark(Description = "V2TrustGraph: 50K avatars, ~250K edges")]
    // public TrustGraph V2TrustGraph_50K()
    // {
    //     var factory = new GraphFactory(RouterAddr, _mock50K);
    //     return factory.V2TrustGraph();
    // }
    //
    // [Benchmark(Description = "V2BalanceGraph: 50K avatars, ~150K balances")]
    // public BalanceGraph V2BalanceGraph_50K()
    // {
    //     var factory = new GraphFactory(RouterAddr, _mock50K);
    //     return factory.V2BalanceGraph();
    // }
}
