using Circles.Cache.Service;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Metrics;
using Circles.Cache.Service.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Regression tests for the #74 self-heal: a transient one-sided over-credit in the cache's
/// incremental V2 balance path (a reorg-triggered phantom balance on a group-mint router that
/// nets to zero on-chain) must be reconciled away by re-deriving recently-active accounts from
/// the authoritative DB aggregation. The DB reads are stubbed so the diff/correct/heal logic is
/// exercised without a live PostgreSQL.
///
/// Ground truth driving this: BaseGroupMintRouter 0xdc287474… showed onChainBalance=0 and 0 DB
/// balance rows, yet the live cache reported it sourcing 9–115 CRC of group tokens; the router is
/// structurally flat (per-block cumulative balance is exactly 0 for every token). Faithful
/// reconciliation against the DB therefore heals it: the router returns no authoritative row, so
/// its cached balance is set to 0 and removed from the read index.
/// </summary>
public class BalanceReconciliationTests
{
    private const string Router = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private const string GroupToken = "0xc19bc204eb1c1d5b3fe500e5e5dfabab625f286c";
    private const string OtherToken = "0x47684687ecc5c5375ac3144d91dd0274182f1632";

    private static readonly NpgsqlDataSource DummyDataSource =
        NpgsqlDataSource.Create("Host=localhost;Database=dummy");

    private static string Key(string account, string token) => $"{account}:{token}";

    private static (CacheServiceState state, CacheContainer caches) NewCache(long head)
    {
        var state = new CacheServiceState(rollbackCapacity: 64);
        state.WarmupComplete = true;
        state.WarmupTargetBlock = head - 100;
        state.LastProcessedBlock = head;
        var caches = new CacheContainer(rollbackCapacity: 64);
        return (state, caches);
    }

    private static void SeedBalance(CacheContainer caches, long block, string account, string token, decimal balance)
    {
        var key = Key(account, token);
        caches.V2BalancesByAccountAndToken.Add(block, key, balance);
        caches.UpdateBalanceIndex(key, isV1: false, balance);
    }

    [Fact]
    public async Task PhantomRouterBalance_WithNoAuthoritativeBacking_IsHealedToZeroAndDeindexed()
    {
        const long head = 46497729;
        var (state, caches) = NewCache(head);

        // Phantom: cache thinks the router holds 50 CRC of a group token it nets to 0 on-chain.
        SeedBalance(caches, head, Router, GroupToken, 50m);
        caches.GetTokenIdsForAddress(Router, isV1: false).Should().ContainSingle();

        var before = CacheMetrics.ReconciliationCorrectionsTotal.WithLabels("phantom_cleared").Value;

        // DB authoritative says: router is active recently, but nets to zero → no balance rows.
        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new() { Router },
            authoritative: new()); // empty → router holds nothing

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(1);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(Router, GroupToken), out var healed).Should().BeTrue();
        healed.Should().Be(0m);
        caches.GetTokenIdsForAddress(Router, isV1: false).Should().BeEmpty("the phantom token must drop out of the read index");
        (CacheMetrics.ReconciliationCorrectionsTotal.WithLabels("phantom_cleared").Value - before).Should().Be(1);
    }

    [Fact]
    public async Task OverstatedBalance_IsCorrectedDownToAuthoritative()
    {
        const long head = 46497729;
        var (state, caches) = NewCache(head);
        SeedBalance(caches, head, Router, GroupToken, 50m);

        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new() { Router },
            authoritative: new() { [Key(Router, GroupToken)] = (30m, head) });

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(1);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(Router, GroupToken), out var v).Should().BeTrue();
        v.Should().Be(30m);
        caches.GetTokenIdsForAddress(Router, isV1: false).Should().ContainSingle("a positive corrected balance stays indexed");
    }

    [Fact]
    public async Task MissingCredit_PresentInDbButNotCache_IsAdded()
    {
        const long head = 46497729;
        var (state, caches) = NewCache(head);
        // Cache has nothing for the account, DB authoritative says it holds 10 CRC.
        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new() { Router },
            authoritative: new() { [Key(Router, OtherToken)] = (10m, head) });

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(1);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(Router, OtherToken), out var v).Should().BeTrue();
        v.Should().Be(10m);
        caches.GetTokenIdsForAddress(Router, isV1: false).Should().Contain(OtherToken);
    }

    [Fact]
    public async Task CacheMatchesAuthoritative_NoCorrectionApplied()
    {
        const long head = 46497729;
        var (state, caches) = NewCache(head);
        SeedBalance(caches, head, Router, GroupToken, 30m);

        var phantomBefore = CacheMetrics.ReconciliationCorrectionsTotal.WithLabels("phantom_cleared").Value;
        var valueBefore = CacheMetrics.ReconciliationCorrectionsTotal.WithLabels("value_corrected").Value;

        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new() { Router },
            authoritative: new() { [Key(Router, GroupToken)] = (30m, head) });

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(0);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(Router, GroupToken), out var v).Should().BeTrue();
        v.Should().Be(30m);
        (CacheMetrics.ReconciliationCorrectionsTotal.WithLabels("phantom_cleared").Value - phantomBefore).Should().Be(0);
        (CacheMetrics.ReconciliationCorrectionsTotal.WithLabels("value_corrected").Value - valueBefore).Should().Be(0);
    }

    [Fact]
    public async Task NoRecentlyActiveAccounts_IsANoOp()
    {
        const long head = 46497729;
        var (state, caches) = NewCache(head);

        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new(),
            authoritative: new(),
            failIfAuthoritativeQueried: true); // must not hit the (stubbed) authoritative path

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(0);
    }

    [Fact]
    public async Task MultipleAccountsAndTokens_OnlyDriftedEntriesCorrected()
    {
        const long head = 46497729;
        var (state, caches) = NewCache(head);
        const string accountB = "0x402acd040000000000000000000000000000beef";

        // Router: GroupToken phantom (50→0), OtherToken correct (12==auth, untouched).
        SeedBalance(caches, head, Router, GroupToken, 50m);
        SeedBalance(caches, head, Router, OtherToken, 12m);
        // Account B: overstated (40→25).
        SeedBalance(caches, head, accountB, GroupToken, 40m);

        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new() { Router, accountB },
            authoritative: new()
            {
                [Key(Router, OtherToken)] = (12m, head),   // matches → no correction
                [Key(accountB, GroupToken)] = (25m, head), // corrected down
                // Router:GroupToken absent → healed to 0
            });

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(2);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(Router, GroupToken), out var a1);
        a1.Should().Be(0m);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(Router, OtherToken), out var a2);
        a2.Should().Be(12m);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(accountB, GroupToken), out var b1);
        b1.Should().Be(25m);
        caches.GetTokenIdsForAddress(Router, isV1: false).Should().BeEquivalentTo(new[] { OtherToken });
        caches.GetTokenIdsForAddress(accountB, isV1: false).Should().ContainSingle();
    }

    [Fact]
    public async Task AuthoritativeAccountBeyondRecentList_IsStillReconciled()
    {
        // Exercises the accountSet union: an account surfaced only by the authoritative map (not the
        // recent list) must still be applied.
        const long head = 46497729;
        var (state, caches) = NewCache(head);
        const string extra = "0xabcdef0000000000000000000000000000001234";

        var listener = new StubReconciliationListener(
            state, caches,
            recentAccounts: new() { Router },
            authoritative: new() { [Key(extra, GroupToken)] = (7m, head) });

        var corrections = await listener.ReconcileRecentV2BalancesAsync(head, CancellationToken.None);

        corrections.Should().Be(1);
        caches.V2BalancesByAccountAndToken.TryGetValue(Key(extra, GroupToken), out var v).Should().BeTrue();
        v.Should().Be(7m);
    }

    [Fact]
    public async Task Throttle_SkipsWithinInterval_FiresAtBoundary()
    {
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString = "Host=localhost",
            ReconciliationIntervalBlocks = 8
        };
        var listener = new ThrottleListener(settings);

        await listener.MaybeReconcileAsync(50, CancellationToken.None);  // fires (cursor was -1)
        await listener.MaybeReconcileAsync(57, CancellationToken.None);  // 7 < 8 → skip
        await listener.MaybeReconcileAsync(58, CancellationToken.None);  // 8 >= 8 → fires

        listener.CalledHeads.Should().Equal(50, 58);
    }

    [Fact]
    public async Task Throttle_FailedPass_DoesNotAdvanceCursor_AndRetriesNextBlock()
    {
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString = "Host=localhost",
            ReconciliationIntervalBlocks = 8
        };
        var listener = new ThrottleListener(settings, throwFirstN: 1);

        await listener.MaybeReconcileAsync(100, CancellationToken.None); // fires, throws → cursor stays -1
        await listener.MaybeReconcileAsync(101, CancellationToken.None); // retries (cursor still -1), succeeds
        await listener.MaybeReconcileAsync(105, CancellationToken.None); // 4 < 8 → skip
        await listener.MaybeReconcileAsync(109, CancellationToken.None); // 8 >= 8 → fires

        listener.CalledHeads.Should().Equal(100, 101, 109);
    }

    [Fact]
    public async Task Throttle_Disabled_NeverRuns()
    {
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString = "Host=localhost",
            ReconciliationEnabled = false
        };
        var listener = new ThrottleListener(settings);

        await listener.MaybeReconcileAsync(100, CancellationToken.None);

        listener.CalledHeads.Should().BeEmpty();
    }

    /// <summary>
    /// Subclass that stubs the two DB reads (recent accounts + authoritative balances) and the
    /// readonly-connection acquisition so the reconciliation diff/correct logic runs DB-free.
    /// </summary>
    private sealed class StubReconciliationListener : NotificationListenerService
    {
        private readonly List<string> _recentAccounts;
        private readonly Dictionary<string, (decimal Balance, long LastActivity)> _authoritative;
        private readonly bool _failIfAuthoritativeQueried;

        public StubReconciliationListener(
            CacheServiceState state,
            CacheContainer caches,
            List<string> recentAccounts,
            Dictionary<string, (decimal Balance, long LastActivity)> authoritative,
            bool failIfAuthoritativeQueried = false)
            : base(
                NullLogger<NotificationListenerService>.Instance,
                new CacheServiceSettings { PostgresConnectionString = "Host=localhost" },
                state,
                caches,
                DummyDataSource)
        {
            _recentAccounts = recentAccounts;
            _authoritative = authoritative;
            _failIfAuthoritativeQueried = failIfAuthoritativeQueried;
        }

        protected override Task WithReadonlyConnectionAsync(
            Func<NpgsqlConnection, CancellationToken, Task> action, CancellationToken ct)
            => action(null!, ct); // no real DB; the stubbed reads below ignore the connection

        protected override Task<List<string>> GetRecentlyActiveAccountsAsync(
            NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
            => Task.FromResult(_recentAccounts);

        protected override Task<Dictionary<string, (decimal Balance, long LastActivity)>>
            FetchAuthoritativeV2BalancesAsync(
                NpgsqlConnection conn, IReadOnlyCollection<string> accounts, long toBlock, CancellationToken ct)
        {
            if (_failIfAuthoritativeQueried)
                throw new InvalidOperationException("authoritative query must not run when no accounts are active");
            return Task.FromResult(_authoritative);
        }
    }

    /// <summary>
    /// Subclass that records each reconciliation call (and can fail the first N) so the throttle /
    /// advance-on-success logic in <see cref="NotificationListenerService.MaybeReconcileAsync"/> is
    /// testable without a DB.
    /// </summary>
    private sealed class ThrottleListener : NotificationListenerService
    {
        public readonly List<long> CalledHeads = new();
        private int _throwFirstN;

        public ThrottleListener(CacheServiceSettings settings, int throwFirstN = 0)
            : base(
                NullLogger<NotificationListenerService>.Instance,
                settings,
                new CacheServiceState(rollbackCapacity: 8) { WarmupComplete = true },
                new CacheContainer(rollbackCapacity: 8),
                DummyDataSource)
        {
            _throwFirstN = throwFirstN;
        }

        internal override async Task<int> ReconcileRecentV2BalancesAsync(long toBlock, CancellationToken ct)
        {
            CalledHeads.Add(toBlock);
            await Task.Yield();
            if (_throwFirstN > 0)
            {
                _throwFirstN--;
                throw new InvalidOperationException("simulated reconcile failure");
            }
            return 0;
        }
    }
}
