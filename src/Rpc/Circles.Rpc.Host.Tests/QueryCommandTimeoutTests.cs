using System.Reflection;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Regression test ensuring the circles_query handler sets CommandTimeout.
/// Without a timeout, complex view queries (GroupMintRedeem, GroupWrapUnWrap)
/// can hang indefinitely, blocking database connections.
///
/// This is a source-level check because instantiating CirclesRpcModule requires
/// Nethermind runtime which isn't always available in CI.
/// </summary>
[TestFixture, Parallelizable]
public class QueryCommandTimeoutTests
{
    [Test]
    public void CirclesRpcModule_QueryMethod_SetsCommandTimeout()
    {
        // Read the source file and verify CommandTimeout is set near the Query method's NpgsqlCommand
        var assembly = typeof(CirclesRpcModule).Assembly;
        var sourceDir = FindSourceDirectory(assembly);

        if (sourceDir == null)
        {
            // Fallback: check via reflection that Settings has the property
            var settingsType = typeof(Settings);
            var field = settingsType.GetField("DatabaseQueryTimeoutSeconds",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null,
                "Settings.DatabaseQueryTimeoutSeconds field must exist for CommandTimeout to work");
            return;
        }

        // CirclesRpcModule is split across multiple partial-class files (main file
        // + RpcModule/CirclesRpcModule.*.cs). Concatenate them all so the source grep
        // is resilient to where the Query method actually lives.
        var sourceFiles = Directory.GetFiles(sourceDir, "CirclesRpcModule*.cs", SearchOption.AllDirectories);
        if (sourceFiles.Length == 0)
        {
            Assert.Inconclusive("Could not find CirclesRpcModule source files to verify CommandTimeout");
            return;
        }

        var source = string.Join("\n", sourceFiles.Select(File.ReadAllText));

        // The Query method creates NpgsqlCommand with finalSql. Verify CommandTimeout is set.
        // Look for the pattern: var finalSql = $"SELECT {columns} FROM {fromClause} ..."
        var queryMethodIndex = source.IndexOf("var finalSql = $\"SELECT {columns} FROM {fromClause}",
            StringComparison.Ordinal);
        Assert.That(queryMethodIndex, Is.GreaterThan(0),
            "Could not find the Query method's finalSql construction");

        // Check that CommandTimeout appears within the next 200 characters
        var regionAfterFinalSql = source.Substring(queryMethodIndex,
            Math.Min(500, source.Length - queryMethodIndex));
        Assert.That(regionAfterFinalSql, Does.Contain("CommandTimeout"),
            "The circles_query handler must set CommandTimeout on NpgsqlCommand to prevent " +
            "indefinite hangs on complex view queries");
        Assert.That(regionAfterFinalSql, Does.Contain("DatabaseQueryTimeoutSeconds"),
            "CommandTimeout should use the configurable DatabaseQueryTimeoutSeconds setting, " +
            "not a hardcoded value");
    }

    [Test]
    public void Settings_DatabaseQueryTimeoutSeconds_DefaultIs30()
    {
        // Common.Settings requires POSTGRES_CONNECTION_STRING
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING",
            "Host=localhost;Database=test;Username=test;Password=test");
        try
        {
            var settings = new Settings();
            Assert.That(settings.DatabaseQueryTimeoutSeconds, Is.EqualTo(30),
                "Default query timeout should be 30 seconds");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", null);
        }
    }

    private static string? FindSourceDirectory(Assembly assembly)
    {
        // Walk up from test assembly output to find the RPC host source
        var assemblyDir = Path.GetDirectoryName(assembly.Location);
        if (assemblyDir == null) return null;

        // Try common relative path from test output to source
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "Circles.Rpc.Host")),
            Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "Rpc", "Circles.Rpc.Host")),
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }
}
