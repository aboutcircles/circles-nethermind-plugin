using System.Reflection;

namespace Circles.Index.CirclesViews.Tests;

/// <summary>
/// Regression tests ensuring the PL/pgSQL demurrage functions are marked as STABLE.
/// Without STABLE, PostgreSQL cannot cache NOW() across repeated calls within a single
/// statement — causing millions of unnecessary EXTRACT(EPOCH FROM NOW()) evaluations
/// in views like GroupMintRedeem and BalancesByAccountAndToken.
/// </summary>
[TestFixture, Parallelizable]
public class DemurrageFunctionStabilityTests
{
    private string _balancesSql = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var assembly = typeof(DatabaseSchema).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(n => n.Contains("V_CrcV2_BalancesByAccountAndToken"));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        _balancesSql = reader.ReadToEnd();
    }

    [Test]
    public void CrcDay_IsMarkedStable()
    {
        // Find the crc_day function definition and verify it ends with STABLE
        var crcDayBlock = ExtractFunctionBlock(_balancesSql, "crc_day");
        Assert.That(crcDayBlock, Does.Contain("LANGUAGE plpgsql STABLE"),
            "crc_day function must be marked STABLE for PostgreSQL to cache NOW() " +
            "within a single statement execution");
    }

    [Test]
    public void CrcDemurrage_IsMarkedStable()
    {
        var crcDemurrageBlock = ExtractFunctionBlock(_balancesSql, "crc_demurrage");
        Assert.That(crcDemurrageBlock, Does.Contain("LANGUAGE plpgsql STABLE"),
            "crc_demurrage function must be marked STABLE for PostgreSQL to cache NOW() " +
            "within a single statement execution");
    }

    [Test]
    public void NoPlpgsqlFunctionsWithoutStable()
    {
        // Ensure no PL/pgSQL function definition exists without STABLE
        // This catches future regressions if someone adds a new function
        var lines = _balancesSql.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Contains("LANGUAGE plpgsql", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("STABLE", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail($"Line {i + 1} has plpgsql function without STABLE: {line}");
            }
        }
    }

    /// <summary>
    /// Extracts the SQL block for a specific function from CREATE FUNCTION ... $$ ... $$ LANGUAGE ...
    /// </summary>
    private static string ExtractFunctionBlock(string sql, string functionName)
    {
        var idx = sql.IndexOf($"FUNCTION {functionName}(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            idx = sql.IndexOf($"FUNCTION {functionName} (", StringComparison.OrdinalIgnoreCase);

        Assert.That(idx, Is.GreaterThanOrEqualTo(0), $"Could not find function {functionName} in SQL");

        // Find the closing $$ LANGUAGE plpgsql line after the function
        var rest = sql.Substring(idx);
        var langIdx = rest.IndexOf("LANGUAGE plpgsql", StringComparison.OrdinalIgnoreCase);
        Assert.That(langIdx, Is.GreaterThan(0), $"Could not find LANGUAGE plpgsql after {functionName}");

        // Include enough to capture STABLE if present
        var endIdx = Math.Min(langIdx + 50, rest.Length);
        return rest.Substring(0, endIdx);
    }
}
