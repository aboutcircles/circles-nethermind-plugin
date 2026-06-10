using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for HistoricalGraphCache eviction behavior (no database required —
/// uses the LoadGraphOverride test hook to substitute in-memory graph factories).
/// </summary>
[TestFixture]
[NonParallelizable] // mutates HISTORICAL_GRAPH_CACHE_MAX_ENTRIES
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
}
