using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// A filtered request (withWrap, fromTokens, toTokens, exclusions, quantized) gets an ad-hoc
/// capacity graph from <see cref="CapacityGraphPool.Rent"/>, labelled with the pool snapshot's
/// block.
///
/// <see cref="NetworkStateUpdaterService.BuildGraphsFromLoadGraph"/> publishes the new trust lookup
/// and balance graph to NetworkState, then builds the base capacity graph, and only then moves the
/// pool to the new block. Rent used to build the ad-hoc graph from the balances and trust the
/// request handler read from NetworkState, so a filtered request arriving in between got block N+1
/// state labelled N: the simulation canary, which replays the path at the labelled block, then
/// reported an insufficient-balance revert for a holder credited in block N+1.
/// </summary>
[TestFixture]
public class FilteredGraphBlockConsistencyTests
{
    private const string RouterAddr = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private const string Alice = "0xa7c0000000000000000000000000000000000001";
    private const string Bob = "0xb7c0000000000000000000000000000000000002";
    private const string OneCrc = "1000000000000000000";
    private const string HundredAndOneCrc = "101000000000000000000";
    private static readonly UInt256 ThousandCrc = UInt256.Parse("1000000000000000000000");

    [OneTimeSetUp]
    public void EnsureEnvVars()
    {
        // Common.Settings constructor throws without these; set dummy values for unit tests.
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING",
            Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? "Host=localhost;Database=test;Username=test;Password=test");
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL",
            Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL") ?? "http://localhost:8545");
    }

    private static NetworkStateUpdaterService CreateService(NetworkState networkState, CapacityGraphPool pool)
    {
        var settings = new Circles.Pathfinder.Host.Settings { FullRefreshIntervalBlocks = 200, IncrementalEnabled = true };
        var dummyLoadGraph = new LoadGraph("Host=localhost;Database=dummy;Username=x;Password=x", new Circles.Pathfinder.Settings());
        return new NetworkStateUpdaterService(networkState, settings, NullLogger<NetworkStateUpdaterService>.Instance, pool, dummyLoadGraph);
    }

    /// <summary>Alice and Bob; Alice holds <paramref name="aliceWei"/> of her own token.</summary>
    private static MockLoadGraph BlockData(string aliceWei, bool bobTrustsAlice)
    {
        var data = new MockLoadGraph();
        data.AddRegisteredAvatar(Alice);
        data.AddRegisteredAvatar(Bob);
        if (bobTrustsAlice)
            data.AddTrust(Bob, Alice);
        data.AddTrust(Alice, Alice);
        data.AddTrust(Bob, Bob);
        data.AddBalanceWei(Alice, Alice, aliceWei);
        return data;
    }

    // Filtered: withWrap forces an ad-hoc graph (CapacityGraphPool.RequestNeedsFiltering).
    private static readonly FlowRequest WithWrapRequest = new()
    {
        Source = Alice,
        Sink = Bob,
        WithWrap = true,
    };

    private readonly record struct Observed(long Block, long AliceCapacity, string MaxFlow);

    /// <summary>
    /// The call FindPathHandler makes for every request (pool.Rent), then the solver on the rented
    /// graph. Records the graph's block label, the capacity of Alice's own-token balance edge, and
    /// the max flow from Alice to Bob.
    /// </summary>
    private static Observed RentAndSolve(CapacityGraphPool pool)
    {
        using var handle = pool.Rent(WithWrapRequest).GetAwaiter().GetResult();
        var alice = AddressIdPool.IdOf(Alice);
        var aliceCapacity = handle.Graph.Edges
            .Single(e => e.From == alice && e.Token == alice && e.To == AddressIdPool.TokenPoolIdOf(alice))
            .InitialCapacity;
        var maxFlow = new V2Pathfinder().ComputeMaxFlowWithPath(handle.Graph, WithWrapRequest, ThousandCrc).MaxFlow;
        return new Observed(handle.Graph.Block, aliceCapacity, maxFlow!);
    }

    private static IEnumerable<TestCaseData> BlockChanges()
    {
        yield return new TestCaseData(BlockData(OneCrc, bobTrustsAlice: true), BlockData(HundredAndOneCrc, bobTrustsAlice: true))
            .SetName("FilteredRequest_DuringGraphUpdate_UsesTheBalancesOfTheLabelledBlock");
        yield return new TestCaseData(BlockData(OneCrc, bobTrustsAlice: true), BlockData(OneCrc, bobTrustsAlice: false))
            .SetName("FilteredRequest_DuringGraphUpdate_UsesTheTrustOfTheLabelledBlock");
    }

    [TestCaseSource(nameof(BlockChanges))]
    public void FilteredRequest_DuringGraphUpdate_SeesTheStateOfTheLabelledBlock(MockLoadGraph block100, MockLoadGraph block101)
    {
        var networkState = new NetworkState();
        var pool = new CapacityGraphPool(RouterAddr, block100);
        var svc = CreateService(networkState, pool);

        // What each block's data produces when nothing is in flight.
        svc.BuildGraphsFromLoadGraph(block101, lastBlock: 101);
        var at101 = RentAndSolve(pool);
        svc.BuildGraphsFromLoadGraph(block100, lastBlock: 100);
        var at100 = RentAndSolve(pool);
        Assert.That(at100.Block, Is.EqualTo(100));
        Assert.That(at101.Block, Is.EqualTo(101));
        Assert.That(at101.MaxFlow, Is.Not.EqualTo(at100.MaxFlow), "test setup: block 101 changes the max flow");

        // Publish block 101 again with a request arriving while its base capacity graph is built.
        Observed? seen = null;
        long? poolBlockThen = null;
        bool? networkStateAheadThen = null;
        var duringUpdate = new CallbackLoadGraph(block101, onFirstRegisteredAvatarsLoad: () =>
        {
            poolBlockThen = pool.CurrentSnapshot!.Block;
            networkStateAheadThen = !ReferenceEquals(networkState.BalanceGraph, pool.CurrentSnapshot.Balances)
                                    && !ReferenceEquals(networkState.AccountTrusts, pool.CurrentSnapshot.Trust);
            seen = RentAndSolve(pool);
        });
        svc.BuildGraphsFromLoadGraph(duringUpdate, lastBlock: 101);

        // The request really ran in the window: the pool still on block 100, NetworkState on 101.
        Assert.That(seen, Is.Not.Null, "test setup: the request ran during the update");
        Assert.That(poolBlockThen, Is.EqualTo(100), "test setup: the pool had not moved to block 101 yet");
        Assert.That(networkStateAheadThen, Is.True, "test setup: NetworkState already held block 101");

        Assert.That(seen!.Value, Is.EqualTo(at100),
            "a filtered graph labelled block 100 must hold block 100's balances and trust");
        Assert.That(pool.CurrentSnapshot!.Block, Is.EqualTo(101), "the update completed");
        Assert.That(RentAndSolve(pool), Is.EqualTo(at101), "after the update, requests see block 101");
    }

    /// <summary>
    /// Delegates to a <see cref="MockLoadGraph"/> and runs a callback on the first
    /// LoadRegisteredAvatars call, which CreateBaseCapacityGraph makes after the balance graph is
    /// published and before the pool snapshot is replaced.
    /// </summary>
    private sealed class CallbackLoadGraph(MockLoadGraph inner, Action onFirstRegisteredAvatarsLoad) : ILoadGraph
    {
        private bool _fired;

        public IEnumerable<string> LoadRegisteredAvatars()
        {
            if (!_fired)
            {
                _fired = true;
                onFirstRegisteredAvatarsLoad();
            }
            return inner.LoadRegisteredAvatars();
        }

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust() => inner.LoadV2Trust();
        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances() => inner.LoadV2Balances();
        public IEnumerable<string> LoadGroups() => inner.LoadGroups();
        public IEnumerable<string> LoadOrganizations() => inner.LoadOrganizations();
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts() => inner.LoadGroupTrusts();
        public IEnumerable<(string GroupAddress, string RouterAddress)> LoadGroupRouters() => inner.LoadGroupRouters();
        public IEnumerable<(string GroupAddress, string CollateralToken, string AvailableLimit)> LoadScoreGroupMintLimits() => inner.LoadScoreGroupMintLimits();
        public IEnumerable<(string Account, string Operator)> LoadOperatorApprovals(IEnumerable<string> accounts) => inner.LoadOperatorApprovals(accounts);
        public IEnumerable<string> LoadScoreRouters() => inner.LoadScoreRouters();
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags() => inner.LoadConsentedFlowFlags();
        public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, CirclesType CirclesType)> LoadWrapperMappings() => inner.LoadWrapperMappings();
    }
}
