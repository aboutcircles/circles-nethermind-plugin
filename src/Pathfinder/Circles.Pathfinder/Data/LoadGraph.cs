using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Npgsql;
using System.Reflection;
using Circles.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        private readonly ILogger _logger;

        /// <summary>
        /// Optional callback invoked after each DB query with (queryName, elapsed).
        /// The host can wire this to a Prometheus histogram.
        /// </summary>
        public Action<string, TimeSpan>? OnQueryCompleted { get; set; }

        // Demurrage constants (same as CirclesConverter)
        private const uint InflationDayZeroUnix = 1_675_209_600; // Feb 1, 2023 00:00 UTC
        private const ulong SecondsPerDay = 86_400;

        public LoadGraph(string connectionString, Settings settings, ILogger<LoadGraph>? logger = null)
        {
            _connectionString = connectionString;
            _settings = settings;
            _logger = logger ?? NullLogger<LoadGraph>.Instance;
        }

        public LoadGraph(Settings settings, ILogger<LoadGraph>? logger = null)
            : this(GetConnectionStringFromSettings(settings), settings, logger)
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

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
            using var reader = command.ExecuteReader();

            // Calculate target day for demurrage (configurable for testing, defaults to NOW)
            var targetTimestamp = _settings.TargetDemurrageTimestamp ?? DateTimeOffset.UtcNow;
            var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp, InflationDayZeroUnix);

            // Safety margin: only in live mode (no frozen timestamp) to account for execution delay
            bool applyMargin = _settings.TargetDemurrageTimestamp == null
                               && _settings.DemurrageSafetyMargin < 1.0;

            while (reader.Read())
            {
                var balance = reader.GetString(0);
                var account = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var lastActivity = reader.GetInt64(3);
                var isWrapped = reader.GetBoolean(4);
                var type = reader.GetString(5);

                if (type == "static")
                {
                    // Convert static (inflationary) Circles to demurraged Circles at target day
                    var staticAttoCircles = BigInteger.Parse(balance);
                    var demurragedAttoCircles = CirclesConverter.InflationaryToDemurrage(staticAttoCircles, targetDay);
                    if (demurragedAttoCircles == 0)
                    {
                        continue;
                    }

                    if (staticAttoCircles > 0)
                    {
                        var pctDelta = 100.0 * (1.0 - (double)demurragedAttoCircles / (double)staticAttoCircles);
                        _logger.LogDebug("[LoadGraph] Demurrage static: acct={Account}, raw={Raw}, adj={Adjusted}, delta={Delta}%, targetDay={TargetDay}",
                            account[..10], staticAttoCircles, demurragedAttoCircles, pctDelta.ToString("F2"), targetDay);
                    }

                    balance = demurragedAttoCircles.ToString(CultureInfo.InvariantCulture);
                }
                else if (type == "demurraged")
                {
                    // Guard: corrupted data where lastActivity predates Circles epoch
                    if (lastActivity < InflationDayZeroUnix)
                    {
                        _logger.LogWarning("[LoadGraph] lastActivity {LastActivity} < InflationDayZero {Epoch} for account={Account}, token={Token} — skipping (corrupted data)",
                            lastActivity, InflationDayZeroUnix, account[..10], tokenAddress[..10]);
                        continue;
                    }

                    // Apply demurrage from lastActivity to target timestamp
                    var inflationaryBalance = BigInteger.Parse(balance);
                    var lastActivityDay = (ulong)(lastActivity - InflationDayZeroUnix) / SecondsPerDay;
                    var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;

                    if (daysDelta > 0)
                    {
                        // Apply demurrage: balance * gamma^daysDelta
                        var demurragedBalance = CirclesConverter.InflationaryToDemurrage(inflationaryBalance, daysDelta);

                        if (inflationaryBalance > 0)
                        {
                            var pctDelta = 100.0 * (1.0 - (double)demurragedBalance / (double)inflationaryBalance);
                            _logger.LogDebug("[LoadGraph] Demurrage flow: acct={Account}, raw={Raw}, adj={Adjusted}, delta={Delta}%, daysDiff={DaysDiff}",
                                account[..10], inflationaryBalance, demurragedBalance, pctDelta.ToString("F2"), daysDelta);
                        }

                        balance = demurragedBalance.ToString(CultureInfo.InvariantCulture);
                    }
                    // If no delta, balance stays as-is (already in correct form)
                }

                // Apply safety margin in live mode to prevent stale-balance reverts
                if (applyMargin && balance != "0")
                {
                    var raw = BigInteger.Parse(balance);
                    var margined = (BigInteger)((double)raw * _settings.DemurrageSafetyMargin);
                    balance = margined.ToString(CultureInfo.InvariantCulture);
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

            sw.Stop();
            OnQueryCompleted?.Invoke("balances", sw.Elapsed);
            return results;
        }

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
        {
            // We now only have one trust query that includes wrap tokens
            var trustQuery = LoadQueryFromResource("trustQuery.sql");
            var results = new List<(string Truster, string Trustee, int Limit)>();

            var sw = Stopwatch.StartNew();
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

            sw.Stop();
            OnQueryCompleted?.Invoke("trust", sw.Elapsed);
            return results;
        }

        // Load groups
        public IEnumerable<string> LoadGroups()
        {
            var groupQuery = LoadQueryFromResource("groupQuery.sql");
            var results = new List<string>();

            var sw = Stopwatch.StartNew();
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

            sw.Stop();
            OnQueryCompleted?.Invoke("groups", sw.Elapsed);
            return results;
        }

        // Load group trust relationships
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
        {
            var groupTrustQuery = LoadQueryFromResource("groupTrustQuery.sql");
            var results = new List<(string GroupAddress, string TrustedToken)>();

            var sw = Stopwatch.StartNew();
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

            sw.Stop();
            OnQueryCompleted?.Invoke("group_trusts", sw.Elapsed);
            return results;
        }

        // Load consented flow flags (latest per avatar)
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
        {
            var query = LoadQueryFromResource("consentedFlowQuery.sql");
            var results = new List<(string Avatar, bool HasConsentedFlow)>();

            var sw = Stopwatch.StartNew();
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

            sw.Stop();
            OnQueryCompleted?.Invoke("consented_flow", sw.Elapsed);
            return results;
        }
    }
}