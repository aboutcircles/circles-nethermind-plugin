using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Gap replay for CacheWarmupService.
/// Initializes the block ring buffer with recent blocks and replays any blocks that arrived
/// during warmup Phase 2, delegating per-domain logic to the shared NotificationListener
/// processor. Handles reorgs that occur during the gap window.
/// </summary>
public partial class CacheWarmupService
{
    /// <summary>
    /// Initializes the block ring buffer with the most recent blocks from the database.
    /// </summary>
    protected virtual async Task InitializeBlockRingBufferAsync(NpgsqlConnection conn, long fromBlock, CancellationToken ct)
    {
        // Clear the buffer before initialization to prevent race conditions.
        // After TriggerFullRewarmup() clears the buffer, the notification listener's
        // async callback can still fire and add newer blocks via UpdateFromBlocks()
        // before we reach this point. Without this clear, Add() would throw
        // "Block number must be greater than current latest" when we try to add
        // blocks from the warmup target range (which are older than what the
        // listener just added).
        _state.BlockRingBuffer.Clear();

        var capacity = _settings.RollbackCapacity;
        var startBlock = Math.Max(0, fromBlock - capacity + 1);

        _logger.LogInformation("Initializing block ring buffer with blocks {StartBlock} to {EndBlock}...",
            startBlock, fromBlock);

        const string sql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            WHERE ""blockNumber"" >= @startBlock AND ""blockNumber"" <= @endBlock
            ORDER BY ""blockNumber"" ASC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("startBlock", startBlock);
        cmd.Parameters.AddWithValue("endBlock", fromBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var blockHashStr = reader.GetString(1);

            _state.BlockRingBuffer.Add(blockNumber, blockHashStr);
            count++;
        }

        _logger.LogInformation("Block ring buffer initialized with {Count} blocks", count);
    }

    /// <summary>
    /// Processes blocks that arrived during the warmup phase.
    /// Reuses the same logic as the notification listener.
    /// </summary>
    protected virtual async Task ProcessBlockGapAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Processing block gap: {FromBlock} to {ToBlock}", fromBlock, toBlock);

        // Update block ring buffer with new blocks
        const string blocksSql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"" ASC";

        var newBlocks = new List<(long BlockNumber, string BlockHash)>();

        await using (var blocksCmd = new NpgsqlCommand(blocksSql, conn))
        {
            blocksCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            blocksCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var blocksReader = await blocksCmd.ExecuteReaderAsync(ct);
            while (await blocksReader.ReadAsync(ct))
            {
                var blockNumber = blocksReader.GetInt64(0);
                var blockHashStr = blocksReader.GetString(1);
                newBlocks.Add((blockNumber, blockHashStr));
            }
        }

        // Check for reorgs when updating the ring buffer
        var reorgPoint = _state.BlockRingBuffer.UpdateFromBlocks(newBlocks);

        if (reorgPoint.HasValue)
        {
            if (reorgPoint.Value <= _state.WarmupTargetBlock)
            {
                _logger.LogWarning(
                    "Reorg at block {ReorgBlock} during gap processing crossed warmup seed boundary {WarmupTarget}; restarting warmup.",
                    reorgPoint.Value,
                    _state.WarmupTargetBlock);

                RewarmupReset.Trigger(_state, ClearCaches);
                throw new InvalidOperationException("Warmup gap replay crossed warmup seed boundary due to reorg.");
            }

            _logger.LogWarning("Reorg detected at block {ReorgBlock} during gap processing! Rolling back caches...",
                reorgPoint.Value);

            // Rollback all caches
            _caches.RollbackAll(reorgPoint.Value);

            // Rebuild secondary indexes after rollback
            _logger.LogInformation("Rebuilding secondary indexes after rollback...");
            _caches.RebuildSecondaryIndexes();

            // Update state to reflect rolled-back position BEFORE replay.
            // Without this, a failure in ReplayRangeAsync would leave LastProcessedBlock
            // at warmupTarget while caches are actually rolled back to reorgPoint - 1.
            _state.LastProcessedBlock = reorgPoint.Value - 1;

            // Adjust the fromBlock to start processing from the reorg point
            fromBlock = reorgPoint.Value;
        }

        // Replay all domains via the same processor used by the live notification listener.
        await _gapReplayProcessor.ReplayRangeAsync(fromBlock, toBlock, ct);

        _logger.LogInformation("Successfully processed block gap {FromBlock} to {ToBlock}", fromBlock, toBlock);
    }

    /// <summary>
    /// Process V1 events in a specific block range (used for gap processing).
    /// </summary>
    private async Task ProcessV1EventsInRangeAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Signups (humans)
        const string humanSignupSql = @"
            SELECT s.""blockNumber"", s.""user"", s.""token""
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var user = reader.GetString(1);
                var token = reader.GetString(2);

                var userKey = user.ToLowerInvariant();
                var tokenKey = token.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, userKey, ("CrcV1_Signup", token));
                _caches.V1TokenOwnerByToken.Add(blockNumber, tokenKey, user);
            }
        }

        // Process V1 Organization Signups
        const string orgSignupSql = @"
            SELECT o.""blockNumber"", o.""organization""
            FROM ""CrcV1_OrganizationSignup"" o
            WHERE o.""blockNumber"" >= @fromBlock AND o.""blockNumber"" <= @toBlock
            ORDER BY o.""blockNumber"", o.""transactionIndex"", o.""logIndex""";

        await using var orgCmd = new NpgsqlCommand(orgSignupSql, conn);
        orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
        orgCmd.Parameters.AddWithValue("toBlock", toBlock);

        await using (var orgReader = await orgCmd.ExecuteReaderAsync(ct))
        {
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var organization = orgReader.GetString(1);

                var orgKey = organization.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, orgKey, ("CrcV1_OrganizationSignup", null));
            }
        }
    }

    /// <summary>
    /// Process V2 events in a specific block range (used for gap processing).
    /// </summary>
    private async Task ProcessV2EventsInRangeAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterHuman
        const string humanSql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""avatar""
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var cmd = new NpgsqlCommand(humanSql, conn))
        {
            cmd.Parameters.AddWithValue("fromBlock", fromBlock);
            cmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var avatar = reader.GetString(2);

                var avatarKey = avatar.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, avatarKey, ("CrcV2_RegisterHuman", timestamp));
            }
        }

        // Process V2 RegisterOrganization
        const string orgSql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""organization""
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var orgCmd = new NpgsqlCommand(orgSql, conn))
        {
            orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            orgCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var orgReader = await orgCmd.ExecuteReaderAsync(ct);
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var timestamp = orgReader.GetInt64(1);
                var organization = orgReader.GetString(2);

                var orgKey = organization.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, orgKey, ("CrcV2_RegisterOrganization", timestamp));
            }
        }

        // Process V2 RegisterGroup
        const string groupSql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint"", r.""symbol""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var groupCmd = new NpgsqlCommand(groupSql, conn))
        {
            groupCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            groupCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var groupReader = await groupCmd.ExecuteReaderAsync(ct);
            while (await groupReader.ReadAsync(ct))
            {
                var blockNumber = groupReader.GetInt64(0);
                var group = groupReader.GetString(1);
                var name = groupReader.GetString(2);
                var mint = groupReader.GetString(3);
                var symbol = groupReader.GetString(4);

                var groupKey = group.ToLowerInvariant();

                _caches.Groups.Add(blockNumber, groupKey, (name, mint, symbol));
            }
        }

        // Process V2 ERC20WrapperDeployed
        const string wrapperSql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper"", e.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" >= @fromBlock AND e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using (var wrapperCmd = new NpgsqlCommand(wrapperSql, conn))
        {
            wrapperCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            wrapperCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var wrapperReader = await wrapperCmd.ExecuteReaderAsync(ct);
            while (await wrapperReader.ReadAsync(ct))
            {
                var blockNumber = wrapperReader.GetInt64(0);
                var avatar = wrapperReader.GetString(1);
                var erc20Wrapper = wrapperReader.GetString(2);
                var circlesType = (CirclesType)wrapperReader.GetInt32(3);

                // Key by wrapper address (not avatar) to support avatars with multiple wrappers
                var wrapperKey = erc20Wrapper.ToLowerInvariant();

                _caches.UpsertWrapper(blockNumber, wrapperKey, avatar, circlesType);
            }
        }
    }
}
