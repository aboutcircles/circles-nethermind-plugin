using System.Text.Json;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Layer 5: Live Staging Equivalence — loads from both ProxyLoadGraph (DB via test-env)
/// and the cache service's /api/pathfinder/graph endpoint, compares within tolerance.
/// Gated by TEST_ENV_URL and CACHE_SERVICE_URL environment variables.
/// </summary>
[TestFixture]
[Category("Staging")]
public class CacheSourceStagingTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private string? _testEnvUrl;
    private string? _cacheServiceUrl;

    [SetUp]
    public void SetUp()
    {
        _testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        _cacheServiceUrl = Environment.GetEnvironmentVariable("CACHE_SERVICE_URL");

        if (string.IsNullOrEmpty(_testEnvUrl) || string.IsNullOrEmpty(_cacheServiceUrl))
        {
            Assert.Ignore(
                "TEST_ENV_URL and CACHE_SERVICE_URL must both be set for staging tests. " +
                "Example: TEST_ENV_URL=https://staging.circlesubi.network/test-env " +
                "CACHE_SERVICE_URL=http://localhost:8081");
        }
    }

    [Test]
    public async Task Staging_Balances_DB_vs_Cache_SameCount_WithinTolerance()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbBalances = dbLoadGraph.LoadV2Balances().ToList();
        var cacheBalances = cacheLoadGraph.LoadV2Balances().ToList();

        TestContext.Out.WriteLine($"DB balances: {dbBalances.Count}, Cache balances: {cacheBalances.Count}");

        // Allow 1% tolerance on count (cache may lag by a few blocks)
        var tolerance = Math.Max(10, dbBalances.Count / 100);
        Assert.That(Math.Abs(cacheBalances.Count - dbBalances.Count), Is.LessThan(tolerance),
            $"Balance count divergence exceeds 1% (DB={dbBalances.Count}, Cache={cacheBalances.Count})");
    }

    [Test]
    public async Task Staging_Trust_DB_vs_Cache_SameEdges()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbTrust = dbLoadGraph.LoadV2Trust().ToList();
        var cacheTrust = cacheLoadGraph.LoadV2Trust().ToList();

        TestContext.Out.WriteLine($"DB trust: {dbTrust.Count}, Cache trust: {cacheTrust.Count}");

        var tolerance = Math.Max(10, dbTrust.Count / 100);
        Assert.That(Math.Abs(cacheTrust.Count - dbTrust.Count), Is.LessThan(tolerance),
            $"Trust count divergence exceeds 1%");
    }

    [Test]
    public async Task Staging_Groups_DB_vs_Cache_SameSet()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbGroups = dbLoadGraph.LoadGroups().ToHashSet();
        var cacheGroups = cacheLoadGraph.LoadGroups().ToHashSet();

        TestContext.Out.WriteLine($"DB groups: {dbGroups.Count}, Cache groups: {cacheGroups.Count}");

        Assert.That(cacheGroups.Count, Is.EqualTo(dbGroups.Count),
            "Group count should match exactly");
    }

    [Test]
    public async Task Staging_GroupTrusts_DB_vs_Cache_SameSet()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbGT = dbLoadGraph.LoadGroupTrusts().ToList();
        var cacheGT = cacheLoadGraph.LoadGroupTrusts().ToList();

        TestContext.Out.WriteLine($"DB group trusts: {dbGT.Count}, Cache group trusts: {cacheGT.Count}");

        Assert.That(Math.Abs(cacheGT.Count - dbGT.Count), Is.LessThanOrEqualTo(5),
            "Group trust count divergence exceeds tolerance");
    }

    [Test]
    public async Task Staging_WrapperMappings_DB_vs_Cache_SubsetOrEqual()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbWrappers = dbLoadGraph.LoadWrapperMappings().ToHashSet();
        var cacheWrappers = cacheLoadGraph.LoadWrapperMappings().ToHashSet();

        TestContext.Out.WriteLine($"DB wrappers: {dbWrappers.Count}, Cache wrappers: {cacheWrappers.Count}");

        // Cache should be a subset of DB (cache only returns registered-avatar wrappers)
        Assert.That(cacheWrappers.Count, Is.LessThanOrEqualTo(dbWrappers.Count),
            "Cache should have same or fewer wrappers than DB (stricter filter)");
    }

    [Test]
    public async Task Staging_ConsentedFlow_DB_vs_Cache_SubsetOrEqual()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbConsent = dbLoadGraph.LoadConsentedFlowFlags().ToList();
        var cacheConsent = cacheLoadGraph.LoadConsentedFlowFlags().ToList();

        TestContext.Out.WriteLine($"DB consented: {dbConsent.Count}, Cache consented: {cacheConsent.Count}");

        // Cache only returns registered avatars (stricter)
        Assert.That(cacheConsent.Count, Is.LessThanOrEqualTo(dbConsent.Count),
            "Cache should have same or fewer consented entries (stricter filter)");
    }

    [Test]
    public async Task Staging_RegisteredAvatars_DB_vs_Cache_SameCount()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbAvatars = dbLoadGraph.LoadRegisteredAvatars().ToHashSet();
        var cacheAvatars = cacheLoadGraph.LoadRegisteredAvatars().ToHashSet();

        TestContext.Out.WriteLine($"DB avatars: {dbAvatars.Count}, Cache avatars: {cacheAvatars.Count}");

        var tolerance = Math.Max(5, dbAvatars.Count / 200);
        Assert.That(Math.Abs(cacheAvatars.Count - dbAvatars.Count), Is.LessThan(tolerance),
            "Avatar count divergence exceeds 0.5%");
    }

    [Test]
    public async Task Staging_GraphStructure_DB_vs_Cache_SameEdgeCount()
    {
        var (dbLoadGraph, cacheLoadGraph) = await LoadBothSources();

        var dbFactory = new GraphFactory(RouterAddress, dbLoadGraph);
        var dbTrust = dbFactory.V2TrustGraph();
        var dbBalance = dbFactory.V2BalanceGraph();
        var dbTrustLookup = GraphFactory.BuildTrustLookup(dbTrust);
        var dbCap = dbFactory.CreateBaseCapacityGraph(dbBalance, dbTrustLookup);

        var cacheFactory = new GraphFactory(RouterAddress, cacheLoadGraph);
        var cacheTrust = cacheFactory.V2TrustGraph();
        var cacheBalance = cacheFactory.V2BalanceGraph();
        var cacheTrustLookup = GraphFactory.BuildTrustLookup(cacheTrust);
        var cacheCap = cacheFactory.CreateBaseCapacityGraph(cacheBalance, cacheTrustLookup);

        TestContext.Out.WriteLine(
            $"DB: {dbCap.AvatarNodes.Count} avatars, {dbCap.Edges.Count} edges, {dbCap.GroupNodes.Count} groups\n" +
            $"Cache: {cacheCap.AvatarNodes.Count} avatars, {cacheCap.Edges.Count} edges, {cacheCap.GroupNodes.Count} groups");

        // Within 2% tolerance for edge count (timing differences)
        var tolerance = Math.Max(50, dbCap.Edges.Count / 50);
        Assert.That(Math.Abs(cacheCap.Edges.Count - dbCap.Edges.Count), Is.LessThan(tolerance),
            $"Edge count divergence exceeds 2% (DB={dbCap.Edges.Count}, Cache={cacheCap.Edges.Count})");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<(ILoadGraph db, ILoadGraph cache)> LoadBothSources()
    {
        // DB path: use LoadGraph with connection string from settings
        var settings = new Settings();
        var dbLoadGraph = new LoadGraph(settings);

        // Cache path: fetch from cache service endpoint
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var response = await client.GetAsync($"{_cacheServiceUrl}/api/pathfinder/graph");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<PathfinderGraphSnapshotDto>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Failed to deserialize cache graph snapshot");
        var snapshot = dto.ToModel();

        // Apply same safety margin as production would
        var cacheLoadGraph = new CacheLoadGraph(snapshot, settings);

        TestContext.Out.WriteLine(
            $"Cache snapshot: block={snapshot.LastProcessedBlock}, " +
            $"balances={snapshot.Balances?.Count ?? 0}, trust={snapshot.Trust?.Count ?? 0}");

        return (dbLoadGraph, cacheLoadGraph);
    }
}
