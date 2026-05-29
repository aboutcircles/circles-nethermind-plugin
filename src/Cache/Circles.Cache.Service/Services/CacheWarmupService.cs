using Circles.Cache.Service.Caches;
using Circles.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that performs initial cache warmup by replaying all events from PostgreSQL.
/// Runs once on startup to build the cache state from historical blockchain data.
///
/// The CacheWarmupService is split into multiple partial class files for maintainability:
/// - CacheWarmupService.cs                  - Constructor, fields, ClearCaches, nested types
/// - Warmup/CacheWarmupService.Lifecycle.cs - ExecuteAsync, RunWarmupIterationAsync, DB/sync waits, timestamp helpers
/// - Warmup/CacheWarmupService.Connections.cs - Readonly + snapshot-bound connection helpers
/// - Warmup/CacheWarmupService.V1Events.cs   - V1 signup replay
/// - Warmup/CacheWarmupService.V2Events.cs   - V2 avatar/group/wrapper registration replay
/// - Warmup/CacheWarmupService.Balances.cs   - V1/V2/wrapper balance loaders + transfer fallbacks
/// - Warmup/CacheWarmupService.Metadata.cs   - Group memberships, trust, consent flags, CIDs, short names
/// - Warmup/CacheWarmupService.GapReplay.cs  - Block ring buffer init + post-Phase-2 gap replay
/// </summary>
public partial class CacheWarmupService : BackgroundService
{
    private readonly ILogger<CacheWarmupService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;
    private readonly NpgsqlDataSource _readonlyDataSource;
    private readonly GapReplayNotificationListener _gapReplayProcessor;

    protected CacheServiceSettings Settings => _settings;
    protected CacheServiceState State => _state;
    protected CacheContainer Caches => _caches;

    // Fields for periodic reminder logging
    private DateTime _lastReminderLogTime = DateTime.MinValue;
    private readonly TimeSpan _reminderInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// SQL CTE that materializes all registered V2 avatars (humans, orgs, groups) up to @toBlock.
    /// Used as a filter in warmup queries to ensure only registered addresses enter the cache.
    /// Single source of truth — change here to update all warmup queries.
    /// </summary>
    private const string RegisteredAvatarsCte = @"
            registered_avatars AS MATERIALIZED (
                SELECT organization AS avatar FROM ""CrcV2_RegisterOrganization"" WHERE ""blockNumber"" <= @toBlock
                UNION ALL
                SELECT ""group"" AS avatar FROM ""CrcV2_RegisterGroup"" WHERE ""blockNumber"" <= @toBlock
                UNION ALL
                SELECT avatar FROM ""CrcV2_RegisterHuman"" WHERE ""blockNumber"" <= @toBlock
            )";

    public CacheWarmupService(
        ILogger<CacheWarmupService> logger,
        CacheServiceSettings settings,
        CacheServiceState state,
        CacheContainer caches,
        NpgsqlDataSource readonlyDataSource)
    {
        _logger = logger;
        _settings = settings;
        _state = state;
        _caches = caches;
        _readonlyDataSource = readonlyDataSource;
        _gapReplayProcessor = new GapReplayNotificationListener(settings, state, caches, readonlyDataSource);
    }

    /// <summary>
    /// Clears all caches to ensure a clean state for warmup replay.
    /// </summary>
    protected virtual void ClearCaches()
    {
        // Note: BlockRingBuffer is cleared by RewarmupReset.Trigger before this callback.
        // Do not clear it here to avoid confusing double-clear.

        _caches.V1Avatars.Seed(new Dictionary<string, (string, string?)>());
        _caches.V1TokenOwnerByToken.Seed(new Dictionary<string, string>());
        _caches.V1AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2Avatars.Seed(new Dictionary<string, (string, long)>());
        _caches.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, CirclesType)>());
        _caches.Groups.Seed(new Dictionary<string, (string, string, string)>());
        _caches.GroupMemberships.Seed(new Dictionary<string, (string, long)>());
        _caches.V2AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>());
        _caches.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2LastActivity.Seed(new Dictionary<string, long>());
        _caches.V1TrustRelations.Seed(new Dictionary<string, long>());
        _caches.V2TrustRelations.Seed(new Dictionary<string, long>());
        _caches.ConsentedFlowFlags.Seed(new Dictionary<string, byte[]>());

        // Clear secondary indexes (plain Dictionaries, not RollbackCaches) to prevent
        // stale phantom entries from being served during warmup Phase 2.
        _caches.RebuildSecondaryIndexes();
    }

    /// <summary>
    /// Adapter that exposes NotificationListenerService block-range processing
    /// so warmup gap replay can reuse the exact same per-domain logic.
    /// </summary>
    private sealed class GapReplayNotificationListener : NotificationListenerService
    {
        public GapReplayNotificationListener(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            NpgsqlDataSource readonlyDataSource)
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches, readonlyDataSource)
        {
        }

        public Task ReplayRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
            => ProcessBlockRangeAsync(fromBlock, toBlock, ct);
    }

    protected sealed class WarmupSnapshotContext : IAsyncDisposable
    {
        public NpgsqlConnection ExportConnection { get; }
        public NpgsqlTransaction ExportTransaction { get; }
        public string SnapshotId { get; }

        public WarmupSnapshotContext(NpgsqlConnection exportConnection, NpgsqlTransaction exportTransaction, string snapshotId)
        {
            ExportConnection = exportConnection;
            ExportTransaction = exportTransaction;
            SnapshotId = snapshotId;
        }

        public async ValueTask DisposeAsync()
        {
            await ExportTransaction.DisposeAsync();
            await ExportConnection.DisposeAsync();
        }
    }
}
