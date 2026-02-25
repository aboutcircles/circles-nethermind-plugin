using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
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
