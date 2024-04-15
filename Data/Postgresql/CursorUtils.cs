using Circles.Index.Data.Model;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.Data.Postgresql;

public static class CursorUtils
{
    public static (string CursorConditionSql, NpgsqlParameter[] cursorParameters) GenerateCursorConditionAndParameters(
        string? cursor, SortOrder sortOrder)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return ("1 = 1", Array.Empty<NpgsqlParameter>());
        }

        if (TryParseCursor(cursor, out long cursorBlockNumber, out long cursorTransactionIndex,
                out long cursorLogIndex))
        {
            NpgsqlParameter[] cursorParameters =
            {
                new("@CursorBlockNumber", NpgsqlDbType.Bigint) { Value = cursorBlockNumber },
                new("@CursorTransactionIndex", NpgsqlDbType.Bigint) { Value = cursorTransactionIndex },
                new("@CursorLogIndex", NpgsqlDbType.Bigint) { Value = cursorLogIndex }
            };

            string cursorConditionSql = sortOrder == SortOrder.Ascending
                ? "(block_number > @CursorBlockNumber OR (block_number = @CursorBlockNumber AND (transaction_index > @CursorTransactionIndex OR (transaction_index = @CursorTransactionIndex AND log_index > @CursorLogIndex))))"
                : "(block_number < @CursorBlockNumber OR (block_number = @CursorBlockNumber AND (transaction_index < @CursorTransactionIndex OR (transaction_index = @CursorTransactionIndex AND log_index < @CursorLogIndex))))";

            return (cursorConditionSql, cursorParameters);
        }

        throw new ArgumentException("Invalid cursor format", nameof(cursor));
    }

    private static bool TryParseCursor(string cursor, out long blockNumber, out long transactionIndex,
        out long logIndex)
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