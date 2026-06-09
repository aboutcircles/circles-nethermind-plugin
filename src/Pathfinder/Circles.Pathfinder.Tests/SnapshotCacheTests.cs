using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Tests.Helpers;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

[TestFixture, Parallelizable]
public class SnapshotCacheTests
{
    private static NetworkState CreatePopulatedState(long blockNumber = 100)
    {
        var state = new NetworkState();
        var bg = new BalanceGraph();
        bg.AddAvatar(1);
        bg.AddBalance(1, 2, 1000, isWrapped: false, isStatic: false);
        var trusts = new Dictionary<int, HashSet<int>> { { 1, new HashSet<int> { 2 } } };
        state.Replace(balanceGraph: bg, accountTrusts: trusts, lastKnownBlockNumber: blockNumber);
        return state;
    }

    [Test]
    public void GetOrBuildSnapshot_GraphsNotReady_ReturnsNull()
    {
        var cache = new SnapshotCache();
        var state = new NetworkState(); // default: null BalanceGraph, empty trusts

        var (json, etag) = cache.GetOrBuildSnapshot(state);

        Assert.That(json, Is.Null);
        Assert.That(etag, Is.Null);
    }

    [Test]
    public void GetOrBuildSnapshot_GraphsReady_ReturnsJson()
    {
        var cache = new SnapshotCache();
        var state = CreatePopulatedState();

        var (json, etag) = cache.GetOrBuildSnapshot(state);

        Assert.That(json, Is.Not.Null);
        Assert.That(json!.Length, Is.GreaterThan(0));
        Assert.That(etag, Is.Not.Null);
        Assert.That(etag, Is.Not.Empty);
    }

    [Test]
    public void GetOrBuildSnapshot_SameBlock_ReturnsCached()
    {
        var cache = new SnapshotCache();
        var state = CreatePopulatedState(blockNumber: 100);

        var (json1, etag1) = cache.GetOrBuildSnapshot(state);
        var (json2, etag2) = cache.GetOrBuildSnapshot(state);

        Assert.That(json1, Is.Not.Null);
        Assert.That(ReferenceEquals(json1, json2), Is.True,
            "Same block should return the same cached byte[] reference");
        Assert.That(etag1, Is.EqualTo(etag2));
    }

    [Test]
    public void GetOrBuildSnapshot_DifferentBlock_Invalidates()
    {
        var cache = new SnapshotCache();
        var state = CreatePopulatedState(blockNumber: 100);

        var (_, etag1) = cache.GetOrBuildSnapshot(state);

        // Advance block number
        state.Replace(lastKnownBlockNumber: 200);

        var (_, etag2) = cache.GetOrBuildSnapshot(state);

        Assert.That(etag1, Is.Not.Null);
        Assert.That(etag2, Is.Not.Null);
        Assert.That(etag2, Is.Not.EqualTo(etag1),
            "Different block number should produce a different ETag");
    }

    [Test]
    public void ETag_Format_ContainsBlockNumber()
    {
        var cache = new SnapshotCache();
        var state = CreatePopulatedState(blockNumber: 100);

        cache.GetOrBuildSnapshot(state);

        Assert.That(cache.CurrentETag, Is.EqualTo("\"100\""),
            "ETag should be the block number wrapped in quotes");
    }

    [Test]
    public void CachedBlockNumber_ReflectsLatestBuild()
    {
        var cache = new SnapshotCache();
        var state = CreatePopulatedState(blockNumber: 100);

        Assert.That(cache.CachedBlockNumber, Is.EqualTo(-1),
            "Before any build, CachedBlockNumber should be -1");

        cache.GetOrBuildSnapshot(state);

        Assert.That(cache.CachedBlockNumber, Is.EqualTo(100));
    }

    // ─────────────────────── Historical (block-pinned) snapshot ───────────────────────

    /// <summary>
    /// Builds a minimal in-memory GraphFactory (one trust + one balance) so the historical
    /// snapshot path has non-empty trust/balance graphs to project.
    /// </summary>
    private static GraphFactory CreateFactory()
    {
        // Allocate ids in the global pool first (MockLoadGraph.AddTrust(int,int) resolves them
        // back to addresses via AddressIdPool.StringOf, which requires prior registration).
        var source = AddressIdPool.IdOf("0x0000000000000000000000000000000000000001");
        var token = AddressIdPool.IdOf("0x000000000000000000000000000000000000000a");
        var mock = new MockLoadGraph();
        mock.AddTrust(source, token);
        mock.AddBalance(source, token, 200_000_000L);
        return new GraphFactory("0x0000000000000000000000000000000000000000", mock);
    }

    [Test]
    public void GetOrBuildHistoricalSnapshot_ReturnsJsonWithBlockEtag()
    {
        var cache = new SnapshotCache();

        var (json, etag) = cache.GetOrBuildHistoricalSnapshot(CreateFactory(), 42);

        Assert.That(json, Is.Not.Null);
        Assert.That(json.Length, Is.GreaterThan(0));
        Assert.That(etag, Is.EqualTo("\"historical-42\""),
            "Historical ETag should be namespaced by block (never aliases the live ETag)");
        Assert.That(etag, Is.EqualTo(SnapshotCache.HistoricalETag(42)),
            "Endpoint and cache must agree on the historical ETag format");
    }

    [Test]
    public void GetOrBuildHistoricalSnapshot_SameBlock_ReturnsCached()
    {
        var cache = new SnapshotCache();
        var factory = CreateFactory();

        var (json1, etag1) = cache.GetOrBuildHistoricalSnapshot(factory, 42);
        var (json2, etag2) = cache.GetOrBuildHistoricalSnapshot(factory, 42);

        Assert.That(ReferenceEquals(json1, json2), Is.True,
            "Same block should return the same cached byte[] reference (single-slot cache hit)");
        Assert.That(etag1, Is.EqualTo(etag2));
    }

    [Test]
    public void GetOrBuildHistoricalSnapshot_DifferentBlock_Rebuilds()
    {
        var cache = new SnapshotCache();
        var factory = CreateFactory();

        var (json1, etag1) = cache.GetOrBuildHistoricalSnapshot(factory, 42);
        var (json2, etag2) = cache.GetOrBuildHistoricalSnapshot(factory, 99);

        Assert.That(etag1, Is.EqualTo("\"historical-42\""));
        Assert.That(etag2, Is.EqualTo("\"historical-99\""));
        Assert.That(ReferenceEquals(json1, json2), Is.False,
            "Different block should rebuild (single slot evicted)");
    }

    [Test]
    public void HistoricalETag_NeverAliasesLiveETag_ForSameBlock()
    {
        // The live ETag is the bare block number; the historical ETag is namespaced. The two must
        // differ for the same block so a client mixing modes can't get a cross-variant 304.
        Assert.That(SnapshotCache.HistoricalETag(100), Is.Not.EqualTo("\"100\""));
        Assert.That(SnapshotCache.HistoricalETag(100), Is.EqualTo("\"historical-100\""));
    }

    [Test]
    public void GetOrBuildHistoricalSnapshot_DoesNotTouchLiveSlot()
    {
        var cache = new SnapshotCache();

        cache.GetOrBuildHistoricalSnapshot(CreateFactory(), 42);

        // The historical slot is independent of the live slot.
        Assert.That(cache.CurrentETag, Is.Null,
            "Historical builds must not populate the live ETag");
        Assert.That(cache.CachedBlockNumber, Is.EqualTo(-1),
            "Historical builds must not populate the live cached block number");
    }

    [Test]
    public void ConcurrentAccess_NoExceptions()
    {
        var cache = new SnapshotCache();
        var state = CreatePopulatedState(blockNumber: 100);
        var exceptions = new List<Exception>();

        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    var (json, etag) = cache.GetOrBuildSnapshot(state);
                    Assert.That(json, Is.Not.Null);
                    Assert.That(etag, Is.Not.Null);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.That(exceptions, Is.Empty,
            $"Concurrent access should not throw. Got: {string.Join("; ", exceptions.Select(e => e.Message))}");
    }
}
