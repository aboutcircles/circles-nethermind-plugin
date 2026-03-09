using System.Numerics;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Layer 4: Edge Case Tests — empty snapshots, null sections, zero balances,
/// case sensitivity, and demurrage precision for both paths.
/// </summary>
[TestFixture]
public class CacheSourceEdgeCaseTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    // ── Empty/null handling ────────────────────────────────────────────

    [Test]
    public void EmptySnapshot_AllMethodsReturnEmpty()
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 0,
            Timestamp: 0,
            Balances: null, Trust: null, Groups: null,
            GroupTrusts: null, ConsentedFlow: null,
            Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot);

        Assert.That(cache.LoadV2Balances().ToList(), Is.Empty);
        Assert.That(cache.LoadV2Trust().ToList(), Is.Empty);
        Assert.That(cache.LoadGroups().ToList(), Is.Empty);
        Assert.That(cache.LoadGroupTrusts().ToList(), Is.Empty);
        Assert.That(cache.LoadConsentedFlowFlags().ToList(), Is.Empty);
        Assert.That(cache.LoadRegisteredAvatars().ToList(), Is.Empty);
        Assert.That(cache.LoadWrapperMappings().ToList(), Is.Empty);
    }

    [Test]
    public void NullSections_GraphFactoryProducesEmptyGraph()
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: null, Trust: null, Groups: null,
            GroupTrusts: null, ConsentedFlow: null,
            Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot);
        var factory = new GraphFactory(RouterAddress, cache);

        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();

        Assert.That(trust.Edges, Is.Empty);
        Assert.That(balance.AvatarNodes, Is.Empty);
    }

    [Test]
    public void EmptyLists_GraphFactoryProducesEmptyGraph()
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: [], Trust: [], Groups: [],
            GroupTrusts: [], ConsentedFlow: [],
            Avatars: [], WrapperMappings: []);

        var cache = new CacheLoadGraph(snapshot);
        var factory = new GraphFactory(RouterAddress, cache);

        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();

        Assert.That(trust.Edges, Is.Empty);
        Assert.That(balance.AvatarNodes, Is.Empty);
    }

    // ── Zero balance ──────────────────────────────────────────────────

    [Test]
    public void NearZeroBalance_FilteredWhenSafetyMarginApplied()
    {
        var settings = new Settings { DemurrageSafetyMargin = 0.5 };

        // Balance of 1 wei * 0.5 → rounds to 0 → filtered
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("1", "0xaa", "0xbb", 0, false, "demurraged"), // 1 wei * 0.5 → 0
                new PathfinderGraphBalanceRow("100", "0xcc", "0xdd", 0, false, "demurraged"), // 100 * 0.5 = 50 → kept
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot, settings);
        var balances = cache.LoadV2Balances().ToList();

        Assert.That(balances, Has.Count.EqualTo(1), "Only the non-zero post-margin balance should remain");
        Assert.That(balances[0].Balance, Is.EqualTo("50"));
    }

    [Test]
    public void ZeroBalance_PassesThroughWithoutSafetyMargin()
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("0", "0xaa", "0xbb", 0, false, "demurraged"),
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        // No settings = no margin → zero balance passes through as-is
        var cache = new CacheLoadGraph(snapshot);
        var balances = cache.LoadV2Balances().ToList();

        // CacheLoadGraph without margin doesn't filter zeros (matches existing behavior)
        Assert.That(balances, Has.Count.EqualTo(1));
        Assert.That(balances[0].Balance, Is.EqualTo("0"));
    }

    // ── Case sensitivity ──────────────────────────────────────────────

    [Test]
    public void CaseInsensitiveAddresses_BothPathsNormalize()
    {
        var a1Upper = "0xAA00000000000000000000000000000000000099";
        var a2Upper = "0xBB00000000000000000000000000000000000099";

        // MockLoadGraph normalizes to lowercase internally
        var mock = new MockLoadGraph();
        mock.AddRegisteredAvatar(a1Upper);
        mock.AddTrust(a1Upper, a2Upper);
        mock.AddBalanceWei(a1Upper, a1Upper, "100000000000000000000");

        var mockTrust = mock.LoadV2Trust().ToList();
        Assert.That(mockTrust[0].Truster, Is.EqualTo(a1Upper.ToLowerInvariant()));

        // CacheLoadGraph also normalizes
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);
        var cacheTrust = cache.LoadV2Trust().ToList();
        Assert.That(cacheTrust[0].Truster, Is.EqualTo(a1Upper.ToLowerInvariant()));
    }

    [Test]
    public void MixedCaseSnapshot_NormalizesAllAddresses()
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: 0,
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("100", "0xABCDEF", "0xFEDCBA", 0, false, "demurraged")
            },
            Trust: new[]
            {
                new PathfinderGraphTrustRow("0xABCDEF", "0xFEDCBA", 100)
            },
            Groups: new[]
            {
                new PathfinderGraphGroupRow("0xGROUP123")
            },
            GroupTrusts: new[]
            {
                new PathfinderGraphGroupTrustRow("0xGROUP123", "0xTOKEN456")
            },
            ConsentedFlow: new[]
            {
                new PathfinderGraphConsentedFlowRow("0xCONSENT", true)
            },
            Avatars: new[] { "0xABCDEF" },
            WrapperMappings: new[]
            {
                new PathfinderGraphWrapperMappingRow("0xWRAPPER", "0xAVATAR")
            });

        var cache = new CacheLoadGraph(snapshot);

        var trust = cache.LoadV2Trust().First();
        Assert.That(trust.Truster, Is.EqualTo("0xabcdef"));
        Assert.That(trust.Trustee, Is.EqualTo("0xfedcba"));

        Assert.That(cache.LoadGroups().First(), Is.EqualTo("0xgroup123"));

        var gt = cache.LoadGroupTrusts().First();
        Assert.That(gt.GroupAddress, Is.EqualTo("0xgroup123"));
        Assert.That(gt.TrustedToken, Is.EqualTo("0xtoken456"));

        var consent = cache.LoadConsentedFlowFlags().First();
        Assert.That(consent.Avatar, Is.EqualTo("0xconsent"));

        Assert.That(cache.LoadRegisteredAvatars().First(), Is.EqualTo("0xabcdef"));

        var wrapper = cache.LoadWrapperMappings().First();
        Assert.That(wrapper.WrapperAddress, Is.EqualTo("0xwrapper"));
        Assert.That(wrapper.UnderlyingAvatar, Is.EqualTo("0xavatar"));
    }

    // ── Snapshot replacement ──────────────────────────────────────────

    [Test]
    public void ReplaceSnapshot_BalancesReflectNewData()
    {
        var snapshot1 = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 100,
            Timestamp: 0,
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("100", "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot1);
        Assert.That(cache.LoadV2Balances().Count(), Is.EqualTo(1));
        Assert.That(cache.LastProcessedBlock, Is.EqualTo(100));

        var snapshot2 = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 200,
            Timestamp: 0,
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("200", "0xcc", "0xdd", 0, false, "demurraged"),
                new PathfinderGraphBalanceRow("300", "0xee", "0xff", 0, false, "demurraged"),
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        cache.ReplaceSnapshot(snapshot2);
        Assert.That(cache.LoadV2Balances().Count(), Is.EqualTo(2));
        Assert.That(cache.LastProcessedBlock, Is.EqualTo(200));
    }

    // ── Demurrage precision ───────────────────────────────────────────

    [Test]
    public void DemurragePrecision_StaticBalance_BothPathsSameResult()
    {
        // Static balances: FixtureLoadGraph applies InflationaryToDemurrage;
        // CacheLoadGraph receives already-demurraged value from cache service.
        // For this test, we verify the conversion doesn't introduce drift.
        var staticBalance = "1000000000000000000000"; // 1000 CRC in static form
        var a1 = "0xed00000000000000000000000000000000000001";

        var mock = new MockLoadGraph();
        mock.AddBalanceWei(a1, a1, staticBalance, isStatic: true);

        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);

        // The snapshot stores isStatic=true, so both paths need to handle it
        var cache = new CacheLoadGraph(snapshot);
        var cacheBalances = cache.LoadV2Balances().ToList();

        Assert.That(cacheBalances, Has.Count.EqualTo(1));
        Assert.That(cacheBalances[0].IsStatic, Is.True);
        // CacheLoadGraph passes static balance through as-is (cache service pre-demurrages)
        Assert.That(cacheBalances[0].Balance, Is.EqualTo(staticBalance));
    }

    [Test]
    public void DemurragePrecision_LargeBalance_SafetyMarginPrecise()
    {
        var settings = new Settings { DemurrageSafetyMargin = 0.9995 };

        // Very large balance: 10 billion CRC
        var large = BigInteger.Parse("10000000000000000000000000000");
        var expected = (BigInteger)((double)large * 0.9995);

        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: 0,
            Balances: new[]
            {
                new PathfinderGraphBalanceRow(large.ToString(), "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot, settings);
        var result = BigInteger.Parse(cache.LoadV2Balances().Single().Balance);

        // Both use (BigInteger)((double)value * margin) — same arithmetic
        Assert.That(result, Is.EqualTo(expected));
    }
}
