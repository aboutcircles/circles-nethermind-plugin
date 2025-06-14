namespace Circles.Index.Common.Tests;

/// <summary>
/// Verification tests for <see cref="RollbackCache{TKey,TValue}"/>.
/// </summary>
[TestFixture]
public sealed class RollbackCacheTests
{
    /* ----------------------------------------------------------------- */
    /*  ADD / GET                                                        */
    /* ----------------------------------------------------------------- */

    [Test]
    public void Add_ExposesValues_And_ReturnsInsertionFlag()
    {
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 4);

        var isNewA = cache.Add(1, "a", 10);
        var isNewB = cache.Add(1, "b", 20);
        var isNewA2 = cache.Add(1, "a", 30); // overwrite in same block

        Assert.Multiple(() =>
        {
            Assert.That(isNewA, Is.True);
            Assert.That(isNewB, Is.True);
            Assert.That(isNewA2, Is.False);
            Assert.That(cache.Get("a"), Is.EqualTo(30));
            Assert.That(cache.Get("b"), Is.EqualTo(20));
            Assert.That(cache.LastBlockNo, Is.EqualTo(1));
        });
    }

    [Test]
    public void LaterBlock_OverwritesValue_And_UpdatesLastBlock()
    {
        var cache = new RollbackCache<string, int>("Test");

        cache.Add(1, "x", 1);
        var isNew = cache.Add(2, "x", 2);

        Assert.Multiple(() =>
        {
            Assert.That(isNew, Is.False);
            Assert.That(cache.Get("x"), Is.EqualTo(2));
            Assert.That(cache.LastBlockNo, Is.EqualTo(2));
        });
    }

    [Test]
    public void DuplicateKeyWithinSameBlock_LastWriteWins()
    {
        var cache = new RollbackCache<string, int>("Test");

        cache.Add(1, "dup", 1);
        cache.Add(1, "dup", 42);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Get("dup"), Is.EqualTo(42));
            Assert.That(cache.LastBlockNo, Is.EqualTo(1));
        });
    }

    /* ----------------------------------------------------------------- */
    /*  SEED & LAST-BLOCK                                                */
    /* ----------------------------------------------------------------- */

    [Test]
    public void Seed_ReinitialisesState_And_SetsLastBlockNoToZero()
    {
        var cache = new RollbackCache<int, int>("Test");

        cache.Add(5, 99, 99);
        cache.Seed(new Dictionary<int, int> { [1] = 123 });

        Assert.Multiple(() =>
        {
            Assert.That(cache.LastBlockNo, Is.EqualTo(0));
            Assert.That(cache.Get(1), Is.EqualTo(123));
            Assert.That(cache.ContainsKey(99), Is.False);
        });
    }

    [Test]
    public void LastBlockNo_TracksHighestBlock_And_ChangesOnDelete()
    {
        var cache = new RollbackCache<int, int>("Test");

        cache.Seed(new Dictionary<int, int> { [111] = 0 }); // LastBlockNo == 0
        cache.Add(5, 1, 1);
        cache.Add(6, 2, 2);

        Assert.That(cache.LastBlockNo, Is.EqualTo(6));

        var stats = cache.DeleteAllGreaterOrEqualBlock(5);

        Assert.Multiple(() =>
        {
            Assert.That(stats.Removed, Is.EqualTo(2));
            Assert.That(stats.Restored, Is.EqualTo(0));
            Assert.That(cache.LastBlockNo, Is.EqualTo(long.MinValue));
        });
    }

    /* ----------------------------------------------------------------- */
    /*  DELETE (rollback)                                                */
    /* ----------------------------------------------------------------- */

    [Test]
    public void Delete_RemovesBlocksAndRestoresState()
    {
        var cache = new RollbackCache<string, int>("Test");

        cache.Add(1, "k", 1);
        cache.Add(2, "k", 2);
        cache.Add(2, "m", 5);

        var stats = cache.DeleteAllGreaterOrEqualBlock(2);

        Assert.Multiple(() =>
        {
            Assert.That(stats.Removed, Is.EqualTo(1));
            Assert.That(stats.Restored, Is.EqualTo(1));
            Assert.That(cache.Get("k"), Is.EqualTo(1));
            Assert.That(cache.ContainsKey("m"), Is.False);
            Assert.That(cache.LastBlockNo, Is.EqualTo(1));
        });
    }

    [Test]
    public void DeletePastCapacity_Throws()
    {
        var cache = new RollbackCache<int, int>("Test", rollbackCapacity: 2);

        cache.Add(1, 1, 1);
        cache.Add(2, 2, 2);
        cache.Add(3, 3, 3); // drops block-1 from history

        var ex = Assert.Throws<InvalidOperationException>(() => cache.DeleteAllGreaterOrEqualBlock(1));
        Assert.That(ex!.Message, Does.Contain("history"));
    }

    [Test]
    public void DeleteBeforeEarliestBlock_Throws()
    {
        var cache = new RollbackCache<int, int>("Test");

        cache.Add(5, 1, 1);
        cache.Add(6, 2, 2);

        var ex = Assert.Throws<InvalidOperationException>(() => cache.DeleteAllGreaterOrEqualBlock(4));
        Assert.That(ex!.Message, Does.Contain("history"));
    }

    /* ----------------------------------------------------------------- */
    /*  VALIDATION & EDGE CASES                                          */
    /* ----------------------------------------------------------------- */

    [Test]
    public void AddWithNonIncreasingBlock_Throws()
    {
        var cache = new RollbackCache<int, int>("Test");

        cache.Add(5, 0, 0);

        var ex = Assert.Throws<ArgumentException>(() => cache.Add(4, 1, 1));
        Assert.That(ex!.Message, Does.Contain("monotonically increasing"));
    }

    [Test]
    public void Remove_ReturnsCorrectFlag()
    {
        var cache = new RollbackCache<string, int>("Test");

        cache.Add(1, "x", 9);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Remove("x"), Is.True);
            Assert.That(cache.Remove("x"), Is.False);
            Assert.That(cache.ContainsKey("x"), Is.False);
        });
    }

    [Test]
    public void Get_ForMissingKey_Throws()
    {
        var cache = new RollbackCache<string, int>("Test");
        Assert.Throws<KeyNotFoundException>(() => cache.Get("missing"));
    }

    /* ----------------------------------------------------------------- */
    /*  BASIC CONCURRENCY                                                */
    /* ----------------------------------------------------------------- */

    [Test]
    [Timeout(15_000)]
    public async Task ConcurrentReaders_DoNotThrow()
    {
        const int readerCount = 16;
        const int iterations = 1_000;

        var cache = new RollbackCache<int, int>("Test");

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
                cache.Add(i + 1, i, i); // one key per block
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

    [Test]
    public void CommitEmptyBlock_AdvancesLastBlock_WithoutTouchingState()
    {
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 4);

        cache.Add(1, "x", 1); // real change
        cache.CommitEmptyBlock(2); // no events
        cache.CommitEmptyBlock(3); // no events

        Assert.Multiple(() =>
        {
            Assert.That(cache.LastBlockNo, Is.EqualTo(3), "head should advance");
            Assert.That(cache.Get("x"), Is.EqualTo(1), "state must be unchanged");
            Assert.That(cache.Count, Is.EqualTo(1), "no extra entries created");
            // window not full yet → still no safe snapshot
            Assert.That(cache.GetLastSafeSnapshot(), Is.Null);
        });
    }

    [Test]
    public void CommitEmptyBlock_NonIncreasingBlock_Throws()
    {
        var cache = new RollbackCache<int, int>("Test");
        cache.CommitEmptyBlock(5);

        var ex = Assert.Throws<ArgumentException>(() => cache.CommitEmptyBlock(4));
        Assert.That(ex!.Message, Does.Contain("monotonically increasing"));
    }

    [Test]
    public void CommitEmptyBlock_PrunesHistory_WhenCapacityExceeded()
    {
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 2);

        cache.Add(1, "a", 10); // diff for block-1
        cache.CommitEmptyBlock(2); // empty diff
        cache.CommitEmptyBlock(3); // empty diff → capacity exceeded, block-1 pruned

        // Trying to roll back to block-1 is now outside retained history
        var ex = Assert.Throws<InvalidOperationException>(() => cache.DeleteAllGreaterOrEqualBlock(1));
        Assert.That(ex!.Message, Does.Contain("history"));
    }

    [Test]
    public void SafeSnapshot_IsExposed_AfterRollbackWindowIsFilled()
    {
        // rollbackCapacity = 2 -> need 3 committed blocks before a snapshot is safe
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 2);

        // block-1 (safe candidate)
        cache.Add(1, "a", 1);

        // block-2 (still within window -> state changes shouldn't appear in safe snapshot)
        cache.Add(2, "a", 2);
        cache.Add(2, "b", 9);

        // block-3 (empty block, just advances head -> window now full)
        cache.CommitEmptyBlock(3);

        // Now a snapshot must be available and reflect *only* block-1 state.
        var snap = cache.GetLastSafeSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snap, Is.Not.Null, "snapshot should exist once window is full");
            Assert.That(snap!.Count, Is.EqualTo(1));
            Assert.That(snap["a"], Is.EqualTo(1), "value from block-1");
            Assert.That(snap.ContainsKey("b"), Is.False, "block-2 data must not surface yet");
        });
    }

    [Test]
    public void SafeSnapshot_SlidesForward_WhenWindowAdvances()
    {
        var cache = new RollbackCache<string, int>("Test", rollbackCapacity: 2);

        // block-1
        cache.Add(1, "x", 1);

        // block-2
        cache.Add(2, "x", 2);
        cache.Add(2, "y", 5);

        // block-3 (empty) -> first safe snapshot available (reflects block-1)
        cache.CommitEmptyBlock(3);
        var snap1 = cache.GetLastSafeSnapshot();

        // block-4 (empty) -> window slides, now block-2 becomes safe
        cache.CommitEmptyBlock(4);
        var snap2 = cache.GetLastSafeSnapshot();

        Assert.Multiple(() =>
        {
            // ── first snapshot shows block-1 only ───────────────────────────
            Assert.That(snap1!["x"], Is.EqualTo(1));
            Assert.That(snap1.ContainsKey("y"), Is.False);

            // ── second snapshot shows state after block-2 ───────────────────
            Assert.That(snap2!["x"], Is.EqualTo(2));
            Assert.That(snap2!["y"], Is.EqualTo(5));

            // snapshot dictionaries are different instances (immutable view)
            Assert.That(ReferenceEquals(snap1, snap2), Is.False);
        });
    }
}