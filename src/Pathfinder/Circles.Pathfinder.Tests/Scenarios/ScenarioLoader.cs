using System.Text.Json;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests.Scenarios;

/// <summary>
/// Loads transfer scenarios from JSON files in the RegressionScenarios directory.
/// Used by data-driven tests to execute scenarios against the test environment.
/// </summary>
public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads all scenarios from the RegressionScenarios directory.
    /// </summary>
    public static IEnumerable<TransferScenario> LoadAllScenarios()
    {
        var scenariosDir = GetScenariosDirectory();
        if (!Directory.Exists(scenariosDir))
        {
            TestContext.WriteLine($"Scenarios directory not found: {scenariosDir}");
            yield break;
        }

        foreach (var file in Directory.GetFiles(scenariosDir, "*.json"))
        {
            // Skip non-scenario files like README.md or index files
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("_") || fileName == "scenario-index.json")
                continue;

            TransferScenario? scenario = null;
            try
            {
                var json = File.ReadAllText(file);
                scenario = JsonSerializer.Deserialize<TransferScenario>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                TestContext.WriteLine($"Failed to parse scenario file {file}: {ex.Message}");
            }

            if (scenario != null)
            {
                yield return scenario;
            }
        }
    }

    /// <summary>
    /// Loads scenarios filtered by category.
    /// </summary>
    public static IEnumerable<TransferScenario> LoadByCategory(string category)
    {
        return LoadAllScenarios().Where(s =>
            s.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads scenarios filtered by tag.
    /// </summary>
    public static IEnumerable<TransferScenario> LoadByTag(string tag)
    {
        return LoadAllScenarios().Where(s =>
            s.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Loads scenarios that should run on Anvil.
    /// </summary>
    public static IEnumerable<TransferScenario> LoadAnvilScenarios()
    {
        return LoadAllScenarios().Where(s => s.RunOnAnvil);
    }

    /// <summary>
    /// Loads a specific scenario by ID.
    /// </summary>
    public static TransferScenario? LoadById(string id)
    {
        return LoadAllScenarios().FirstOrDefault(s =>
            s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the path to the RegressionScenarios directory.
    /// Handles both test execution context and IDE context.
    /// </summary>
    private static string GetScenariosDirectory()
    {
        // Try test context first (when running tests)
        var testDir = TestContext.CurrentContext?.TestDirectory;
        if (!string.IsNullOrEmpty(testDir))
        {
            var scenariosPath = Path.Combine(testDir, "RegressionScenarios");
            if (Directory.Exists(scenariosPath))
                return scenariosPath;
        }

        // Try relative to executing assembly
        var assemblyLocation = typeof(ScenarioLoader).Assembly.Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            var scenariosPath = Path.Combine(assemblyDir, "RegressionScenarios");
            if (Directory.Exists(scenariosPath))
                return scenariosPath;
        }

        // Try current directory (IDE/debugging)
        var currentDir = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(currentDir, "RegressionScenarios"),
            Path.Combine(currentDir, "src", "Pathfinder", "Circles.Pathfinder.Tests", "RegressionScenarios"),
            Path.Combine(currentDir, "..", "..", "..", "RegressionScenarios")
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // Default fallback
        return Path.Combine(currentDir, "RegressionScenarios");
    }

    /// <summary>
    /// Provides test case data for NUnit's TestCaseSource attribute.
    /// </summary>
    public static IEnumerable<TestCaseData> AllScenariosTestData()
    {
        foreach (var scenario in LoadAllScenarios())
        {
            yield return new TestCaseData(scenario)
                .SetName($"{scenario.Category}/{scenario.Id}")
                .SetDescription(scenario.Description ?? scenario.Name)
                .SetCategory(scenario.Category);
        }
    }

    /// <summary>
    /// Provides test case data for Anvil E2E scenarios.
    /// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
    /// </summary>
    public static IEnumerable<TestCaseData> AnvilScenariosTestData()
    {
        foreach (var scenario in LoadAnvilScenarios())
        {
            yield return new TestCaseData(scenario)
                .SetName($"E2E/{scenario.Category}/{scenario.Id}")
                .SetDescription(scenario.Description ?? scenario.Name)
                .SetCategory(scenario.Category);
        }
    }
}
