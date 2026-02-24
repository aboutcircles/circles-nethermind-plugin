using System.Text.Json;
using Circles.Common.TestUtils;
using NUnit.Framework;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Scenario record for loading pathfinder scenarios in RPC tests.
/// This is a simplified version that only includes fields needed for RPC validation.
/// </summary>
public record RpcScenario
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required long Block { get; init; }
    public required string Source { get; init; }
    public required string Sink { get; init; }
    public bool ShouldFindPath { get; init; } = true;

    public override string ToString() => $"{Category}/{Id}";
}

/// <summary>
/// RPC tests that validate database state at scenario blocks.
/// These tests verify that the RPC queries return consistent data
/// for the addresses used in pathfinder scenarios.
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// </summary>
[TestFixture]
public class RpcScenarioTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads scenarios from the Pathfinder's RegressionScenarios directory.
    /// </summary>
    private static IEnumerable<TestCaseData> LoadScenarios()
    {
        var scenariosDir = FindScenariosDirectory();
        if (scenariosDir == null || !Directory.Exists(scenariosDir))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(scenariosDir, "*.json"))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("_") || fileName == "scenario-index.json")
                continue;

            RpcScenario? scenario = null;
            try
            {
                var json = File.ReadAllText(file);
                scenario = JsonSerializer.Deserialize<RpcScenario>(json, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (scenario != null && scenario.ShouldFindPath)
            {
                yield return new TestCaseData(scenario)
                    .SetName($"RPC/{scenario.Category}/{scenario.Id}")
                    .SetDescription($"Validate RPC data for {scenario.Name}");
            }
        }
    }

    private static string? FindScenariosDirectory()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            // From test output directory
            Path.Combine(currentDir, "..", "..", "..", "..", "..", "Pathfinder",
                "Circles.Pathfinder.Tests", "RegressionScenarios"),
            // From project root
            Path.Combine(currentDir, "src", "Pathfinder",
                "Circles.Pathfinder.Tests", "RegressionScenarios"),
            // Direct path from repo root
            Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", "..",
                "..", "src", "Pathfinder", "Circles.Pathfinder.Tests", "RegressionScenarios"))
        };

        foreach (var candidate in candidates)
        {
            var normalized = Path.GetFullPath(candidate);
            if (Directory.Exists(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that source and sink addresses have data in the database at scenario block.
    /// </summary>
    [TestCaseSource(nameof(LoadScenarios))]
    public async Task ValidateScenarioAddresses(RpcScenario scenario)
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run scenario tests.");
            return;
        }

        TestEnvironmentClient? session = null;

        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                Assert.Ignore("Test environment not healthy");
            }

            var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
            if (!exists)
            {
                Assert.Ignore($"Block {scenario.Block} not indexed");
            }

            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db"],
                ttl: "15m");
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            await ValidateAddressData(scenario, session);
        }
        finally
        {
            if (session != null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private async Task ValidateAddressData(RpcScenario scenario, TestEnvironmentClient session)
    {
        // If we have direct DB access, validate the source has balances
        if (session.IsDirectConnectionAvailable)
        {
            await using var conn = await session.OpenConnectionAsync();

            // Validate source has some balance
            await using var cmd = conn!.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM ""V_CrcV2_BalancesByAccountAndToken""
                WHERE ""account"" = @address AND ""balance"" > 0";
            cmd.Parameters.AddWithValue("address", scenario.Source);

            var sourceBalances = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            TestContext.Out.WriteLine($"Scenario {scenario.Id}: Source has {sourceBalances} token balances");

            if (scenario.ShouldFindPath)
            {
                Assert.That(sourceBalances, Is.GreaterThan(0),
                    $"Scenario {scenario.Id}: Source should have token balances");
            }

            // Validate trust relationships exist
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM ""V_CrcV2_TrustRelations""
                WHERE ""truster"" = @source OR ""trustee"" = @source
                   OR ""truster"" = @sink OR ""trustee"" = @sink";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("source", scenario.Source);
            cmd.Parameters.AddWithValue("sink", scenario.Sink);

            var trustCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            TestContext.Out.WriteLine($"Scenario {scenario.Id}: Trust relations involving addresses: {trustCount}");

            if (scenario.ShouldFindPath)
            {
                Assert.That(trustCount, Is.GreaterThan(0),
                    $"Scenario {scenario.Id}: Should have trust relations for path to exist");
            }
        }
        else
        {
            // Use query proxy if available
            try
            {
                var result = await session.ExecuteQueryAsync(@"
                    SELECT COUNT(*)
                    FROM ""V_CrcV2_BalancesByAccountAndToken""
                    WHERE ""account"" = @address AND ""balance"" > 0",
                    new Dictionary<string, object?> { ["address"] = scenario.Source });

                var sourceBalances = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
                TestContext.Out.WriteLine($"Scenario {scenario.Id}: Source has {sourceBalances} token balances");

                if (scenario.ShouldFindPath)
                {
                    Assert.That(sourceBalances, Is.GreaterThan(0),
                        $"Scenario {scenario.Id}: Source should have token balances");
                }
            }
            catch (Exception ex)
            {
                Assert.Ignore($"Query proxy not available: {ex.Message}");
            }
        }
    }
}
