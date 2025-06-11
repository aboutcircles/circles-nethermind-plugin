using System.Globalization;
using System.Numerics;
using Npgsql;
using System.Reflection;
using Circles.Index.Common;

namespace Circles.Pathfinder.Data
{
    // TODO: Use CirclesQuery<T> and remove the Npgsql dependency
    public interface ILoadGraph
    {
        IEnumerable<(string Balance, int Account, int TokenAddress, int TokenOwner, bool IsWrapped, bool IsStatic)>
            LoadV2Balances();

        IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust();
    }

    public class LoadGraph : ILoadGraph
    {
        private readonly string _connectionString;

        public LoadGraph(string connectionString)
        {
            _connectionString = connectionString;
        }

        private string LoadQueryFromResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fullResourceName = $"Circles.Pathfinder.Data.Queries.{resourceName}";

            using var stream = assembly.GetManifestResourceStream(fullResourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"SQL query resource not found: {fullResourceName}");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public IEnumerable<(string Balance, int Account, int TokenAddress, int TokenOwner, bool IsWrapped, bool IsStatic)>
            LoadV2Balances()
        {
            // We now only have one balance query that includes the isWrapped column
            var balanceQuery = LoadQueryFromResource("balanceQuery.sql");

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            using var reader = command.ExecuteReader();

            var now = DateTime.UtcNow;

            while (reader.Read())
            {
                var balance = reader.GetString(0);
                var account = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var tokenOwner = reader.GetString(3);
                var isWrapped = reader.GetBoolean(4);
                var type = reader.GetString(5);

                if (type == "static")
                {
                    // Convert to Circles
                    var staticAttoCircles = BigInteger.Parse(balance);
                    var demurragedAttoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                    if (demurragedAttoCircles == 0)
                    {
                        continue;
                    }

                    balance = demurragedAttoCircles.ToString(CultureInfo.InvariantCulture);
                }

                if (balance == "0")
                {
                    continue;
                }

                // yield return (balance, account, tokenAddress, tokenOwner, isWrapped, type == "static");
                yield return (balance,
                    AddressIdPool.IdOf(account.ToLowerInvariant()),
                    AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                    AddressIdPool.IdOf(tokenOwner.ToLowerInvariant()),
                    isWrapped,
                    type == "static");
            }
        }

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
        {
            // We now only have one trust query that includes wrap tokens
            var trustQuery = LoadQueryFromResource("trustQuery.sql");

            using var connection = new NpgsqlConnection(_connectionString);
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