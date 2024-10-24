using Npgsql;

namespace Circles.Pathfinder.Data
{
    // TODO: Use CirclesQuery<T> and remove the Npgsql dependency
    public class LoadGraph(string connectionString)
    {
        public IEnumerable<(string Balance, string Account, string TokenAddress)> LoadV2Balances()
        {
            var balanceQuery = @"
                select ""demurragedTotalBalance""::text, ""account"", ""tokenAddress""
                from ""V_CrcV2_BalancesByAccountAndToken""
                where ""demurragedTotalBalance"" > 0;
            ";

            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var balance = reader.GetString(0);
                var account = reader.GetString(1);
                var tokenAddress = reader.GetString(2);

                yield return (balance, account, tokenAddress);
            }
        }

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
        {
            var trustQuery = @"
                select truster, trustee
                from ""V_CrcV2_TrustRelations"";
            ";

            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(trustQuery, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var truster = reader.GetString(0);
                var trustee = reader.GetString(1);

                yield return (truster, trustee, 100); // Assuming a default trust limit of 100 in V2
            }
        }
    }
}