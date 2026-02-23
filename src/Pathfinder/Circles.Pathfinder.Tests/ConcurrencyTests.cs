using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.State;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for atomic graph replacement in <see cref="NetworkState"/>.
/// Validates that concurrent readers/writers never observe torn or corrupted state.
/// </summary>
[TestFixture]
[Category("Concurrency")]
public class ConcurrencyTests
{
    /// <summary>
    /// Multiple reader threads continuously read state while a writer swaps it.
    /// No reader should ever see null BalanceGraph when block > 0 (partial state).
    /// </summary>
    [Test]
    public void Replace_ConcurrentReaders_NoTornState()
    {
        var state = new NetworkState();
        const int writerIterations = 10_000;
        const int readerCount = 4;

        using var cts = new CancellationTokenSource();
        var violations = new List<string>();
        var violationLock = new object();

        // Start reader threads that continuously sample the state
        var readers = Enumerable.Range(0, readerCount).Select(readerId => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var snapshot = state.Current;

                // Invariant: if block > 0, BalanceGraph must not be null
                // (writer always sets both together)
                if (snapshot.Block > 0 && snapshot.BalanceGraph == null)
                {
                    lock (violationLock)
                    {
                        violations.Add(
                            $"Reader {readerId}: Block={snapshot.Block} but BalanceGraph is null (torn state)");
                    }
                }

                // Invariant: block number should never be negative
                if (snapshot.Block < 0)
                {
                    lock (violationLock)
                    {
                        violations.Add($"Reader {readerId}: Block={snapshot.Block} is negative");
                    }
                }
            }
        }, cts.Token)).ToArray();

        // Writer: rapidly swap states
        for (int i = 1; i <= writerIterations; i++)
        {
            var bg = new BalanceGraph();
            var trusts = new Dictionary<int, HashSet<int>>();
            state.Replace(balanceGraph: bg, accountTrusts: trusts, lastKnownBlockNumber: i);
        }

        cts.Cancel();
        Task.WaitAll(readers);

        Assert.That(violations, Is.Empty,
            $"Found {violations.Count} torn-state violations:\n{string.Join("\n", violations.Take(10))}");
        Assert.That(state.LastKnownBlockNumber, Is.EqualTo(writerIterations),
            "Final block number should match last write");
    }

    /// <summary>
    /// Two writer threads race to replace state. The final state must be
    /// from one writer or the other — never a mix (CAS guarantees atomicity).
    /// </summary>
    [Test]
    public void Replace_ConcurrentWriters_LastWriteWins()
    {
        var state = new NetworkState();
        const int iterations = 5_000;

        // Writer A: sets block to positive values (1..iterations)
        // Writer B: sets block to negative offset (-1..-iterations)
        // We use block numbers to identify which writer's state we see.
        var writerA = Task.Run(() =>
        {
            for (int i = 1; i <= iterations; i++)
            {
                var bg = new BalanceGraph();
                state.Replace(balanceGraph: bg, lastKnownBlockNumber: i);
            }
        });

        var writerB = Task.Run(() =>
        {
            for (int i = 1; i <= iterations; i++)
            {
                var bg = new BalanceGraph();
                state.Replace(balanceGraph: bg, lastKnownBlockNumber: -i);
            }
        });

        Task.WaitAll(writerA, writerB);

        var finalSnapshot = state.Current;

        // Final state must be from A or B, never corrupted
        Assert.That(finalSnapshot.BalanceGraph, Is.Not.Null,
            "Final state should have a BalanceGraph");
        Assert.That(finalSnapshot.Block, Is.Not.EqualTo(0),
            "Final block should be non-zero (written by A or B)");

        // Block should be a valid value from either writer
        bool fromWriterA = finalSnapshot.Block >= 1 && finalSnapshot.Block <= iterations;
        bool fromWriterB = finalSnapshot.Block >= -iterations && finalSnapshot.Block <= -1;
        Assert.That(fromWriterA || fromWriterB, Is.True,
            $"Final block {finalSnapshot.Block} is not from either writer");
    }

    /// <summary>
    /// Validates that partial Replace() (only some fields) preserves other fields.
    /// </summary>
    [Test]
    public void Replace_PartialUpdate_PreservesUnchangedFields()
    {
        var state = new NetworkState();

        // Set initial state with all fields
        var bg1 = new BalanceGraph();
        var trusts1 = new Dictionary<int, HashSet<int>> { [1] = new() { 2 } };
        state.Replace(balanceGraph: bg1, accountTrusts: trusts1, lastKnownBlockNumber: 100);

        // Partial update: only change block number
        state.Replace(lastKnownBlockNumber: 200);

        var snapshot = state.Current;
        Assert.That(snapshot.Block, Is.EqualTo(200));
        Assert.That(snapshot.BalanceGraph, Is.SameAs(bg1),
            "BalanceGraph should be preserved from previous state");
        Assert.That(snapshot.AccountTrusts, Is.SameAs(trusts1),
            "AccountTrusts should be preserved from previous state");
    }
}
