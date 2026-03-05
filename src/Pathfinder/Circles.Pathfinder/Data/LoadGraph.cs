using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Circles.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Pathfinder.Data
{
    /// <summary>
    /// Result of <see cref="LoadGraph.LoadAll"/> — all 6 ILoadGraph queries executed
    /// in a single REPEATABLE READ transaction.
    /// </summary>
    public sealed record LoadAllResult(
        IReadOnlyList<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> Balances,
        IReadOnlyList<(string Truster, string Trustee, int Limit)> Trust,
        IReadOnlyList<string> Groups,
        IReadOnlyList<(string GroupAddress, string TrustedToken)> GroupTrusts,
        IReadOnlyList<(string Avatar, bool HasConsentedFlow)> ConsentedFlags,
        IReadOnlyList<string> RegisteredAvatars,
        IReadOnlyList<(string WrapperAddress, string UnderlyingAvatar)> WrapperMappings
    );

    public interface ILoadGraph
    {
        IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2Balances();

        IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust();

        IEnumerable<string> LoadGroups();
        IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts();

        IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags();

        IEnumerable<string> LoadRegisteredAvatars();

        IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings();
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

        private static readonly ConcurrentDictionary<string, string> _queryCache = new();

        private static string LoadQueryFromResource(string resourceName)
        {
            return _queryCache.GetOrAdd(resourceName, static name =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fullResourceName = $"Circles.Pathfinder.Data.Queries.{name}";

                using var stream = assembly.GetManifestResourceStream(fullResourceName);
                if (stream == null)
                {
                    throw new FileNotFoundException($"SQL query resource not found: {fullResourceName}");
                }

                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });
        }

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2Balances()
        {
            var balanceQuery = LoadQueryFromResource("balanceQuery.sql");
            var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>(50_000);

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(balanceQuery, connection);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
            using var reader = command.ExecuteReader();

            var ctx = DemurrageCalculator.CreateContext(_settings);

            while (reader.Read())
            {
                var balanceStr = reader.GetString(0);
                var account = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var lastActivity = reader.GetInt64(3);
                var isWrapped = reader.GetBoolean(4);
                var type = reader.GetString(5);

                var balanceValue = BigInteger.Parse(balanceStr);
                var adjusted = DemurrageCalculator.Apply(
                    balanceValue, lastActivity, type == "static", ctx,
                    _logger, account.Length >= 10 ? account[..10] : account);

                if (adjusted == null) continue;

                results.Add((adjusted.Value.ToString(CultureInfo.InvariantCulture),
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
            var trustQuery = LoadQueryFromResource("trustQuery.sql");
            var results = new List<(string Truster, string Trustee, int Limit)>(200_000);

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
            var results = new List<string>(1_000);

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupQuery, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            command.Parameters.AddWithValue("$1", _settings.GroupRouterAddress);
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
            var results = new List<(string GroupAddress, string TrustedToken)>(5_000);

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(groupTrustQuery, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            command.Parameters.AddWithValue("$1", _settings.GroupRouterAddress);
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

        // ──────────────────────────────────────────────────────────────────
        // Batched load: all 6 ILoadGraph queries in a single connection
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Executes all 6 ILoadGraph queries on a single REPEATABLE READ connection,
        /// eliminating 5 extra connection open/close round-trips per graph refresh.
        /// </summary>
        public LoadAllResult LoadAll()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var tx = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);

            var balances = LoadV2BalancesInternal(connection, tx);
            var trust = LoadV2TrustInternal(connection, tx);
            var groups = LoadGroupsInternal(connection, tx);
            var groupTrusts = LoadGroupTrustsInternal(connection, tx);
            var consentedFlags = LoadConsentedFlowFlagsInternal(connection, tx);
            var registeredAvatars = LoadRegisteredAvatarsInternal(connection, tx);
            var wrapperMappings = LoadWrapperMappingsInternal(connection, tx);

            tx.Commit();

            return new LoadAllResult(balances, trust, groups, groupTrusts, consentedFlags, registeredAvatars, wrapperMappings);
        }

        private List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
            LoadV2BalancesInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var balanceQuery = LoadQueryFromResource("balanceQuery.sql");
            var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>(50_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(balanceQuery, connection, tx);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
            using var reader = command.ExecuteReader();

            var ctx = DemurrageCalculator.CreateContext(_settings);

            while (reader.Read())
            {
                var balanceStr = reader.GetString(0);
                var account = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var lastActivity = reader.GetInt64(3);
                var isWrapped = reader.GetBoolean(4);
                var type = reader.GetString(5);

                var balanceValue = BigInteger.Parse(balanceStr);
                var adjusted = DemurrageCalculator.Apply(
                    balanceValue, lastActivity, type == "static", ctx);

                if (adjusted == null) continue;

                results.Add((adjusted.Value.ToString(CultureInfo.InvariantCulture),
                    AddressIdPool.IdOf(account.ToLowerInvariant()),
                    AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                    isWrapped,
                    type == "static"));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("balances", sw.Elapsed);
            return results;
        }

        private List<(string Truster, string Trustee, int Limit)>
            LoadV2TrustInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var trustQuery = LoadQueryFromResource("trustQuery.sql");
            var results = new List<(string Truster, string Trustee, int Limit)>(200_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(trustQuery, connection, tx);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetString(1), 100));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("trust", sw.Elapsed);
            return results;
        }

        private List<string> LoadGroupsInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var groupQuery = LoadQueryFromResource("groupQuery.sql");
            var results = new List<string>(1_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(groupQuery, connection, tx);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            command.Parameters.AddWithValue("$1", _settings.GroupRouterAddress);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("groups", sw.Elapsed);
            return results;
        }

        private List<(string GroupAddress, string TrustedToken)>
            LoadGroupTrustsInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var groupTrustQuery = LoadQueryFromResource("groupTrustQuery.sql");
            var results = new List<(string GroupAddress, string TrustedToken)>(5_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(groupTrustQuery, connection, tx);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            command.Parameters.AddWithValue("$1", _settings.GroupRouterAddress);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetString(1)));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("group_trusts", sw.Elapsed);
            return results;
        }

        private List<(string Avatar, bool HasConsentedFlow)>
            LoadConsentedFlowFlagsInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var query = LoadQueryFromResource("consentedFlowQuery.sql");
            var results = new List<(string Avatar, bool HasConsentedFlow)>(10_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(query, connection, tx);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var avatar = reader.GetString(0);
                var flag = (byte[])reader.GetValue(1);
                bool hasConsented = flag.Length >= 32 && (flag[31] & 0x01) != 0;
                results.Add((avatar, hasConsented));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("consented_flow", sw.Elapsed);
            return results;
        }

        private List<string> LoadRegisteredAvatarsInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var results = new List<string>(20_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(
                "SELECT avatar FROM \"V_CrcV2_Avatars\"", connection, tx);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("registered_avatars", sw.Elapsed);
            return results;
        }

        private List<(string WrapperAddress, string UnderlyingAvatar)>
            LoadWrapperMappingsInternal(NpgsqlConnection connection, NpgsqlTransaction tx)
        {
            var query = LoadQueryFromResource("wrapperMappingQuery.sql");
            var results = new List<(string WrapperAddress, string UnderlyingAvatar)>(10_000);

            var sw = Stopwatch.StartNew();

            using var command = new NpgsqlCommand(query, connection, tx);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetString(1)));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("wrapper_mappings", sw.Elapsed);
            return results;
        }

        // ──────────────────────────────────────────────────────────────────
        // Full-state + delta methods (used by incremental update orchestrator)
        // Not part of ILoadGraph — only called by NetworkStateUpdaterService.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Expose connection string so callers can open a shared REPEATABLE READ
        /// transaction across multiple full-state queries.
        /// </summary>
        public string ConnectionString => _connectionString;

        public IEnumerable<(string Balance, string Account, string TokenAddress, long LastActivity)>
            LoadRawBalances() => LoadRawBalances(null, null);

        public IEnumerable<(string Balance, string Account, string TokenAddress, long LastActivity)>
            LoadRawBalances(NpgsqlConnection? sharedConn, NpgsqlTransaction? sharedTx)
        {
            var query = LoadQueryFromResource("balanceFullStateQuery.sql");
            var results = new List<(string, string, string, long)>();

            var sw = Stopwatch.StartNew();
            var conn = sharedConn ?? new NpgsqlConnection(_connectionString);
            try
            {
                if (sharedConn == null) conn.Open();

                using var command = new NpgsqlCommand(query, conn, sharedTx);
                command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    results.Add((
                        reader.GetString(0),  // balance
                        reader.GetString(1),  // account
                        reader.GetString(2),  // tokenAddress
                        reader.GetInt64(3)    // lastActivity
                    ));
                }
            }
            finally
            {
                if (sharedConn == null) conn.Dispose();
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("raw_balances", sw.Elapsed);
            return results;
        }

        public IEnumerable<(string Truster, string Trustee, long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex)>
            LoadRawTrusts() => LoadRawTrusts(null, null);

        public IEnumerable<(string Truster, string Trustee, long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex)>
            LoadRawTrusts(NpgsqlConnection? sharedConn, NpgsqlTransaction? sharedTx)
        {
            var query = LoadQueryFromResource("trustFullStateQuery.sql");
            var results = new List<(string, string, long, long, int, int)>();

            var sw = Stopwatch.StartNew();
            var conn = sharedConn ?? new NpgsqlConnection(_connectionString);
            try
            {
                if (sharedConn == null) conn.Open();

                using var command = new NpgsqlCommand(query, conn, sharedTx);
                command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    results.Add((
                        reader.GetString(0),  // truster
                        reader.GetString(1),  // trustee
                        reader.GetInt64(2),   // expiryTime
                        reader.GetInt64(3),   // blockNumber
                        reader.GetInt32(4),   // transactionIndex
                        reader.GetInt32(5)    // logIndex
                    ));
                }
            }
            finally
            {
                if (sharedConn == null) conn.Dispose();
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("raw_trusts", sw.Elapsed);
            return results;
        }

        public IEnumerable<(string Avatar, string Type)> LoadAllAvatars()
            => LoadAllAvatars(null, null);

        public IEnumerable<(string Avatar, string Type)> LoadAllAvatars(
            NpgsqlConnection? sharedConn, NpgsqlTransaction? sharedTx)
        {
            var results = new List<(string, string)>();

            var sw = Stopwatch.StartNew();
            var conn = sharedConn ?? new NpgsqlConnection(_connectionString);
            try
            {
                if (sharedConn == null) conn.Open();

                using var command = new NpgsqlCommand("SELECT avatar, type FROM \"V_CrcV2_Avatars\"", conn, sharedTx);
                command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    results.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            finally
            {
                if (sharedConn == null) conn.Dispose();
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("all_avatars", sw.Elapsed);
            return results;
        }

        public IEnumerable<(long Timestamp, string From, string To, string TokenAddress, string Value)>
            LoadTransfersSince(long lastBlock)
        {
            var query = LoadQueryFromResource("balanceDeltaQuery.sql");
            var results = new List<(long, string, string, string, string)>();

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((
                    reader.GetInt64(0),   // timestamp
                    reader.GetString(1),  // from
                    reader.GetString(2),  // to
                    reader.GetString(3),  // tokenAddress
                    reader.GetString(4)   // value
                ));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("delta_transfers", sw.Elapsed);
            return results;
        }

        public IEnumerable<(long BlockNumber, int TxIndex, int LogIndex, string Truster, string Trustee, long ExpiryTime)>
            LoadTrustEventsSince(long lastBlock)
        {
            var query = LoadQueryFromResource("trustDeltaQuery.sql");
            var results = new List<(long, int, int, string, string, long)>();

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((
                    reader.GetInt64(0),   // blockNumber
                    reader.GetInt32(1),   // transactionIndex
                    reader.GetInt32(2),   // logIndex
                    reader.GetString(3),  // truster
                    reader.GetString(4),  // trustee
                    reader.GetInt64(5)    // expiryTime
                ));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("delta_trusts", sw.Elapsed);
            return results;
        }

        public IEnumerable<(string Avatar, string Type)> LoadNewAvatarsSince(long lastBlock)
        {
            var query = LoadQueryFromResource("newAvatarsQuery.sql");
            var results = new List<(string, string)>();

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetString(1)));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("delta_avatars", sw.Elapsed);
            return results;
        }

        public IEnumerable<string> LoadStoppedAvatars() => LoadStoppedAvatars(null, null);

        public IEnumerable<string> LoadStoppedAvatars(NpgsqlConnection? sharedConn, NpgsqlTransaction? sharedTx)
        {
            var query = LoadQueryFromResource("stoppedAvatarsQuery.sql");
            var results = new List<string>();

            var sw = Stopwatch.StartNew();
            var conn = sharedConn ?? new NpgsqlConnection(_connectionString);
            try
            {
                if (sharedConn == null) conn.Open();

                using var command = new NpgsqlCommand(query, conn, sharedTx);
                command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    results.Add(reader.GetString(0));
            }
            finally
            {
                if (sharedConn == null) conn.Dispose();
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("stopped_avatars", sw.Elapsed);
            return results;
        }

        public IEnumerable<string> LoadStoppedAvatarsSince(long lastBlock)
        {
            var query = LoadQueryFromResource("stoppedAvatarsDeltaQuery.sql");
            var results = new List<string>();

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            using var reader = command.ExecuteReader();

            while (reader.Read())
                results.Add(reader.GetString(0));

            sw.Stop();
            OnQueryCompleted?.Invoke("delta_stopped_avatars", sw.Elapsed);
            return results;
        }

        /// <summary>
        /// Load complete balances for specific avatar addresses.
        /// Used to backfill balances when new avatars are detected during incremental updates.
        /// </summary>
        public IEnumerable<(string Balance, string Account, string TokenAddress, long LastActivity)>
            LoadBalancesForAvatars(IEnumerable<string> avatarAddresses)
        {
            var avatarList = avatarAddresses.ToArray();
            if (avatarList.Length == 0) return Array.Empty<(string, string, string, long)>();

            var query = LoadQueryFromResource("avatarBalancesQuery.sql");
            var results = new List<(string, string, string, long)>();

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderBalanceTimeoutSeconds;
            command.Parameters.AddWithValue("avatars", avatarList);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),  // balance
                    reader.GetString(1),  // account
                    reader.GetString(2),  // tokenAddress
                    reader.GetInt64(3)    // lastActivity
                ));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("avatar_balances_backfill", sw.Elapsed);
            return results;
        }

        /// <summary>
        /// Load the block hash for a specific block number.
        /// Used for reorg detection (D10): if the stored hash no longer matches,
        /// a chain reorganization occurred and a full refresh is needed.
        /// </summary>
        public string? LoadBlockHash(long blockNumber)
        {
            var query = LoadQueryFromResource("blockHashQuery.sql");

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = 10;
            command.Parameters.AddWithValue("blockNumber", blockNumber);
            var result = command.ExecuteScalar();

            sw.Stop();
            OnQueryCompleted?.Invoke("block_hash", sw.Elapsed);
            return result != null && result != DBNull.Value ? result.ToString() : null;
        }

        public long LoadMaxBlockTimestamp() => LoadMaxBlockTimestamp(null, null);

        public long LoadMaxBlockTimestamp(NpgsqlConnection? sharedConn, NpgsqlTransaction? sharedTx)
        {
            var query = LoadQueryFromResource("maxBlockTimestampQuery.sql");

            var sw = Stopwatch.StartNew();
            var conn = sharedConn ?? new NpgsqlConnection(_connectionString);
            try
            {
                if (sharedConn == null) conn.Open();

                using var command = new NpgsqlCommand(query, conn, sharedTx);
                command.CommandTimeout = 30;
                var result = command.ExecuteScalar();

                sw.Stop();
                OnQueryCompleted?.Invoke("max_block_timestamp", sw.Elapsed);
                return result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0;
            }
            finally
            {
                if (sharedConn == null) conn.Dispose();
            }
        }

        // Load consented flow flags (latest per avatar)
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
        {
            var query = LoadQueryFromResource("consentedFlowQuery.sql");
            var results = new List<(string Avatar, bool HasConsentedFlow)>(10_000);

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

        public IEnumerable<string> LoadRegisteredAvatars()
        {
            var results = new List<string>(20_000);

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(
                "SELECT avatar FROM \"V_CrcV2_Avatars\"", connection);
            command.CommandTimeout = _settings.PathfinderTrustTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("registered_avatars", sw.Elapsed);
            return results;
        }

        public IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings()
        {
            var query = LoadQueryFromResource("wrapperMappingQuery.sql");
            var results = new List<(string WrapperAddress, string UnderlyingAvatar)>(10_000);

            var sw = Stopwatch.StartNew();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = new NpgsqlCommand(query, connection);
            command.CommandTimeout = _settings.PathfinderGroupTimeoutSeconds;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetString(1)));
            }

            sw.Stop();
            OnQueryCompleted?.Invoke("wrapper_mappings", sw.Elapsed);
            return results;
        }
    }
}
