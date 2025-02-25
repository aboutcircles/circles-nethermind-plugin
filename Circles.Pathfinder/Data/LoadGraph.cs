using Npgsql;
using System.Reflection;

namespace Circles.Pathfinder.Data
{
    // TODO: Use CirclesQuery<T> and remove the Npgsql dependency
    public interface ILoadGraph
    {
        IEnumerable<(string Balance, string Account, string TokenAddress)> LoadV2Balances();
        IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust();
        bool WithWrap { get; }
    }

    public class LoadGraph : ILoadGraph
    {
        private readonly string _connectionString;
        private readonly bool _withWrap;

        public bool WithWrap => _withWrap;

        public LoadGraph(string connectionString, bool withWrap = false)
        {
            _connectionString = connectionString;
            _withWrap = withWrap;
        }

        private string LoadQueryFromResource(string baseFileName)
        {
            // Determine which SQL file to use based on _withWrap flag
            string fileName = _withWrap 
                ? $"{baseFileName}Wrap.sql" 
                : $"{baseFileName}.sql";
            
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"Circles.Pathfinder.Data.Queries.{fileName}";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"SQL query resource not found: {resourceName}");
            }
            
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public IEnumerable<(string Balance, string Account, string TokenAddress)> LoadV2Balances()
        {
            // Load the appropriate query based on _withWrap flag
            var balanceQuery = LoadQueryFromResource("balanceQuery");

            using var connection = new NpgsqlConnection(_connectionString);
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
            // Load the appropriate query based on _withWrap flag
            var trustQuery = LoadQueryFromResource("trustQuery");

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