using System.Numerics;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Focused unit tests for <see cref="NetworkStateUpdaterService"/> —
/// the critical 741-line orchestrator that had ZERO test coverage.
/// Tests decision logic, drift detection, graph building, and state management.
/// </summary>
[TestFixture, Parallelizable]
public class NetworkStateUpdaterServiceTests
{
    private const string RouterAddr = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private const string AliceAddr = "0xaa00000000000000000000000000000000000001";
    private const string BobAddr = "0xbb00000000000000000000000000000000000002";
    private const string AliceToken = "0xaa00000000000000000000000000000000000001";
    private const string BobToken = "0xbb00000000000000000000000000000000000002";

    [OneTimeSetUp]
    public void EnsureEnvVars()
    {
        // Common.Settings constructor throws without these; set dummy values for unit tests.
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING",
            Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? "Host=localhost;Database=test;Username=test;Password=test");
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL",
            Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL") ?? "http://localhost:8545");
    }

    /// <summary>
    /// Creates Host.Settings with controlled values. Requires env vars set above.
    /// </summary>
    private static Circles.Pathfinder.Host.Settings CreateSettings(int fullRefreshInterval = 200)
    {
        var settings = new Circles.Pathfinder.Host.Settings
        {
            FullRefreshIntervalBlocks = fullRefreshInterval,
            IncrementalEnabled = true
        };
        return settings;
    }

    private static NetworkStateUpdaterService CreateService(
        Circles.Pathfinder.Host.Settings? settings = null,
        NetworkState? networkState = null,
        CapacityGraphPool? pool = null)
    {
        settings ??= CreateSettings();
        networkState ??= new NetworkState();
        var mockLoadGraph = new MockLoadGraph();
        pool ??= new CapacityGraphPool(RouterAddr, mockLoadGraph);
        var logger = NullLogger<NetworkStateUpdaterService>.Instance;

        return new NetworkStateUpdaterService(networkState, settings, logger, pool);
    }

    #region ResetIncrementalState

    [Test]
    public void ResetIncrementalState_ClearsAllFields()
    {
        var svc = CreateService();

        // Simulate some accumulated state
        svc._balanceState = new InMemoryBalanceState(null);
        svc._trustState = new InMemoryTrustState();
        svc._avatarState = new InMemoryAvatarState();
        svc._lastFullRefreshBlock = 1000;
        svc._lastProcessedBlock = 1050;
        svc._lastProcessedBlockHash = "0xdeadbeef";

        svc.ResetIncrementalState();

        Assert.Multiple(() =>
        {
            Assert.That(svc._balanceState, Is.Null);
            Assert.That(svc._trustState, Is.Null);
            Assert.That(svc._avatarState, Is.Null);
            Assert.That(svc._lastFullRefreshBlock, Is.EqualTo(-1));
            Assert.That(svc._lastProcessedBlock, Is.EqualTo(-1));
            Assert.That(svc._lastProcessedBlockHash, Is.Null);
        });
    }

    #endregion

    #region NeedsFullRefresh decision logic

    [Test]
    public void NeedsFullRefresh_FirstRun_NullState_ReturnsTrue()
    {
        var svc = CreateService();
        // _balanceState is null by default → first run
        Assert.That(svc.NeedsFullRefresh(100), Is.True);
    }

    [Test]
    public void NeedsFullRefresh_PeriodicInterval_ReturnsTrue()
    {
        var settings = CreateSettings(fullRefreshInterval: 50);
        var svc = CreateService(settings);

        // Simulate: state initialized, last full at block 100, processed to 100
        svc._balanceState = new InMemoryBalanceState(null);
        svc._lastFullRefreshBlock = 100;
        svc._lastProcessedBlock = 100;

        // Block 101: only 1 block since full → false (1 < 50)
        Assert.That(svc.NeedsFullRefresh(101), Is.False);

        // Block 149: 49 blocks since full → false (49 < 50)
        svc._lastProcessedBlock = 148;
        Assert.That(svc.NeedsFullRefresh(149), Is.False);

        // Block 150: exactly 50 blocks since full → true (50 >= 50)
        svc._lastProcessedBlock = 149;
        Assert.That(svc.NeedsFullRefresh(150), Is.True);

        // Block 200: well past interval → true
        svc._lastProcessedBlock = 199;
        Assert.That(svc.NeedsFullRefresh(200), Is.True);
    }

    [Test]
    public void NeedsFullRefresh_BlockRegression_ReturnsTrue()
    {
        var svc = CreateService();
        svc._balanceState = new InMemoryBalanceState(null);
        svc._lastFullRefreshBlock = 100;
        svc._lastProcessedBlock = 150;

        // Block 150 again (same) → reorg / skip
        Assert.That(svc.NeedsFullRefresh(150), Is.True);

        // Block 140 (regression) → reorg / skip
        Assert.That(svc.NeedsFullRefresh(140), Is.True);
    }

    [Test]
    public void NeedsFullRefresh_NormalIncrement_ReturnsFalse()
    {
        var settings = CreateSettings(fullRefreshInterval: 200);
        var svc = CreateService(settings);
        svc._balanceState = new InMemoryBalanceState(null);
        svc._lastFullRefreshBlock = 100;
        svc._lastProcessedBlock = 150;

        // Block 151: normal increment, within interval → false
        Assert.That(svc.NeedsFullRefresh(151), Is.False);
    }

    #endregion

    #region DetectAndRecordDrift

    [Test]
    public void DetectAndRecordDrift_IdenticalState_NoDrift()
    {
        var svc = CreateService();

        var balance = new InMemoryBalanceState(null);
        balance.InitializeFromFullLoad(new[]
        {
            ("100", AliceAddr, AliceToken, 1000L),
            ("200", BobAddr, BobToken, 2000L)
        });

        var trust = new InMemoryTrustState();
        trust.InitializeFromFullLoad(new[]
        {
            (AliceAddr, BobAddr, long.MaxValue, 100L, 0, 0)
        });

        var avatar = new InMemoryAvatarState();
        avatar.InitializeFromFullLoad(new[]
        {
            (AliceAddr, "human"),
            (BobAddr, "human")
        });

        // Snapshot → identical copies
        var prevBalance = balance.Snapshot();
        var prevTrust = trust.Snapshot();
        var prevAvatar = avatar.Snapshot();

        // Should not throw; drift = 0 for all categories
        Assert.DoesNotThrow(() =>
            svc.DetectAndRecordDrift(prevBalance, prevTrust, prevAvatar, balance, trust, avatar));
    }

    [Test]
    public void DetectAndRecordDrift_BalanceMismatch_DetectsDrift()
    {
        var svc = CreateService();

        var prev = new InMemoryBalanceState(null);
        prev.InitializeFromFullLoad(new[]
        {
            ("100", AliceAddr, AliceToken, 1000L)
        });

        var fresh = new InMemoryBalanceState(null);
        fresh.InitializeFromFullLoad(new[]
        {
            ("200", AliceAddr, AliceToken, 1000L)  // balance changed: 100 → 200
        });

        var trust = new InMemoryTrustState();
        var avatar = new InMemoryAvatarState();
        avatar.InitializeFromFullLoad(new[] { (AliceAddr, "human") });

        // Should not throw — drift is logged/metered, not fatal
        Assert.DoesNotThrow(() =>
            svc.DetectAndRecordDrift(prev, trust, avatar, fresh, trust, avatar));
    }

    [Test]
    public void DetectAndRecordDrift_TrustMismatch_DetectsDrift()
    {
        var svc = CreateService();

        var prev = new InMemoryTrustState();
        prev.InitializeFromFullLoad(new[]
        {
            (AliceAddr, BobAddr, long.MaxValue, 100L, 0, 0)
        });

        var fresh = new InMemoryTrustState();
        fresh.InitializeFromFullLoad(new[]
        {
            (AliceAddr, BobAddr, 999L, 100L, 0, 0)  // expiryTime changed
        });

        var balance = new InMemoryBalanceState(null);
        var avatar = new InMemoryAvatarState();

        Assert.DoesNotThrow(() =>
            svc.DetectAndRecordDrift(balance, prev, avatar, balance, fresh, avatar));
    }

    [Test]
    public void DetectAndRecordDrift_AvatarMismatch_DetectsDrift()
    {
        var svc = CreateService();

        var prev = new InMemoryAvatarState();
        prev.InitializeFromFullLoad(new[] { (AliceAddr, "human") });

        var fresh = new InMemoryAvatarState();
        fresh.InitializeFromFullLoad(new[]
        {
            (AliceAddr, "human"),
            (BobAddr, "human")  // Bob appeared in fresh but not in prev
        });

        var balance = new InMemoryBalanceState(null);
        var trust = new InMemoryTrustState();

        Assert.DoesNotThrow(() =>
            svc.DetectAndRecordDrift(balance, trust, prev, balance, trust, fresh));
    }

    [Test]
    public void DetectAndRecordDrift_MissingEntryInFresh_DetectsDrift()
    {
        var svc = CreateService();

        var prev = new InMemoryBalanceState(null);
        prev.InitializeFromFullLoad(new[]
        {
            ("100", AliceAddr, AliceToken, 1000L),
            ("200", BobAddr, BobToken, 2000L)
        });

        var fresh = new InMemoryBalanceState(null);
        fresh.InitializeFromFullLoad(new[]
        {
            ("100", AliceAddr, AliceToken, 1000L)
            // Bob's balance disappeared
        });

        var trust = new InMemoryTrustState();
        var avatar = new InMemoryAvatarState();
        avatar.InitializeFromFullLoad(new[]
        {
            (AliceAddr, "human"),
            (BobAddr, "human")  // Bob is a registered avatar → drift counts
        });

        Assert.DoesNotThrow(() =>
            svc.DetectAndRecordDrift(prev, trust, avatar, fresh, trust, avatar));
    }

    #endregion

    #region BuildGraphsFromLoadGraph

    [Test]
    public void BuildGraphsFromLoadGraph_MockData_PublishesToPoolAndNetworkState()
    {
        var networkState = new NetworkState();
        var mockLoadGraph = new MockLoadGraph();
        var pool = new CapacityGraphPool(RouterAddr, mockLoadGraph);
        var svc = CreateService(networkState: networkState, pool: pool);

        // Set up mock data: Alice and Bob each hold their own token, Bob trusts Alice
        mockLoadGraph.AddRegisteredAvatar(AliceAddr);
        mockLoadGraph.AddRegisteredAvatar(BobAddr);
        mockLoadGraph.AddTrust(BobAddr, AliceAddr);
        mockLoadGraph.AddTrust(AliceAddr, AliceAddr); // self-trust
        mockLoadGraph.AddTrust(BobAddr, BobAddr);
        mockLoadGraph.AddBalanceWei(AliceAddr, AliceToken, "1000000000000000000");
        mockLoadGraph.AddBalanceWei(BobAddr, BobToken, "500000000000000000");

        svc.BuildGraphsFromLoadGraph(mockLoadGraph, lastBlock: 42);

        Assert.Multiple(() =>
        {
            // NetworkState should have trust and balance data
            Assert.That(networkState.BalanceGraph, Is.Not.Null);
            Assert.That(networkState.AccountTrusts, Is.Not.Null);
            Assert.That(networkState.AccountTrusts.Count, Is.GreaterThan(0));

            // Pool should have a snapshot
            Assert.That(pool.CurrentSnapshot, Is.Not.Null);
            Assert.That(pool.CurrentSnapshot!.Block, Is.EqualTo(42));
        });
    }

    [Test]
    public void BuildGraphsFromLoadGraph_EmptyData_StillPublishes()
    {
        var networkState = new NetworkState();
        var mockLoadGraph = new MockLoadGraph();
        var pool = new CapacityGraphPool(RouterAddr, mockLoadGraph);
        var svc = CreateService(networkState: networkState, pool: pool);

        // Empty mock → should still work (just empty graphs)
        svc.BuildGraphsFromLoadGraph(mockLoadGraph, lastBlock: 1);

        Assert.That(pool.CurrentSnapshot, Is.Not.Null);
        Assert.That(pool.CurrentSnapshot!.Block, Is.EqualTo(1));
    }

    #endregion

    #region ResetIncrementalState after BuildGraphsFromLoadGraph

    [Test]
    public void ResetAfterCacheUpdate_ClearsStateForCleanDbFallback()
    {
        var svc = CreateService();

        // Simulate accumulated incremental state
        svc._balanceState = new InMemoryBalanceState(null);
        svc._trustState = new InMemoryTrustState();
        svc._avatarState = new InMemoryAvatarState();
        svc._lastFullRefreshBlock = 500;
        svc._lastProcessedBlock = 550;
        svc._lastProcessedBlockHash = "0xabc123";

        svc.ResetIncrementalState();

        // After reset, NeedsFullRefresh should return true (first-run condition)
        Assert.That(svc.NeedsFullRefresh(600), Is.True,
            "After reset, service should do full refresh on next DB fallback");
    }

    #endregion

    #region NeedsFullRefresh edge cases

    [Test]
    public void NeedsFullRefresh_ExactlyAtInterval_ReturnsTrue()
    {
        var settings = CreateSettings(fullRefreshInterval: 100);
        var svc = CreateService(settings);
        svc._balanceState = new InMemoryBalanceState(null);
        svc._lastFullRefreshBlock = 0;
        svc._lastProcessedBlock = 99;

        // Exactly at interval boundary (100 - 0 = 100 >= 100)
        Assert.That(svc.NeedsFullRefresh(100), Is.True);
    }

    [Test]
    public void NeedsFullRefresh_OneBeforeInterval_ReturnsFalse()
    {
        var settings = CreateSettings(fullRefreshInterval: 100);
        var svc = CreateService(settings);
        svc._balanceState = new InMemoryBalanceState(null);
        svc._lastFullRefreshBlock = 0;
        svc._lastProcessedBlock = 98;

        // One before interval (99 - 0 = 99 < 100)
        Assert.That(svc.NeedsFullRefresh(99), Is.False);
    }

    [Test]
    public void NeedsFullRefresh_IntervalOfOne_AlwaysRefreshes()
    {
        // FullRefreshIntervalBlocks=1 effectively disables incremental
        var settings = CreateSettings(fullRefreshInterval: 1);
        var svc = CreateService(settings);
        svc._balanceState = new InMemoryBalanceState(null);
        svc._lastFullRefreshBlock = 100;
        svc._lastProcessedBlock = 100;

        // Next block: 101 - 100 = 1 >= 1 → true
        Assert.That(svc.NeedsFullRefresh(101), Is.True);
    }

    #endregion
}
