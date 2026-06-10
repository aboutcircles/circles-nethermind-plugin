using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Utility test that extracts subgraphs from staging and writes them into scenario JSON files.
///
/// This is NOT a regular test — run it explicitly when you need to populate/refresh fixtures:
///   TEST_ENV_URL=https://staging.circlesubi.network/test-env \
///     dotnet test --filter "FullyQualifiedName~SubgraphPopulateFixtures" --no-build
///
/// After running, the scenario JSON files will contain embedded "subgraph" data,
/// enabling offline unit tests via FixtureLoadGraph.
/// </summary>
[TestFixture]
[Category("FixtureGeneration")]
[Category("RequiresTestEnv")]
public class SubgraphPopulateFixtures
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Extracts subgraphs for ALL positive scenarios and saves them into the JSON files.
    /// Skips scenarios that already have subgraph data (use ForceRefresh to override).
    /// </summary>
    [Test]
    public async Task PopulateAllScenarios()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        var health = await TestEnvironmentClient.GetHealthAsync();
        if (health?.Status != "healthy")
        {
            Assert.Ignore("Test environment not healthy");
            return;
        }

        var scenariosDir = GetScenariosDirectory();
        if (scenariosDir == null)
        {
            Assert.Fail("Cannot find RegressionScenarios source directory");
            return;
        }

        var forceRefresh = Environment.GetEnvironmentVariable("FORCE_REFRESH_FIXTURES") == "1";
        var populated = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var scenario in ScenarioLoader.LoadAllScenarios())
        {
            if (!scenario.ShouldFindPath)
            {
                TestContext.Out.WriteLine($"SKIP {scenario.Id}: negative test");
                skipped++;
                continue;
            }

            // Check if subgraph already exists in source file
            var jsonPath = Path.Combine(scenariosDir, $"{scenario.Id}.json");
            if (!File.Exists(jsonPath))
            {
                TestContext.Out.WriteLine($"SKIP {scenario.Id}: source file not found at {jsonPath}");
                skipped++;
                continue;
            }

            if (!forceRefresh)
            {
                var existingJson = await File.ReadAllTextAsync(jsonPath);
                if (existingJson.Contains("\"subgraph\""))
                {
                    TestContext.Out.WriteLine($"SKIP {scenario.Id}: already has subgraph (set FORCE_REFRESH_FIXTURES=1 to override)");
                    skipped++;
                    continue;
                }
            }

            // Check block availability
            var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
            if (!exists)
            {
                TestContext.Out.WriteLine($"SKIP {scenario.Id}: block {scenario.Block} not indexed");
                skipped++;
                continue;
            }

            try
            {
                await PopulateScenario(scenario, jsonPath);
                populated++;
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"FAIL {scenario.Id}: {ex.Message}");
                failed++;
            }
        }

        TestContext.Out.WriteLine($"\nDone: {populated} populated, {skipped} skipped, {failed} failed");
        Assert.That(failed, Is.EqualTo(0), $"{failed} scenarios failed to populate");
    }

    private async Task PopulateScenario(TransferScenario scenario, string jsonPath)
    {
        // Load full graph (cached per block)
        var fullData = SharedGraphCache.GetOrLoad(scenario.Block);

        // Compute path with full graph
        var factory = new GraphFactory(RouterAddress, fullData.CreateLoadGraph());
        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trust);

        var request = ScenarioTests.BuildFlowRequest(scenario);
        var capacity = factory.CreateCapacityGraph(balance, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        var response = pathfinder.ComputeMaxFlowWithPath(capacity, request, targetFlow);

        // Extract subgraph (include path addresses for better coverage)
        HashSet<string>? pathAddresses = null;
        if (response.Transfers != null && response.Transfers.Count > 0)
        {
            pathAddresses = SubgraphExtractor.GetPathAddresses(response.Transfers);
        }

        var subgraph = SubgraphExtractor.Extract(
            fullData, scenario.Source, scenario.Sink, pathAddresses);

        // Read existing JSON, inject subgraph, write back
        var existingJson = await File.ReadAllTextAsync(jsonPath);
        var jsonDoc = JsonSerializer.Deserialize<JsonElement>(existingJson, ReadOptions);

        // Build a mutable dictionary from the existing JSON
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in jsonDoc.EnumerateObject())
        {
            if (prop.Name != "subgraph") // Remove existing subgraph if any
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        // Add subgraph as a new property
        var subgraphJson = JsonSerializer.SerializeToElement(subgraph, WriteOptions);
        dict["subgraph"] = subgraphJson;

        // Write back in same order (existing fields first, then subgraph at end)
        var outputJson = JsonSerializer.Serialize(dict, WriteOptions);
        await File.WriteAllTextAsync(jsonPath, outputJson + "\n");

        var transferCount = response.Transfers?.Count ?? 0;
        TestContext.Out.WriteLine($"OK {scenario.Id}: " +
            $"{subgraph.Stats?.AddressCount} addresses, " +
            $"{subgraph.Stats?.TrustEdges} trust, " +
            $"{subgraph.Stats?.BalanceEntries} balances " +
            $"(path: {transferCount} steps, flow: {response.MaxFlow})");
    }

    /// <summary>
    /// Gets the source directory of scenario JSON files (not the build output).
    /// We need the source directory so we can write back updated JSON files.
    /// </summary>
    private static string? GetScenariosDirectory()
    {
        // Walk up from the executing assembly to find the source directory
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "Pathfinder",
                "Circles.Pathfinder.Tests", "RegressionScenarios");
            if (Directory.Exists(candidate))
                return candidate;

            dir = Path.GetDirectoryName(dir);
        }

        // Try from working directory
        var workDir = Directory.GetCurrentDirectory();
        var fromWork = Path.Combine(workDir, "src", "Pathfinder",
            "Circles.Pathfinder.Tests", "RegressionScenarios");
        if (Directory.Exists(fromWork))
            return fromWork;

        return null;
    }
}
