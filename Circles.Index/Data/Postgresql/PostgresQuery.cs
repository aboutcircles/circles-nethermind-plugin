using Circles.Index.Common;
using Npgsql;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
}