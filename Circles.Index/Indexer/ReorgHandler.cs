using Circles.Index.Common;
using Circles.Index.Data.Query;
using Nethermind.Logging;
using Npgsql;

namespace Circles.Index.Indexer;

public record ReorgAffectedData(IDictionary<Tables, object[][]> AffectedData);

public class ReorgHandler(NpgsqlConnection connection, ILogger logger) : IReorgHandler
{
    public async Task ReorgAt(long block)
    {
        ReorgAffectedData affectedData = await GetAffectedItems(block);
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

        DeleteFromBlockOnwards(block);
    }

    private void DeleteFromBlockOnwards(long reorgAt)
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


    private Task<ReorgAffectedData> GetAffectedItems(long reorgAt)
    {
        Dictionary<Tables, object[][]> results = new();
        ISchema[] schemas = [new Common.Schema(), new V1.Schema(), new V2.Schema()];

        foreach (var schema in schemas)
        {
            foreach (var table in schema.TableSchemas)
            {
                var q = Query.Select(table.Key, table.Value.Columns.Select(o => o.Column))
                    .Where(Query.GreaterThanOrEqual(table.Key, Columns.BlockNumber, reorgAt));
                
                var result = Query.Execute(connection, q);
                results.Add(table.Key, result.ToArray());
            }
        }

        return Task.FromResult(new ReorgAffectedData(results));
    }
}