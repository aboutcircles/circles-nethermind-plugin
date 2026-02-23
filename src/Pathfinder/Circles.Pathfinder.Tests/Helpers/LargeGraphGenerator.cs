namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Generates large-scale mock graph data for benchmarks and scale tests.
/// Uses deterministic seeding for reproducible results across runs.
/// </summary>
public static class LargeGraphGenerator
{
    /// <summary>
    /// Populates a <see cref="MockLoadGraph"/> with realistic trust and balance data.
    /// </summary>
    /// <param name="avatarCount">Number of avatars to create.</param>
    /// <param name="trustDensity">Average number of trust edges per avatar.</param>
    /// <param name="tokensPerAvatar">Average number of token balances per avatar.</param>
    /// <param name="seed">RNG seed for reproducibility.</param>
    /// <returns>A populated MockLoadGraph and metadata about the generated data.</returns>
    public static (MockLoadGraph Graph, GeneratedGraphInfo Info) Generate(
        int avatarCount = 10_000,
        int trustDensity = 5,
        int tokensPerAvatar = 3,
        int seed = 42)
    {
        var rng = new Random(seed);
        var mock = new MockLoadGraph();

        // Generate unique avatar addresses (0x + 40 hex, padded from index)
        var addresses = new string[avatarCount];
        for (int i = 0; i < avatarCount; i++)
        {
            addresses[i] = $"0x{i:x40}";
        }

        // Generate trust edges: each avatar trusts ~trustDensity random others
        int trustCount = 0;
        for (int i = 0; i < avatarCount; i++)
        {
            int edgeCount = Math.Max(1, trustDensity + rng.Next(-2, 3)); // ±2 variance
            for (int j = 0; j < edgeCount; j++)
            {
                int target = rng.Next(avatarCount);
                if (target == i) continue; // no self-trust

                mock.AddTrust(addresses[i], addresses[target]);
                trustCount++;
            }
        }

        // Generate balances: each avatar holds ~tokensPerAvatar token types
        // Token address = avatar address (in Circles v2, your token IS your address)
        // Track emitted (holder, token) pairs — BalanceGraph.AddBalance throws on duplicates
        var emittedBalances = new HashSet<(int, int)>();
        int balanceCount = 0;
        for (int i = 0; i < avatarCount; i++)
        {
            int holderId = AddressIdPool.IdOf(addresses[i]);
            int tokenCount = Math.Max(1, tokensPerAvatar + rng.Next(-1, 2));
            for (int j = 0; j < tokenCount; j++)
            {
                int tokenOwner = rng.Next(avatarCount);
                int tokenId = AddressIdPool.IdOf(addresses[tokenOwner]);
                if (!emittedBalances.Add((holderId, tokenId))) continue; // skip duplicate pair
                // Balance: 50-500 CRC in truncated form (6 decimals)
                long amount = (50 + rng.Next(450)) * 1_000_000L;
                mock.AddBalance(holderId, tokenId, amount);
                balanceCount++;
            }
        }

        var info = new GeneratedGraphInfo(avatarCount, trustCount, balanceCount, addresses);
        return (mock, info);
    }
}

/// <summary>
/// Metadata about a generated graph for test assertions.
/// </summary>
public sealed record GeneratedGraphInfo(
    int AvatarCount,
    int TrustEdgeCount,
    int BalanceCount,
    string[] Addresses);
