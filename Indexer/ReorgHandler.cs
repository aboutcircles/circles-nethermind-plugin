using Circles.Index.Data;
using Circles.Index.Data.Model;
using Circles.Index.Data.Postgresql;
using Nethermind.Logging;
using Npgsql;

namespace Circles.Index.Indexer;

public static class ReorgHandler
{
    private record ReorgAffectedData(
        CirclesSignupDto[] Signups,
        CirclesTrustDto[] Trusts,
        CirclesHubTransferDto[] HubTransfers,
        CirclesTransferDto[] Transfers);

    public static async Task ReorgAt(NpgsqlConnection connection, ILogger logger, long block)
    {
        ReorgAffectedData affectedData = await GetAffectedItems(connection, block);
        logger.Info($"Deleting all blocks greater or equal {block} from the index ..");
        logger.Info($"Affected signups: {affectedData.Signups.Length}");
        logger.Info($"Affected trusts: {affectedData.Trusts.Length}");
        logger.Info($"Affected hub transfers: {affectedData.HubTransfers.Length}");
        logger.Info($"Affected crc transfers: {affectedData.Transfers.Length}");

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
        CirclesSignupQuery affectedSignupQuery = new() { BlockNumberRange = { Min = reorgAt }, Limit = int.MaxValue };
        CirclesSignupDto[] affectedSignups = PostgresQuery.CirclesSignups(connection, affectedSignupQuery).ToArray();

        CirclesTrustQuery affectedTrustQuery = new() { BlockNumberRange = { Min = reorgAt }, Limit = int.MaxValue };
        CirclesTrustDto[] affectedTrusts = PostgresQuery.CirclesTrusts(connection, affectedTrustQuery).ToArray();

        CirclesHubTransferQuery affectedHubTransferQuery =
            new() { BlockNumberRange = { Min = reorgAt }, Limit = int.MaxValue };
        CirclesHubTransferDto[] affectedHubTransfers =
            PostgresQuery.CirclesHubTransfers(connection, affectedHubTransferQuery).ToArray();

        CirclesTransferQuery affectedTransferQuery =
            new() { BlockNumberRange = { Min = reorgAt }, Limit = int.MaxValue };
        CirclesTransferDto[] affectedTransfers =
            PostgresQuery.CirclesTransfers(connection, affectedTransferQuery).ToArray();

        return new ReorgAffectedData(
            Signups: affectedSignups,
            Trusts: affectedTrusts,
            HubTransfers: affectedHubTransfers,
            Transfers: affectedTransfers);
    }
}