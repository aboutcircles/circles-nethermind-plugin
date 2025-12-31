using System.Globalization;
using System.Numerics;
using Npgsql;
using System.Reflection;
using Circles.Common;

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

        IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags();
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
            var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>();

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
            using var reader = command.ExecuteReader();

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

                results.Add((balance,
                    AddressIdPool.IdOf(account.ToLowerInvariant()),
                    AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                    isWrapped,
                    type == "static"));
            }

            return results;
        }

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
        {
            // We now only have one trust query that includes wrap tokens
            var trustQuery = LoadQueryFromResource("trustQuery.sql");
            var results = new List<(string Truster, string Trustee, int Limit)>();

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(trustQuery, connection);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var truster = reader.GetString(0);
                var trustee = reader.GetString(1);

                results.Add((truster, trustee, 100)); // Assuming a default trust limit of 100 in V2
            }

            return results;
        }

        // Load groups
        public IEnumerable<string> LoadGroups()
        {
            var groupQuery = LoadQueryFromResource("groupQuery.sql");
            var results = new List<string>();

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupQuery, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var groupAddress = reader.GetString(0);
                results.Add(groupAddress);
            }

            return results;
        }

        // Load group trust relationships
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
        {
            var groupTrustQuery = LoadQueryFromResource("groupTrustQuery.sql");
            var results = new List<(string GroupAddress, string TrustedToken)>();

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupTrustQuery, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var groupAddress = reader.GetString(0);
                var trustedToken = reader.GetString(1);
                results.Add((groupAddress, trustedToken));
            }

            return results;
        }

        // Load consented flow flags (latest per avatar)
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
        {
            var query = LoadQueryFromResource("consentedFlowQuery.sql");
            var results = new List<(string Avatar, bool HasConsentedFlow)>();

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var avatar = reader.GetString(0);
                var flag = (byte[])reader.GetValue(1);

                // Decode the consented flow flag from Hub.sol:
                //
                // In Solidity (Hub.sol line 45):
                //   bytes32 internal constant ADVANCED_FLAG_OPTOUT_CONSENTED_FLOW = bytes32(uint256(1));
                //
                // bytes32(uint256(1)) creates a 32-byte array where:
                //   - uint256(1) = 0x0000...0001 (256-bit integer with value 1)
                //   - bytes32(...) stores this as big-endian (most significant byte first)
                //   - Result: [0x00, 0x00, ..., 0x00, 0x01] (31 zeros, then 0x01 at index 31)
                //
                // So we check byte[31] & 0x01 to see if consented flow is enabled.
                bool hasConsented = flag.Length >= 32 && (flag[31] & 0x01) != 0;
                results.Add((avatar, hasConsented));
            }

            return results;
        }
    }
}