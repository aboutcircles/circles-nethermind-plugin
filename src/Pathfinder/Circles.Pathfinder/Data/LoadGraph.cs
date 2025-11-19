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
    }

    public class LoadGraph : ILoadGraph
    {
        private readonly string _connectionString;
        private readonly Settings _settings;

        public LoadGraph(string connectionString, Settings settings)
        {
            _connectionString = connectionString;
            _settings = settings;
        }

        public LoadGraph(Settings settings) : this(GetConnectionStringFromSettings(settings), settings)
        {
        }

        private static string GetConnectionStringFromSettings(Settings settings)
        {
            // Try to get connection string from environment variable first
            var connString = Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(connString))
            {
                return connString;
            }
            
            // If environment variable is not set, try to get from settings object properties
            // This handles both host settings (which inherit from base Settings) and library settings
            var settingsType = settings.GetType();
            
            // Look for IndexReadonlyDbConnectionString property
            var connectionStringProperty = settingsType.GetProperty("IndexReadonlyDbConnectionString");
            if (connectionStringProperty != null)
            {
                var value = connectionStringProperty.GetValue(settings) as string;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            
            throw new ArgumentException("POSTGRES_READONLY_CONNECTION_STRING environment variable is not set or connection string not found in settings.");
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

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2Balances()
        {
            // We now only have one balance query that includes the isWrapped column
            var balanceQuery = LoadQueryFromResource("balanceQuery.sql");

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
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

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(trustQuery, connection);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
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

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupQuery, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
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

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupTrustQuery, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var groupAddress = reader.GetString(0);
                var trustedToken = reader.GetString(1);
                yield return (groupAddress, trustedToken);
            }
        }
    }
}