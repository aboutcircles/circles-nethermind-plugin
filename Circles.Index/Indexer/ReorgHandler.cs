using Circles.Index.Data;
using Circles.Index.Data.Query;
using Nethermind.Logging;
using Npgsql;

namespace Circles.Index.Indexer;

public static class ReorgHandler
{
    private record ReorgAffectedData(IDictionary<Tables, object[][]> AffectedData);

    public static async Task ReorgAt(NpgsqlConnection connection, ILogger logger, long block)
    {
        ReorgAffectedData affectedData = await GetAffectedItems(connection, block);
        logger.Info($"Deleting all blocks greater or equal {block} from the index ..");

        foreach (var affectedTable in affectedData.AffectedData)
        {
            logger.Info($"Affected table: {affectedTable.Key}:");
            int i = 0;
            foreach (var row in affectedTable.Value)
            {
                logger.Info($"{i++}: {string.Join(", ", row)}");
            }
        }

        DeleteFromBlockOnwards(connection, block);
    }

    private static void DeleteFromBlockOnwards(NpgsqlConnection connection, long reorgAt)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var tableName in TableNames.AllTableNames)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {tableName} WHERE block_number >= @reorgAt;";
                command.Parameters.AddWithValue("@reorgAt", reorgAt);
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception)
        {
            transaction.Rollback();
            throw;
        }
    }


    private static async Task<ReorgAffectedData> GetAffectedItems(NpgsqlConnection connection, long reorgAt)
    {
        Dictionary<Tables, object[][]> results = new();
        foreach (KeyValuePair<Tables, TableSchema> table in Schema.TableSchemas)
        {
            var q = Query.Select(table.Key, table.Value.Columns.Select(o => o.Column))
                .Where(Query.GreaterThanOrEqual(table.Key, Columns.BlockNumber, reorgAt));

            var result = Query.Execute(connection, q);
            results.Add(table.Key, result.ToArray());
        }

        return new ReorgAffectedData(results);
    }
}