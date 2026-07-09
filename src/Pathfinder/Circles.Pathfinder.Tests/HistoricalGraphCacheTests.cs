using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Int256;
using Npgsql;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for HistoricalGraphCache eviction behavior (no database required —
/// uses the LoadGraphOverride test hook to substitute in-memory graph factories).
/// </summary>
[TestFixture]
[NonParallelizable] // mutates both HISTORICAL_GRAPH_CACHE_MAX_ENTRIES and POSTGRES_CONNECTION_STRING
public class HistoricalGraphCacheTests
{
    private const string MaxEntriesVar = "HISTORICAL_GRAPH_CACHE_MAX_ENTRIES";
    private const string ConnStringVar = "POSTGRES_CONNECTION_STRING";
    private string? _savedMaxEntries;
    private string? _savedConnString;

    [SetUp]
    public void SetUp()
    {
        _savedMaxEntries = Environment.GetEnvironmentVariable(MaxEntriesVar);
        _savedConnString = Environment.GetEnvironmentVariable(ConnStringVar);

        // Host.Settings construction requires the var to exist; no connection is ever opened.
        if (string.IsNullOrEmpty(_savedConnString))
        {
            Environment.SetEnvironmentVariable(ConnStringVar,
                "Host=localhost;Database=never_connected;Username=never;Password=never");
        }
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(MaxEntriesVar, _savedMaxEntries);
        Environment.SetEnvironmentVariable(ConnStringVar, _savedConnString);
    }

    private static HistoricalGraphCache CreateCache(int maxEntries, List<long> loadedBlocks)
    {
        Environment.SetEnvironmentVariable(MaxEntriesVar, maxEntries.ToString());

        // Data source is never used — LoadGraphOverride bypasses the DB entirely.
        var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=never_connected;Username=never;Password=never");

        return new HistoricalGraphCache(dataSource, new Host.Settings(),
            NullLogger<HistoricalGraphCache>.Instance)
        {
            LoadGraphOverride = block =>
            {
                lock (loadedBlocks) loadedBlocks.Add(block);
                return CreateEmptyFactory();
            }
        };
    }

    private static GraphFactory CreateEmptyFactory() =>
        new("0xdc287474114cc0551a81ddc2eb51783fbf34802f",
            new MaterializedLoadGraph([], [], [], [], [], [], [], [], [], [], [], []));

    [Test]
    public async Task CacheHit_DoesNotReload()
    {
        var loaded = new List<long>();
        var cache = CreateCache(maxEntries: 3, loaded);

        var first = await cache.GetOrLoadFactoryAsync(100);
        var second = await cache.GetOrLoadFactoryAsync(100);

        Assert.That(second, Is.SameAs(first), "cache hit must return the same factory instance");
        Assert.That(loaded, Is.EqualTo(new[] { 100L }), "block must be loaded exactly once");
    }

    [Test]
    public async Task Eviction_AtCapacity_EvictsOldestEntry()
    {
        var loaded = new List<long>();
        var cache = CreateCache(maxEntries: 3, loaded);

        // Fill to capacity with distinct last-access timestamps
        await cache.GetOrLoadFactoryAsync(1);
        await Task.Delay(5);
        await cache.GetOrLoadFactoryAsync(2);
        await Task.Delay(5);
        await cache.GetOrLoadFactoryAsync(3);
        await Task.Delay(5);

        // 4th insert must evict block 1 (oldest access), not a random entry
        await cache.GetOrLoadFactoryAsync(4);

        Assert.That(cache.CachedBlockNumbers, Is.EquivalentTo(new[] { 2L, 3L, 4L }));
    }

    [Test]
    public async Task Eviction_IsLruNotFifo_AccessRefreshesEntry()
    {
        var loaded = new List<long>();
        var cache = CreateCache(maxEntries: 3, loaded);

        await cache.GetOrLoadFactoryAsync(1);
        await Task.Delay(5);
        await cache.GetOrLoadFactoryAsync(2);
        await Task.Delay(5);
        await cache.GetOrLoadFactoryAsync(3);
        await Task.Delay(5);

        // Touch block 1 — block 2 becomes the least recently used
        await cache.GetOrLoadFactoryAsync(1);
        await Task.Delay(5);

        await cache.GetOrLoadFactoryAsync(4);

        Assert.That(cache.CachedBlockNumbers, Is.EquivalentTo(new[] { 1L, 3L, 4L }),
            "access must refresh recency: block 2 (LRU) evicted, block 1 retained");
    }

    [Test]
    public async Task Eviction_NeverExceedsMaxEntries()
    {
        var loaded = new List<long>();
        var cache = CreateCache(maxEntries: 2, loaded);

        for (long block = 1; block <= 8; block++)
        {
            await cache.GetOrLoadFactoryAsync(block);
            Assert.That(cache.CachedBlockNumbers.Count, Is.LessThanOrEqualTo(2));
            await Task.Delay(2);
        }

        Assert.That(cache.CachedBlockNumbers, Is.EquivalentTo(new[] { 7L, 8L }));
    }

    /// <summary>
    /// Guards the wiring by which "what-if" overlays reach the HISTORICAL request path:
    /// FindPathHandler.ExecuteHistorical obtains the factory from the historical cache and
    /// then calls CreateCapacityGraph(request) (FindPathHandler.cs — GetOrLoadFactoryAsync
    /// followed by CreateCapacityGraph). This test reproduces those two steps and asserts a
    /// simulated overlay changes the outcome, so a refactor that dropped `request` in the
    /// historical branch (silently ignoring overlays at a pinned block) would fail here.
    ///
    /// NOTE: LoadGraphOverride substitutes an in-memory factory, so this does NOT exercise
    /// the block-pinned SQL loader (HistoricalLoadGraph / blockNumber filtering) — that is
    /// covered by the staging-backed scenario suites. Scope here is strictly the overlay
    /// wiring, mirroring the other tests in this fixture.
    /// </summary>
    [Test]
    public async Task HistoricalFactory_ThreadsSimulatedOverlayThroughRequest()
    {
        const string router = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
        int Id(int i) => AddressIdPool.IdOf($"0x{i:X40}");
        string Addr(int i) => AddressIdPool.StringOf(Id(i));

        Environment.SetEnvironmentVariable(MaxEntriesVar, "3");
        var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=never_connected;Username=never;Password=never");

        // The graph the (overridden) historical factory returns: sink trusts source and the
        // token, but source holds nothing → no path from this state alone.
        var mock = new MockLoadGraph();
        mock.AddTrust(Id(2), Id(1)); // sink trusts source
        mock.AddTrust(Id(2), Id(10)); // sink trusts token

        var cache = new HistoricalGraphCache(dataSource, new Host.Settings(),
            NullLogger<HistoricalGraphCache>.Instance)
        {
            LoadGraphOverride = _ => new GraphFactory(router, mock)
        };

        // ExecuteHistorical step 1: factory from the historical cache.
        var factory = await cache.GetOrLoadFactoryAsync(43_193_632);
        var target = UInt256.Parse("1000000000000000000");

        // Without overlay → no path at the pinned block.
        var baseRequest = new FlowRequest { Source = Addr(1), Sink = Addr(2) };
        var baseCg = factory.CreateCapacityGraph(
            factory.V2BalanceGraph(), GraphFactory.BuildTrustLookup(factory.V2TrustGraph()), baseRequest);
        var baseResult = new V2Pathfinder().ComputeMaxFlowWithPath(baseCg, baseRequest, target);
        Assert.That(
            baseResult.Transfers is null || baseResult.Transfers.Count == 0
            || string.IsNullOrEmpty(baseResult.MaxFlow) || UInt256.Parse(baseResult.MaxFlow!) == UInt256.Zero,
            Is.True, "Block-pinned state alone must yield no path");

        // ExecuteHistorical step 2: CreateCapacityGraph(request-with-overlay) on the same
        // historical factory → the overlay must compose with the pinned graph.
        var overlayRequest = new FlowRequest
        {
            Source = Addr(1),
            Sink = Addr(2),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = Addr(1), Token = Addr(10), Amount = "100000000000000000000" } // 100 CRC
            }
        };
        var overlayCg = factory.CreateCapacityGraph(
            factory.V2BalanceGraph(), GraphFactory.BuildTrustLookup(factory.V2TrustGraph()), overlayRequest);
        var overlayResult = new V2Pathfinder().ComputeMaxFlowWithPath(overlayCg, overlayRequest, target);
        Assert.That(overlayResult.Transfers, Is.Not.Null.And.Not.Empty,
            "simulated overlay must compose with the block-pinned historical factory");
        Assert.That(UInt256.Parse(overlayResult.MaxFlow!) > UInt256.Zero, Is.True);
    }
}
