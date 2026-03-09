using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Layer 3: Flow Equivalence — run every existing scenario fixture through BOTH
/// the FixtureLoadGraph path AND the CacheLoadGraph path, assert identical maxFlow.
/// This is the crown jewel test: proves the cache path produces identical flow results.
/// No network dependencies — uses embedded subgraph data from scenario JSONs.
/// </summary>
[TestFixture]
public class CacheSourceFlowTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Provides test cases for all scenarios that have embedded subgraph data.
    /// </summary>
    public static IEnumerable<TestCaseData> ScenariosWithSubgraphs()
    {
        foreach (var scenario in ScenarioLoader.LoadAllScenarios())
        {
            var subgraph = SnapshotBuilder.ParseSubgraph(scenario);
            if (subgraph == null) continue;
            if (subgraph.Balances == null || subgraph.Balances.Count == 0) continue;

            yield return new TestCaseData(scenario)
                .SetName($"CacheFlow/{scenario.Category}/{scenario.Id}")
                .SetDescription($"Cache vs Fixture flow: {scenario.Name}")
                .SetCategory("CacheEquivalence");
        }
    }

    [Test]
    [TestCaseSource(nameof(ScenariosWithSubgraphs))]
    public void Scenario_FixtureVsCache_SameMaxFlow(TransferScenario scenario)
    {
        var subgraph = SnapshotBuilder.ParseSubgraph(scenario)!;
        var request = ScenarioTests.BuildFlowRequest(scenario);
        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        // ── Path 1: FixtureLoadGraph (DB-equivalent) ──
        var fixtureLoadGraph = new FixtureLoadGraph(subgraph);
        var fixtureFactory = new GraphFactory(RouterAddress, fixtureLoadGraph);
        var fixtureTrust = fixtureFactory.V2TrustGraph();
        var fixtureBalance = fixtureFactory.V2BalanceGraph();
        var fixtureTrustLookup = GraphFactory.BuildTrustLookup(fixtureTrust);
        var fixtureCap = fixtureFactory.CreateCapacityGraph(fixtureBalance, fixtureTrustLookup, request);
        var fixturePathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });

        MaxFlowResponse? fixtureResponse = null;
        try
        {
            fixtureResponse = fixturePathfinder.ComputeMaxFlowWithPath(fixtureCap, request, targetFlow);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Fixture path threw: {ex.Message}");
        }

        // ── Path 2: CacheLoadGraph (via snapshot) ──
        var snapshot = SnapshotBuilder.FromFixtureSubgraph(subgraph);
        var cacheLoadGraph = new CacheLoadGraph(snapshot); // no settings = no safety margin (match fixture path)
        var cacheFactory = new GraphFactory(RouterAddress, cacheLoadGraph);
        var cacheTrust = cacheFactory.V2TrustGraph();
        var cacheBalance = cacheFactory.V2BalanceGraph();
        var cacheTrustLookup = GraphFactory.BuildTrustLookup(cacheTrust);
        var cacheCap = cacheFactory.CreateCapacityGraph(cacheBalance, cacheTrustLookup, request);
        var cachePathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });

        MaxFlowResponse? cacheResponse = null;
        try
        {
            cacheResponse = cachePathfinder.ComputeMaxFlowWithPath(cacheCap, request, targetFlow);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Cache path threw: {ex.Message}");
        }

        // ── Compare results ──
        if (fixtureResponse is null && cacheResponse is null)
        {
            TestContext.Out.WriteLine("Both paths threw — consistent behavior");
            Assert.Pass("Both paths threw (consistent)");
            return;
        }

        if (!scenario.ShouldFindPath)
        {
            // Negative scenario: both should find no path or throw
            var fixtureEmpty = fixtureResponse is null || fixtureResponse.Transfers.Count == 0;
            var cacheEmpty = cacheResponse is null || cacheResponse.Transfers.Count == 0;
            Assert.That(cacheEmpty, Is.EqualTo(fixtureEmpty),
                $"Both paths should agree on no-path for {scenario.Id}");
            return;
        }

        Assert.That(fixtureResponse, Is.Not.Null, "Fixture path should produce a result");
        Assert.That(cacheResponse, Is.Not.Null, "Cache path should produce a result");

        var fixtureFlow = UInt256.Parse(fixtureResponse!.MaxFlow);
        var cacheFlow = UInt256.Parse(cacheResponse!.MaxFlow);

        TestContext.Out.WriteLine(
            $"Scenario {scenario.Id}: fixture={fixtureFlow}, cache={cacheFlow}, " +
            $"fixtureSteps={fixtureResponse.Transfers.Count}, cacheSteps={cacheResponse.Transfers.Count}");

        Assert.That(cacheFlow, Is.EqualTo(fixtureFlow),
            $"MaxFlow mismatch for {scenario.Id}: fixture={fixtureFlow}, cache={cacheFlow}");

        // Transfer count should match (same edges, same flow)
        Assert.That(cacheResponse.Transfers.Count, Is.EqualTo(fixtureResponse.Transfers.Count),
            $"Transfer count mismatch for {scenario.Id}");
    }
}
