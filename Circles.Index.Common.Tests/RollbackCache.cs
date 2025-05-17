namespace Circles.Index.Common.Tests;

/// <summary>
/// Integration-style tests for <see cref="RollbackCache{TKey,TValue}"/>.
/// </summary>
[TestFixture]
public sealed class RollbackCacheTests
{
    /* --------------------------------------------------------------- */
    /*  ADD / GET                                                      */
    /* --------------------------------------------------------------- */

    [Test]
    public void Add_ExposesValues()
    {
        var cache = new RollbackCache<string, int>(rollbackCapacity: 4);

        cache.Add(1, "a", 10);
        cache.Add(1, "b", 20);

        Assert.That(cache.ContainsKey("a"), Is.True);
        Assert.That(cache.TryGetValue("b", out var v), Is.True);
        Assert.That(v, Is.EqualTo(20));
        Assert.That(cache.Get("a"), Is.EqualTo(10));
    }

    [Test]
    public void LaterBlock_OverwritesValue()
    {
        var cache = new RollbackCache<string, int>();

        cache.Add(1, "x", 1);
        cache.Add(2, "x", 2);

        Assert.That(cache.Get("x"), Is.EqualTo(2));
    }

    [Test]
    public void DuplicateKeyWithinBlock_LastWriteWins()
    {
        var cache = new RollbackCache<string, int>();

        cache.Add(1, "dup", 1);
        cache.Add(1, "dup", 42);

        Assert.That(cache.Get("dup"), Is.EqualTo(42));
    }

    /* --------------------------------------------------------------- */
    /*  ROLLBACK                                                       */
    /* --------------------------------------------------------------- */

    [Test]
    public void Rollback_RestoresPreviousState()
    {
        var cache = new RollbackCache<string, int>();

        cache.Add(1, "k", 1);
        cache.Add(2, "k", 2);
        cache.Add(2, "m", 5);

        cache.Rollback(1);

        Assert.That(cache.Get("k"), Is.EqualTo(1));
        Assert.That(cache.ContainsKey("m"), Is.False);
    }

    [Test]
    public void Rollback_RollbackToSameBlock()
    {
        // Nothing should happen if we rollback to the same block
        var cache = new RollbackCache<string, int>();

        cache.Add(1, "k", 1);
        cache.Add(2, "k", 2);
        cache.Add(2, "m", 5);

        cache.Rollback(2);

        Assert.That(cache.Get("k"), Is.EqualTo(2));
        Assert.That(cache.ContainsKey("k"), Is.True);
        Assert.That(cache.Get("k"), Is.EqualTo(2));
        Assert.That(cache.ContainsKey("m"), Is.True);
    }

    [Test]
    public void RollbackPastCapacity_Throws()
    {
        var cache = new RollbackCache<int, int>(rollbackCapacity: 2);

        cache.Add(1, 1, 1);
        cache.Add(2, 2, 2);
        cache.Add(3, 3, 3); // drops block 1 from history

        var ex = Assert.Throws<InvalidOperationException>(() => cache.Rollback(1));
        Assert.That(ex!.Message, Does.Contain("history"));
    }

    /* --------------------------------------------------------------- */
    /*  VALIDATION & EDGE PATHS                                        */
    /* --------------------------------------------------------------- */

    [Test]
    public void AddWithNonIncreasingBlock_Throws()
    {
        var cache = new RollbackCache<int, int>();

        cache.Add(5, 0, 0);

        var ex = Assert.Throws<ArgumentException>(() => cache.Add(4, 1, 1));
        Assert.That(ex!.Message, Does.Contain("monotonically increasing"));
    }

    [Test]
    public void Remove_ReturnsCorrectFlag()
    {
        var cache = new RollbackCache<string, int>();

        cache.Add(1, "x", 9);

        Assert.That(cache.Remove("x"), Is.True);
        Assert.That(cache.Remove("x"), Is.False);
        Assert.That(cache.ContainsKey("x"), Is.False);
    }

    [Test]
    public void Get_ForMissingKey_Throws()
    {
        var cache = new RollbackCache<string, int>();
        Assert.Throws<KeyNotFoundException>(() => cache.Get("missing"));
    }

    /* --------------------------------------------------------------- */
    /*  BASIC CONCURRENCY                                              */
    /* --------------------------------------------------------------- */

    [Test]
    [Timeout(15_000)]
    public async Task ConcurrentReaders_DoNotThrow()
    {
        const int readerCount = 16;
        const int iterations = 1_000;

        var cache = new RollbackCache<int, int>();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                cache.Add(i + 1, i, i); // one key per block
            }
        });

        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(() =>
        {
            var rnd = new Random(Thread.CurrentThread.ManagedThreadId);
            for (var i = 0; i < iterations; i++)
            {
                var key = rnd.Next(i + 1);
                cache.ContainsKey(key);
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));
    }
}