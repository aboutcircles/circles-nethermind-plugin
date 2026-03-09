using System.Numerics;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Layer 1: Data Equivalence Tests — construct identical synthetic data,
/// feed through both MockLoadGraph and CacheLoadGraph (via PathfinderGraphSnapshot),
/// compare outputs element-by-element.
/// </summary>
[TestFixture]
public class CacheSourceEquivalenceTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    // ── ILoadGraph method equivalence ──────────────────────────────────

    [Test]
    public void MockVsCache_LoadV2Balances_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockBalances = mock.LoadV2Balances().ToList();
        var cacheBalances = cache.LoadV2Balances().ToList();

        Assert.That(cacheBalances, Has.Count.EqualTo(mockBalances.Count),
            "Balance count mismatch");

        for (int i = 0; i < mockBalances.Count; i++)
        {
            Assert.That(cacheBalances[i].Balance, Is.EqualTo(mockBalances[i].Balance),
                $"Balance value mismatch at index {i}");
            Assert.That(cacheBalances[i].Account, Is.EqualTo(mockBalances[i].Account),
                $"Account mismatch at index {i}");
            Assert.That(cacheBalances[i].TokenAddress, Is.EqualTo(mockBalances[i].TokenAddress),
                $"TokenAddress mismatch at index {i}");
            Assert.That(cacheBalances[i].IsWrapped, Is.EqualTo(mockBalances[i].IsWrapped),
                $"IsWrapped mismatch at index {i}");
            Assert.That(cacheBalances[i].IsStatic, Is.EqualTo(mockBalances[i].IsStatic),
                $"IsStatic mismatch at index {i}");
        }
    }

    [Test]
    public void MockVsCache_LoadV2Trust_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockTrust = mock.LoadV2Trust().ToList();
        var cacheTrust = cache.LoadV2Trust().ToList();

        Assert.That(cacheTrust, Has.Count.EqualTo(mockTrust.Count));

        var mockSet = mockTrust.Select(t => (t.Truster, t.Trustee, t.Limit)).ToHashSet();
        var cacheSet = cacheTrust.Select(t => (t.Truster, t.Trustee, t.Limit)).ToHashSet();

        Assert.That(cacheSet.SetEquals(mockSet), Is.True,
            "Trust edges differ between mock and cache");
    }

    [Test]
    public void MockVsCache_LoadGroups_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockGroups = mock.LoadGroups().ToHashSet();
        var cacheGroups = cache.LoadGroups().ToHashSet();

        Assert.That(cacheGroups.SetEquals(mockGroups), Is.True);
    }

    [Test]
    public void MockVsCache_LoadGroupTrusts_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockGT = mock.LoadGroupTrusts().ToHashSet();
        var cacheGT = cache.LoadGroupTrusts().ToHashSet();

        Assert.That(cacheGT.SetEquals(mockGT), Is.True);
    }

    [Test]
    public void MockVsCache_LoadConsentedFlowFlags_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockConsent = mock.LoadConsentedFlowFlags().ToHashSet();
        var cacheConsent = cache.LoadConsentedFlowFlags().ToHashSet();

        Assert.That(cacheConsent.SetEquals(mockConsent), Is.True);
    }

    [Test]
    public void MockVsCache_LoadRegisteredAvatars_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockAvatars = mock.LoadRegisteredAvatars().ToHashSet();
        var cacheAvatars = cache.LoadRegisteredAvatars().ToHashSet();

        Assert.That(cacheAvatars.SetEquals(mockAvatars), Is.True);
    }

    [Test]
    public void MockVsCache_LoadWrapperMappings_IdenticalOutput()
    {
        var mock = BuildTestGraph();
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cache = new CacheLoadGraph(snapshot);

        var mockWrappers = mock.LoadWrapperMappings().ToHashSet();
        var cacheWrappers = cache.LoadWrapperMappings().ToHashSet();

        Assert.That(cacheWrappers.SetEquals(mockWrappers), Is.True);
    }

    // ── Safety margin ──────────────────────────────────────────────────

    [Test]
    public void CacheLoadGraph_WithSafetyMargin_ReducesBalances()
    {
        var settings = new Settings { DemurrageSafetyMargin = 0.9995 };

        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("1000000000000000000000", "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var withMargin = new CacheLoadGraph(snapshot, settings);
        var balance = withMargin.LoadV2Balances().Single();

        var original = BigInteger.Parse("1000000000000000000000");
        var expected = (BigInteger)((double)original * 0.9995);

        Assert.That(BigInteger.Parse(balance.Balance), Is.EqualTo(expected),
            "Safety margin should reduce balance by 0.05%");
    }

    [Test]
    public void CacheLoadGraph_WithoutSafetyMargin_PassesThrough()
    {
        // Safety margin = 1.0 → no reduction
        var settings = new Settings { DemurrageSafetyMargin = 1.0 };

        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("1000000000000000000000", "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var withoutMargin = new CacheLoadGraph(snapshot, settings);
        var balance = withoutMargin.LoadV2Balances().Single();

        Assert.That(balance.Balance, Is.EqualTo("1000000000000000000000"),
            "No safety margin → balance unchanged");
    }

    [Test]
    public void CacheLoadGraph_NullSettings_NoSafetyMargin()
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("1000000000000000000000", "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var noSettings = new CacheLoadGraph(snapshot);
        var balance = noSettings.LoadV2Balances().Single();

        Assert.That(balance.Balance, Is.EqualTo("1000000000000000000000"),
            "Null settings → no safety margin → balance unchanged");
    }

    [Test]
    public void SafetyMargin_CacheVsDB_DeltaWithinTolerance()
    {
        // Simulate what DB path does: DemurrageCalculator.Apply with safety margin
        var settings = new Settings { DemurrageSafetyMargin = 0.9995 };

        var rawBalance = BigInteger.Parse("999999999999999999999");

        // DB path: DemurrageCalculator applies margin
        var dbResult = (BigInteger)((double)rawBalance * settings.DemurrageSafetyMargin);

        // Cache path: CacheLoadGraph applies margin
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow(rawBalance.ToString(), "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot, settings);
        var cacheResult = BigInteger.Parse(cache.LoadV2Balances().Single().Balance);

        Assert.That(cacheResult, Is.EqualTo(dbResult),
            "Both paths use identical margin arithmetic");
    }

    [Test]
    public void CacheLoadGraph_WithTargetTimestamp_NoSafetyMargin()
    {
        // When TargetDemurrageTimestamp is set, safety margin is NOT applied (test mode)
        var settings = new Settings
        {
            DemurrageSafetyMargin = 0.9995,
            TargetDemurrageTimestamp = DateTimeOffset.UtcNow
        };

        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow("1000000000000000000000", "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot, settings);
        var balance = cache.LoadV2Balances().Single();

        Assert.That(balance.Balance, Is.EqualTo("1000000000000000000000"),
            "TargetDemurrageTimestamp set → no safety margin");
    }

    // ── Decimal precision ──────────────────────────────────────────────

    [Test]
    public void DecimalRoundTrip_SmallBalance_ExactMatch()
    {
        AssertBalanceRoundTrip("1000000000000000000"); // 1 CRC
    }

    [Test]
    public void DecimalRoundTrip_LargeBalance_WithinOneWei()
    {
        // 10 billion CRC in attoCircles
        AssertBalanceRoundTrip("10000000000000000000000000000");
    }

    [Test]
    public void DecimalRoundTrip_MaxDecimalPrecision_NoTruncation()
    {
        // 28-digit number (near decimal precision limit)
        AssertBalanceRoundTrip("1234567890123456789012345678");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void AssertBalanceRoundTrip(string balanceWei)
    {
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 1,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow(balanceWei, "0xaa", "0xbb", 0, false, "demurraged")
            },
            Trust: null, Groups: null, GroupTrusts: null,
            ConsentedFlow: null, Avatars: null, WrapperMappings: null);

        var cache = new CacheLoadGraph(snapshot); // no settings = no margin
        var result = cache.LoadV2Balances().Single();

        Assert.That(result.Balance, Is.EqualTo(balanceWei),
            $"Round-trip should preserve exact value for {balanceWei}");
    }

    private static MockLoadGraph BuildTestGraph()
    {
        var mock = new MockLoadGraph();

        // Avatars
        mock.AddRegisteredAvatar("0xaa00000000000000000000000000000000000001");
        mock.AddRegisteredAvatar("0xaa00000000000000000000000000000000000002");
        mock.AddRegisteredAvatar("0xaa00000000000000000000000000000000000003");

        // Balances (using address strings for cleaner round-trip)
        mock.AddBalanceWei("0xaa00000000000000000000000000000000000001",
            "0xaa00000000000000000000000000000000000001",
            "500000000000000000000"); // 500 CRC
        mock.AddBalanceWei("0xaa00000000000000000000000000000000000002",
            "0xaa00000000000000000000000000000000000002",
            "300000000000000000000"); // 300 CRC

        // Trust
        mock.AddTrust("0xaa00000000000000000000000000000000000001",
            "0xaa00000000000000000000000000000000000002");
        mock.AddTrust("0xaa00000000000000000000000000000000000002",
            "0xaa00000000000000000000000000000000000003");

        // Group
        mock.AddGroup("0xbb00000000000000000000000000000000000001");
        mock.AddGroupTrust("0xbb00000000000000000000000000000000000001",
            "0xaa00000000000000000000000000000000000001");

        // Consent
        mock.AddConsentedAvatar("0xaa00000000000000000000000000000000000003", true);

        // Wrapper
        mock.AddWrapperMapping("0xcc00000000000000000000000000000000000001",
            "0xaa00000000000000000000000000000000000001");

        return mock;
    }
}
