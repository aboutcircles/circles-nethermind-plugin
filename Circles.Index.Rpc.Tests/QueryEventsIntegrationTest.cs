using Circles.Index.CirclesV2;
using Circles.Index.Common;
using Circles.Index.Postgres;
using Circles.Index.Rpc;
using Npgsql;

namespace Circles.Index.Rpc.Tests;

/// <summary>
/// Integration test for QueryEvents that connects to a real database.
/// Set the CIRCLES_DB_CONNECTION_STRING environment variable to run this test.
/// Example: export CIRCLES_DB_CONNECTION_STRING="Host=localhost;Database=circles;Username=postgres;Password=postgres"
/// </summary>
[TestFixture]
public class QueryEventsIntegrationTest
{
    private string? _connectionString;
    
    [SetUp]
    public void Setup()
    {
        _connectionString = Environment.GetEnvironmentVariable("CIRCLES_DB_CONNECTION_STRING");
        if (string.IsNullOrEmpty(_connectionString))
        {
            Assert.Ignore("CIRCLES_DB_CONNECTION_STRING environment variable not set. Skipping integration test.");
        }
    }

    [Test]
    public void TestMultipleStreamCompletedEventsFromRealDatabase()
    {
        // This is the transaction hash from the bug report
        var transactionHash = "0xabb47191ab8e30baaff43467c8f28ebf382bce0da0bbac24164c63e0fb6239d8";
        
        var schema = new DatabaseSchema();
        var db = new PostgresDb(_connectionString!, schema);
        
        var settings = new Settings
        {
            IndexReadonlyDbConnectionString = _connectionString
        };
        
        var context = new Context(
            null!, // NethermindApi not needed for query
            null!, // Logger not needed
            settings,
            db,
            db,
            Array.Empty<ILogParser>(),
            null! // Sink not needed
        );

        var queryEvents = new QueryEvents(context);

        // Query for StreamCompleted events with the specific transaction hash
        var events = queryEvents.CirclesEvents(
            null,
            38000000,
            null,
            ["CrcV2_StreamCompleted"],
            [
                new FilterPredicateDto
                {
                    Type = "FilterPredicate",
                    FilterType = "Equals",
                    Column = "transactionHash",
                    Value = transactionHash
                }
            ],
            false
        );

        Console.WriteLine($"Returned {events.Length} events for transaction {transactionHash}");
        
        foreach (var evt in events)
        {
            Console.WriteLine($"  Event: {evt.Event}");
            Console.WriteLine($"    Block: {evt.Values["blockNumber"]}");
            Console.WriteLine($"    Tx Index: {evt.Values["transactionIndex"]}");
            Console.WriteLine($"    Log Index: {evt.Values["logIndex"]}");
            Console.WriteLine($"    Batch Index: {evt.Values["batchIndex"]}");
            Console.WriteLine($"    From: {evt.Values["from"]}");
            Console.WriteLine($"    To: {evt.Values["to"]}");
            Console.WriteLine($"    Amount: {evt.Values["amount"]}");
            Console.WriteLine();
        }

        // The bug report says there should be >10 events
        Assert.That(events.Length, Is.GreaterThan(1), 
            $"Should return multiple StreamCompleted events for transaction {transactionHash}");
    }

    [Test]
    public void TestDirectDatabaseQuery()
    {
        // This test directly queries the database to see what's actually stored
        var transactionHash = "0xabb47191ab8e30baaff43467c8f28ebf382bce0da0bbac24164c63e0fb6239d8";
        
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ""blockNumber"", ""transactionIndex"", ""logIndex"", ""batchIndex"", ""from"", ""to"", ""amount""
            FROM ""CrcV2_StreamCompleted""
            WHERE ""transactionHash"" = @txHash
            ORDER BY ""batchIndex""
        ";
        cmd.Parameters.AddWithValue("@txHash", transactionHash);

        using var reader = cmd.ExecuteReader();
        var count = 0;
        
        Console.WriteLine($"Direct database query results for transaction {transactionHash}:");
        while (reader.Read())
        {
            count++;
            Console.WriteLine($"  Row {count}:");
            Console.WriteLine($"    Block: {reader.GetInt64(0)}");
            Console.WriteLine($"    Tx Index: {reader.GetInt64(1)}");
            Console.WriteLine($"    Log Index: {reader.GetInt64(2)}");
            Console.WriteLine($"    Batch Index: {reader.GetInt64(3)} (Type: {reader.GetFieldType(3).Name})");
            Console.WriteLine($"    From: {reader.GetString(4)}");
            Console.WriteLine($"    To: {reader.GetString(5)}");
            Console.WriteLine($"    Amount: {reader.GetValue(6)}");
            Console.WriteLine();
        }

        Console.WriteLine($"Total rows in database: {count}");
        Assert.That(count, Is.GreaterThan(1), 
            $"Database should contain multiple StreamCompleted events for transaction {transactionHash}");
    }
}
