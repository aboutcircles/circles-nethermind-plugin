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
        IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2Balances();

        IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust();

        IEnumerable<string> LoadGroups();
        IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts();
        IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings();
    }

    public class LoadGraph(Settings settings) : ILoadGraph
    {
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

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2Balances()
        {
            // We now only have one balance query that includes the isWrapped column
            var balanceQuery = LoadQueryFromResource("balanceQuery.sql");

            using var connection = new NpgsqlConnection(settings.IndexReadonlyDbConnectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            using var reader = command.ExecuteReader();

            var now = DateTime.UtcNow;

            while (reader.Read())
            {
                var balance = reader.GetString(0);
                var account = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var isWrapped = reader.GetBoolean(3);
                var type = reader.GetString(4);

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

                // yield return (balance, account, tokenAddress, isWrapped, type == "static");
                yield return (balance,
                    AddressIdPool.IdOf(account.ToLowerInvariant()),
                    AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                    isWrapped,
                    type == "static");
            }
        }

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
        {
            // We now only have one trust query that includes wrap tokens
            var trustQuery = LoadQueryFromResource("trustQuery.sql");

            using var connection = new NpgsqlConnection(settings.IndexReadonlyDbConnectionString);
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

        // Load groups
        public IEnumerable<string> LoadGroups()
        {
            var groupQuery = LoadQueryFromResource("groupQuery.sql");

            using var connection = new NpgsqlConnection(settings.IndexReadonlyDbConnectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupQuery, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var groupAddress = reader.GetString(0);
                yield return groupAddress;
            }
        }

        // Load group trust relationships
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
        {
            var groupTrustQuery = LoadQueryFromResource("groupTrustQuery.sql");

            using var connection = new NpgsqlConnection(settings.IndexReadonlyDbConnectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupTrustQuery, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var groupAddress = reader.GetString(0);
                var trustedToken = reader.GetString(1);
                yield return (groupAddress, trustedToken);
            }
        }
        public IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings()
        {
            var query = LoadQueryFromResource("wrapperMappingQuery.sql");

            using var connection = new NpgsqlConnection(settings.IndexReadonlyDbConnectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var wrapperAddress = reader.GetString(0);
                var avatar = reader.GetString(1);
                yield return (wrapperAddress, avatar);
            }
        }
    }
}