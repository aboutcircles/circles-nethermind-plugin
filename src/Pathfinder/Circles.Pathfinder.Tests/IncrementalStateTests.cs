using System.Numerics;
using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests;

[TestFixture, Parallelizable]
public class InMemoryBalanceStateTests
{
    private const string ZeroAddr = "0x0000000000000000000000000000000000000000";
    private const string Alice = "0x0000000000000000000000000000000000000001";
    private const string Bob = "0x0000000000000000000000000000000000000002";
    private const string TokenA = "0x000000000000000000000000000000000000000a";
    private const string TokenB = "0x000000000000000000000000000000000000000b";

    [Test]
    public void InitializeFromFullLoad_PopulatesState()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[]
        {
            ("1000", Alice, TokenA, 100L),
            ("2000", Bob, TokenA, 200L),
        });

        Assert.That(state.Count, Is.EqualTo(2));
        Assert.That(state.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(1000)));
    }

    [Test]
    public void InitializeFromFullLoad_SkipsZeroBalances()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[]
        {
            ("0", Alice, TokenA, 100L),
            ("-5", Bob, TokenA, 100L),
        });

        Assert.That(state.Count, Is.EqualTo(0));
    }

    [Test]
    public void ApplyTransfer_AddSubtract_Correctness()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[]
        {
            ("1000", Alice, TokenA, 100L),
        });

        state.ApplyTransfer(Alice, Bob, TokenA, "300", 200L);

        Assert.That(state.TryGet((Alice, TokenA), out var aliceEntry), Is.True);
        Assert.That(aliceEntry.Balance, Is.EqualTo(new BigInteger(700)));

        Assert.That(state.TryGet((Bob, TokenA), out var bobEntry), Is.True);
        Assert.That(bobEntry.Balance, Is.EqualTo(new BigInteger(300)));
    }

    [Test]
    public void ApplyTransfer_ZeroAddressFrom_MintOnly()
    {
        var state = new InMemoryBalanceState();

        // Mint: from=zero, to=Alice → only add to Alice, don't subtract from zero
        state.ApplyTransfer(ZeroAddr, Alice, TokenA, "500", 100L);

        Assert.That(state.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(500)));
        Assert.That(state.TryGet((ZeroAddr, TokenA), out _), Is.False);
    }

    [Test]
    public void ApplyTransfer_ZeroAddressTo_BurnOnly()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[]
        {
            ("1000", Alice, TokenA, 100L),
        });

        // Burn: from=Alice, to=zero → subtract from Alice, don't add to zero
        state.ApplyTransfer(Alice, ZeroAddr, TokenA, "300", 200L);

        Assert.That(state.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(700)));
        Assert.That(state.TryGet((ZeroAddr, TokenA), out _), Is.False);
    }

    [Test]
    public void ApplyTransfer_BalanceToZero_RemovesEntry()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[]
        {
            ("500", Alice, TokenA, 100L),
        });

        state.ApplyTransfer(Alice, Bob, TokenA, "500", 200L);

        Assert.That(state.TryGet((Alice, TokenA), out _), Is.False);
        Assert.That(state.TryGet((Bob, TokenA), out var bobEntry), Is.True);
        Assert.That(bobEntry.Balance, Is.EqualTo(new BigInteger(500)));
    }

    [Test]
    public void ApplyTransfer_LastActivity_TakesMax()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[]
        {
            ("1000", Alice, TokenA, 500L),
        });

        // Transfer with earlier timestamp — lastActivity should stay 500
        state.ApplyTransfer(Alice, Bob, TokenA, "100", 200L);
        Assert.That(state.TryGet((Alice, TokenA), out var entry1), Is.True);
        Assert.That(entry1.LastActivity, Is.EqualTo(500L));

        // Transfer with later timestamp — lastActivity should update to 600
        state.ApplyTransfer(Alice, Bob, TokenA, "100", 600L);
        Assert.That(state.TryGet((Alice, TokenA), out var entry2), Is.True);
        Assert.That(entry2.LastActivity, Is.EqualTo(600L));
    }

    [Test]
    public void ApplyTransfer_MultipleToSameTarget_Accumulates()
    {
        var state = new InMemoryBalanceState();

        state.ApplyTransfer(ZeroAddr, Alice, TokenA, "100", 100L);
        state.ApplyTransfer(ZeroAddr, Alice, TokenA, "200", 200L);
        state.ApplyTransfer(ZeroAddr, Alice, TokenA, "300", 300L);

        Assert.That(state.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(600)));
        Assert.That(entry.LastActivity, Is.EqualTo(300L));
    }

    [Test]
    public void ApplyTransfer_DifferentTokens_IndependentEntries()
    {
        var state = new InMemoryBalanceState();

        state.ApplyTransfer(ZeroAddr, Alice, TokenA, "100", 100L);
        state.ApplyTransfer(ZeroAddr, Alice, TokenB, "200", 100L);

        Assert.That(state.Count, Is.EqualTo(2));
        Assert.That(state.TryGet((Alice, TokenA), out var a), Is.True);
        Assert.That(a.Balance, Is.EqualTo(new BigInteger(100)));
        Assert.That(state.TryGet((Alice, TokenB), out var b), Is.True);
        Assert.That(b.Balance, Is.EqualTo(new BigInteger(200)));
    }

    [Test]
    public void ApplyTransfer_SelfTransfer_OnlyUpdatesTimestamp()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[] { ("1000", Alice, TokenA, 100L) });

        // Self-transfer: balance should remain unchanged, only lastActivity updates
        state.ApplyTransfer(Alice, Alice, TokenA, "500", 200L);

        Assert.That(state.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(1000)));
        Assert.That(entry.LastActivity, Is.EqualTo(200L));
    }

    [Test]
    public void ApplyTransfer_SelfTransfer_LargerThanBalance_NoChange()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[] { ("500", Alice, TokenA, 100L) });

        // Self-transfer larger than balance: should NOT corrupt state
        state.ApplyTransfer(Alice, Alice, TokenA, "999", 200L);

        Assert.That(state.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(500)));
        Assert.That(entry.LastActivity, Is.EqualTo(200L));
    }

    [Test]
    public void Snapshot_CreatesIndependentCopy()
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(new[] { ("1000", Alice, TokenA, 100L) });

        var snapshot = state.Snapshot();
        state.ApplyTransfer(Alice, Bob, TokenA, "500", 200L);

        // Snapshot should be unaffected
        Assert.That(snapshot.TryGet((Alice, TokenA), out var entry), Is.True);
        Assert.That(entry.Balance, Is.EqualTo(new BigInteger(1000)));
    }
}

[TestFixture, Parallelizable]
public class InMemoryTrustStateTests
{
    private const string Alice = "0x0000000000000000000000000000000000000001";
    private const string Bob = "0x0000000000000000000000000000000000000002";
    private const string Carol = "0x0000000000000000000000000000000000000003";
    private const string GroupAddr = "0x0000000000000000000000000000000000000099";

    [Test]
    public void InitializeFromFullLoad_PopulatesState()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 999999L, 100L, 0, 0),
        });

        Assert.That(state.Count, Is.EqualTo(1));
    }

    [Test]
    public void ApplyTrustEvent_NewerEventReplaces()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 1000L, 100L, 0, 0),
        });

        // Newer event (higher block)
        state.ApplyTrustEvent(200, 0, 0, Alice, Bob, 2000);

        Assert.That(state.TryGet((Alice, Bob), out var entry), Is.True);
        Assert.That(entry.ExpiryTime, Is.EqualTo(2000));
        Assert.That(entry.BlockNumber, Is.EqualTo(200));
    }

    [Test]
    public void ApplyTrustEvent_OlderEventIgnored()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 2000L, 200L, 0, 0),
        });

        // Older event (lower block) — should be ignored
        state.ApplyTrustEvent(100, 0, 0, Alice, Bob, 1000);

        Assert.That(state.TryGet((Alice, Bob), out var entry), Is.True);
        Assert.That(entry.ExpiryTime, Is.EqualTo(2000));
        Assert.That(entry.BlockNumber, Is.EqualTo(200));
    }

    [Test]
    public void ApplyTrustEvent_SameBlock_HigherTxIndexWins()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 1000L, 100L, 5, 0),
        });

        state.ApplyTrustEvent(100, 10, 0, Alice, Bob, 2000);

        Assert.That(state.TryGet((Alice, Bob), out var entry), Is.True);
        Assert.That(entry.ExpiryTime, Is.EqualTo(2000));
        Assert.That(entry.TxIndex, Is.EqualTo(10));
    }

    [Test]
    public void ApplyTrustEvent_SameBlockAndTx_HigherLogIndexWins()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 1000L, 100L, 5, 2),
        });

        state.ApplyTrustEvent(100, 5, 7, Alice, Bob, 2000);

        Assert.That(state.TryGet((Alice, Bob), out var entry), Is.True);
        Assert.That(entry.ExpiryTime, Is.EqualTo(2000));
        Assert.That(entry.LogIndex, Is.EqualTo(7));
    }

    [Test]
    public void GetActiveTrusts_FiltersExpired()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 500L, 100L, 0, 0),   // expires at 500
            (Alice, Carol, 2000L, 100L, 0, 0), // expires at 2000
        });

        var avatars = new HashSet<string> { Alice, Bob, Carol };
        var groups = new HashSet<string>();

        var active = state.GetActiveTrusts(1000, avatars, groups).ToList();

        Assert.That(active, Has.Count.EqualTo(1));
        Assert.That(active[0].Truster, Is.EqualTo(Alice));
        Assert.That(active[0].Trustee, Is.EqualTo(Carol));
    }

    [Test]
    public void GetActiveTrusts_FiltersNonAvatars()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, Bob, 999999L, 100L, 0, 0),
        });

        // Bob is not in avatarSet
        var avatars = new HashSet<string> { Alice };
        var groups = new HashSet<string>();

        var active = state.GetActiveTrusts(0, avatars, groups).ToList();
        Assert.That(active, Is.Empty);
    }

    [Test]
    public void GetActiveTrusts_ExcludesGroupTrusters()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (GroupAddr, Bob, 999999L, 100L, 0, 0),
            (Alice, Bob, 999999L, 100L, 0, 0),
        });

        var avatars = new HashSet<string> { Alice, Bob, GroupAddr };
        var groups = new HashSet<string> { GroupAddr };

        var active = state.GetActiveTrusts(0, avatars, groups).ToList();

        Assert.That(active, Has.Count.EqualTo(1));
        Assert.That(active[0].Truster, Is.EqualTo(Alice));
    }

    [Test]
    public void GetGroupTrusts_OnlyGroupTrusters()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (GroupAddr, Bob, 999999L, 100L, 0, 0),
            (Alice, Bob, 999999L, 100L, 0, 0),
        });

        var groups = new HashSet<string> { GroupAddr };

        var groupTrusts = state.GetGroupTrusts(groups, 0).ToList();

        Assert.That(groupTrusts, Has.Count.EqualTo(1));
        Assert.That(groupTrusts[0].GroupAddress, Is.EqualTo(GroupAddr));
    }

    [Test]
    public void GetGroupTrusts_FiltersExpired()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[]
        {
            (GroupAddr, Bob, 500L, 100L, 0, 0), // expired
        });

        var groups = new HashSet<string> { GroupAddr };
        var result = state.GetGroupTrusts(groups, 1000).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Snapshot_CreatesIndependentCopy()
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(new[] { (Alice, Bob, 1000L, 100L, 0, 0) });

        var snapshot = state.Snapshot();
        state.ApplyTrustEvent(200, 0, 0, Alice, Bob, 5000);

        // Snapshot unaffected
        Assert.That(snapshot.TryGet((Alice, Bob), out var entry), Is.True);
        Assert.That(entry.ExpiryTime, Is.EqualTo(1000));
    }
}

[TestFixture, Parallelizable]
public class InMemoryAvatarStateTests
{
    private const string Alice = "0x0000000000000000000000000000000000000001";
    private const string Bob = "0x0000000000000000000000000000000000000002";
    private const string GroupAddr = "0x0000000000000000000000000000000000000099";

    [Test]
    public void InitializeFromFullLoad_SeparatesAvatarsAndGroups()
    {
        var state = new InMemoryAvatarState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, "CrcV2_RegisterHuman"),
            (GroupAddr, "CrcV2_RegisterGroup"),
        });

        Assert.That(state.Count, Is.EqualTo(2));
        Assert.That(state.GroupCount, Is.EqualTo(1));
        Assert.That(state.Contains(Alice), Is.True);
        Assert.That(state.Contains(GroupAddr), Is.True);
        Assert.That(state.IsGroup(Alice), Is.False);
        Assert.That(state.IsGroup(GroupAddr), Is.True);
    }

    [Test]
    public void AddAvatar_Group_AddsToBothSets()
    {
        var state = new InMemoryAvatarState();
        state.AddAvatar(GroupAddr, "CrcV2_RegisterGroup");

        Assert.That(state.Contains(GroupAddr), Is.True);
        Assert.That(state.IsGroup(GroupAddr), Is.True);
    }

    [Test]
    public void AddAvatar_Duplicate_IsIdempotent()
    {
        var state = new InMemoryAvatarState();
        state.AddAvatar(Alice, "CrcV2_RegisterHuman");
        state.AddAvatar(Alice, "CrcV2_RegisterHuman");

        Assert.That(state.Count, Is.EqualTo(1));
    }

    [Test]
    public void StoppedAvatar_ExcludedFromContains()
    {
        var state = new InMemoryAvatarState();
        state.InitializeFromFullLoad(new[]
        {
            (Alice, "CrcV2_RegisterHuman"),
            (Bob, "CrcV2_RegisterHuman"),
        });

        state.InitializeStoppedAvatars(new[] { Alice });

        Assert.That(state.Contains(Alice), Is.False);
        Assert.That(state.Contains(Bob), Is.True);
        Assert.That(state.StoppedCount, Is.EqualTo(1));
    }

    [Test]
    public void MarkStopped_ExcludesFromContains()
    {
        var state = new InMemoryAvatarState();
        state.InitializeFromFullLoad(new[] { (Alice, "CrcV2_RegisterHuman") });

        Assert.That(state.Contains(Alice), Is.True);

        state.MarkStopped(Alice);

        Assert.That(state.Contains(Alice), Is.False);
    }

    [Test]
    public void StoppedAvatar_Snapshot_PreservesStopped()
    {
        var state = new InMemoryAvatarState();
        state.InitializeFromFullLoad(new[] { (Alice, "CrcV2_RegisterHuman") });
        state.MarkStopped(Alice);

        var snapshot = state.Snapshot();
        Assert.That(snapshot.Contains(Alice), Is.False);
        Assert.That(snapshot.StoppedCount, Is.EqualTo(1));
    }

    [Test]
    public void Snapshot_CreatesIndependentCopy()
    {
        var state = new InMemoryAvatarState();
        state.InitializeFromFullLoad(new[] { (Alice, "CrcV2_RegisterHuman") });

        var snapshot = state.Snapshot();
        state.AddAvatar(Bob, "CrcV2_RegisterHuman");

        Assert.That(snapshot.Count, Is.EqualTo(1));
        Assert.That(snapshot.Contains(Bob), Is.False);
    }
}

[TestFixture, Parallelizable]
public class IncrementalLoadGraphTests
{
    private const string Alice = "0x0000000000000000000000000000000000inc001";
    private const string Bob = "0x0000000000000000000000000000000000inc002";
    private const string TokenA = "0x0000000000000000000000000000000000inc00a";
    private const string GroupAddr = "0x0000000000000000000000000000000000inc099";

    // Circles V2 epoch: Feb 1, 2023 00:00 UTC
    private const uint InflationDayZeroUnix = 1_675_209_600;

    private InMemoryBalanceState CreateBalanceState(
        params (string Balance, string Account, string TokenAddress, long LastActivity)[] rows)
    {
        var state = new InMemoryBalanceState();
        state.InitializeFromFullLoad(rows);
        return state;
    }

    private InMemoryTrustState CreateTrustState(
        params (string Truster, string Trustee, long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex)[] rows)
    {
        var state = new InMemoryTrustState();
        state.InitializeFromFullLoad(rows);
        return state;
    }

    private InMemoryAvatarState CreateAvatarState(params (string Avatar, string Type)[] rows)
    {
        var state = new InMemoryAvatarState();
        state.InitializeFromFullLoad(rows);
        return state;
    }

    [Test]
    public void LoadV2Balances_FiltersNonAvatars()
    {
        var balances = CreateBalanceState(
            ("1000000000000000000", Alice, TokenA, InflationDayZeroUnix + 1000),
            ("1000000000000000000", Bob, TokenA, InflationDayZeroUnix + 1000));

        // Only Alice is registered
        var avatars = CreateAvatarState((Alice, "CrcV2_RegisterHuman"));
        var trusts = CreateTrustState();

        var settings = new Settings { TargetDemurrageTimestamp = DateTimeOffset.FromUnixTimeSeconds(InflationDayZeroUnix + 1000) };
        var stubInner = new StubLoadGraph();
        var incGraph = new IncrementalLoadGraph(balances, trusts, avatars, stubInner, settings, 0);

        var results = incGraph.LoadV2Balances().ToList();

        Assert.That(results, Has.Count.EqualTo(1));
    }

    [Test]
    public void LoadV2Balances_SkipsPreEpochLastActivity()
    {
        var balances = CreateBalanceState(
            ("1000000000000000000", Alice, TokenA, InflationDayZeroUnix - 100)); // pre-epoch

        var avatars = CreateAvatarState((Alice, "CrcV2_RegisterHuman"));
        var trusts = CreateTrustState();
        var settings = new Settings { TargetDemurrageTimestamp = DateTimeOffset.FromUnixTimeSeconds(InflationDayZeroUnix + 86400) };
        var stubInner = new StubLoadGraph();
        var incGraph = new IncrementalLoadGraph(balances, trusts, avatars, stubInner, settings, 0);

        var results = incGraph.LoadV2Balances().ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void LoadV2Balances_AppliesDemurrage_WhenDaysDeltaPositive()
    {
        // Set lastActivity at day 0, target at day 1 → should apply 1 day of demurrage
        long lastActivity = InflationDayZeroUnix;
        var balance = "1000000000000000000000"; // 1000 * 1e18

        var balances = CreateBalanceState((balance, Alice, TokenA, lastActivity));
        var avatars = CreateAvatarState((Alice, "CrcV2_RegisterHuman"));
        var trusts = CreateTrustState();

        var targetTime = DateTimeOffset.FromUnixTimeSeconds(InflationDayZeroUnix + 86400); // 1 day later
        var settings = new Settings { TargetDemurrageTimestamp = targetTime };
        var stubInner = new StubLoadGraph();
        var incGraph = new IncrementalLoadGraph(balances, trusts, avatars, stubInner, settings, 0);

        var results = incGraph.LoadV2Balances().ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        var resultBalance = BigInteger.Parse(results[0].Balance);
        var originalBalance = BigInteger.Parse(balance);
        // Demurraged balance should be less than original (7% annual decay)
        Assert.That(resultBalance, Is.LessThan(originalBalance));
        Assert.That(resultBalance, Is.GreaterThan(BigInteger.Zero));
    }

    [Test]
    public void LoadV2Balances_NoDemurrage_WhenSameDay()
    {
        long lastActivity = InflationDayZeroUnix + 100; // same day
        var balance = "1000000000000000000000";

        var balances = CreateBalanceState((balance, Alice, TokenA, lastActivity));
        var avatars = CreateAvatarState((Alice, "CrcV2_RegisterHuman"));
        var trusts = CreateTrustState();

        var targetTime = DateTimeOffset.FromUnixTimeSeconds(InflationDayZeroUnix + 200); // same day
        var settings = new Settings { TargetDemurrageTimestamp = targetTime };
        var stubInner = new StubLoadGraph();
        var incGraph = new IncrementalLoadGraph(balances, trusts, avatars, stubInner, settings, 0);

        var results = incGraph.LoadV2Balances().ToList();
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Balance, Is.EqualTo(balance));
    }

    [Test]
    public void LoadV2Trust_FiltersCorrectly()
    {
        var trusts = CreateTrustState(
            (Alice, Bob, 999999L, 100L, 0, 0),      // valid
            (GroupAddr, Bob, 999999L, 100L, 0, 0),   // group truster → excluded
            (Alice, "0x0000000000000000000000000000000000inc003", 500L, 100L, 0, 0) // expired
        );

        var avatars = CreateAvatarState(
            (Alice, "CrcV2_RegisterHuman"),
            (Bob, "CrcV2_RegisterHuman"),
            (GroupAddr, "CrcV2_RegisterGroup"),
            ("0x0000000000000000000000000000000000inc003", "CrcV2_RegisterHuman"));

        var balances = CreateBalanceState();
        var settings = new Settings();
        var stubInner = new StubLoadGraph();
        var incGraph = new IncrementalLoadGraph(balances, trusts, avatars, stubInner, settings, 1000);

        var results = incGraph.LoadV2Trust().ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Truster, Is.EqualTo(Alice));
        Assert.That(results[0].Trustee, Is.EqualTo(Bob));
        Assert.That(results[0].Limit, Is.EqualTo(100));
    }

    [Test]
    public void LoadGroupTrusts_DerivesFromTrustState()
    {
        var trusts = CreateTrustState(
            (GroupAddr, TokenA, 999999L, 100L, 0, 0),
            (Alice, Bob, 999999L, 100L, 0, 0)); // not a group

        var avatars = CreateAvatarState(
            (Alice, "CrcV2_RegisterHuman"),
            (Bob, "CrcV2_RegisterHuman"),
            (GroupAddr, "CrcV2_RegisterGroup"));

        var balances = CreateBalanceState();
        var settings = new Settings();
        var stubInner = new StubLoadGraph();
        stubInner.Groups.Add(GroupAddr); // router-filtered groups from DB
        var incGraph = new IncrementalLoadGraph(balances, trusts, avatars, stubInner, settings, 0);

        var results = incGraph.LoadGroupTrusts().ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].GroupAddress, Is.EqualTo(GroupAddr));
        Assert.That(results[0].TrustedToken, Is.EqualTo(TokenA));
    }

    [Test]
    public void LoadGroups_DelegatesToInner()
    {
        var stubInner = new StubLoadGraph();
        stubInner.Groups.Add("group1");
        stubInner.Groups.Add("group2");

        var incGraph = new IncrementalLoadGraph(
            CreateBalanceState(), CreateTrustState(), CreateAvatarState(),
            stubInner, new Settings(), 0);

        var results = incGraph.LoadGroups().ToList();
        Assert.That(results, Has.Count.EqualTo(2));
    }

    [Test]
    public void LoadConsentedFlowFlags_DelegatesToInner()
    {
        var stubInner = new StubLoadGraph();
        stubInner.ConsentedFlags.Add(("avatar1", true));

        var incGraph = new IncrementalLoadGraph(
            CreateBalanceState(), CreateTrustState(), CreateAvatarState(),
            stubInner, new Settings(), 0);

        var results = incGraph.LoadConsentedFlowFlags().ToList();
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].HasConsentedFlow, Is.True);
    }

    /// <summary>
    /// Stub ILoadGraph for testing delegation.
    /// </summary>
    private class StubLoadGraph : ILoadGraph
    {
        public List<string> Groups { get; } = new();
        public List<(string GroupAddress, string TrustedToken)> GroupTrustEntries { get; } = new();
        public List<(string Avatar, bool HasConsentedFlow)> ConsentedFlags { get; } = new();

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2Balances() => Enumerable.Empty<(string, int, int, bool, bool)>();

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
            => Enumerable.Empty<(string, string, int)>();

        public IEnumerable<string> LoadGroups() => Groups;

        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts() => GroupTrustEntries;

        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags() => ConsentedFlags;
        public IEnumerable<string> LoadRegisteredAvatars() => Enumerable.Empty<string>();
    }
}
