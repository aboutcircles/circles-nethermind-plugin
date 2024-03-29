using System.Data;
using System.Globalization;
using Circles.Index.Data.Model;
using Microsoft.Data.Sqlite;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.Data.Sqlite;

public static class Query
{
    public static long? LatestBlock(SqliteConnection connection)
    {
        SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT MAX(block_number)
            FROM (
                SELECT MAX(block_number) as block_number FROM {TableNames.BlockRelevant}
                UNION
                SELECT MAX(block_number) as block_number FROM {TableNames.BlockIrrelevant}
            ) as max_blocks
        ";

        object? result = cmd.ExecuteScalar();
        if (result is long longResult)
        {
            return longResult;
        }

        return null;
    }

    public static long? LatestRelevantBlock(SqliteConnection connection)
    {
        SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT MAX(block_number)
            FROM {TableNames.BlockRelevant}
        ";

        object? result = cmd.ExecuteScalar();
        if (result is long longResult)
        {
            return longResult;
        }

        return null;
    }

    public static IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(SqliteConnection connection,
        int count = 100)
    {
        SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT block_number, block_hash
            FROM {TableNames.BlockRelevant}
            ORDER BY block_number DESC
            LIMIT {count}
        ";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetInt64(0), Keccak.Compute(reader.GetString(1)));
        }
    }

    public static IEnumerable<Address> TokenAddressesForAccount(SqliteConnection connection, Address circlesAccount)
    {
        const string sql = @$"
            select token_address
            from {TableNames.CirclesTransfer}
            where to_address = @circlesAccount
            group by token_address;";

        using SqliteCommand selectCmd = connection.CreateCommand();
        selectCmd.CommandText = sql;
        selectCmd.Parameters.AddWithValue("@circlesAccount", circlesAccount.ToString(true, false));

        using SqliteDataReader reader = selectCmd.ExecuteReader();
        while (reader.Read())
        {
            string tokenAddress = reader.GetString(0);
            yield return new Address(tokenAddress);
        }
    }

    public static IEnumerable<CirclesSignupDto> CirclesSignups(SqliteConnection connection, CirclesSignupQuery query,
        bool closeConnection = false)
    {
        SqliteCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";

        var (cursorConditionSql, cursorParameters) = CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

        cmd.CommandText = $@"
            SELECT block_number, transaction_index, log_index, timestamp, transaction_hash, circles_address, token_address
            FROM {TableNames.CirclesSignup}
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
        cmd.Parameters.AddWithValue("@TransactionHash", query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UserAddress", query.UserAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@TokenAddress", query.TokenAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using SqliteDataReader reader =
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

    public static IEnumerable<CirclesTrustDto> CirclesTrusts(SqliteConnection connection, CirclesTrustQuery query,
        bool closeConnection = false)
    {
        SqliteCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";
        var (cursorConditionSql, cursorParameters) = CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);
        
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
            FROM {TableNames.CirclesTrust}
            WHERE {(query.Mode == QueryMode.And ? whereAndSql : whereOrSql)}
            ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
            LIMIT @PageSize
            ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UserAddress", query.UserAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CanSendToAddress", query.CanSendToAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using SqliteDataReader reader =
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

    public static IEnumerable<CirclesHubTransferDto> CirclesHubTransfers(SqliteConnection connection,
        CirclesHubTransferQuery query, bool closeConnection = false)
    {
        SqliteCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";
        var (cursorConditionSql, cursorParameters) = CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

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
        FROM {TableNames.CirclesHubTransfer}
        WHERE {(query.Mode == QueryMode.And ? whereAndSql : whereOrSql)}
        ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
        LIMIT @PageSize
    ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FromAddress", query.FromAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ToAddress", query.ToAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using SqliteDataReader reader =
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

    public static IEnumerable<CirclesTransferDto> CirclesTransfers(SqliteConnection connection,
        CirclesTransferQuery query, bool closeConnection = false)
    {
        SqliteCommand cmd = connection.CreateCommand();

        string sortOrder = query.SortOrder == SortOrder.Ascending ? "ASC" : "DESC";
        var (cursorConditionSql, cursorParameters) = CursorUtils.GenerateCursorConditionAndParameters(query.Cursor, query.SortOrder);

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
        FROM {TableNames.CirclesTransfer}
        WHERE {(query.Mode == QueryMode.And ? whereAndSql : whereOrSql)}
        ORDER BY block_number {sortOrder}, transaction_index {sortOrder}, log_index {sortOrder}
        LIMIT @PageSize
    ";

        cmd.Parameters.AddRange(cursorParameters);
        cmd.Parameters.AddWithValue("@MinBlockNumber", query.BlockNumberRange.Min);
        cmd.Parameters.AddWithValue("@MaxBlockNumber", query.BlockNumberRange.Max);
        cmd.Parameters.AddWithValue("@TransactionHash", query.TransactionHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@TokenAddress", query.TokenAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FromAddress", query.FromAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ToAddress", query.ToAddress?.ToLower() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PageSize", query.Limit ?? 100);

        using SqliteDataReader reader =
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
                Amount: reader.GetString(8),
                Cursor: cursor);
        }
    }
}

public static class CursorUtils
{
    public static (string CursorConditionSql, SqliteParameter[] cursorParameters) GenerateCursorConditionAndParameters(string? cursor, SortOrder sortOrder)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return ("1 = 1", Array.Empty<SqliteParameter>());
        }

        if (TryParseCursor(cursor, out long cursorBlockNumber, out long cursorTransactionIndex, out long cursorLogIndex))
        {
            SqliteParameter[] cursorParameters = {
                new("@CursorBlockNumber", DbType.Int64) { Value = cursorBlockNumber },
                new("@CursorTransactionIndex", DbType.Int64) { Value = cursorTransactionIndex },
                new("@CursorLogIndex", DbType.Int64) { Value = cursorLogIndex }
            };

            string cursorConditionSql = sortOrder == SortOrder.Ascending
                ? "(block_number > @CursorBlockNumber OR (block_number = @CursorBlockNumber AND (transaction_index > @CursorTransactionIndex OR (transaction_index = @CursorTransactionIndex AND log_index > @CursorLogIndex))))"
                : "(block_number < @CursorBlockNumber OR (block_number = @CursorBlockNumber AND (transaction_index < @CursorTransactionIndex OR (transaction_index = @CursorTransactionIndex AND log_index < @CursorLogIndex))))";
            
            return (cursorConditionSql, cursorParameters);
        }

        throw new ArgumentException("Invalid cursor format", nameof(cursor));
    }

    private static bool TryParseCursor(string cursor, out long blockNumber, out long transactionIndex, out long logIndex)
    {
        blockNumber = 0;
        transactionIndex = 0;
        logIndex = 0;

        var parts = cursor.Split('-');
        if (parts.Length != 3)
        {
            return false;
        }

        return long.TryParse(parts[0], out blockNumber) &&
               long.TryParse(parts[1], out transactionIndex) &&
               long.TryParse(parts[2], out logIndex);
    }
}
