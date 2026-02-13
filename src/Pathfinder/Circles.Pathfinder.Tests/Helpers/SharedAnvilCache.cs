using System.Collections.Concurrent;
using Circles.Common.TestUtils;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Caches Anvil fork sessions per block number for differential testing.
///
/// Since SimulateTransferPathAsync uses eth_call (stateless, no state changes),
/// multiple scenarios at the same block can safely share one Anvil session.
///
/// The session includes both DB and Anvil features. It registers itself
/// with SharedGraphCache so graph loading reuses the same session
/// (avoids duplicate DB sessions that can hit resource limits).
///
/// With only 4 distinct block numbers across 22+ Anvil scenarios,
/// this reduces session creation from 22 to 4 (82% reduction).
/// </summary>
public static class SharedAnvilCache
{
    private static readonly ConcurrentDictionary<long, AnvilSessionData> Cache = new();
    private static readonly ConcurrentDictionary<long, object> CreateLocks = new();

    /// <summary>
    /// Gets (or creates) a shared Anvil session for the given block number.
    /// First call per block creates the session and fetches the block timestamp.
    /// Subsequent calls return the cached data immediately.
    /// Also registers the session with SharedGraphCache for reuse.
    /// </summary>
    public static AnvilSessionData GetOrCreate(long blockNumber)
    {
        // Fast path
        if (Cache.TryGetValue(blockNumber, out var cached))
            return cached;

        var lockObj = CreateLocks.GetOrAdd(blockNumber, _ => new object());
        lock (lockObj)
        {
            // Double-check
            if (Cache.TryGetValue(blockNumber, out cached))
                return cached;

            Console.WriteLine($"[SharedAnvilCache] Creating Anvil session for block {blockNumber}...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
            var session = TestEnvironmentClient.CreateSessionAsync(
                blockNumber,
                features: ["db", "anvil"],
                ttl: "10m",
                testEnvUrl: testEnvUrl
            ).GetAwaiter().GetResult();

            if (!session.HasAnvil)
                throw new InvalidOperationException($"Anvil not available for block {blockNumber}");

            var anvil = new AnvilExecutionHelper(session);
            var timestamp = anvil.GetBlockTimestampAsync().GetAwaiter().GetResult();

            sw.Stop();
            Console.WriteLine($"[SharedAnvilCache] Block {blockNumber}: Anvil session ready " +
                $"(timestamp {timestamp:u}) — created in {sw.ElapsedMilliseconds}ms");

            var data = new AnvilSessionData
            {
                Session = session,
                Anvil = anvil,
                BlockTimestamp = timestamp,
                BlockNumber = blockNumber
            };

            Cache[blockNumber] = data;

            // Register with SharedGraphCache so it reuses this session for DB queries
            SharedGraphCache.RegisterSession(blockNumber, session);

            return data;
        }
    }

    /// <summary>
    /// Clears all cached sessions and disposes resources.
    /// Call from [OneTimeTearDown] in test fixtures.
    /// </summary>
    public static async Task ClearAsync()
    {
        foreach (var kvp in Cache)
        {
            try
            {
                kvp.Value.Anvil.Dispose();
                await kvp.Value.Session.DisposeAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        Cache.Clear();
        CreateLocks.Clear();
    }
}

/// <summary>
/// Cached Anvil session data for a specific block number.
/// </summary>
public class AnvilSessionData
{
    public long BlockNumber { get; init; }
    public TestEnvironmentClient Session { get; init; } = null!;
    public AnvilExecutionHelper Anvil { get; init; } = null!;
    public DateTimeOffset BlockTimestamp { get; init; }
}
