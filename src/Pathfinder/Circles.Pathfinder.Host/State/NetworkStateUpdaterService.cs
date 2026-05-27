using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Pathfinder;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Npgsql;
using Prometheus;

namespace Circles.Pathfinder.Host.State;

public class NetworkStateUpdaterService : BackgroundService
{
    private readonly NetworkState _networkState;
    private readonly Settings _settings;
    private readonly List<Exception> _getCurrentBlockErrors = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly ILogger<NetworkStateUpdaterService> _log;
    private readonly CapacityGraphPool _pool;
    private readonly LoadGraph _loadGraph;

    // Incremental state (only used when IncrementalEnabled=true)
    // Internal for testability (InternalsVisibleTo: Circles.Pathfinder.Tests)
    internal InMemoryBalanceState? _balanceState;
    internal InMemoryTrustState? _trustState;
    internal InMemoryAvatarState? _avatarState;
    internal long _lastFullRefreshBlock = -1;
    internal long _lastProcessedBlock = -1;
    internal string? _lastProcessedBlockHash;  // D10: reorg detection
    internal long _lastFastMatViewRefreshBlock = -1;
    internal long _lastSlowMatViewRefreshBlock = -1;

    // Cache-source state (only used when UseCacheGraphSource=true)
    private CacheGraphClient? _cacheGraphClient;
    private CacheLoadGraph? _cacheLoadGraph;
    private string? _cacheGraphEtag;

    // C3: log score-policy posture on each transition so an operator can read the log
    // and reconstruct boot → indexer-catchup → healthy progression without a metric
    // dashboard. Initial state is NotLogged so the first observation always emits one
    // log batch; subsequent identical observations are suppressed.
    private ScorePolicyDiagnosticState _scorePolicyState = ScorePolicyDiagnosticState.NotLogged;

    private enum ScorePolicyDiagnosticState
    {
        NotLogged,
        FailOpenNoRouters,      // policy configured but no GroupInitialized event indexed yet
        FailClosedNoApprovals,  // routers indexed but no ApprovalForAll events indexed yet
        Healthy                  // routers + approvals both present
    }

    public NetworkStateUpdaterService(NetworkState networkState,
        Circles.Pathfinder.Host.Settings settings,
        ILogger<NetworkStateUpdaterService> log,
        CapacityGraphPool pool,
        LoadGraph loadGraph)
    {
        _networkState = networkState;
        _settings = settings;
        _log = log;
        _pool = pool;
        _loadGraph = loadGraph;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loadGraph = _loadGraph;

        // Initialize cache graph client if enabled
        if (_settings.UseCacheGraphSource)
        {
            if (string.IsNullOrWhiteSpace(_settings.CacheServiceUrl))
                throw new InvalidOperationException("USE_CACHE_GRAPH_SOURCE is enabled but CACHE_SERVICE_URL is not configured.");

            _cacheGraphClient = new CacheGraphClient(
                new HttpClient(),
                _settings.CacheServiceUrl,
                TimeSpan.FromSeconds(_settings.CacheGraphRequestTimeoutSeconds));

            _log.LogInformation(
                "Cache graph source ENABLED (url={Url}, timeout={Timeout}s, fallback={Fallback})",
                _settings.CacheServiceUrl, _settings.CacheGraphRequestTimeoutSeconds, _settings.CacheGraphFallbackToDb);
        }

        if (_settings.IncrementalEnabled)
        {
            _log.LogInformation(
                "Incremental graph updates ENABLED (full refresh every {Interval} blocks)",
                _settings.FullRefreshIntervalBlocks);
        }
        else
        {
            _log.LogInformation("Incremental graph updates DISABLED — using original full-refresh path");
        }

        long lastBlock = _networkState.LastKnownBlockNumber;
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogDebug("Waiting for next block…");
                lastBlock = await WaitForNextBlock(stoppingToken, lastBlock);
                _networkState.Replace(lastKnownBlockNumber: lastBlock);

                _log.LogDebug("→ got block {Block}", lastBlock);

                // ── Try cache source first (if enabled) ──
                if (await TryCacheSourceUpdate(lastBlock, stoppingToken))
                {
                    consecutiveErrors = 0;
                    continue;
                }

                // ── Fall back to DB-based update ──
                var swTotal = Stopwatch.StartNew();

                if (_settings.IncrementalEnabled)
                {
                    await UpdateIncremental(loadGraph, lastBlock, stoppingToken);
                }
                else
                {
                    await UpdateOriginal(loadGraph, lastBlock, stoppingToken);
                }

                swTotal.Stop();
                GraphUpdateMetrics.PathfinderGraphSourceTotal.WithLabels("db").Inc();

                // Common metrics
                GraphUpdateMetrics.UpdateDuration.WithLabels("total").Observe(swTotal.Elapsed.TotalSeconds);
                GraphUpdateMetrics.UpdateTotal.WithLabels("success").Inc();
                GraphUpdateMetrics.LastUpdateTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                GraphUpdateMetrics.LastProcessedBlock.Set(lastBlock);
                GraphUpdateMetrics.ConsecutiveErrors.Set(0);

                // Graph size gauges
                var bg = _networkState.BalanceGraph;
                if (bg != null)
                {
                    GraphUpdateMetrics.AvatarCount.Set(bg.AvatarNodes.Count);
                    GraphUpdateMetrics.BalanceCount.Set(bg.BalanceNodes.Count);
                }

                var currentSnap = _pool.CurrentSnapshot;
                if (currentSnap != null)
                {
                    GraphUpdateMetrics.EdgeCount.Set(currentSnap.Base.Edges.Count);
                    GraphUpdateMetrics.GroupCount.Set(currentSnap.Base.GroupNodes.Count);
                    GraphUpdateMetrics.ConsentedAvatarCount.Set(currentSnap.Base.ConsentedAvatars.Count);
                    GraphUpdateMetrics.RouterTrustCoverageTotal.Set(currentSnap.Base.TotalGroupTokenEdges);
                    GraphUpdateMetrics.RouterTrustFilteredCount.Set(currentSnap.Base.RouterFilteredEdges);
                    TryLogScorePolicyDiagnostic(currentSnap.Base);
                }

                GraphUpdateMetrics.AddressPoolSize.Set(AddressIdPool.Count);

                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("NetworkStateUpdaterService stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                GraphUpdateMetrics.UpdateTotal.WithLabels("failure").Inc();
                GraphUpdateMetrics.ConsecutiveErrors.Set(consecutiveErrors);
                _log.LogError(ex, "Error updating network state (attempt {Attempt}/{MaxAttempts})", consecutiveErrors, maxConsecutiveErrors);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _log.LogCritical("Too many consecutive errors ({Count}), crashing service to trigger container restart", consecutiveErrors);
                    throw new InvalidOperationException(
                        $"NetworkStateUpdaterService unrecoverable after {consecutiveErrors} consecutive failures. " +
                        $"Last error: {ex.Message}");
                }

                var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, consecutiveErrors)));
                _log.LogInformation("Retrying in {Delay} seconds", retryDelay.TotalSeconds);

                try
                {
                    await Task.Delay(retryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Original full-refresh path — unchanged behavior, used when IncrementalEnabled=false.
    /// </summary>
    private async Task UpdateOriginal(LoadGraph loadGraph, long lastBlock, CancellationToken ct)
    {
        var graphFactory = new GraphFactory(_settings.BaseGroupRouter, loadGraph);

        var swTrust = Stopwatch.StartNew();
        var trustTask = Task.Run(() =>
        {
            var graph = graphFactory.V2TrustGraph();
            var lookup = GraphFactory.BuildTrustLookup(graph);
            _networkState.Replace(accountTrusts: lookup);
            swTrust.Stop();
        }, ct);

        var swBalance = Stopwatch.StartNew();
        var balanceTask = Task.Run(() =>
        {
            var graph = graphFactory.V2BalanceGraph();
            _networkState.Replace(balanceGraph: graph);
            swBalance.Stop();
        }, ct);

        await Task.WhenAll(trustTask, balanceTask);

        var cap = await CapacityGraphPool.BuildFullGraph(
            _networkState.BalanceGraph ?? throw new InvalidOperationException("Balance graph is null"),
            _networkState.AccountTrusts ?? throw new InvalidOperationException("Account trusts is null"),
            loadGraph,
            _settings.BaseGroupRouter);

        var groupData = new CachedGroupData(
            new HashSet<int>(cap.GroupNodes),
            new HashSet<int>(cap.OrganizationNodes),
            cap.GroupTrustedTokens.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
            new HashSet<int>(cap.ConsentedAvatars),
            new HashSet<int>(cap.RegisteredAvatarIds),
            new Dictionary<int, int>(cap.WrapperToAvatar),
            new Dictionary<int, int>(cap.GroupRouters),
            new Dictionary<(int GroupAddress, int CollateralToken), long>(cap.ScoreGroupMintLimits),
            cap.OperatorApprovals.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
            new HashSet<int>(cap.ScoreRouterIds),
            new HashSet<int>(cap.InflationaryWrappers));

        _pool.UpdateSnapshot(new CapacityGraphSnapshot(lastBlock, cap), groupData);

        // NOTE: Pre-built wrapped snapshot disabled — IsWrapOnly() always returns false.
        // The snapshot lacks source-specific wrapped supply edges, causing maxFlow=0.
        // All withWrap=true requests now build ad-hoc graphs with proper source context.

        // Refresh materialized views periodically (non-fatal if DB unavailable)
        try { RefreshMaterializedViewsIfDue(lastBlock); }
        catch (Exception ex) { _log.LogWarning(ex, "Materialized view refresh failed"); }

        GraphUpdateMetrics.UpdateDuration.WithLabels("trust").Observe(swTrust.Elapsed.TotalSeconds);
        GraphUpdateMetrics.UpdateDuration.WithLabels("balance").Observe(swBalance.Elapsed.TotalSeconds);
        GraphUpdateMetrics.UpdateMode.Set(-1); // -1 = legacy mode

        _log.LogInformation(
            "Graphs updated (legacy) – trust={TrustMs} ms balance={BalanceMs} ms",
            swTrust.ElapsedMilliseconds, swBalance.ElapsedMilliseconds);
    }

    /// <summary>
    /// Incremental update path — maintains in-memory state, applies per-block deltas.
    /// Falls back to full refresh on first run, every N blocks, or on reorg/skip.
    /// </summary>
    /// <summary>
    /// Determines whether a full refresh is needed based on current state.
    /// Does NOT check block hash (that requires DB). Caller must handle hash-based reorg separately.
    /// </summary>
    internal bool NeedsFullRefresh(long lastBlock) =>
        _balanceState == null                                                   // first run
        || (lastBlock - _lastFullRefreshBlock) >= _settings.FullRefreshIntervalBlocks  // periodic
        || lastBlock <= _lastProcessedBlock;                                    // reorg / skip

    internal async Task UpdateIncremental(LoadGraph loadGraph, long lastBlock, CancellationToken ct)
    {
        bool needsFullRefresh = NeedsFullRefresh(lastBlock);

        // D10: same-height reorg detection via block hash comparison
        if (!needsFullRefresh && _lastProcessedBlockHash != null)
        {
            var currentHash = loadGraph.LoadBlockHash(_lastProcessedBlock);
            if (currentHash != null && !string.Equals(currentHash, _lastProcessedBlockHash, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Reorg detected at block {Block}: stored hash {StoredHash} != current hash {CurrentHash}. Forcing full refresh.",
                    _lastProcessedBlock, _lastProcessedBlockHash[..10], currentHash[..10]);
                GraphUpdateMetrics.ReorgDetectedTotal.Inc();
                needsFullRefresh = true;
            }
        }

        if (needsFullRefresh)
        {
            await FullRefresh(loadGraph, lastBlock, ct);
        }
        else
        {
            try
            {
                await IncrementalUpdate(loadGraph, lastBlock, ct);
            }
            catch (Exception ex)
            {
                // Partial delta application may have corrupted in-memory state
                // (ApplyTransfer is not idempotent). Force a full refresh on the
                // next cycle to rebuild from scratch.
                _lastFullRefreshBlock = -1;
                _log.LogWarning(ex, "Incremental update failed at block {Block}, forcing full refresh on next cycle", lastBlock);
                throw;
            }
        }

        _lastProcessedBlock = lastBlock;
        _lastProcessedBlockHash = loadGraph.LoadBlockHash(lastBlock);
    }

    private Task FullRefresh(LoadGraph loadGraph, long lastBlock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Snapshot current state for drift comparison (if not first run)
        var prevBalance = _balanceState?.Snapshot();
        var prevTrust = _trustState?.Snapshot();
        var prevAvatar = _avatarState?.Snapshot();

        // Load fresh state from DB inside a single REPEATABLE READ transaction.
        // This guarantees all 4 queries see a consistent DB snapshot, preventing
        // phantom inconsistencies where balances reflect block N but trusts reflect N+1.
        using var conn = loadGraph.DataSource.OpenConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.RepeatableRead);
        var rawBalances = loadGraph.LoadRawBalances(conn, tx);
        var rawTrusts = loadGraph.LoadRawTrusts(conn, tx);
        var allAvatars = loadGraph.LoadAllAvatars(conn, tx);
        var stoppedAvatars = loadGraph.LoadStoppedAvatars(conn, tx);
        var maxTimestamp = loadGraph.LoadMaxBlockTimestamp(conn, tx);
        tx.Commit();

        // Initialize in-memory state
        _balanceState = new InMemoryBalanceState(_log);
        _balanceState.InitializeFromFullLoad(rawBalances);

        _trustState = new InMemoryTrustState();
        _trustState.InitializeFromFullLoad(rawTrusts);

        _avatarState = new InMemoryAvatarState();
        _avatarState.InitializeFromFullLoad(allAvatars);
        _avatarState.InitializeStoppedAvatars(stoppedAvatars);

        // Drift detection (if not first run)
        if (prevBalance != null && prevTrust != null && prevAvatar != null)
        {
            DetectAndRecordDrift(prevBalance, prevTrust, prevAvatar,
                _balanceState, _trustState, _avatarState);
        }

        // Build graphs from in-memory state
        BuildAndPublishGraphs(loadGraph, lastBlock, maxTimestamp);

        sw.Stop();
        _lastFullRefreshBlock = lastBlock;

        GraphUpdateMetrics.FullRefreshTotal.Inc();
        GraphUpdateMetrics.UpdateMode.Set(0);
        GraphUpdateMetrics.LastFullRefreshBlock.Set(lastBlock);
        GraphUpdateMetrics.BlocksSinceFullRefresh.Set(0);
        GraphUpdateMetrics.UpdateDuration.WithLabels("full_refresh").Observe(sw.Elapsed.TotalSeconds);

        _log.LogInformation(
            "Full refresh completed – {BalanceCount} balances, {TrustCount} trusts, {AvatarCount} avatars in {Ms} ms",
            _balanceState.Count, _trustState.Count, _avatarState.Count, sw.ElapsedMilliseconds);

        return Task.CompletedTask;
    }

    private Task IncrementalUpdate(LoadGraph loadGraph, long lastBlock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Load deltas (fast — indexed by blockNumber)
        var swDelta = Stopwatch.StartNew();
        var transfers = loadGraph.LoadTransfersSince(_lastProcessedBlock);
        var trustEvents = loadGraph.LoadTrustEventsSince(_lastProcessedBlock);
        var newAvatars = loadGraph.LoadNewAvatarsSince(_lastProcessedBlock);
        var stoppedAvatars = loadGraph.LoadStoppedAvatarsSince(_lastProcessedBlock);
        var maxTimestamp = loadGraph.LoadMaxBlockTimestamp();
        swDelta.Stop();
        GraphUpdateMetrics.DeltaQueryDuration.WithLabels("all").Observe(swDelta.Elapsed.TotalSeconds);

        // Apply deltas to in-memory state
        int transferCount = 0, trustCount = 0, avatarCount = 0;

        foreach (var t in transfers)
        {
            _balanceState!.ApplyTransfer(
                t.From,
                t.To,
                t.TokenAddress,
                t.Value,
                t.Timestamp,
                t.IsWrapped,
                t.IsStatic);
            transferCount++;
        }

        foreach (var t in trustEvents)
        {
            _trustState!.ApplyTrustEvent(t.BlockNumber, t.TxIndex, t.LogIndex,
                t.Truster, t.Trustee, t.ExpiryTime);
            trustCount++;
        }

        var newAvatarAddresses = new List<string>();
        foreach (var a in newAvatars)
        {
            _avatarState!.AddAvatar(a.Avatar, a.Type);
            newAvatarAddresses.Add(a.Avatar.ToLowerInvariant());
            avatarCount++;
        }

        int stoppedCount = 0;
        foreach (var avatar in stoppedAvatars)
        {
            _avatarState!.MarkStopped(avatar);
            stoppedCount++;
        }

        // Backfill complete balances for newly registered avatars.
        // New avatars may have received tokens before registering — those transfers
        // predating _lastFullRefreshBlock aren't in our delta window.
        if (newAvatarAddresses.Count > 0)
        {
            var freshBalances = loadGraph.LoadBalancesForAvatars(newAvatarAddresses);
            _balanceState!.BackfillAvatars(newAvatarAddresses, freshBalances);
            _log.LogDebug("Backfilled balances for {Count} new avatars", newAvatarAddresses.Count);
        }

        // Record delta metrics
        GraphUpdateMetrics.DeltaEventsCount.WithLabels("transfer").Set(transferCount);
        GraphUpdateMetrics.DeltaEventsCount.WithLabels("trust").Set(trustCount);
        GraphUpdateMetrics.DeltaEventsCount.WithLabels("avatar").Set(avatarCount);

        // Build graphs from in-memory state
        BuildAndPublishGraphs(loadGraph, lastBlock, maxTimestamp);

        sw.Stop();
        GraphUpdateMetrics.IncrementalUpdateTotal.Inc();
        GraphUpdateMetrics.UpdateMode.Set(1);
        GraphUpdateMetrics.BlocksSinceFullRefresh.Set(lastBlock - _lastFullRefreshBlock);
        GraphUpdateMetrics.UpdateDuration.WithLabels("incremental").Observe(sw.Elapsed.TotalSeconds);

        _log.LogInformation(
            "Incremental update – {Transfers} transfers, {Trusts} trusts, {Avatars} avatars, {Stopped} stopped in {Ms} ms (blocks since full: {BlocksSince})",
            transferCount, trustCount, avatarCount, stoppedCount, sw.ElapsedMilliseconds,
            lastBlock - _lastFullRefreshBlock);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Build balance graph, trust graph, and capacity graph from in-memory state and publish to pool.
    /// Shared by both full refresh and incremental paths.
    /// </summary>
    private void BuildAndPublishGraphs(LoadGraph loadGraph, long lastBlock, long maxBlockTimestamp)
    {
        var incLoadGraph = new IncrementalLoadGraph(
            _balanceState!, _trustState!, _avatarState!,
            loadGraph, _settings, maxBlockTimestamp, _log);

        var graphFactory = new GraphFactory(_settings.BaseGroupRouter, incLoadGraph);

        // Build trust and balance graphs (these are fast — in-memory iteration only)
        var trustGraph = graphFactory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);
        _networkState.Replace(accountTrusts: trustLookup);

        var balanceGraph = graphFactory.V2BalanceGraph();
        _networkState.Replace(balanceGraph: balanceGraph);

        // Build capacity graph
        var cap = graphFactory.CreateBaseCapacityGraph(balanceGraph, trustLookup);

        var groupData = new CachedGroupData(
            new HashSet<int>(cap.GroupNodes),
            new HashSet<int>(cap.OrganizationNodes),
            cap.GroupTrustedTokens.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
            new HashSet<int>(cap.ConsentedAvatars),
            new HashSet<int>(cap.RegisteredAvatarIds),
            new Dictionary<int, int>(cap.WrapperToAvatar),
            new Dictionary<int, int>(cap.GroupRouters),
            new Dictionary<(int GroupAddress, int CollateralToken), long>(cap.ScoreGroupMintLimits),
            cap.OperatorApprovals.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
            new HashSet<int>(cap.ScoreRouterIds),
            new HashSet<int>(cap.InflationaryWrappers));

        _pool.UpdateSnapshot(new CapacityGraphSnapshot(lastBlock, cap), groupData);

        // NOTE: Pre-built wrapped snapshot disabled — IsWrapOnly() always returns false.
        // See CapacityGraphPool.IsWrapOnly() for details.

        // Refresh materialized views periodically (non-fatal if DB unavailable)
        try { RefreshMaterializedViewsIfDue(lastBlock); }
        catch (Exception ex) { _log.LogWarning(ex, "Materialized view refresh failed"); }
    }

    /// <summary>
    /// Compare accumulated in-memory state against fresh DB load to detect drift.
    /// Any drift indicates a bug in delta application logic.
    /// </summary>
    internal void DetectAndRecordDrift(
        InMemoryBalanceState prevBalance, InMemoryTrustState prevTrust, InMemoryAvatarState prevAvatar,
        InMemoryBalanceState freshBalance, InMemoryTrustState freshTrust, InMemoryAvatarState freshAvatar)
    {
        // Balance drift — only compare entries for registered avatars (D11 filters non-avatars
        // from the fresh load, but delta transfers can create non-avatar entries in accumulated state)
        int balanceDrift = 0;
        double maxPctDrift = 0;
        foreach (var kv in freshBalance.GetAll())
        {
            if (!prevBalance.TryGet(kv.Key, out var prevEntry) || prevEntry.Balance != kv.Value.Balance)
            {
                balanceDrift++;
                if (prevBalance.TryGet(kv.Key, out var p) && p.Balance != 0)
                {
                    var delta = (double)(kv.Value.Balance - p.Balance);
                    var pct = Math.Abs(delta / (double)p.Balance) * 100;
                    maxPctDrift = Math.Max(maxPctDrift, pct);
                }
            }
        }
        // Entries in prev but not in fresh — only count avatar entries
        // (non-avatar entries in accumulated are expected noise from delta transfers)
        foreach (var key in prevBalance.Keys)
        {
            if (!freshBalance.TryGet(key, out _) && freshAvatar.AvatarSet.Contains(key.Account))
                balanceDrift++;
        }

        // Trust drift
        int trustDrift = 0;
        foreach (var kv in freshTrust.GetAll())
        {
            if (!prevTrust.TryGet(kv.Key, out var prevEntry)
                || prevEntry.ExpiryTime != kv.Value.ExpiryTime
                || prevEntry.BlockNumber != kv.Value.BlockNumber)
            {
                trustDrift++;
            }
        }
        foreach (var key in prevTrust.Keys)
        {
            if (!freshTrust.TryGet(key, out _))
                trustDrift++;
        }

        // Avatar drift — use AvatarSet directly (not Contains()) because Contains()
        // excludes stopped avatars (D13), which would create false drift
        int avatarDrift = 0;
        foreach (var a in freshAvatar.AvatarSet)
        {
            if (!prevAvatar.AvatarSet.Contains(a)) avatarDrift++;
        }
        foreach (var a in prevAvatar.AvatarSet)
        {
            if (!freshAvatar.AvatarSet.Contains(a)) avatarDrift++;
        }

        GraphUpdateMetrics.DriftEntries.WithLabels("balance").Set(balanceDrift);
        GraphUpdateMetrics.DriftEntries.WithLabels("trust").Set(trustDrift);
        GraphUpdateMetrics.DriftEntries.WithLabels("avatar").Set(avatarDrift);
        GraphUpdateMetrics.DriftMaxBalancePct.Set(maxPctDrift);

        if (balanceDrift > 0 || trustDrift > 0 || avatarDrift > 0)
        {
            _log.LogWarning(
                "Drift detected: {BalanceDrift} balance, {TrustDrift} trust, {AvatarDrift} avatar entries. Max balance drift: {MaxPct:F4}%",
                balanceDrift, trustDrift, avatarDrift, maxPctDrift);

            // Log first few drifted entries for debugging
            int logged = 0;
            foreach (var kv in freshBalance.GetAll())
            {
                if (logged >= 5) break;
                if (!prevBalance.TryGet(kv.Key, out var prevEntry))
                {
                    _log.LogWarning("  Drift: {Account}/{Token} — missing in accumulated, fresh={Balance}",
                        kv.Key.Account[..10], kv.Key.TokenAddress[..10], kv.Value.Balance);
                    logged++;
                }
                else if (prevEntry.Balance != kv.Value.Balance)
                {
                    _log.LogWarning("  Drift: {Account}/{Token} — accumulated={Prev}, fresh={Fresh}",
                        kv.Key.Account[..10], kv.Key.TokenAddress[..10], prevEntry.Balance, kv.Value.Balance);
                    logged++;
                }
            }
        }
        else
        {
            _log.LogDebug("Drift check passed — zero drift across all state");
        }
    }

    /// <summary>
    /// Attempt to update graphs from the cache service. Returns true if the update was handled
    /// (either new data applied or 304 Not Modified), false if cache is unavailable/disabled
    /// and the caller should fall back to DB.
    /// </summary>
    private async Task<bool> TryCacheSourceUpdate(long lastBlock, CancellationToken ct)
    {
        if (_cacheGraphClient == null)
            return false;

        var sw = Stopwatch.StartNew();
        try
        {
            var fetchResult = await _cacheGraphClient.FetchGraphSnapshotAsync(_cacheGraphEtag, ct);
            sw.Stop();
            GraphUpdateMetrics.CacheGraphFetchDuration.Observe(sw.Elapsed.TotalSeconds);

            if (fetchResult.IsNotModified)
            {
                GraphUpdateMetrics.CacheGraphNotModifiedTotal.Inc();
                GraphUpdateMetrics.PathfinderGraphSourceTotal.WithLabels("cache_304").Inc();
                return true; // Graph unchanged — skip rebuild
            }

            var snapshot = fetchResult.Snapshot
                ?? throw new InvalidOperationException("Cache graph response missing snapshot payload.");
            _cacheGraphEtag = fetchResult.Etag;
            GraphUpdateMetrics.CacheGraphPayloadBytes.Observe(fetchResult.PayloadBytes);

            if (_cacheLoadGraph == null)
                _cacheLoadGraph = new CacheLoadGraph(snapshot, _settings);
            else
                _cacheLoadGraph.ReplaceSnapshot(snapshot);

            // Build graphs from cache snapshot
            var effectiveBlock = snapshot.LastProcessedBlock;
            BuildGraphsFromLoadGraph(_cacheLoadGraph, effectiveBlock);

            // Reset incremental state so any subsequent DB fallback starts clean
            // (prevents stale drift detection after cache→DB transition)
            ResetIncrementalState();

            // Clear any lingering drift metrics from a prior DB phase
            GraphUpdateMetrics.DriftEntries.WithLabels("balance").Set(0);
            GraphUpdateMetrics.DriftEntries.WithLabels("trust").Set(0);
            GraphUpdateMetrics.DriftEntries.WithLabels("avatar").Set(0);
            GraphUpdateMetrics.DriftMaxBalancePct.Set(0);
            GraphUpdateMetrics.UpdateMode.Set(2); // 2 = cache mode

            _networkState.Replace(lastKnownBlockNumber: effectiveBlock);
            GraphUpdateMetrics.PathfinderGraphSourceTotal.WithLabels("cache").Inc();
            GraphUpdateMetrics.UpdateTotal.WithLabels("success").Inc();
            GraphUpdateMetrics.LastUpdateTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            GraphUpdateMetrics.LastProcessedBlock.Set(effectiveBlock);
            GraphUpdateMetrics.ConsecutiveErrors.Set(0);

            // Graph size gauges
            var bg = _networkState.BalanceGraph;
            if (bg != null)
            {
                GraphUpdateMetrics.AvatarCount.Set(bg.AvatarNodes.Count);
                GraphUpdateMetrics.BalanceCount.Set(bg.BalanceNodes.Count);
            }
            var currentSnap = _pool.CurrentSnapshot;
            if (currentSnap != null)
            {
                GraphUpdateMetrics.EdgeCount.Set(currentSnap.Base.Edges.Count);
                GraphUpdateMetrics.GroupCount.Set(currentSnap.Base.GroupNodes.Count);
                GraphUpdateMetrics.ConsentedAvatarCount.Set(currentSnap.Base.ConsentedAvatars.Count);
                GraphUpdateMetrics.RouterTrustCoverageTotal.Set(currentSnap.Base.TotalGroupTokenEdges);
                GraphUpdateMetrics.RouterTrustFilteredCount.Set(currentSnap.Base.RouterFilteredEdges);
                TryLogScorePolicyDiagnostic(currentSnap.Base);
            }
            GraphUpdateMetrics.AddressPoolSize.Set(AddressIdPool.Count);

            _log.LogInformation(
                "Graphs updated (cache) – block={Block}, fetch={FetchMs}ms, payload={Bytes}B",
                effectiveBlock, sw.ElapsedMilliseconds, fetchResult.PayloadBytes);

            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            GraphUpdateMetrics.CacheGraphErrorsTotal.Inc();

            if (_settings.CacheGraphFallbackToDb)
            {
                _log.LogWarning(ex, "Cache graph fetch failed, falling back to DB for this cycle");
                return false; // Caller will use DB path
            }

            _log.LogError(ex, "Cache graph fetch failed and DB fallback is disabled");
            throw;
        }
    }

    /// <summary>
    /// Build trust graph, balance graph, and capacity graph from any ILoadGraph and publish to pool.
    /// Used by both the cache-source path and could be reused by other graph sources.
    /// </summary>
    internal void BuildGraphsFromLoadGraph(ILoadGraph loadGraph, long lastBlock)
    {
        var graphFactory = new GraphFactory(_settings.BaseGroupRouter, loadGraph);

        var trustGraph = graphFactory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);
        _networkState.Replace(accountTrusts: trustLookup);

        var balanceGraph = graphFactory.V2BalanceGraph();
        _networkState.Replace(balanceGraph: balanceGraph);

        var cap = graphFactory.CreateBaseCapacityGraph(balanceGraph, trustLookup);

        var groupData = new CachedGroupData(
            new HashSet<int>(cap.GroupNodes),
            new HashSet<int>(cap.OrganizationNodes),
            cap.GroupTrustedTokens.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
            new HashSet<int>(cap.ConsentedAvatars),
            new HashSet<int>(cap.RegisteredAvatarIds),
            new Dictionary<int, int>(cap.WrapperToAvatar),
            new Dictionary<int, int>(cap.GroupRouters),
            new Dictionary<(int GroupAddress, int CollateralToken), long>(cap.ScoreGroupMintLimits),
            cap.OperatorApprovals.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
            new HashSet<int>(cap.ScoreRouterIds),
            new HashSet<int>(cap.InflationaryWrappers));

        _pool.UpdateSnapshot(new CapacityGraphSnapshot(lastBlock, cap), groupData);

        // NOTE: Pre-built wrapped snapshot disabled — IsWrapOnly() always returns false.
        // The snapshot lacks source-specific wrapped supply edges, causing maxFlow=0.
        // All withWrap=true requests now build ad-hoc graphs with proper source context.

        // Refresh materialized views periodically (non-fatal if DB unavailable)
        try { RefreshMaterializedViewsIfDue(lastBlock); }
        catch (Exception ex) { _log.LogWarning(ex, "Materialized view refresh failed"); }
    }

    /// <summary>
    /// Build and publish a pre-built wrapped graph snapshot.
    /// This avoids rebuilding the full graph for every withWrap=true request.
    /// </summary>
    private void BuildAndPublishWrappedSnapshot(ILoadGraph loadGraph, long lastBlock, CachedGroupData? groupData)
    {
        try
        {
            var balanceGraph = _networkState.BalanceGraph;
            var accountTrusts = _networkState.AccountTrusts;
            if (balanceGraph == null || accountTrusts == null)
            {
                _log.LogWarning("Cannot build wrapped snapshot: balance graph or trust data not available");
                return;
            }

            var sw = Stopwatch.StartNew();
            var wrappedCap = CapacityGraphPool.BuildFullWrappedGraph(
                balanceGraph, accountTrusts, loadGraph, _settings.BaseGroupRouter, groupData).Result;
            sw.Stop();

            _pool.UpdateWrappedSnapshot(new CapacityGraphSnapshot(lastBlock, wrappedCap));

            _log.LogInformation("Pre-built wrapped graph snapshot in {Ms} ms – {Nodes} nodes, {Edges} edges",
                sw.ElapsedMilliseconds, wrappedCap.Nodes.Count, wrappedCap.Edges.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build wrapped snapshot — withWrap requests will fall back to ad-hoc build");
        }
    }

    /// <summary>
    /// Refreshes materialized views on two cadences:
    /// - Fast tier (~5 min): balances, avatars, groups, receive counts
    /// - Slow tier (~1 hour): trust scores (expensive window function)
    /// Uses CONCURRENTLY to avoid blocking readers. Failures are logged but do not crash the service.
    /// </summary>
    internal void RefreshMaterializedViewsIfDue(long currentBlock)
    {
        if (!_settings.MaterializedViewRefreshEnabled)
            return;

        bool fastDue = _lastFastMatViewRefreshBlock < 0
            || (currentBlock - _lastFastMatViewRefreshBlock) >= _settings.MaterializedViewRefreshFastBlocks;

        bool slowDue = _lastSlowMatViewRefreshBlock < 0
            || (currentBlock - _lastSlowMatViewRefreshBlock) >= _settings.MaterializedViewRefreshSlowBlocks;

        if (!fastDue && !slowDue)
            return;

        using var conn = new NpgsqlConnection(_settings.MaterializedViewDbConnectionString);
        conn.Open();

        if (fastDue)
        {
            var fastViews = new[]
            {
                "M_CrcV2_BalancesByAccountAndToken",
                "M_CrcV2_Avatars",
                "M_CrcV2_ReceiveCount",
                "M_CrcV2_Groups"
            };

            foreach (var viewName in fastViews)
                RefreshSingleMatView(conn, viewName);

            _lastFastMatViewRefreshBlock = currentBlock;
            GraphUpdateMetrics.LastMatViewRefreshBlock.WithLabels("fast").Set(currentBlock);
        }

        if (slowDue)
        {
            RefreshSingleMatView(conn, "V_TrustScores_Current");
            _lastSlowMatViewRefreshBlock = currentBlock;
            GraphUpdateMetrics.LastMatViewRefreshBlock.WithLabels("slow").Set(currentBlock);
        }
    }

    private void RefreshSingleMatView(NpgsqlConnection conn, string viewName)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"REFRESH MATERIALIZED VIEW CONCURRENTLY \"{viewName}\"";
            cmd.CommandTimeout = 300; // 5 minutes max
            cmd.ExecuteNonQuery();
            sw.Stop();

            GraphUpdateMetrics.MatViewRefreshDuration.WithLabels(viewName).Observe(sw.Elapsed.TotalSeconds);
            GraphUpdateMetrics.MatViewRefreshTotal.WithLabels(viewName).Inc();

            _log.LogInformation("Refreshed materialized view {View} in {Ms} ms", viewName, sw.ElapsedMilliseconds);
        }
        catch (PostgresException pex) when (pex.SqlState == "55000")
        {
            // WITH NO DATA matview has never been populated — CONCURRENTLY requires prior data.
            // Fall back to blocking REFRESH for initial population.
            _log.LogInformation(
                "Materialized view {View} has no data yet, falling back to blocking REFRESH", viewName);
            try
            {
                var sw = Stopwatch.StartNew();
                using var fallbackCmd = conn.CreateCommand();
                fallbackCmd.CommandText = $"REFRESH MATERIALIZED VIEW \"{viewName}\"";
                fallbackCmd.CommandTimeout = 300;
                fallbackCmd.ExecuteNonQuery();
                sw.Stop();

                GraphUpdateMetrics.MatViewRefreshDuration.WithLabels(viewName).Observe(sw.Elapsed.TotalSeconds);
                GraphUpdateMetrics.MatViewRefreshTotal.WithLabels(viewName).Inc();

                _log.LogInformation("Initial population of {View} completed in {Ms} ms", viewName, sw.ElapsedMilliseconds);
            }
            catch (Exception fallbackEx)
            {
                GraphUpdateMetrics.MatViewRefreshErrors.WithLabels(viewName).Inc();
                _log.LogWarning(fallbackEx, "Failed to populate materialized view {View} — stale data until next refresh", viewName);
            }
        }
        catch (Exception ex)
        {
            GraphUpdateMetrics.MatViewRefreshErrors.WithLabels(viewName).Inc();
            _log.LogWarning(ex, "Failed to refresh materialized view {View} — stale data until next refresh", viewName);
        }
    }

    /// <summary>
    /// C3 observability: log the score-policy posture on each transition so an operator
    /// can read the log and reconstruct boot → catchup → healthy progression. Fires when
    /// either local <c>ScoreGroupMintPolicies</c> is configured OR the cache has
    /// materialized at least one score router (the cache producer can ship routers
    /// independently of local config under <c>USE_CACHE_GRAPH_SOURCE</c>; either signal
    /// means the gate is live). No-op when both are empty — gate stays legacy fail-OPEN
    /// and there's nothing to surface. INFO on every transition; WARN only when the
    /// posture is non-Healthy (so chronic indexer lag stays loud, but a node that boots
    /// healthy logs exactly one info line).
    /// </summary>
    private void TryLogScorePolicyDiagnostic(CapacityGraph baseGraph)
    {
        var policiesConfigured = _settings.ScoreGroupMintPolicies.Length > 0;
        var routersIndexed = baseGraph.ScoreRouterIds.Count > 0;
        if (!policiesConfigured && !routersIndexed)
            return;

        ScorePolicyDiagnosticState newState =
            !routersIndexed                            ? ScorePolicyDiagnosticState.FailOpenNoRouters
            : baseGraph.OperatorApprovals.Count == 0   ? ScorePolicyDiagnosticState.FailClosedNoApprovals
            :                                            ScorePolicyDiagnosticState.Healthy;

        if (newState == _scorePolicyState) return;
        var previousState = _scorePolicyState;
        _scorePolicyState = newState;

        _log.LogInformation(
            "Score-policy posture {NewState} (was {PreviousState}): {PolicyCount} policies configured, {RouterCount} routers indexed, {ApprovalCount} operator approvals indexed",
            newState, previousState,
            _settings.ScoreGroupMintPolicies.Length,
            baseGraph.ScoreRouterIds.Count,
            baseGraph.OperatorApprovals.Count);

        if (newState == ScorePolicyDiagnosticState.FailOpenNoRouters)
        {
            _log.LogWarning(
                "Score-policy gate FAIL-OPEN: ScoreRouterIds empty — no CrcV2_ScoreGroup.GroupInitialized events indexed yet. " +
                "Either no score groups exist OR the indexer is behind. " +
                "ApproveCRCRequired will not fire until at least one router is indexed.");
        }
        else if (newState == ScorePolicyDiagnosticState.FailClosedNoApprovals)
        {
            _log.LogWarning(
                "Score-policy gate FAIL-CLOSED: {RouterCount} routers indexed but OperatorApprovals empty — " +
                "indexer likely behind on approval events. " +
                "ApproveCRCRequired will drop all router edges until approvals catch up.",
                baseGraph.ScoreRouterIds.Count);
        }
    }

    /// <summary>
    /// Reset all incremental state so the next DB fallback starts with a clean FullRefresh
    /// (first-run path) instead of comparing against stale accumulated state.
    /// Called after every successful cache-source update.
    /// </summary>
    internal void ResetIncrementalState()
    {
        _balanceState = null;
        _trustState = null;
        _avatarState = null;
        _lastFullRefreshBlock = -1;
        _lastProcessedBlock = -1;
        _lastProcessedBlockHash = null;
        // Note: don't reset matview refresh blocks — matview refresh cadence is independent
    }

    private async Task<long> WaitForNextBlock(CancellationToken stoppingToken, long lastBlock)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            long currentBlock = await GetBlockNumber();
            if (currentBlock <= lastBlock)
            {
                await Task.Delay(1_000, stoppingToken);
            }
            else
            {
                return currentBlock;
            }
        }

        return lastBlock;
    }

    private async Task<long> GetBlockNumber()
    {
        try
        {
            var requestBody = new
            {
                jsonrpc = "2.0",
                method = "eth_blockNumber",
                @params = Array.Empty<object>(),
                id = 1
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var response = await HttpClient.PostAsync(_settings.NethermindRpcUrl, content);
            response.EnsureSuccessStatusCode();

            var rpcResponse = await response.Content.ReadFromJsonAsync<EthBlockNumberResponse>()
                              ?? throw new InvalidOperationException("Failed to deserialize Nethermind RPC response.");

            if (long.TryParse(rpcResponse.Result?.Replace("0x", ""),
                    NumberStyles.HexNumber, null, out var num))
            {
                _getCurrentBlockErrors.Clear();
                return num;
            }

            return -1;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Error getting block number");
            _getCurrentBlockErrors.Add(e);

            if (_getCurrentBlockErrors.Count >= Constants.MaxGetBlockErrors)
            {
                var errors = new List<Exception>(_getCurrentBlockErrors);
                _getCurrentBlockErrors.Clear();
                throw new AggregateException("Too many errors getting block number.", errors);
            }
        }

        return -1;
    }

    private sealed class EthBlockNumberResponse
    {
        [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
    }
}
