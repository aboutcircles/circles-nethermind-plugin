using Circles.Index.Common;
using Npgsql;
using Nethermind.Core.Crypto;

namespace Circles.Index.Data.Postgresql;

public class PostgresSystemQueries(NpgsqlConnection connection) : ISystemQueries
{
    public long? LatestBlock()
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

    public long? FirstGap()
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

    public IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count = 100)
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
}