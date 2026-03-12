using System.Reflection;
using System.Text.RegularExpressions;

namespace Circles.Index.CirclesViews.Tests;

/// <summary>
/// Phase 3 regression tests:
/// 3a — Calendar generate_series removal from GroupMintRedeem and GroupWrapUnWrap views
/// 3b — Materialized BalancesByAccountAndToken view
/// 3c — Inline demurrage (no crc_demurrage() calls in views)
/// </summary>
[TestFixture, Parallelizable]
public class Phase3RegressionTests
{
    // Demurrage gamma constant that must appear in inline expressions
    private const string Gamma = "0.9998013320085989574306481700129226782902039065082930593676448873";
    // V2 Hub epoch on gnosis mainnet (same as V1). Differential usage in SQL views — epoch cancels.
    private const string InflationDayZero = "1602720000";

    private Dictionary<string, string> _viewSql = null!;

    [OneTimeSetUp]
    public void LoadAllViewSql()
    {
        _viewSql = new Dictionary<string, string>();
        var assembly = typeof(DatabaseSchema).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Circles.Index.CirclesViews.queries.") && n.EndsWith(".sql"));

        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var key = resource
                .Replace("Circles.Index.CirclesViews.queries.", "")
                .Replace(".sql", "");
            _viewSql[key] = reader.ReadToEnd();
        }
    }

    // ================================================================
    // 3a: Calendar generate_series removal
    // ================================================================

    [Test]
    public void GroupMintRedeem_1h_NoGenerateSeries()
    {
        var sql = _viewSql["V_CrcV2_GroupMintRedeem_1h"];
        Assert.That(sql, Does.Not.Contain("generate_series"),
            "GroupMintRedeem_1h must not use generate_series — causes calendar explosion. " +
            "Return only actual activity rows with running SUM window function.");
    }

    [Test]
    public void GroupMintRedeem_1d_NoGenerateSeries()
    {
        var sql = _viewSql["V_CrcV2_GroupMintRedeem_1d"];
        Assert.That(sql, Does.Not.Contain("generate_series"),
            "GroupMintRedeem_1d must not use generate_series — causes calendar explosion.");
    }

    [Test]
    public void GroupWrapUnWrap_1h_NoGenerateSeries()
    {
        var sql = _viewSql["V_CrcV2_GroupWrapUnWrap_1h"];
        Assert.That(sql, Does.Not.Contain("generate_series"),
            "GroupWrapUnWrap_1h must not use generate_series — causes calendar explosion.");
    }

    [Test]
    public void GroupWrapUnWrap_1d_NoGenerateSeries()
    {
        var sql = _viewSql["V_CrcV2_GroupWrapUnWrap_1d"];
        Assert.That(sql, Does.Not.Contain("generate_series"),
            "GroupWrapUnWrap_1d must not use generate_series — causes calendar explosion.");
    }

    [Test]
    public void GroupMintRedeem_1h_StillHasRunningSupply()
    {
        var sql = _viewSql["V_CrcV2_GroupMintRedeem_1h"];
        Assert.That(sql, Does.Contain("OVER").IgnoreCase,
            "Must still have window function for running supply calculation.");
        Assert.That(sql, Does.Contain("PARTITION BY").IgnoreCase,
            "Window function must partition by group.");
    }

    [Test]
    public void GroupWrapUnWrap_1h_StillHasRunningSupply()
    {
        var sql = _viewSql["V_CrcV2_GroupWrapUnWrap_1h"];
        Assert.That(sql, Does.Contain("OVER").IgnoreCase,
            "Must still have window function for running wrapSupply.");
        Assert.That(sql, Does.Contain("PARTITION BY").IgnoreCase,
            "Window function must partition by group and tokenAddress.");
    }

    [Test]
    public void GroupMintRedeem_PreservesOutputColumns()
    {
        // Verify the view definition includes all expected output columns
        var sql = _viewSql["V_CrcV2_GroupMintRedeem_1h"];
        var expectedColumns = new[]
        {
            "\"group\"", "\"timestamp\"", "\"minted\"", "\"burned\"",
            "\"supply\"", "\"demurragedMinted\"", "\"demurragedBurned\"", "\"demurragedSupply\""
        };
        foreach (var col in expectedColumns)
        {
            Assert.That(sql, Does.Contain(col),
                $"GroupMintRedeem_1h must contain output column {col}");
        }
    }

    [Test]
    public void GroupWrapUnWrap_PreservesOutputColumns()
    {
        var sql = _viewSql["V_CrcV2_GroupWrapUnWrap_1h"];
        var expectedColumns = new[]
        {
            "\"group\"", "\"timestamp\"", "\"tokenAddress\"", "\"tokenType\"",
            "\"wrapAmount\"", "\"unwrapAmount\"", "\"wrapSupply\""
        };
        foreach (var col in expectedColumns)
        {
            Assert.That(sql, Does.Contain(col),
                $"GroupWrapUnWrap_1h must contain output column {col}");
        }
    }

    // ================================================================
    // 3b: Materialized BalancesByAccountAndToken
    // ================================================================

    [Test]
    public void BalancesByAccountAndToken_HasMaterializedView()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        Assert.That(sql, Does.Contain("CREATE MATERIALIZED VIEW").IgnoreCase,
            "Must create a materialized view M_CrcV2_BalancesByAccountAndToken for pre-aggregated balances.");
    }

    [Test]
    public void BalancesByAccountAndToken_MaterializedViewHasUniqueIndex()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        Assert.That(sql, Does.Contain("CREATE UNIQUE INDEX").IgnoreCase,
            "Materialized view needs a UNIQUE INDEX for REFRESH MATERIALIZED VIEW CONCURRENTLY.");
    }

    [Test]
    public void BalancesByAccountAndToken_MaterializedViewHasNoNow()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        // Extract only the MATERIALIZED VIEW definition (before the regular view)
        var matViewStart = sql.IndexOf("CREATE MATERIALIZED VIEW", StringComparison.OrdinalIgnoreCase);
        var regularViewStart = sql.IndexOf("create or replace view", matViewStart + 1, StringComparison.OrdinalIgnoreCase);
        var matViewSql = sql.Substring(matViewStart, regularViewStart - matViewStart);

        Assert.That(matViewSql, Does.Not.Contain("NOW()").IgnoreCase,
            "Materialized view must not use NOW() — demurrage depends on current time " +
            "and would be stale after materialization.");
        Assert.That(matViewSql, Does.Not.Contain("crc_demurrage").IgnoreCase,
            "Materialized view must not call crc_demurrage — compute at read time.");
    }

    [Test]
    public void BalancesByAccountAndToken_RegularViewReadsMaterialized()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        Assert.That(sql, Does.Contain("M_CrcV2_BalancesByAccountAndToken"),
            "Regular view V_CrcV2_BalancesByAccountAndToken must SELECT from the materialized view.");
    }

    [Test]
    public void BalancesByAccountAndToken_MaterializedViewHasLookupIndexes()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        Assert.That(sql, Does.Contain("idx_M_CrcV2_BalancesByAccountAndToken_account").IgnoreCase,
            "Must have index on account for per-address lookups.");
        Assert.That(sql, Does.Contain("idx_M_CrcV2_BalancesByAccountAndToken_tokenAddress").IgnoreCase,
            "Must have index on tokenAddress for token-based lookups.");
    }

    [Test]
    public void BalancesByAccountAndToken_PreservesOutputColumns()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        // Check the regular view has all expected columns
        var expectedColumns = new[]
        {
            "account", "\"tokenId\"", "\"tokenAddress\"",
            "\"lastActivity\"", "\"totalBalance\"", "\"demurragedTotalBalance\""
        };
        foreach (var col in expectedColumns)
        {
            Assert.That(sql, Does.Contain(col),
                $"BalancesByAccountAndToken must contain output column {col}");
        }
    }

    // ================================================================
    // 3c: Inline demurrage — no crc_demurrage() calls in view bodies
    // ================================================================

    [Test]
    public void GroupMintRedeem_1h_UsesInlineDemurrage()
    {
        var sql = _viewSql["V_CrcV2_GroupMintRedeem_1h"];
        AssertInlineDemurrage(sql, "V_CrcV2_GroupMintRedeem_1h");
    }

    [Test]
    public void GroupMintRedeem_1d_UsesInlineDemurrage()
    {
        var sql = _viewSql["V_CrcV2_GroupMintRedeem_1d"];
        AssertInlineDemurrage(sql, "V_CrcV2_GroupMintRedeem_1d");
    }

    [Test]
    public void GroupCollateralByToken_UsesInlineDemurrage()
    {
        var sql = _viewSql["V_CrcV2_GroupCollateralByToken"];
        AssertInlineDemurrage(sql, "V_CrcV2_GroupCollateralByToken");
    }

    [Test]
    public void GroupVaultBalancesByToken_UsesInlineDemurrage()
    {
        var sql = _viewSql["V_CrcV2_GroupVaultBalancesByToken"];
        AssertInlineDemurrage(sql, "V_CrcV2_GroupVaultBalancesByToken");
    }

    [Test]
    public void BalancesByAccountAndToken_RegularView_UsesInlineDemurrage()
    {
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        // The regular view (not the mat view or function defs) should use inline POWER()
        var regularViewStart = sql.LastIndexOf("create or replace view", StringComparison.OrdinalIgnoreCase);
        var regularViewSql = sql.Substring(regularViewStart);

        Assert.That(regularViewSql, Does.Not.Contain("crc_demurrage("),
            "Regular view should use inline POWER() expression, not crc_demurrage() function.");
        Assert.That(regularViewSql, Does.Contain("POWER("),
            "Regular view must use inline POWER() for demurrage computation.");
        Assert.That(regularViewSql, Does.Contain(Gamma),
            "Regular view must use the correct gamma constant.");
    }

    [Test]
    public void CrcDemurrageFunctionsStillExist()
    {
        // Functions must still exist for backward compatibility — other views or ad-hoc queries may use them
        var sql = _viewSql["V_CrcV2_BalancesByAccountAndToken"];
        Assert.That(sql, Does.Contain("CREATE OR REPLACE FUNCTION crc_day"),
            "crc_day function must still be defined for backward compatibility.");
        Assert.That(sql, Does.Contain("CREATE OR REPLACE FUNCTION crc_demurrage"),
            "crc_demurrage function must still be defined for backward compatibility.");
    }

    [Test]
    public void AllInlineDemurrageUsesCorrectGamma()
    {
        var viewsToCheck = new[]
        {
            "V_CrcV2_GroupMintRedeem_1h",
            "V_CrcV2_GroupMintRedeem_1d",
            "V_CrcV2_GroupCollateralByToken",
            "V_CrcV2_GroupVaultBalancesByToken",
            "V_CrcV2_BalancesByAccountAndToken"
        };

        foreach (var viewName in viewsToCheck)
        {
            var sql = _viewSql[viewName];

            // Strip function definitions (CREATE OR REPLACE FUNCTION ... $$ ... $$)
            // to only check POWER() calls in view bodies, not in PL/pgSQL function bodies
            var viewBodySql = StripFunctionDefinitions(sql);

            // Every POWER() call in view bodies should use the correct gamma
            var powerMatches = Regex.Matches(viewBodySql, @"POWER\s*\(", RegexOptions.IgnoreCase);
            foreach (Match match in powerMatches)
            {
                var afterPower = viewBodySql.Substring(match.Index, Math.Min(200, viewBodySql.Length - match.Index));
                Assert.That(afterPower, Does.Contain(Gamma),
                    $"POWER() in {viewName} view body at position {match.Index} must use gamma={Gamma}");
            }
        }
    }

    [Test]
    public void AllInlineDemurrageUsesCorrectInflationDayZero()
    {
        var viewsToCheck = new[]
        {
            "V_CrcV2_GroupMintRedeem_1h",
            "V_CrcV2_GroupMintRedeem_1d",
            "V_CrcV2_GroupCollateralByToken",
            "V_CrcV2_GroupVaultBalancesByToken",
            "V_CrcV2_BalancesByAccountAndToken"
        };

        foreach (var viewName in viewsToCheck)
        {
            var sql = _viewSql[viewName];
            Assert.That(sql, Does.Contain(InflationDayZero),
                $"{viewName} must use inflationDayZero={InflationDayZero} in inline demurrage.");
        }
    }

    // ================================================================
    // Schema registration
    // ================================================================

    [Test]
    public void DatabaseSchema_StillRegistersAllViews()
    {
        var schema = new DatabaseSchema();
        var expectedViews = new[]
        {
            ("V_CrcV2", "GroupMintRedeem_1h"),
            ("V_CrcV2", "GroupMintRedeem_1d"),
            ("V_CrcV2", "GroupWrapUnWrap_1h"),
            ("V_CrcV2", "GroupWrapUnWrap_1d"),
            ("V_CrcV2", "BalancesByAccountAndToken"),
            ("V_CrcV2", "GroupCollateralByToken"),
            ("V_CrcV2", "GroupVaultBalancesByToken"),
            ("V_CrcV2", "GroupTokenHoldersBalance")
        };

        foreach (var (ns, table) in expectedViews)
        {
            Assert.That(schema.Tables.ContainsKey((ns, table)), Is.True,
                $"Schema must register view ({ns}, {table})");
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// Removes PL/pgSQL function definitions ($$ ... $$) from SQL so we only
    /// check POWER() usage in view bodies, not inside function bodies.
    /// </summary>
    private static string StripFunctionDefinitions(string sql)
    {
        // Remove everything between pairs of $$ delimiters (PL/pgSQL function bodies)
        return Regex.Replace(sql, @"\$\$.*?\$\$", "$$STRIPPED$$", RegexOptions.Singleline);
    }

    /// <summary>
    /// Asserts that a view SQL uses inline POWER() demurrage, not crc_demurrage() function calls.
    /// </summary>
    private static void AssertInlineDemurrage(string sql, string viewName)
    {
        // Extract only the view body (after CREATE ... VIEW ... AS), skipping function definitions
        var viewBodyStart = sql.LastIndexOf(" as\n", StringComparison.OrdinalIgnoreCase);
        if (viewBodyStart < 0) viewBodyStart = sql.LastIndexOf(" AS\n", StringComparison.OrdinalIgnoreCase);
        if (viewBodyStart < 0) viewBodyStart = sql.LastIndexOf(" AS ", StringComparison.OrdinalIgnoreCase);

        // For views that don't have demurrage at all (WrapUnWrap), skip the function check
        if (!sql.Contains("demurrage", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("POWER(", StringComparison.OrdinalIgnoreCase))
            return;

        Assert.That(sql, Does.Not.Contain("crc_demurrage("),
            $"{viewName}: Must use inline POWER() expression, not crc_demurrage() function. " +
            "PL/pgSQL function call overhead is ~100-1000x vs inline SQL.");

        Assert.That(sql, Does.Contain("POWER("),
            $"{viewName}: Must use inline POWER() for demurrage computation.");

        Assert.That(sql, Does.Contain(Gamma),
            $"{viewName}: Must use gamma constant {Gamma}.");
    }
}
