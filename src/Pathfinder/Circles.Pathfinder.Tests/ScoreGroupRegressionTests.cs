using System.Numerics;
using System.Text.Json;
using Circles.Common;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Grounded, block-pinned test matrix for the Circles V2 ScoreGroup mint policy
/// (OffchainScoreBasedMintPolicy) and its ScoreGroupMintRouter — the highest-priority
/// custom mint policy. Scenarios live in <c>ScoreGroupScenarios/*.json</c> and are pinned
/// to real Gnosis blocks where the score group was active.
///
/// Three independent tiers, each runnable in isolation and each gracefully skipping when
/// <c>TEST_ENV_URL</c> is not set:
///
///   1. Projection — loads the block-pinned graph through a score-aware <see cref="LoadGraph"/>
///      and asserts the pathfinder solver's view (path found / no path / sink-only behavior).
///   2. DbState    — asserts the indexed CrcV2_ScoreGroup_* row math at the pinned block via
///      the session scalar-query API. (Raw event tables are not schema-twinned, so the fixture
///      SQL carries an explicit "blockNumber <= N" filter.)
///   3. Anvil      — replays the real transaction calldata on an Anvil fork (impersonating the
///      original sender), optionally applying a single-field mutation to isolate a revert branch,
///      and asserts on-chain success or the expected custom-error.
///
/// Why replay: the score policy's preconditions (valid Merkle proof, operator approval, snapshot
/// atomicity) cannot be synthesized cold on a historical fork. Replaying a real tx keeps every
/// precondition valid; a one-field mutation isolates each downstream revert.
/// </summary>
[TestFixture]
[NonParallelizable] // Serialize: each tier creates its own test-env session; concurrent creation flakes on staging.
[Category("Regression")]
[Category("RequiresTestEnv")]
public class ScoreGroupRegressionTests
{
    /// <summary>Standard group router used by GraphFactory; score routers are loaded from the DB.</summary>
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// The deployed OffchainScoreBasedMintPolicy on Gnosis (the GroupInitialized emitter).
    /// Setting it on <see cref="Settings.ScoreGroupMintPolicies"/> activates the score-group
    /// loaders in <see cref="LoadGraph"/>.
    /// </summary>
    private const string ScoreGroupMintPolicy = "0x450d68272e43c4cab7cbc7faa37893a50fae9569";

    // =====================================================================
    //                              Loader
    // =====================================================================

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static IEnumerable<TransferScenario> LoadAll()
    {
        var dir = ScenariosDirectory();
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            if (Path.GetFileName(file).StartsWith('_'))
                continue;

            TransferScenario? scenario = null;
            try
            {
                scenario = JsonSerializer.Deserialize<TransferScenario>(File.ReadAllText(file), JsonOptions);
            }
            catch (JsonException ex)
            {
                TestContext.Out.WriteLine($"Failed to parse score-group scenario {file}: {ex.Message}");
            }

            if (scenario is { IsComplete: true })
                yield return scenario;
        }
    }

    private static string ScenariosDirectory()
    {
        var testDir = TestContext.CurrentContext?.TestDirectory;
        if (!string.IsNullOrEmpty(testDir))
        {
            var p = Path.Combine(testDir, "ScoreGroupScenarios");
            if (Directory.Exists(p))
                return p;
        }

        var asmDir = Path.GetDirectoryName(typeof(ScoreGroupRegressionTests).Assembly.Location);
        return Path.Combine(asmDir ?? Directory.GetCurrentDirectory(), "ScoreGroupScenarios");
    }

    public static IEnumerable<TestCaseData> ProjectionCases() =>
        LoadAll().Where(s => !s.SkipProjection)
            .Select(s => new TestCaseData(s).SetName($"Projection/{s.Category}/{s.Id}"));

    public static IEnumerable<TestCaseData> DbStateCases() =>
        LoadAll().Where(s => s.DbStateAssertion != null)
            .Select(s => new TestCaseData(s).SetName($"DbState/{s.Category}/{s.Id}"));

    public static IEnumerable<TestCaseData> AnvilCases() =>
        LoadAll().Where(s => s.RunOnAnvil)
            .Select(s => new TestCaseData(s).SetName($"Anvil/{s.Category}/{s.Id}"));

    // =====================================================================
    //                       Tier 1: Projection
    // =====================================================================

    [TestCaseSource(nameof(ProjectionCases))]
    public async Task Projection(TransferScenario scenario)
    {
        // Authoring guard (runs even without staging): a score-group projection scenario that expects
        // a path MUST assert which token gets minted, otherwise it silently degrades to a weak
        // "found any path" check that wouldn't catch a regressed score-group loader.
        if (scenario.ShouldFindPath
            && scenario.Category.StartsWith("score-group", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(scenario.ExpectedPathTokenOwner))
        {
            Assert.Fail($"{scenario.Id}: a score-group projection scenario with shouldFindPath=true must set " +
                        "expectedPathTokenOwner (the group token) to prove the path routes through group minting.");
        }

        if (!TestEnvAvailable())
            return;

        if (!await BlockReadyAsync(scenario.Block))
            return;

        TestEnvironmentClient? session = null;
        try
        {
            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block, features: ["db"], ttl: "15m",
                testEnvUrl: Environment.GetEnvironmentVariable("TEST_ENV_URL"));
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            // ProxyLoadGraph runs every query through the test-env HTTP query proxy with an
            // inlined "WHERE blockNumber <= N" filter — so it is genuinely block-pinned AND
            // reachable from a developer machine (no direct DB connection / credentials needed;
            // staging does not expose Postgres remotely). It now implements the score-group
            // loaders (group routers, score routers, operator approvals), so the pathfinder
            // recognizes the score group and applies the router-trust / operator-approval gates
            // instead of degrading it to a regular group.
            //
            // Demurrage is pinned to the block's own timestamp (not UtcNow) so balances match the
            // historical block exactly — required for older-block tier-C cases to be correct.
            var settings = new Settings
            {
                DisableConsentedFlow = false,
                ScoreGroupMintPolicies = [ScoreGroupMintPolicy],
                TargetDemurrageTimestamp = await BlockTimestampAsync(session, scenario.Block)
            };

            var loadGraph = new ProxyLoadGraph(session, settings);
            var factory = new GraphFactory(RouterAddress, loadGraph);

            var trustGraph = factory.V2TrustGraph();
            var balanceGraph = factory.V2BalanceGraph();
            var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

            var request = ScenarioTests.BuildFlowRequest(scenario);
            var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

            var pathfinder = new V2Pathfinder(settings: settings);
            var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
                ? UInt256.Parse("1000000000000000000")
                : UInt256.Parse(scenario.MinFlow);

            var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
            var stepCount = response.Transfers?.Count ?? 0;

            if (!scenario.ShouldFindPath)
            {
                Assert.That(stepCount, Is.EqualTo(0),
                    $"{scenario.Id}: expected NO path (score-group projection), but found {stepCount} step(s)");
                TestContext.Out.WriteLine($"{scenario.Id}: correctly found no path");
            }
            else
            {
                Assert.That(response.Transfers, Is.Not.Null, $"{scenario.Id}: expected a path");
                Assert.That(stepCount, Is.GreaterThan(0), $"{scenario.Id}: path has no steps");

                // Routing assertion: prove the path actually went THROUGH the score group
                // (a step mints the group's own token), not merely that some path exists.
                if (!string.IsNullOrEmpty(scenario.ExpectedPathTokenOwner))
                {
                    var owners = response.Transfers!.Select(t => t.TokenOwner).ToList();
                    Assert.That(
                        owners.Any(o => string.Equals(o, scenario.ExpectedPathTokenOwner,
                            StringComparison.OrdinalIgnoreCase)),
                        Is.True,
                        $"{scenario.Id}: expected a step minting token {scenario.ExpectedPathTokenOwner} " +
                        $"(route through the score group), but none found. Token owners: [{string.Join(", ", owners)}]");
                    TestContext.Out.WriteLine(
                        $"{scenario.Id}: path routes through {scenario.ExpectedPathTokenOwner} " +
                        $"({stepCount} step(s), maxFlow={response.MaxFlow})");
                }
                else
                {
                    TestContext.Out.WriteLine($"{scenario.Id}: found path with {stepCount} step(s), maxFlow={response.MaxFlow}");
                }
            }
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }

    // =====================================================================
    //                       Tier 2: DB-state
    // =====================================================================

    [TestCaseSource(nameof(DbStateCases))]
    public async Task DbState(TransferScenario scenario)
    {
        if (!TestEnvAvailable())
            return;

        if (!await BlockReadyAsync(scenario.Block))
            return;

        var assertion = scenario.DbStateAssertion!;

        TestEnvironmentClient? session = null;
        try
        {
            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block, features: ["db"], ttl: "10m",
                testEnvUrl: Environment.GetEnvironmentVariable("TEST_ENV_URL"));
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            var scalar = await session.ExecuteScalarAsync(assertion.ScalarSql);
            var actual = scalar?.ToString() ?? "";
            TestContext.Out.WriteLine($"{scenario.Id}: DB scalar = '{actual}'");

            if (assertion.Expect != null)
            {
                Assert.That(actual, Is.EqualTo(assertion.Expect),
                    $"{scenario.Id}: DB scalar mismatch (sql: {assertion.ScalarSql})");
            }

            if (assertion.MinValue != null)
            {
                Assert.That(BigInteger.TryParse(actual, out var actualValue), Is.True,
                    $"{scenario.Id}: DB scalar '{actual}' is not numeric but minValue was set");
                Assert.That(actualValue, Is.GreaterThanOrEqualTo(BigInteger.Parse(assertion.MinValue)),
                    $"{scenario.Id}: DB scalar {actualValue} below minValue {assertion.MinValue}");
            }
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }

    // =====================================================================
    //                       Tier 3: Anvil (replay)
    // =====================================================================

    [TestCaseSource(nameof(AnvilCases))]
    public async Task Anvil(TransferScenario scenario)
    {
        if (!TestEnvAvailable())
            return;

        if (scenario.ReplayCalldata == null && string.IsNullOrEmpty(scenario.ReplayTxHash))
        {
            Assert.Fail($"{scenario.Id}: Anvil score-group scenarios must provide replayCalldata or replayTxHash");
            return;
        }

        if (!await BlockReadyAsync(scenario.Block))
            return;

        AnvilSessionData anvilData;
        try
        {
            anvilData = SharedAnvilCache.GetOrCreate(scenario.Block);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        var anvil = anvilData.Anvil;
        TestContext.Out.WriteLine(
            $"{scenario.Id}: Anvil fork at block {anvilData.BlockNumber}, timestamp {anvilData.BlockTimestamp:u}");

        // Execute with a transient-infra retry. Each attempt snapshots first and reverts before
        // retrying, so a half-applied tx never leaks into the next attempt or other tests.
        ExecutionResult? result = null;
        string? snapshotId = null;
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                snapshotId = await anvil.SnapshotAsync();
                result = await ExecuteAnvilAsync(anvil, scenario);

                if (!IsTransientInfra(result.Error))
                    break;

                // Discard any partial state and retry the proxied Anvil call.
                await anvil.RevertAsync(snapshotId);
                snapshotId = null;
                if (attempt < 2)
                    await Task.Delay(2000 * attempt);
            }

            if (result != null && IsTransientInfra(result.Error))
            {
                Assert.Ignore($"{scenario.Id}: transient Anvil infra error after retry: {result.Error}");
                return;
            }

            if (!string.IsNullOrEmpty(scenario.ExpectedRevertReason))
            {
                Assert.That(result!.Success, Is.False,
                    $"{scenario.Id}: expected revert '{scenario.ExpectedRevertReason}' but the tx succeeded");
                Assert.That(result.Error, Does.Contain(scenario.ExpectedRevertReason).IgnoreCase,
                    $"{scenario.Id}: expected revert '{scenario.ExpectedRevertReason}' but got '{result.Error}'");
                TestContext.Out.WriteLine($"{scenario.Id}: reverted as expected: {result.Error}");
            }
            else
            {
                Assert.That(result!.Success, Is.True,
                    $"{scenario.Id}: expected success but the tx reverted: {result.Error}");
                TestContext.Out.WriteLine($"{scenario.Id}: succeeded, gas {result.GasUsed}, tx {result.TxHash}");
            }
        }
        finally
        {
            if (snapshotId != null)
                await anvil.RevertAsync(snapshotId);
        }
    }

    private static async Task<ExecutionResult> ExecuteAnvilAsync(AnvilExecutionHelper anvil, TransferScenario scenario)
    {
        if (scenario.ReplayCalldata != null)
        {
            var rc = scenario.ReplayCalldata;
            var data = scenario.Mutation != null
                ? AnvilExecutionHelper.ApplyMutation(rc.Input, scenario.Mutation)
                : rc.Input;
            return await anvil.ExecuteTransactionAsync(rc.From, rc.To, data);
        }

        return await anvil.ReplayTransactionAsync(scenario.ReplayTxHash!, scenario.Mutation);
    }

    /// <summary>
    /// True for proxied-Anvil/test-env infrastructure hiccups (gateway timeouts, receipt-polling
    /// timeouts, rate limits) that must not be reported as contract reverts.
    /// </summary>
    private static bool IsTransientInfra(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        ReadOnlySpan<string> markers =
        [
            "GatewayTimeout", "timed out", "timeout", "receipt not found",
            "429", "TooManyRequests", "502", "503", "Bad Gateway", "Service Unavailable"
        ];
        foreach (var m in markers)
            if (error.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // =====================================================================
    //                              Helpers
    // =====================================================================

    /// <summary>
    /// Timestamp of the pinned block (max System_Block.timestamp at or below it), as a
    /// DateTimeOffset, so the projection tier computes demurrage at the historical block time
    /// rather than UtcNow. Falls back to UtcNow if the query returns nothing.
    /// </summary>
    private static async Task<DateTimeOffset> BlockTimestampAsync(TestEnvironmentClient session, long block)
    {
        var scalar = await session.ExecuteScalarAsync(
            $"SELECT max(\"timestamp\") FROM \"System_Block\" WHERE \"blockNumber\" <= {block}");
        if (scalar != null && long.TryParse(scalar.ToString(), out var ts) && ts > 0)
            return DateTimeOffset.FromUnixTimeSeconds(ts);

        // Fail loudly rather than silently falling back to UtcNow: for a pinned historical block
        // that would compute demurrage at wall-clock time, invalidating every balance in the fixture.
        // A real indexed block always has a System_Block row, so this only fires on a plumbing bug.
        Assert.Fail($"Could not resolve System_Block timestamp for pinned block {block} (scalar='{scalar}'). " +
                    "Refusing to compute demurrage at wall-clock time.");
        return default; // unreachable — Assert.Fail throws
    }

    private static bool TestEnvAvailable()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ENV_URL")))
            return true;
        Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run ScoreGroup matrix.");
        return false;
    }

    /// <summary>
    /// Returns true when the block is loadable. Calls Assert.Ignore (and returns false) when the
    /// environment is unhealthy or the block is not indexed.
    /// </summary>
    private static async Task<bool> BlockReadyAsync(long block)
    {
        if (SharedGraphCache.IsCached(block) || SharedAnvilCache.IsCached(block))
            return true;

        // Retry transient health/availability blips before giving up — staging occasionally
        // returns a slow/empty health response under load, which must not turn into a spurious skip.
        const int attempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var health = await TestEnvironmentClient.GetHealthAsync();
                if (health?.Status != "healthy")
                {
                    lastError = new InvalidOperationException($"health status '{health?.Status}'");
                }
                else if (!await TestEnvironmentClient.BlockExistsAsync(block))
                {
                    lastError = new InvalidOperationException($"block {block} not indexed");
                }
                else
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < attempts)
                await Task.Delay(1500 * attempt);
        }

        Assert.Ignore($"Test environment not available after {attempts} attempts: {lastError?.Message}");
        return false;
    }
}
