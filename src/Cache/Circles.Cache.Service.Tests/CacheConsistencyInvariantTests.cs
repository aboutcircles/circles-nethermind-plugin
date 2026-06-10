using Circles.Cache.Service.Caches;
using Circles.Common;
using FluentAssertions;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Invariant tests for CacheContainer's atomicity guarantees: indexed readers must never
/// observe a secondary-index entry pointing at a missing/torn value while writers, removers,
/// or rollbacks run concurrently. These tests pin the single-lock-scope contract of the
/// Upsert*/Remove*/UpsertBalance methods.
/// </summary>
public class CacheConsistencyInvariantTests
{
    private const string Truster = "0x1111111111111111111111111111111111111111";
    private const string Trustee = "0x2222222222222222222222222222222222222222";
    private const string Account = "0x3333333333333333333333333333333333333333";
    private const string Token = "0x4444444444444444444444444444444444444444";

    [Fact]
    public async Task ConcurrentTrustUpsertRemove_ReadersNeverSeeTornState()
    {
        using var container = new CacheContainer(rollbackCapacity: 12);
        var stop = false;
        var violations = new List<string>();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var rels = container.GetTrustsFor(Truster, isV1: false).ToList();
                lock (violations)
                {
                    if (rels.Count > 1)
                        violations.Add($"duplicate results: {rels.Count}");
                    // Upserts only ever write even block numbers as expiry — anything
                    // else means the reader observed a torn value.
                    foreach (var (trustee, expiry) in rels)
                    {
                        if (trustee != Trustee || expiry % 2 != 0 || expiry < 2)
                            violations.Add($"torn read: trustee={trustee} expiry={expiry}");
                    }
                }
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            for (long block = 2; block <= 600; block++)
            {
                if (block % 2 == 0)
                    container.UpsertV2Trust(block, Truster, Trustee, expiryTime: block);
                else
                    container.RemoveV2Trust(block, Truster, Trustee);
            }
        });

        await writer;
        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        violations.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentBalanceUpserts_IndexAndValueStayConsistent()
    {
        using var container = new CacheContainer(rollbackCapacity: 12);
        var key = $"{Account}:{Token}";
        var stop = false;
        var violations = new List<string>();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var tokens = container.GetTokenIdsForAddress(Account, isV1: false).ToList();
                lock (violations)
                {
                    if (tokens.Count > 1)
                        violations.Add($"duplicate index entries: {tokens.Count}");
                    foreach (var tokenId in tokens)
                    {
                        if (tokenId != Token)
                        {
                            violations.Add($"unexpected token in index: {tokenId}");
                            continue;
                        }

                        // Once a key has been written, the value store always has it
                        // (zero balances stay in the store; only the index drops them).
                        if (!container.V2BalancesByAccountAndToken.TryGetValue(key, out var balance))
                            violations.Add("index entry without value");
                        else if (balance != 0m && balance != 100m)
                            violations.Add($"torn balance: {balance}");
                    }
                }
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            for (long block = 1; block <= 600; block++)
            {
                var balance = block % 2 == 0 ? 100m : 0m;
                container.UpsertBalance(block, key, isV1: false, balance);
            }
        });

        await writer;
        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void RollbackAll_LeavesAllCachesAtSameBlock()
    {
        using var container = new CacheContainer(rollbackCapacity: 12);

        for (long block = 1; block <= 10; block++)
        {
            container.UpsertV2Trust(block, Truster, $"0x{block:d40}", expiryTime: block);
            container.UpsertBalance(block, $"{Account}:0x{block:d40}", isV1: false, balance: block);
            container.UpsertGroupMembership(block, $"0x{block:d40}", Trustee, expiryTime: block);
            container.UpsertWrapper(block, $"0xaa{block:d38}", Account, CirclesType.DemurrageCircles);
            container.V2Avatars.Add(block, $"0x{block:d40}", ("Human", block));
        }

        container.RollbackAll(toBlock: 6);

        container.V2TrustRelations.LastBlockNo.Should().Be(5);
        container.V2BalancesByAccountAndToken.LastBlockNo.Should().Be(5);
        container.GroupMemberships.LastBlockNo.Should().Be(5);
        container.Erc20WrapperAddresses.LastBlockNo.Should().Be(5);
        container.V2Avatars.LastBlockNo.Should().Be(5);

        // Entries written at block >= 6 must be gone from the value stores
        container.V2TrustRelations.ContainsKey($"{Truster}:0x{7:d40}").Should().BeFalse();
        container.V2TrustRelations.ContainsKey($"{Truster}:0x{5:d40}").Should().BeTrue();

        // After the rebuild that the rollback flow performs, indexed reads must
        // reflect only pre-rollback state.
        container.RebuildSecondaryIndexes();
        var trustees = container.GetTrustsFor(Truster, isV1: false).Select(t => t.Trustee).ToList();
        trustees.Should().HaveCount(5);
        trustees.Should().NotContain($"0x{7:d40}");
    }

    [Fact]
    public async Task ConcurrentReads_DuringRollbackAndRebuild_NeverThrow()
    {
        using var container = new CacheContainer(rollbackCapacity: 12);

        for (long block = 1; block <= 10; block++)
        {
            container.UpsertV2Trust(block, Truster, $"0x{block:d40}", expiryTime: block);
            container.UpsertBalance(block, $"{Account}:0x{block:d40}", isV1: false, balance: block);
        }

        var stop = false;
        var failures = new List<Exception>();

        var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    container.GetTrustsFor(Truster, isV1: false).ToList();
                    container.GetTokenIdsForAddress(Account, isV1: false).ToList();
                }
                catch (Exception ex)
                {
                    lock (failures) failures.Add(ex);
                }
            }
        })).ToArray();

        var mutator = Task.Run(() =>
        {
            container.RollbackAll(toBlock: 6);
            container.RebuildSecondaryIndexes();
            for (long block = 6; block <= 12; block++)
            {
                container.UpsertV2Trust(block, Truster, $"0x{block:d40}", expiryTime: block);
            }
        });

        await mutator;
        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        failures.Should().BeEmpty();

        // Final state: trustees from blocks 1-5 (survived rollback) + 6-12 (re-added)
        var trustees = container.GetTrustsFor(Truster, isV1: false).Select(t => t.Trustee).ToList();
        trustees.Should().HaveCount(12);
    }
}
