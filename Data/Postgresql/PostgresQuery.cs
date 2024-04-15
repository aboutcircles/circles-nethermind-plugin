using Dapper;
using System.Data;
using System.Globalization;
using System.Text;
using Circles.Index.Data.Model;
using Npgsql;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NpgsqlTypes;

namespace Circles.Index.Data.Postgresql;

public static class PostgresQuery
{
    public static long? LatestBlock(NpgsqlConnection connection)
    {
        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT MAX(block_number) as block_number FROM {TableNames.Block}
        ";

        object? result = cmd.ExecuteScalar();
        if (result is long longResult)
        {
            return longResult;
        }

        return null;
    }
    public static long? FirstGap(NpgsqlConnection connection)
    {
        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
        SELECT (prev.block_number + 1) AS gap_start
        FROM (
            SELECT block_number, LEAD(block_number) OVER (ORDER BY block_number) AS next_block_number 
            FROM (
                SELECT block_number FROM {TableNames.Block} ORDER BY block_number DESC LIMIT 500000
            ) AS sub
        ) AS prev
        WHERE prev.next_block_number - prev.block_number > 1
        ORDER BY gap_start
        LIMIT 1;
    ";

        object? result = cmd.ExecuteScalar();
        if (result is long longResult)
        {
            return longResult;
        }

        return null;
    }

    public static IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(NpgsqlConnection connection,
        int count = 100)
    {
        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT block_number, block_hash
            FROM {TableNames.Block}
            ORDER BY block_number DESC
            LIMIT {count}
        ";

        using NpgsqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetInt64(0), new Hash256(reader.GetString(1)));
        }
    }

    public static IEnumerable<Address> TokenAddressesForAccount(NpgsqlConnection connection, Address circlesAccount)
    {
        const string sql = @$"
            select token_address
            from {TableNames.Erc20Transfer}
            where to_address = @circlesAccount
            group by token_address;";

        using NpgsqlCommand selectCmd = connection.CreateCommand();
        selectCmd.CommandText = sql;
        selectCmd.Parameters.AddWithValue("@circlesAccount", circlesAccount.ToString(true, false));

        using NpgsqlDataReader reader = selectCmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new Address((byte[])reader.GetValue(0));
        }
    }

    public static IEnumerable<CirclesSignupDto> CirclesSignups(NpgsqlConnection connection, CirclesSignupQuery query,
        bool closeConnection = false)
    {
        NpgsqlCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";

        var (cursorConditionSql, cursorParameters) =
            CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

        cmd.CommandText = $@"
            SELECT block_number, transaction_index, log_index, timestamp, transaction_hash, circles_address, token_address
            FROM {TableNames.CrcV1Signup}
            WHERE {cursorConditionSql}
            AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
            AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
            AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
            AND (@UserAddress IS NULL OR circles_address = @UserAddress)
            AND (@TokenAddress IS NULL OR token_address = @TokenAddress)
            ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
            LIMIT @PageSize
        ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", NpgsqlDbType.Text, query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UserAddress", NpgsqlDbType.Text, query.UserAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@TokenAddress", NpgsqlDbType.Text, query.TokenAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using NpgsqlDataReader reader =
            cmd.ExecuteReader(closeConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default);
        while (reader.Read())
        {
            long blockNumber = reader.GetInt64(0);
            long transactionIndex = reader.GetInt64(1);
            long logIndex = reader.GetInt64(2);
            string cursor = $"{blockNumber}-{transactionIndex}-{logIndex}";

            yield return new CirclesSignupDto(
                Timestamp: reader.GetInt64(3).ToString(),
                BlockNumber: blockNumber.ToString(NumberFormatInfo.InvariantInfo),
                TransactionHash: reader.GetString(4),
                CirclesAddress: reader.GetString(5),
                TokenAddress: reader.IsDBNull(6) ? null : reader.GetString(6),
                Cursor: cursor);
        }
    }

    public static IEnumerable<CirclesTrustDto> CirclesTrusts(NpgsqlConnection connection, CirclesTrustQuery query,
        bool closeConnection = false)
    {
        NpgsqlCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";
        var (cursorConditionSql, cursorParameters) =
            CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

        string whereAndSql = $@"
            {cursorConditionSql}
            AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
            AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
            AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
            AND (@UserAddress IS NULL OR user_address = @UserAddress)
            AND (@CanSendToAddress IS NULL OR can_send_to_address = @CanSendToAddress)";

        string whereOrSql = $@"
            {cursorConditionSql}
            AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
            AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
            AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
            AND ((@UserAddress IS NULL OR user_address = @UserAddress)
                  OR (@CanSendToAddress IS NULL OR can_send_to_address = @CanSendToAddress))";

        cmd.CommandText = $@"
            SELECT block_number, transaction_index, log_index, timestamp, transaction_hash, user_address, can_send_to_address, ""limit""
            FROM {TableNames.CrcV1Trust}
                        WHERE {(query.Mode == QueryMode.And ? whereAndSql : whereOrSql)}
            ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
            LIMIT @PageSize
            ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", NpgsqlDbType.Text, query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UserAddress", NpgsqlDbType.Text, query.UserAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CanSendToAddress", NpgsqlDbType.Text, query.CanSendToAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using NpgsqlDataReader reader =
            cmd.ExecuteReader(closeConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default);
        while (reader.Read())
        {
            long blockNumber = reader.GetInt64(0);
            long transactionIndex = reader.GetInt64(1);
            long logIndex = reader.GetInt64(2);
            string cursor = $"{blockNumber}-{transactionIndex}-{logIndex}";

            yield return new CirclesTrustDto(
                Timestamp: reader.GetInt64(3).ToString(),
                BlockNumber: blockNumber.ToString(NumberFormatInfo.InvariantInfo),
                TransactionHash: reader.GetString(4),
                UserAddress: reader.GetString(5),
                CanSendToAddress: reader.GetString(6),
                Limit: reader.GetInt32(7),
                Cursor: cursor);
        }
    }

    public static IEnumerable<CirclesHubTransferDto> CirclesHubTransfers(NpgsqlConnection connection,
        CirclesHubTransferQuery query, bool closeConnection = false)
    {
        NpgsqlCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";
        var (cursorConditionSql, cursorParameters) =
            CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

        string whereAndSql = $@"
        {cursorConditionSql}
        AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
        AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
        AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
        AND (@FromAddress IS NULL OR from_address = @FromAddress)
        AND (@ToAddress IS NULL OR to_address = @ToAddress)";

        string whereOrSql = $@"
        {cursorConditionSql}
        AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
        AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
        AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
        AND ((@FromAddress IS NULL OR from_address = @FromAddress)
              OR (@ToAddress IS NULL OR to_address = @ToAddress))";

        cmd.CommandText = $@"
        SELECT block_number, transaction_index, log_index, timestamp, transaction_hash, from_address, to_address, amount
        FROM {TableNames.CrcV1HubTransfer}
        WHERE {(query.Mode == QueryMode.And ? whereAndSql : whereOrSql)}
        ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
        LIMIT @PageSize
    ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", NpgsqlDbType.Text, query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FromAddress", NpgsqlDbType.Text, query.FromAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ToAddress", NpgsqlDbType.Text, query.ToAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using NpgsqlDataReader reader =
            cmd.ExecuteReader(closeConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default);
        while (reader.Read())
        {
            long blockNumber = reader.GetInt64(0);
            long transactionIndex = reader.GetInt64(1);
            long logIndex = reader.GetInt64(2);
            string cursor = $"{blockNumber}-{transactionIndex}-{logIndex}";

            yield return new CirclesHubTransferDto(
                Timestamp: reader.GetInt64(3).ToString(),
                BlockNumber: blockNumber.ToString(NumberFormatInfo.InvariantInfo),
                TransactionHash: reader.GetString(4),
                FromAddress: reader.GetString(5),
                ToAddress: reader.GetString(6),
                Amount: reader.GetString(7),
                Cursor: cursor);
        }
    }

    public static IEnumerable<CirclesTransferDto> CirclesTransfers(NpgsqlConnection connection,
        CirclesTransferQuery query, bool closeConnection = false)
    {
        NpgsqlCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";
        var (cursorConditionSql, cursorParameters) =
            CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

        string whereAndSql = $@"
        {cursorConditionSql}
        AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
        AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
        AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
        AND (@TokenAddress IS NULL OR token_address = @TokenAddress)
        AND (@FromAddress IS NULL OR from_address = @FromAddress)
        AND (@ToAddress IS NULL OR to_address = @ToAddress)";

        string whereOrSql = $@"
        {cursorConditionSql}
        AND (@MinBlockNumber = 0 OR block_number >= @MinBlockNumber)
        AND (@MaxBlockNumber = 0 OR block_number <= @MaxBlockNumber)
        AND (@TransactionHash IS NULL OR transaction_hash = @TransactionHash)
        AND (@TokenAddress IS NULL OR token_address = @TokenAddress)
        AND ((@FromAddress IS NULL OR from_address = @FromAddress)
              OR (@ToAddress IS NULL OR to_address = @ToAddress))";

        cmd.CommandText = $@"
        SELECT block_number, transaction_index, log_index, timestamp, transaction_hash, token_address, from_address, to_address, amount
        FROM {TableNames.Erc20Transfer}
        WHERE {(query.Mode == QueryMode.And ? whereAndSql : whereOrSql)}
        ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
        LIMIT @PageSize
    ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", NpgsqlDbType.Text, query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@TokenAddress", NpgsqlDbType.Text, query.TokenAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FromAddress", NpgsqlDbType.Text, query.FromAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ToAddress", NpgsqlDbType.Text, query.ToAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using NpgsqlDataReader reader =
            cmd.ExecuteReader(closeConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default);
        while (reader.Read())
        {
            long blockNumber = reader.GetInt64(0);
            long transactionIndex = reader.GetInt64(1);
            long logIndex = reader.GetInt64(2);
            string cursor = $"{blockNumber}-{transactionIndex}-{logIndex}";

            yield return new CirclesTransferDto(
                Timestamp: reader.GetInt64(3).ToString(),
                BlockNumber: blockNumber.ToString(NumberFormatInfo.InvariantInfo),
                TransactionHash: reader.GetString(4),
                TokenAddress: reader.GetString(5),
                FromAddress: reader.GetString(6),
                ToAddress: reader.GetString(7),
                Amount: reader.GetValue(8).ToString(),
                Cursor: cursor);
        }
    }
}