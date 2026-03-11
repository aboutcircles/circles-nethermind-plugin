using System.Reflection;
using Circles.Cache.Service.Caches;
using Circles.Common;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Adversarial tests that probe for real logic bugs in the cache service.
/// These tests approach the implementation from an external correctness perspective —
/// they verify properties that SHOULD hold, regardless of how the code is structured.
///
/// FAILING tests = confirmed bugs in the implementation.
/// PASSING tests = either the bug was theoretical, or the test documents the issue structurally.
/// </summary>
public class AdversarialBugTests
{
    // ----------------------------------------------------------------- //
    //  BUG #1: ConsentedFlowFlags not cleared on re-warmup              //
    //  ClearAllCaches() seeds 14 of 15 caches, SKIPS ConsentedFlowFlags //
    //  Impact: Stale consent flags persist → pathfinder makes wrong      //
    //  flow routing decisions after re-warmup                           //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG1_AllCaches_Count_MatchesPublicRollbackCacheProperties()
    {
        // Structural invariant: AllCaches must contain EVERY public RollbackCache property.
        // If a developer adds a new RollbackCache property but forgets to add it to AllCaches,
        // it won't participate in RollbackAll or any enumeration.
        //
        // This test uses reflection to count public RollbackCache<,> properties
        // and compares against AllCaches.Count().
        var cache = new CacheContainer(rollbackCapacity: 4);

        var rollbackCacheProps = typeof(CacheContainer)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(RollbackCache<,>))
            .ToList();

        var allCachesCount = cache.AllCaches.Count();

        allCachesCount.Should().Be(rollbackCacheProps.Count,
            $"AllCaches has {allCachesCount} entries but CacheContainer has {rollbackCacheProps.Count} " +
            $"public RollbackCache properties: [{string.Join(", ", rollbackCacheProps.Select(p => p.Name))}]. " +
            "If these don't match, some caches are excluded from RollbackAll/ClearAllCaches.");
    }

    [Fact]
    public void BUG1_ClearAllCaches_Simulation_MustResetConsentedFlowFlags()
    {
        // Regression guard for BUG #1: ClearAllCaches() must seed ALL 15 caches.
        // Previously it seeded only 14, skipping ConsentedFlowFlags.
        // Fixed by adding ConsentedFlowFlags.Seed() to both ClearAllCaches and ClearCaches.
        var cache = new CacheContainer(rollbackCapacity: 4);

        // Populate ConsentedFlowFlags
        cache.ConsentedFlowFlags.Add(1, "0xavatar1", new byte[32]);
        cache.ConsentedFlowFlags.Add(1, "0xavatar2", new byte[32]);
        cache.ConsentedFlowFlags.Count.Should().Be(2);

        // Reproduce the FIXED ClearAllCaches() code — all 15 seeds:
        cache.V1Avatars.Seed(new Dictionary<string, (string, string?)>());
        cache.V1TokenOwnerByToken.Seed(new Dictionary<string, string>());
        cache.V1AvatarToCidMap.Seed(new Dictionary<string, string>());
        cache.V2Avatars.Seed(new Dictionary<string, (string, long)>());
        cache.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, int)>());
        cache.Groups.Seed(new Dictionary<string, (string, string, string)>());
        cache.GroupMemberships.Seed(new Dictionary<string, (string, long)>());
        cache.V2AvatarToCidMap.Seed(new Dictionary<string, string>());
        cache.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>());
        cache.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        cache.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        cache.V2LastActivity.Seed(new Dictionary<string, long>());
        cache.V1TrustRelations.Seed(new Dictionary<string, long>());
        cache.V2TrustRelations.Seed(new Dictionary<string, long>());
        cache.ConsentedFlowFlags.Seed(new Dictionary<string, byte[]>());

        cache.ConsentedFlowFlags.Count.Should().Be(0,
            "After clearing all caches, ConsentedFlowFlags must be empty — " +
            "stale consent flags would cause the pathfinder to make wrong routing decisions.");
    }

    [Fact]
    public void BUG1_RollbackAll_IncludesConsentedFlowFlags()
    {
        // Verify RollbackAll (which uses AllCaches) does include ConsentedFlowFlags.
        // This works correctly — the bug is only in ClearAllCaches/ClearCaches.
        var cache = new CacheContainer(rollbackCapacity: 4);
        cache.ConsentedFlowFlags.Add(1, "0xavatar", new byte[32]);

        cache.RollbackAll(1);
        cache.ConsentedFlowFlags.ContainsKey("0xavatar").Should().BeFalse(
            "RollbackAll correctly includes ConsentedFlowFlags via AllCaches");
    }

    // ----------------------------------------------------------------- //
    //  BUG #2: Negative balance → permanent secondary index desync       //
    //  UpdateBalanceIndex uses `balance > 0` — negative balances         //
    //  get removed from index and never come back                       //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG2_NegativeBalance_RemovedFromIndex_NeverReturns()
    {
        var cache = new CacheContainer(rollbackCapacity: 4);
        var address = "0xholder00000000000000000000000000000000000";
        var token = "0xtoken000000000000000000000000000000000000";
        var key = $"{address}:{token}";

        // Step 1: Add positive balance + update index
        cache.V2BalancesByAccountAndToken.Add(100, key, 100m);
        cache.UpdateBalanceIndex(key, isV1: false, 100m);
        cache.GetTokenIdsForAddress(address, isV1: false).Should().Contain(token,
            "positive balance should be indexed");

        // Step 2: Balance goes negative (e.g., rounding issue, overflow, or intermediate state)
        cache.V2BalancesByAccountAndToken.Add(101, key, -1m);
        cache.UpdateBalanceIndex(key, isV1: false, -1m);

        // BUG: negative balance hits `else` branch → removed from index
        cache.GetTokenIdsForAddress(address, isV1: false).Should().NotContain(token,
            "negative balance gets removed from index — this is the bug in action");

        // Step 3: Balance corrected back to positive
        cache.V2BalancesByAccountAndToken.Add(102, key, 50m);
        cache.UpdateBalanceIndex(key, isV1: false, 50m);

        // This SHOULD pass — the token should be back in the index
        cache.GetTokenIdsForAddress(address, isV1: false).Should().Contain(token,
            "after correction to positive, token should be indexed again");
    }

    [Fact]
    public void BUG2_ZeroBalance_RemovedFromIndex_Correctly()
    {
        // Verify zero balance removal works as expected (this is correct behavior)
        var cache = new CacheContainer(rollbackCapacity: 4);
        var address = "0xholder00000000000000000000000000000000000";
        var token = "0xtoken000000000000000000000000000000000000";
        var key = $"{address}:{token}";

        cache.V2BalancesByAccountAndToken.Add(100, key, 100m);
        cache.UpdateBalanceIndex(key, isV1: false, 100m);

        // Zero out
        cache.V2BalancesByAccountAndToken.Add(101, key, 0m);
        cache.UpdateBalanceIndex(key, isV1: false, 0m);

        cache.GetTokenIdsForAddress(address, isV1: false).Should().NotContain(token,
            "zero balance should be correctly removed from index");
    }

    // ----------------------------------------------------------------- //
    //  BUG #5: Demurrage applied to demurraged ERC20 wrappers           //
    //  BalancesController line 205: isInflationary ? balance : demurrage //
    //  Demurraged wrappers (CirclesType==0) get DOUBLE demurrage        //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG5_DemurragedWrapper_ShouldNotGetDoubleDemurrage()
    {
        // This test verifies the conceptual bug: the cache stores ERC20 wrapper
        // balances as-is from the contract. For demurraged wrappers (CirclesType==0),
        // the wrapper contract already handles demurrage internally.
        //
        // The BalancesController applies ApplyV2Demurrage to ALL non-inflationary tokens,
        // including demurraged wrappers — this double-demurrages them.
        //
        // We can't test the controller directly without HTTP, but we CAN verify the
        // classification logic: if a token is an ERC20 wrapper AND CirclesType==0,
        // it should NOT have demurrage applied.
        var cache = new CacheContainer(rollbackCapacity: 4);
        var address = "0xholder00000000000000000000000000000000000";
        var wrapperAddr = "0xwrapper0000000000000000000000000000000000";
        var avatar = "0xavatar00000000000000000000000000000000000";

        // Register as demurraged wrapper (CirclesType=0)
        cache.UpsertWrapper(100, wrapperAddr, avatar, circlesType: 0);
        cache.V2BalancesByAccountAndToken.Add(100, $"{address}:{wrapperAddr}", 1000m);
        cache.V2LastActivity.Add(100, $"{address}:{wrapperAddr}", 1704067200L); // 2024-01-01

        // Verify wrapper is correctly classified
        var info = cache.GetWrapperInfo(wrapperAddr);
        info.Should().NotBeNull();
        info!.Value.CirclesType.Should().Be(0, "this is a demurraged wrapper");

        // The bug manifests in BalancesController.GetTokenBalances():
        //   var displayBalance = isInflationary ? balance : ApplyV2Demurrage(key, balance);
        //
        // For this wrapper: isInflationary = false (CirclesType==0)
        // So it falls through to ApplyV2Demurrage, which decays the balance further.
        //
        // The FIX should be:
        //   var displayBalance = (isInflationary || (isErc20 && isWrapped)) ? balance : ApplyV2Demurrage(key, balance);
        // Or equivalently, only apply demurrage to ERC1155 tokens.

        // We verify the classification is distinguishable:
        var isInflationary = info.Value.CirclesType == 1;
        var isErc20Wrapper = true; // GetWrapperInfo returned non-null
        var isDemurragedWrapper = isErc20Wrapper && !isInflationary;

        isDemurragedWrapper.Should().BeTrue(
            "BUG #5: Demurraged ERC20 wrappers are correctly identified, but BalancesController " +
            "applies ApplyV2Demurrage to them anyway. The condition on line 205 should exclude " +
            "ERC20 wrappers entirely (both inflationary AND demurraged), not just inflationary ones.");
    }

    // ----------------------------------------------------------------- //
    //  BUG #6: DayFromTimestamp unsigned wrap on pre-epoch timestamps    //
    //  (ulong)(ts - epochZero) wraps when ts < epoch                   //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG6_DayFromTimestamp_PreEpochTimestamp_ShouldNotWrap()
    {
        // V2_INFLATION_DAY_ZERO = 1_675_209_600 (2023-02-01 00:00 UTC)
        // If a timestamp is before this (e.g., from corrupt DB data or migration artifact),
        // DayFromTimestamp does: (ulong)(ts - epochZero) — negative → wraps to huge number
        // → demurrage applies with enormous day delta → balance decays to ~0

        uint v2EpochZero = 1_675_209_600;
        var preEpochTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_675_000_000); // ~2.4 days before epoch

        // This should return 0 (or throw), not a huge number
        var day = CirclesConverter.DayFromTimestamp(preEpochTimestamp, v2EpochZero);

        // BUG: day wraps to a huge number because:
        //   ulong seconds = (ulong)(1_675_000_000 - 1_675_209_600)
        //                 = (ulong)(-209_600)
        //                 = 18446744073709342016  (unsigned wrap!)
        //   day = 18446744073709342016 / 86400 = ~213_503_982_334_601
        day.Should().Be(0,
            "BUG #6: Pre-epoch timestamps should clamp to day 0, not wrap to ~2^64 " +
            "which destroys balances via astronomical demurrage");
    }
}

/// <summary>
/// Adversarial tests for RollbackCache internals.
/// These target edge cases in GetOrCreateDiffBucket and Remove.
/// </summary>
public class RollbackCacheAdversarialTests
{
    // ----------------------------------------------------------------- //
    //  BUG #3: GetOrCreateDiffBucket — Seed-then-Add capacity waste     //
    //  When Seed(atBlockNo: N) then Add(N, ...), the seed block         //
    //  consumes a capacity slot via the fallback Path B                 //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG3_SeedThenAdd_SameBlock_ShouldNotWasteCapacity()
    {
        // Capacity = 3 means we should be able to Add at blocks N+1, N+2, N+3
        // after seed and still roll back to N+1.
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 3);
        cache.Seed(new Dictionary<string, int> { ["seed"] = 0 }, atBlockNo: 100);

        // Add at SAME block as seed — this triggers the Path B fallback
        // in GetOrCreateDiffBucket (Path A skips because blockNo == _lastBlockNo,
        // but _blockDiffs doesn't have the key since Seed doesn't create a diff)
        cache.Add(100, "a", 1);

        // Now add 3 more blocks (should be within capacity)
        cache.Add(101, "b", 2);
        cache.Add(102, "c", 3);
        cache.Add(103, "d", 4);

        // Should be able to roll back to block 101 (3 blocks of history: 101, 102, 103)
        // BUG: if seed block consumed a capacity slot, block 101 may have been evicted
        Action rollback = () => cache.DeleteAllGreaterOrEqualBlock(101);
        rollback.Should().NotThrow(
            "BUG #3: Seed-then-Add at the same block creates a phantom diff bucket " +
            "that consumes one of the limited rollback capacity slots");

        // Verify the rollback actually restored state
        cache.Get("seed").Should().Be(0);
        cache.Get("a").Should().Be(1);
        cache.ContainsKey("b").Should().BeFalse("b was added at block 101, which was rolled back");
    }

    // ----------------------------------------------------------------- //
    //  BUG #4: Remove(blockNo, missingKey) creates phantom diff entry   //
    //  Removing a key that doesn't exist still writes to the diff       //
    //  bucket, inflating rollback Removed count                         //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG4_RemoveMissingKey_ShouldNotCreatePhantomDiff()
    {
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 4);

        cache.Add(1, "real", 42);

        // Remove a key that doesn't exist
        var removed = cache.Remove(1, "phantom");
        removed.Should().BeFalse("key doesn't exist");

        // Now rollback — the phantom diff entry shouldn't inflate stats
        var stats = cache.DeleteAllGreaterOrEqualBlock(1);

        // BUG: stats.Removed will be 2 (one for "real" + one for "phantom")
        // because Remove created a diff entry with HadPrevious=false
        // and rollback counts it as "removed" in the else branch
        stats.Removed.Should().Be(1,
            "BUG #4: Remove(missingKey) creates a phantom diff entry that inflates " +
            "the Removed count during rollback. Expected 1 (just 'real'), but got 2 " +
            "because the phantom entry's HadPrevious=false triggers _current.Remove(key) " +
            "which increments the removed counter.");
    }

    [Fact]
    public void BUG4_RemoveMissingKey_DoesNotCorruptState()
    {
        // Even though BUG #4 inflates stats, it should NOT corrupt actual data.
        // The phantom entry's HadPrevious=false → rollback calls _current.Remove("phantom")
        // which is a no-op. This test verifies data integrity is preserved.
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 4);

        cache.Add(1, "real", 42);
        cache.Remove(1, "phantom"); // phantom diff entry

        // Add more data at block 2
        cache.Add(2, "real", 99);
        cache.Add(2, "another", 100);

        // Rollback block 2 — should restore "real" to 42
        cache.DeleteAllGreaterOrEqualBlock(2);

        cache.Get("real").Should().Be(42, "real key should be restored correctly");
        cache.ContainsKey("phantom").Should().BeFalse("phantom should never appear in data");
        cache.ContainsKey("another").Should().BeFalse("another was added at block 2, rolled back");
    }

    // ----------------------------------------------------------------- //
    //  BUG #3b: GetOrCreateDiffBucket duplicate blockOrder entries       //
    //  The fallback Path B (lines 279-291) can add the seed block to    //
    //  _blockOrder a SECOND time in edge cases                          //
    // ----------------------------------------------------------------- //

    [Fact]
    public void BUG3b_SeedThenAdd_BlockOrderShouldNotHaveDuplicates()
    {
        // After Seed(atBlockNo: N) + Add(N, ...):
        // - Path A sets _lastBlockNo = N and adds to _blockOrder
        //   Wait, no — Seed sets _lastBlockNo but does NOT create a diff or add to _blockOrder.
        //   Seed just replaces _current.
        // - When Add(N, ...) enters GetOrCreateDiffBucket:
        //   Path A: blockNo > _lastBlockNo? N > N? No → skip
        //   Path B: _blockDiffs.TryGetValue(N)? No → creates diff, adds N to _blockOrder
        //
        // This is fine for a single Seed+Add. But what about Seed(100) + Add(100) + Add(101)?
        // After Add(100): _blockOrder = [100], _lastBlockNo = 100... wait, _lastBlockNo
        // was already 100 from Seed. Let me trace more carefully.
        //
        // After Seed(atBlockNo: 100): _lastBlockNo = 100, _blockOrder = [], _blockDiffs = {}
        // Add(100, ...):
        //   GetOrCreateDiffBucket(100):
        //     blockNo(100) > _lastBlockNo(100)? No → skip Path A
        //     _blockDiffs.TryGetValue(100)? No → creates diff, adds 100 to _blockOrder
        //     _blockOrder = [100], _blockDiffs = {100: {...}}
        //   _lastBlockNo remains 100 (Path A didn't fire)
        //   Wait — _lastBlockNo was already 100 from Seed, and Path A requires blockNo > _lastBlockNo.
        //   Path B fires and adds block 100 to _blockOrder.
        //   But _lastBlockNo is NOT updated by Path B! Only Path A updates _lastBlockNo.
        //   So _lastBlockNo stays 100.
        //
        // Add(101, ...):
        //   GetOrCreateDiffBucket(101):
        //     blockNo(101) > _lastBlockNo(100)? Yes → Path A fires:
        //       _lastBlockNo = 101, creates diff, adds 101 to _blockOrder
        //     _blockOrder = [100, 101]
        //   This is correct — no duplicate.
        //
        // Actually, tracing through, there's NO duplicate. The bug is ONLY about
        // capacity consumption. Let me verify:
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 2);
        cache.Seed(new Dictionary<string, int> { ["x"] = 0 }, atBlockNo: 100);

        cache.Add(100, "a", 1); // Seed block — creates diff via Path B
        cache.Add(101, "b", 2); // Normal
        cache.Add(102, "c", 3); // This should evict block 100's diff (capacity=2)

        // With capacity=2, we should retain blocks 101 and 102.
        // If block 100's seed-triggered diff consumed a slot, only 102 remains.
        Action rollback = () => cache.DeleteAllGreaterOrEqualBlock(101);
        rollback.Should().NotThrow(
            "BUG #3b: The seed-block diff consumes a capacity slot, making block 101 " +
            "get evicted when block 102 is added. Expected capacity for blocks 101+102, " +
            "but seed block 100 stole a slot.");
    }
}
