using System.Numerics;
using System.Text.Json;
using Circles.Common;
using Circles.Common.Dto;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Transaction history methods for CirclesRpcModule.
/// Includes plain history and enriched history (with avatar/profile/demurrage).
/// </summary>
public partial class CirclesRpcModule
{
    #region GetTransactionHistory - Version-specific query builders

    /// <summary>
    /// Builds SQL query for V1 TransferSummary table (excludeIntermediary=true).
    /// </summary>
    private static string BuildV1TransferSummaryQuery(bool hasCursor)
    {
        return $@"
            SELECT
                ""blockNumber"",
                timestamp,
                ""transactionIndex"",
                ""logIndex"",
                0 as ""batchIndex"",
                ""transactionHash"",
                1 as version,
                NULL::text as operator,
                ""from"",
                ""to"",
                NULL::text as id,
                amount as value,
                0 as ""isInflationary""
            FROM ""CrcV1_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Builds SQL query for V1 Transfer + HubTransfer tables (excludeIntermediary=false).
    /// </summary>
    private static string BuildV1TransfersQuery(bool hasCursor)
    {
        // V1 transfers come from both Transfer (ERC20) and HubTransfer (direct hub transfers)
        // We need to UNION them and present a unified format
        return $@"
            SELECT * FROM (
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    1 as version,
                    NULL::text as operator,
                    ""from"",
                    ""to"",
                    ""tokenAddress"" as id,
                    amount as value,
                    0 as ""isInflationary""
                FROM ""CrcV1_Transfer""
                WHERE (""from"" = @address OR ""to"" = @address)
                UNION ALL
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    1 as version,
                    NULL::text as operator,
                    ""from"",
                    ""to"",
                    NULL::text as id,
                    amount as value,
                    0 as ""isInflationary""
                FROM ""CrcV1_HubTransfer""
                WHERE (""from"" = @address OR ""to"" = @address)
            ) AS v1_transfers
            WHERE true
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 TransferSummary table (excludeIntermediary=true).
    /// </summary>
    private static string BuildV2TransferSummaryQuery(bool hasCursor)
    {
        return $@"
            SELECT
                ""blockNumber"",
                timestamp,
                ""transactionIndex"",
                ""logIndex"",
                0 as ""batchIndex"",
                ""transactionHash"",
                2 as version,
                NULL::text as operator,
                ""from"",
                ""to"",
                NULL::text as id,
                amount as value,
                0 as ""isInflationary""
            FROM ""CrcV2_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 transfer tables (excludeIntermediary=false).
    /// Queries TransferSingle, TransferBatch, and Erc20WrapperTransfer directly.
    /// </summary>
    private static string BuildV2TransfersQuery(bool hasCursor)
    {
        return $@"
            SELECT * FROM (
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    2 as version,
                    operator,
                    ""from"",
                    ""to"",
                    id::text,
                    value,
                    0 as ""isInflationary""
                FROM ""CrcV2_TransferSingle""
                WHERE (""from"" = @address OR ""to"" = @address)
                UNION ALL
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    ""batchIndex"",
                    ""transactionHash"",
                    2 as version,
                    operator,
                    ""from"",
                    ""to"",
                    id::text,
                    value,
                    0 as ""isInflationary""
                FROM ""CrcV2_TransferBatch""
                WHERE (""from"" = @address OR ""to"" = @address)
                UNION ALL
                SELECT
                    wt.""blockNumber"",
                    wt.timestamp,
                    wt.""transactionIndex"",
                    wt.""logIndex"",
                    0 as ""batchIndex"",
                    wt.""transactionHash"",
                    2 as version,
                    NULL::text as operator,
                    wt.""from"",
                    wt.""to"",
                    wt.""tokenAddress"" as id,
                    wt.amount as value,
                    CASE WHEN wd.""circlesType"" = 1 THEN 1 ELSE 0 END as ""isInflationary""
                FROM ""CrcV2_Erc20WrapperTransfer"" wt
                INNER JOIN ""CrcV2_ERC20WrapperDeployed"" wd ON wd.""erc20Wrapper"" = wt.""tokenAddress""
                WHERE (wt.""from"" = @address OR wt.""to"" = @address)
            ) AS v2_transfers
            WHERE true
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" = @cursorLogIndex AND ""batchIndex"" < @cursorBatchIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC, ""batchIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Executes a transaction history query and returns results.
    /// </summary>
    private async Task<List<TransactionHistoryRow>> ExecuteTransactionHistoryQuery(
        NpgsqlConnection connection,
        string sql,
        string normalizedAddress,
        int limit,
        long? cursorBlock,
        int? cursorTxIndex,
        int? cursorLogIndex,
        int? cursorBatchIndex)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", normalizedAddress);
        cmd.Parameters.AddWithValue("limit", limit + 1);

        if (cursorBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("cursorBlock", cursorBlock.Value);
            cmd.Parameters.AddWithValue("cursorTxIndex", cursorTxIndex!.Value);
            cmd.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
            cmd.Parameters.AddWithValue("cursorBatchIndex", cursorBatchIndex ?? 0);
        }

        var results = new List<TransactionHistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var row = ReadTransactionHistoryRow(reader);
            results.Add(row);
        }

        return results;
    }

    /// <summary>
    /// Reads a single TransactionHistoryRow from a data reader.
    /// </summary>
    private static TransactionHistoryRow ReadTransactionHistoryRow(NpgsqlDataReader reader)
    {
        var blockNumber = reader.GetInt64(0);
        var timestamp = reader.GetInt64(1);
        var transactionIndex = reader.GetInt32(2);
        var logIndex = reader.GetInt32(3);
        var batchIndex = reader.GetInt32(4);
        var transactionHash = reader.GetString(5);
        var ver = reader.GetInt32(6);
        var operatorAddr = reader.IsDBNull(7) ? null : reader.GetString(7);
        var from = reader.GetString(8);
        var to = reader.GetString(9);
        var id = reader.IsDBNull(10) ? null : reader.GetString(10);
        var valueRaw = reader.GetFieldValue<System.Numerics.BigInteger>(11);
        var isInflationary = reader.GetInt32(12);

        // Calculate all circle amount formats
        BigInteger attoCirclesDemurraged;
        BigInteger staticAttoCircles;
        BigInteger attoCrc;

        if (ver == 1)
        {
            // V1: value is raw attoCrc
            attoCrc = valueRaw;
            attoCirclesDemurraged = CirclesConverter.AttoCrcToAttoCircles(attoCrc, (ulong)timestamp);
            staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCirclesDemurraged);
        }
        else if (isInflationary == 1)
        {
            // V2 inflationary wrapper: value is static (inflationary) attoCircles
            staticAttoCircles = valueRaw;
            var timestampUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var day = CirclesConverter.DayFromTimestamp(timestampUtc, 1_602_720_000);
            attoCirclesDemurraged = CirclesConverter.InflationaryToDemurrage(staticAttoCircles, day);
            attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCirclesDemurraged, (ulong)timestamp);
        }
        else
        {
            // V2 demurraged: value is demurraged attoCircles
            attoCirclesDemurraged = valueRaw;
            var timestampUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var day = CirclesConverter.DayFromTimestamp(timestampUtc, 1_602_720_000);
            staticAttoCircles = CirclesConverter.DemurrageToInflationary(attoCirclesDemurraged, day);
            attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCirclesDemurraged, (ulong)timestamp);
        }

        var circles = CirclesConverter.AttoCirclesToCircles(attoCirclesDemurraged);
        var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
        var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

        return new TransactionHistoryRow(
            BlockNumber: blockNumber,
            Timestamp: timestamp,
            TransactionIndex: transactionIndex,
            LogIndex: logIndex,
            TransactionHash: transactionHash,
            Version: ver,
            From: from,
            To: to,
            Operator: operatorAddr,
            Id: id,
            Value: valueRaw.ToString(),
            Circles: circles.ToString(),
            AttoCircles: attoCirclesDemurraged.ToString(),
            Crc: crc.ToString(),
            AttoCrc: attoCrc.ToString(),
            StaticCircles: staticCircles.ToString(),
            StaticAttoCircles: staticAttoCircles.ToString()
        );
    }

    #endregion

    public async Task<PagedResponse<TransactionHistoryRow>> GetTransactionHistory(
        string avatarAddress,
        int limit = 50,
        string? cursor = null,
        int? version = null,
        bool excludeIntermediary = true)
    {
        var normalizedAddress = ValidateAndNormalizeAddress(avatarAddress, nameof(avatarAddress));
        await using var connection = await CreateConnectionAsync();

        var (cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex) = CursorUtils.DecodeCursorWithBatch(cursor);
        var hasCursor = cursorBlock.HasValue;

        List<TransactionHistoryRow> results;

        if (version.HasValue)
        {
            // Query specific version directly - no UNION needed
            string sql = (version.Value, excludeIntermediary) switch
            {
                (1, true) => BuildV1TransferSummaryQuery(hasCursor),
                (1, false) => BuildV1TransfersQuery(hasCursor),
                (2, true) => BuildV2TransferSummaryQuery(hasCursor),
                (2, false) => BuildV2TransfersQuery(hasCursor),
                _ => throw new ArgumentException($"Invalid version: {version.Value}. Must be 1 or 2.")
            };

            results = await ExecuteTransactionHistoryQuery(
                connection, sql, normalizedAddress, limit,
                cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex);
        }
        else
        {
            // Query both versions separately and merge results in application code
            // This avoids SQL UNION across V1+V2 which causes performance issues
            var v1Sql = excludeIntermediary
                ? BuildV1TransferSummaryQuery(hasCursor)
                : BuildV1TransfersQuery(hasCursor);
            var v2Sql = excludeIntermediary
                ? BuildV2TransferSummaryQuery(hasCursor)
                : BuildV2TransfersQuery(hasCursor);

            // Execute both queries
            var v1Results = await ExecuteTransactionHistoryQuery(
                connection, v1Sql, normalizedAddress, limit,
                cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex);

            var v2Results = await ExecuteTransactionHistoryQuery(
                connection, v2Sql, normalizedAddress, limit,
                cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex);

            // Merge and sort by block/tx/log descending, take limit+1
            results = v1Results
                .Concat(v2Results)
                .OrderByDescending(r => r.BlockNumber)
                .ThenByDescending(r => r.TransactionIndex)
                .ThenByDescending(r => r.LogIndex)
                .Take(limit + 1)
                .ToList();
        }

        // Check if there are more results
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }

        // Generate next cursor
        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var lastResult = results[^1];
            nextCursor = CursorUtils.EncodeCursorWithBatch(lastResult.BlockNumber, lastResult.TransactionIndex, lastResult.LogIndex, 0);
        }

        return new PagedResponse<TransactionHistoryRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    public async Task<PagedResponse<TransferDataRow>> GetTransferData(
        string address,
        string? direction = null,
        string? counterparty = null,
        long? fromBlock = null,
        long? toBlock = null,
        int limit = 50,
        string? cursor = null)
    {
        var addr = ValidateAndNormalizeAddress(address);
        limit = Math.Clamp(limit, 1, 1000);

        if (direction != null && direction != "sent" && direction != "received")
            throw new ArgumentException("direction must be 'sent', 'received', or null");

        await using var connection = await CreateConnectionAsync();
        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);
        var hasCursor = cursorBlock.HasValue;

        // Build WHERE clause
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        var counterAddr = counterparty?.ToLower();

        if (direction == "sent")
        {
            conditions.Add(@"""from"" = @addr");
            parameters.Add(new NpgsqlParameter("addr", addr));
            if (counterAddr != null)
            {
                conditions.Add(@"""to"" = @counterparty");
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
        }
        else if (direction == "received")
        {
            conditions.Add(@"""to"" = @addr");
            parameters.Add(new NpgsqlParameter("addr", addr));
            if (counterAddr != null)
            {
                conditions.Add(@"""from"" = @counterparty");
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
        }
        else // both directions
        {
            if (counterAddr != null)
            {
                // (from=addr AND to=counter) OR (from=counter AND to=addr)
                conditions.Add(@"(""from"" = @addr AND ""to"" = @counterparty) OR (""from"" = @counterparty AND ""to"" = @addr)");
                parameters.Add(new NpgsqlParameter("addr", addr));
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
            else
            {
                conditions.Add(@"(""from"" = @addr OR ""to"" = @addr)");
                parameters.Add(new NpgsqlParameter("addr", addr));
            }
        }

        if (fromBlock.HasValue)
        {
            conditions.Add(@"""blockNumber"" >= @fromBlock");
            parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        }

        if (toBlock.HasValue)
        {
            conditions.Add(@"""blockNumber"" <= @toBlock");
            parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));
        }

        if (hasCursor)
        {
            conditions.Add(
                @"(""blockNumber"", ""transactionIndex"", ""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)");
            parameters.Add(new NpgsqlParameter("cursorBlock", cursorBlock!.Value));
            parameters.Add(new NpgsqlParameter("cursorTxIndex", cursorTxIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
        }

        var where = string.Join(" AND ", conditions.Select((c, i) =>
            // Wrap the OR clause in parens so AND binds correctly
            c.Contains(" OR ") ? $"({c})" : c));

        var sql = $@"
            SELECT ""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"",
                   ""transactionHash"", ""from"", ""to"", ""data""
            FROM ""CrcV2_TransferData""
            WHERE {where}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit";

        parameters.Add(new NpgsqlParameter("limit", limit + 1));

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddRange(parameters.ToArray());

        var results = new List<TransferDataRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dataBytes = reader.GetFieldValue<byte[]>(7);
            results.Add(new TransferDataRow(
                BlockNumber: reader.GetInt64(0),
                Timestamp: reader.GetInt64(1),
                TransactionIndex: reader.GetInt32(2),
                LogIndex: reader.GetInt32(3),
                TransactionHash: reader.GetString(4),
                From: reader.GetString(5),
                To: reader.GetString(6),
                Data: "0x" + Convert.ToHexString(dataBytes).ToLower()
            ));
        }

        var hasMore = results.Count > limit;
        if (hasMore)
            results.RemoveAt(results.Count - 1);

        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var last = results[^1];
            nextCursor = CursorUtils.EncodeCursor(last.BlockNumber, last.TransactionIndex, last.LogIndex);
        }

        return new PagedResponse<TransferDataRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    /// <summary>
    /// Gets transaction history with enriched data including demurrage calculations and profile info.
    /// Reduces need for separate profile lookups and demurrage computations on client side.
    /// </summary>
    #region GetTransactionHistoryEnriched - Version-specific query builders

    /// <summary>
    /// Builds SQL query for V1 enriched TransferSummary (excludeIntermediary=true).
    /// </summary>
    private static string BuildV1EnrichedTransferSummaryQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'value', amount::text,
                    'version', 1,
                    'type', 'CrcV1_TransferSummary'
                ) as event_payload
            FROM ""CrcV1_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V1 enriched transfers (excludeIntermediary=false).
    /// </summary>
    private static string BuildV1EnrichedTransfersQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            -- CrcV1_Transfer: ERC20 token transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'id', ""tokenAddress"",
                    'value', amount::text,
                    'version', 1,
                    'type', 'CrcV1_Transfer'
                ) as event_payload
            FROM ""CrcV1_Transfer""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}

            UNION ALL

            -- CrcV1_HubTransfer: direct hub transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'value', amount::text,
                    'version', 1,
                    'type', 'CrcV1_HubTransfer'
                ) as event_payload
            FROM ""CrcV1_HubTransfer""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V1 enriched trust events.
    /// </summary>
    private static string BuildV1EnrichedTrustQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'trust' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'canSendTo', ""canSendTo"",
                    'user', ""user"",
                    'limit', ""limit""::text,
                    'version', 1,
                    'type', 'CrcV1_Trust'
                ) as event_payload
            FROM ""CrcV1_Trust""
            WHERE (""canSendTo"" = @address OR ""user"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 enriched TransferSummary (excludeIntermediary=true).
    /// </summary>
    private static string BuildV2EnrichedTransferSummaryQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'value', amount::text,
                    'version', 2,
                    'type', 'CrcV2_TransferSummary'
                ) as event_payload
            FROM ""CrcV2_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 enriched transfers (excludeIntermediary=false).
    /// </summary>
    private static string BuildV2EnrichedTransfersQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            -- CrcV2_TransferSingle: most common V2 transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'operator', operator,
                    'from', ""from"",
                    'to', ""to"",
                    'id', id::text,
                    'value', value::text,
                    'version', 2,
                    'type', 'CrcV2_TransferSingle'
                ) as event_payload
            FROM ""CrcV2_TransferSingle""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}

            UNION ALL

            -- CrcV2_TransferBatch: batch transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'batchIndex', ""batchIndex"",
                    'transactionHash', ""transactionHash"",
                    'operator', operator,
                    'from', ""from"",
                    'to', ""to"",
                    'id', id::text,
                    'value', value::text,
                    'version', 2,
                    'type', 'CrcV2_TransferBatch'
                ) as event_payload
            FROM ""CrcV2_TransferBatch""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}

            UNION ALL

            -- CrcV2_Erc20WrapperTransfer: ERC20 wrapper transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'id', ""tokenAddress"",
                    'value', amount::text,
                    'version', 2,
                    'type', 'CrcV2_Erc20WrapperTransfer'
                ) as event_payload
            FROM ""CrcV2_Erc20WrapperTransfer""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 enriched trust events.
    /// </summary>
    private static string BuildV2EnrichedTrustQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'trust' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'truster', truster,
                    'trustee', trustee,
                    'expiryTime', ""expiryTime""::text,
                    'version', 2,
                    'type', 'CrcV2_Trust'
                ) as event_payload
            FROM ""CrcV2_Trust""
            WHERE (truster = @address OR trustee = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Executes an enriched transaction history query and returns raw events.
    /// </summary>
    private async Task<List<JsonElement>> ExecuteEnrichedTransactionQuery(
        NpgsqlConnection connection,
        string sql,
        string normalizedAddress,
        long fromBlock,
        long? toBlock,
        int limit,
        long? cursorBlock,
        int? cursorTxIndex,
        int? cursorLogIndex)
    {
        var wrappedSql = $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                event_name,
                event_payload
            FROM ({sql}) combined
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";

        await using var cmd = new NpgsqlCommand(wrappedSql, connection);
        cmd.Parameters.AddWithValue("address", normalizedAddress);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("limit", limit + 1);

        if (toBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("toBlock", toBlock.Value);
        }

        if (cursorBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("cursorBlock", cursorBlock.Value);
            cmd.Parameters.AddWithValue("cursorTxIndex", cursorTxIndex!.Value);
            cmd.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
        }

        var events = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var eventPayloadJson = reader.GetString(5);
            var eventPayload = JsonSerializer.Deserialize<JsonElement>(eventPayloadJson);
            events.Add(eventPayload);
        }

        return events;
    }

    #endregion

    public async Task<PagedResponse<EnrichedTransaction>> GetTransactionHistoryEnriched(
        string address,
        long fromBlock,
        long? toBlock = null,
        int? limit = null,
        string? cursor = null,
        int? version = null,
        bool excludeIntermediary = true)
    {
        var normalizedAddress = ValidateAndNormalizeAddress(address);
        await using var connection = await CreateConnectionAsync();

        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);
        var effectiveLimit = limit ?? 20;

        var cursorCondition = cursorBlock.HasValue ? @"AND (
                    ""blockNumber"" < @cursorBlock OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
                  )" : "";
        var toBlockCondition = toBlock.HasValue ? "AND \"blockNumber\" <= @toBlock" : "";

        List<JsonElement> events;

        // Determine which version to query
        // Default: version=null means V2 only (backward compatibility with existing behavior)
        var effectiveVersion = version ?? 2;

        if (effectiveVersion == 1)
        {
            // V1 queries
            var transferSql = excludeIntermediary
                ? BuildV1EnrichedTransferSummaryQuery(cursorCondition, toBlockCondition)
                : BuildV1EnrichedTransfersQuery(cursorCondition, toBlockCondition);
            var trustSql = BuildV1EnrichedTrustQuery(cursorCondition, toBlockCondition);
            var combinedSql = $"{transferSql} UNION ALL {trustSql}";

            events = await ExecuteEnrichedTransactionQuery(
                connection, combinedSql, normalizedAddress, fromBlock, toBlock, effectiveLimit,
                cursorBlock, cursorTxIndex, cursorLogIndex);
        }
        else
        {
            // V2 queries (default)
            var transferSql = excludeIntermediary
                ? BuildV2EnrichedTransferSummaryQuery(cursorCondition, toBlockCondition)
                : BuildV2EnrichedTransfersQuery(cursorCondition, toBlockCondition);
            var trustSql = BuildV2EnrichedTrustQuery(cursorCondition, toBlockCondition);
            var combinedSql = $"{transferSql} UNION ALL {trustSql}";

            events = await ExecuteEnrichedTransactionQuery(
                connection, combinedSql, normalizedAddress, fromBlock, toBlock, effectiveLimit,
                cursorBlock, cursorTxIndex, cursorLogIndex);
        }

        // Check if there are more results
        var hasMore = events.Count > effectiveLimit;
        if (hasMore)
        {
            events.RemoveAt(events.Count - 1);
        }

        // Extract all involved addresses from events
        var involvedAddresses = new HashSet<string>();
        foreach (var evt in events)
        {
            if (evt.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(from.GetString()!);
            if (evt.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(to.GetString()!);
            if (evt.TryGetProperty("truster", out var truster) && truster.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(truster.GetString()!);
            if (evt.TryGetProperty("trustee", out var trustee) && trustee.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(trustee.GetString()!);
            // V1 Trust uses canSendTo and user
            if (evt.TryGetProperty("canSendTo", out var canSendTo) && canSendTo.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(canSendTo.GetString()!);
            if (evt.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(user.GetString()!);
        }

        // Batch fetch avatar info and profiles for all involved addresses (in parallel)
        var addressArray = involvedAddresses.ToArray();
        AvatarInfo?[] avatars;
        JsonElement?[] profiles;

        if (addressArray.Length > 0)
        {
            var avatarTask = GetAvatarInfoBatchInternal(addressArray);
            var profileTask = GetProfileByAddressBatch(addressArray);
            await Task.WhenAll(avatarTask, profileTask);
            avatars = await avatarTask;
            profiles = await profileTask;
        }
        else
        {
            avatars = Array.Empty<AvatarInfo?>();
            profiles = Array.Empty<JsonElement?>();
        }

        var avatarDict = avatars.Where(a => a != null).ToDictionary(a => a!.Avatar, a => a);
        var profileDict = involvedAddresses.Zip(profiles, (addr, prof) => new { addr, prof })
            .Where(x => x.prof != null)
            .ToDictionary(x => x.addr, x => x.prof);

        // Enrich each event
        var enrichedTransactions = new List<EnrichedTransaction>();
        foreach (var evt in events)
        {
            var blockNumber = evt.TryGetProperty("blockNumber", out var bn) ? bn.GetInt64() : 0;
            var timestamp = evt.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0;
            var transactionHash = evt.TryGetProperty("transactionHash", out var th) ? th.GetString() ?? "" : "";
            var transactionIndex = evt.TryGetProperty("transactionIndex", out var ti) ? ti.GetInt32() : 0;
            var logIndex = evt.TryGetProperty("logIndex", out var li) ? li.GetInt32() : 0;

            var enriched = new EnrichedTransaction
            {
                BlockNumber = blockNumber,
                Timestamp = timestamp,
                TransactionHash = transactionHash,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                Event = evt,
                Participants = new Dictionary<string, ParticipantInfo>()
            };

            var eventAddresses = new HashSet<string>();
            if (evt.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                eventAddresses.Add(from.GetString()!);
            if (evt.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
                eventAddresses.Add(to.GetString()!);
            if (evt.TryGetProperty("truster", out var truster) && truster.ValueKind == JsonValueKind.String)
                eventAddresses.Add(truster.GetString()!);
            if (evt.TryGetProperty("trustee", out var trustee) && trustee.ValueKind == JsonValueKind.String)
                eventAddresses.Add(trustee.GetString()!);
            if (evt.TryGetProperty("canSendTo", out var canSendTo) && canSendTo.ValueKind == JsonValueKind.String)
                eventAddresses.Add(canSendTo.GetString()!);
            if (evt.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String)
                eventAddresses.Add(user.GetString()!);

            foreach (var addr in eventAddresses)
            {
                var participantInfo = new ParticipantInfo
                {
                    AvatarInfo = avatarDict.TryGetValue(addr, out var avatar) ? avatar : null,
                    Profile = profileDict.TryGetValue(addr, out var profile) ? profile : null
                };
                enriched.Participants[addr] = participantInfo;
            }

            enrichedTransactions.Add(enriched);
        }

        // Generate next cursor
        string? nextCursor = null;
        if (hasMore && enrichedTransactions.Count > 0)
        {
            var lastEvent = enrichedTransactions[^1].Event;
            if (lastEvent.TryGetProperty("blockNumber", out var blockNum) &&
                lastEvent.TryGetProperty("transactionIndex", out var txIdx) &&
                lastEvent.TryGetProperty("logIndex", out var logIdx))
            {
                nextCursor = CursorUtils.EncodeCursor(
                    blockNum.GetInt64(),
                    txIdx.GetInt32(),
                    logIdx.GetInt32());
            }
        }

        return new PagedResponse<EnrichedTransaction>(
            Results: enrichedTransactions.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }
}
