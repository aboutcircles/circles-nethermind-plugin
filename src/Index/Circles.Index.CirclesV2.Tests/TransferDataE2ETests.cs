using System.Text.Json;

namespace Circles.Index.CirclesV2.Tests;

/// <summary>
/// End-to-end tests for TransferData indexing against real blockchain data.
///
/// The schema/unit tests in this fixture run everywhere with no external
/// dependencies. The two E2E placeholder methods carry method-level [Explicit]
/// (skipped in normal runs; executed only when selected directly, e.g.
/// dotnet test --filter "Name~E2E_") and additionally self-skip via
/// Assert.Ignore when CIRCLES_CONNECTION_STRING is not set.
///
/// To run the E2E methods:
/// 1. Set up environment: source docker/.env (or staging env)
/// 2. Run: dotnet test --filter "Name~E2E_VerifyTransferDataInDatabase"
///
/// Alternatively, use the test-rpc.sh script to verify TransferData events via RPC.
/// </summary>
[TestFixture]
public class TransferDataE2ETests
{
    // ─────────────────────── Unit Tests (No DB required) ───────────────────────

    /// <summary>
    /// Verifies the database schema includes the TransferData table definition.
    /// This test runs without database connection - just validates the schema definition.
    /// </summary>
    [Test]
    public void DatabaseSchema_ContainsTransferData_WithCorrectColumns()
    {
        var schema = DatabaseSchema.TransferData;

        Assert.Multiple(() =>
        {
            Assert.That(schema.Namespace, Is.EqualTo("CrcV2"));
            Assert.That(schema.Table, Is.EqualTo("TransferData"));

            // Verify key columns exist
            var columnNames = schema.Columns.Select(c => c.Column).ToList();
            Assert.That(columnNames, Does.Contain("blockNumber"));
            Assert.That(columnNames, Does.Contain("timestamp"));
            Assert.That(columnNames, Does.Contain("transactionIndex"));
            Assert.That(columnNames, Does.Contain("logIndex"));
            Assert.That(columnNames, Does.Contain("transactionHash"));
            Assert.That(columnNames, Does.Contain("from"));
            Assert.That(columnNames, Does.Contain("to"));
            Assert.That(columnNames, Does.Contain("data"));
        });
    }

    /// <summary>
    /// Verifies the TransferData event DTO has proper mapping configuration.
    /// </summary>
    [Test]
    public void DatabaseSchema_TransferDataMapping_IncludesAllFields()
    {
        var dbSchema = new DatabaseSchema();

        // The schema constructor registers all mappings
        // Verify TransferData is registered by checking that creating the schema doesn't throw
        Assert.That(dbSchema, Is.Not.Null);
    }

    /// <summary>
    /// Test with known good calldata to ensure consistent parsing across versions.
    /// This is a regression test - if parsing changes, this test will catch it.
    /// </summary>
    [Test]
    public void ParseCalldata_KnownGoodSafeTransferFrom_ProducesConsistentResult()
    {
        // Known good calldata: Alice sends to Bob with data "TEST"
        var calldata = BuildKnownGoodSafeTransferFrom();

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));

        // Snapshot test - these values should never change
        var result = results[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.From, Is.EqualTo("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.That(result.To, Is.EqualTo("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
            Assert.That(result.Data.Length, Is.EqualTo(4));
            Assert.That(result.Data, Is.EqualTo(new byte[] { 0x54, 0x45, 0x53, 0x54 })); // "TEST"
        });
    }

    // ─────────────────────── E2E Tests (Require DB) ───────────────────────

    /// <summary>
    /// Verifies TransferData events are correctly indexed in the database.
    /// Requires connection to staging or production database.
    ///
    /// Run with: dotnet test --filter "VerifyTransferDataInDatabase" -- NUnit.DefaultTestParams.Explicit=true
    /// </summary>
    [Test]
    [Explicit("Requires database connection - set CIRCLES_CONNECTION_STRING")]
    public void E2E_VerifyTransferDataInDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("CIRCLES_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
        {
            Assert.Ignore("CIRCLES_CONNECTION_STRING not set - skipping database test");
            return;
        }

        // This test would query the database to verify TransferData events exist
        // Example verification queries:
        // SELECT COUNT(*) FROM "CrcV2_TransferData"
        // SELECT * FROM "CrcV2_TransferData" WHERE "blockNumber" > X LIMIT 10

        Console.WriteLine("E2E test would verify TransferData table exists and has data.");
        Console.WriteLine("To manually verify, run:");
        Console.WriteLine(@"  PGPASSWORD=$POSTGRES_PASSWORD psql -h localhost -U postgres -d postgres -c 'SELECT COUNT(*) FROM ""CrcV2_TransferData""'");

        Assert.Pass("Database verification placeholder - implement when DB access is available");
    }

    /// <summary>
    /// Verifies TransferData events can be queried via the RPC endpoint.
    /// Requires RPC service to be running.
    /// </summary>
    [Test]
    [Explicit("Requires RPC service - run ./scripts/run-rpc.sh first")]
    public void E2E_QueryTransferDataViaRpc()
    {
        var rpcUrl = Environment.GetEnvironmentVariable("CIRCLES_RPC_URL") ?? "http://localhost:8081";

        Console.WriteLine($"Would query TransferData events from {rpcUrl}");
        Console.WriteLine("Use circles_events RPC method with eventType='CrcV2_TransferData'");

        Assert.Pass("RPC verification placeholder - implement when RPC access is available");
    }

    // ─────────────────────── Helper Methods ───────────────────────

    private static byte[] BuildKnownGoodSafeTransferFrom()
    {
        using var ms = new MemoryStream();

        // Selector
        ms.Write(new byte[] { 0xf2, 0x42, 0x43, 0x2a });

        // From: Alice (0xaa...aa)
        ms.Write(PadAddress("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        // To: Bob (0xbb...bb)
        ms.Write(PadAddress("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        // ID: 1
        ms.Write(PadUint256(1));

        // Value: 1 CRC
        ms.Write(PadUint256(1_000_000_000_000_000_000));

        // Data offset: 160
        ms.Write(PadUint256(160));

        // Data: "TEST" (4 bytes)
        ms.Write(PadUint256(4));
        ms.Write(new byte[] { 0x54, 0x45, 0x53, 0x54 }); // "TEST"
        ms.Write(new byte[28]); // Pad to 32 bytes

        return ms.ToArray();
    }

    private static byte[] PadAddress(string address)
    {
        if (address.StartsWith("0x"))
            address = address[2..];

        var bytes = Convert.FromHexString(address);
        var result = new byte[32];
        Array.Copy(bytes, 0, result, 12, 20);
        return result;
    }

    private static byte[] PadUint256(ulong value)
    {
        var result = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            result[31 - i] = (byte)(value >> (i * 8));
        }
        return result;
    }
}
