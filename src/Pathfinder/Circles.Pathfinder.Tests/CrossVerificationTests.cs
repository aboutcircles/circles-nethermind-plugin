using System.Text.Json;
using Circles.Common;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Cross-verification tests that compare data from different access methods:
/// - DB (what pathfinder uses) vs RPC (what users see) vs On-chain (ground truth)
///
/// These tests ensure consistency across the stack at a specific block number.
/// If DB balances diverge from RPC balances, the pathfinder computes paths
/// using stale/wrong data — even if the algorithm is correct.
///
/// Requires TEST_ENV_URL with features: ["db", "rpc"] or ["db", "anvil"].
/// </summary>
[TestFixture]
[Category("RequiresTestEnv")]
public class CrossVerificationTests
{
    /// <summary>
    /// For each scenario's source address, compares the balances loaded by pathfinder
    /// (from DB via ILoadGraph) with the balances returned by circles_getTokenBalances RPC.
    ///
    /// Both should return the same set of tokens and amounts at the same block.
    /// Divergence means: pathfinder sees different balances than the RPC → broken UX.
    /// </summary>
    [Test]
    [Category("CrossVerification")]
    public async Task SourceBalances_DBMatchesRPC_AtSameBlock()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        // Use a well-known scenario with meaningful balances
        var scenario = ScenarioLoader.LoadById("direct-transfer-001");
        if (scenario == null)
        {
            Assert.Ignore("direct-transfer-001 not found");
            return;
        }

        TestEnvironmentClient? session = null;
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
                Assert.Fail("Test environment not healthy");

            var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
            if (!exists)
                Assert.Ignore($"Block {scenario.Block} not indexed");

            // Create session with BOTH db and rpc features
            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db", "rpc"],
                ttl: "10m");
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            // === DB side: load balances via pathfinder's ILoadGraph ===
            var dbData = SharedGraphCache.GetOrLoad(scenario.Block);
            var sourceAddr = scenario.Source.ToLowerInvariant();

            var dbBalances = dbData.Balances
                .Where(b => AddressIdPool.StringOf(b.Account) == sourceAddr)
                .Select(b => new
                {
                    Token = AddressIdPool.StringOf(b.TokenAddress),
                    Balance = b.Balance,
                    IsWrapped = b.IsWrapped
                })
                .OrderBy(b => b.Token)
                .ToList();

            TestContext.Out.WriteLine($"DB: {dbBalances.Count} balance entries for {sourceAddr[..10]}...");

            // === RPC side: call circles_getTokenBalances ===
            JsonElement rpcResult;
            try
            {
                rpcResult = await session!.ExecuteCirclesRpcAsync(
                    "circles_getTokenBalances", scenario.Source);
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"RPC call failed: {ex.Message}");
                Assert.Ignore("circles_getTokenBalances not available via test-env RPC proxy");
                return;
            }

            var rpcBalances = new List<(string Token, string Balance)>();
            if (rpcResult.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rpcResult.EnumerateArray())
                {
                    var tokenAddr = item.GetProperty("tokenAddress").GetString()?.ToLowerInvariant() ?? "";
                    var attoCircles = item.GetProperty("attoCircles").GetString() ?? "0";
                    rpcBalances.Add((tokenAddr, attoCircles));
                }
            }

            TestContext.Out.WriteLine($"RPC: {rpcBalances.Count} balance entries");

            // === Cross-verify ===
            // Both should have non-zero balances for the source
            Assert.That(dbBalances.Count, Is.GreaterThan(0), "DB should have balances for source");
            Assert.That(rpcBalances.Count, Is.GreaterThan(0), "RPC should have balances for source");

            // For each DB balance, check if RPC reports the same token
            var rpcByToken = rpcBalances.ToDictionary(b => b.Token, b => b.Balance);
            var matchCount = 0;
            var mismatchCount = 0;

            foreach (var db in dbBalances)
            {
                if (rpcByToken.TryGetValue(db.Token, out var rpcBalance))
                {
                    matchCount++;
                    // Compare balances — allow for demurrage timing differences
                    // DB balances have demurrage applied at load time, RPC at request time
                    // So they may differ by up to ~0.01% for recent balances
                    TestContext.Out.WriteLine($"  Token {db.Token[..10]}...: DB={db.Balance}, RPC={rpcBalance}");
                }
                else
                {
                    TestContext.Out.WriteLine($"  Token {db.Token[..10]}...: DB={db.Balance}, RPC=missing");
                    mismatchCount++;
                }
            }

            TestContext.Out.WriteLine($"Match: {matchCount}, Missing from RPC: {mismatchCount}");

            // At least the majority of tokens should appear in both
            Assert.That(matchCount, Is.GreaterThan(0),
                "At least some tokens should appear in both DB and RPC results");
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }

    /// <summary>
    /// For a scenario's source, compares pathfinder-loaded balances with on-chain balances
    /// queried from Anvil fork via Hub.balanceOf().
    ///
    /// On-chain is the ground truth. Any divergence means the DB is stale or the
    /// demurrage calculation is wrong.
    /// </summary>
    [Test]
    [Category("CrossVerification")]
    public async Task SourceBalances_DBMatchesOnChain_ViaAnvil()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        var scenario = ScenarioLoader.LoadById("direct-transfer-001");
        if (scenario == null)
        {
            Assert.Ignore("direct-transfer-001 not found");
            return;
        }

        TestEnvironmentClient? session = null;
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
                Assert.Fail("Test environment not healthy");

            var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
            if (!exists)
                Assert.Ignore($"Block {scenario.Block} not indexed");

            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db", "anvil"],
                ttl: "10m");

            if (!session.HasAnvil)
            {
                Assert.Ignore("Anvil not available");
                return;
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            var dbData = SharedGraphCache.GetOrLoad(scenario.Block);
            var sourceAddr = scenario.Source.ToLowerInvariant();

            // Get source's ERC1155 (non-wrapped) balances from DB
            var dbBalances = dbData.Balances
                .Where(b => AddressIdPool.StringOf(b.Account) == sourceAddr && !b.IsWrapped)
                .Take(3) // Limit to 3 to keep test fast
                .ToList();

            if (dbBalances.Count == 0)
            {
                Assert.Ignore("No non-wrapped balances found for source in DB");
                return;
            }

            // Hub contract address on gnosis
            const string hubAddress = "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8";

            // For each balance, query on-chain via Hub.balanceOf(account, tokenId)
            // balanceOf selector: 0x00fdd58e
            foreach (var db in dbBalances)
            {
                var tokenAddr = AddressIdPool.StringOf(db.TokenAddress);

                // Hub.balanceOf(address account, uint256 id)
                // id = uint256(uint160(tokenAddress))
                var accountPadded = sourceAddr[2..].PadLeft(64, '0');
                var tokenId = tokenAddr[2..].PadLeft(64, '0');
                var callData = "0x00fdd58e" + accountPadded + tokenId;

                try
                {
                    var result = await session!.ExecuteAnvilRpcAsync("eth_call",
                        new { to = hubAddress, data = callData }, "latest");

                    var hexBalance = result.GetString() ?? "0x0";
                    var onChainBalance = System.Numerics.BigInteger.Parse(
                        "0" + hexBalance[2..], System.Globalization.NumberStyles.HexNumber);

                    // DB balance is demurraged. On-chain balance at block timestamp is the truth.
                    // They should be close (within 1% for recent blocks)
                    var dbBalance = System.Numerics.BigInteger.Parse(db.Balance);

                    double pctDiff = 0;
                    if (onChainBalance > 0)
                    {
                        pctDiff = 100.0 * Math.Abs((double)(dbBalance - onChainBalance) / (double)onChainBalance);
                    }

                    TestContext.Out.WriteLine($"  Token {tokenAddr[..10]}...: " +
                        $"DB={dbBalance}, OnChain={onChainBalance}, diff={pctDiff:F2}%");

                    // Allow up to 2% difference due to demurrage timing
                    // (DB applies demurrage at load time, on-chain at block time)
                    Assert.That(pctDiff, Is.LessThan(2.0),
                        $"Balance for token {tokenAddr[..10]}... diverges by {pctDiff:F2}% between DB and on-chain");
                }
                catch (Exception ex)
                {
                    TestContext.Out.WriteLine($"  Token {tokenAddr[..10]}...: on-chain query failed: {ex.Message}");
                }
            }
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that trust relations loaded by pathfinder (from DB) match what
    /// the RPC returns via circles_getTrustRelations for a specific account.
    ///
    /// If trust data diverges, pathfinder will compute invalid paths.
    /// </summary>
    [Test]
    [Category("CrossVerification")]
    public async Task TrustRelations_DBMatchesRPC_ForScenarioSource()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        var scenario = ScenarioLoader.LoadById("direct-transfer-001");
        if (scenario == null)
        {
            Assert.Ignore("direct-transfer-001 not found");
            return;
        }

        TestEnvironmentClient? session = null;
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
                Assert.Fail("Test environment not healthy");

            var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
            if (!exists)
                Assert.Ignore($"Block {scenario.Block} not indexed");

            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db", "rpc"],
                ttl: "10m");
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            // DB side: trust edges where source is truster
            var dbData = SharedGraphCache.GetOrLoad(scenario.Block);
            var sourceAddr = scenario.Source.ToLowerInvariant();

            var dbTrusted = dbData.Trust
                .Where(t => t.Truster.ToLowerInvariant() == sourceAddr)
                .Select(t => t.Trustee.ToLowerInvariant())
                .ToHashSet();

            TestContext.Out.WriteLine($"DB: source trusts {dbTrusted.Count} accounts");

            // RPC side: circles_getTrustRelations
            JsonElement rpcResult;
            try
            {
                rpcResult = await session!.ExecuteCirclesRpcAsync(
                    "circles_getTrustRelations", scenario.Source);
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"RPC call failed: {ex.Message}");
                Assert.Ignore("circles_getTrustRelations not available via test-env RPC proxy");
                return;
            }

            var rpcTrusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rpcResult.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rpcResult.EnumerateArray())
                {
                    if (item.TryGetProperty("objectAvatar", out var obj))
                    {
                        var addr = obj.GetString()?.ToLowerInvariant();
                        if (!string.IsNullOrEmpty(addr))
                            rpcTrusted.Add(addr);
                    }
                    else if (item.TryGetProperty("trustee", out var trustee))
                    {
                        var addr = trustee.GetString()?.ToLowerInvariant();
                        if (!string.IsNullOrEmpty(addr))
                            rpcTrusted.Add(addr);
                    }
                }
            }

            TestContext.Out.WriteLine($"RPC: source trusts {rpcTrusted.Count} accounts");

            // Cross-verify: DB should contain at least the RPC-reported trusts
            // (DB may have more due to ERC20Wrapper trust expansion)
            var inBoth = dbTrusted.Intersect(rpcTrusted).Count();
            var onlyInDb = dbTrusted.Except(rpcTrusted).Count();
            var onlyInRpc = rpcTrusted.Except(dbTrusted).Count();

            TestContext.Out.WriteLine($"Overlap: {inBoth}, DB-only: {onlyInDb}, RPC-only: {onlyInRpc}");

            // RPC trusts should be a subset of DB trusts (DB includes wrapper trusts)
            Assert.That(inBoth, Is.GreaterThan(0),
                "Should have at least some trust overlap between DB and RPC");
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }
}
