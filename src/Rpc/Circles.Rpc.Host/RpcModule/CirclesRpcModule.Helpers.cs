using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host;

/// <summary>
/// Helper methods and utilities for CirclesRpcModule.
/// Contains static helper methods and schema-related operations.
/// </summary>
public partial class CirclesRpcModule
{
    /// <summary>
    /// Removes JSON-LD fields (@type, @context) from a profile JsonElement.
    /// The remote implementation doesn't include these semantic web fields in responses.
    /// </summary>
    private static JsonElement? StripJsonLdFields(JsonElement? profile)
    {
        if (profile == null || profile.Value.ValueKind != JsonValueKind.Object)
        {
            return profile;
        }

        var dict = new Dictionary<string, JsonElement>();

        foreach (var prop in profile.Value.EnumerateObject())
        {
            // Skip JSON-LD semantic fields only
            if (prop.Name == "@type" || prop.Name == "@context")
            {
                continue;
            }

            dict[prop.Name] = prop.Value;
        }

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
    }

    /// <summary>
    /// Escapes ILIKE/LIKE special characters (%, _, \) in user-provided input
    /// before appending a trailing wildcard. Prevents wildcard injection.
    /// </summary>
    private static string EscapeLikePattern(string input)
        => input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Validates that an address is a well-formed Ethereum address (0x + 40 hex chars)
    /// and returns it lowercased.
    /// </summary>
    private static string ValidateAndNormalizeAddress(string address, string parameterName = "address")
    {
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException($"{parameterName} must not be empty.");

        if (!Regex.IsMatch(address, @"^0x[0-9a-fA-F]{40}$"))
            throw new ArgumentException($"{parameterName} is not a valid Ethereum address: {address}");

        return address.ToLowerInvariant();
    }

    /// <summary>
    /// Validates that an identifier contains only safe characters (letters, digits, underscore).
    /// </summary>
    private static string ValidateIdentifier(string identifier, string identifierType)
    {
        if (identifier == null)
        {
            throw new ArgumentNullException(nameof(identifier), $"{identifierType} cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException($"{identifierType} cannot be empty or whitespace.");
        }

        if (!Regex.IsMatch(identifier, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
        {
            throw new ArgumentException($"{identifierType} contains invalid characters. Only letters, digits, and underscores are allowed, and it must start with a letter or underscore.");
        }

        return identifier;
    }

    public async Task<HealthResponse> GetHealth()
    {
        string databaseStatus;
        string indexStatus;
        string overallStatus;

        try
        {
            await using var connection = await CreateConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
            databaseStatus = "connected";
        }
        catch (Exception)
        {
            databaseStatus = "disconnected";
            indexStatus = "unknown";
            overallStatus = "unhealthy";
            return new HealthResponse(
                Status: overallStatus,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Database: databaseStatus,
                Index: indexStatus
            );
        }

        // Check indexer synchronization
        try
        {
            // Get latest block from database
            long? lastPersisted;
            await using var connection = await CreateConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT MAX(\"blockNumber\") as block_number FROM \"System_Block\"", connection);
            var result = await command.ExecuteScalarAsync();
            lastPersisted = result is long longResult ? longResult : 0;

            // Get latest block from Nethermind
            long blockHead = 0;
            if (_nethermindRpcClient != null)
            {
                blockHead = await _nethermindRpcClient.GetLatestBlockNumber();
            }

            if (blockHead - lastPersisted >= 3)
            {
                indexStatus = "lagging";
                overallStatus = "unhealthy";
            }
            else
            {
                indexStatus = "synchronized";
                overallStatus = "healthy";
            }
        }
        catch (Exception)
        {
            indexStatus = "unknown";
            overallStatus = "unhealthy";
        }

        return new HealthResponse(
            Status: overallStatus,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Database: databaseStatus,
            Index: indexStatus
        );
    }

    public Task<TableNamespace[]> GetTables()
    {
        var namespaces = new List<TableNamespace>();

        foreach (var schema in SchemaProvider.AllSchemas)
        {
            var schemaNamespaces = schema.Tables.GroupBy(o => o.Key.Namespace);

            foreach (var @namespace in schemaNamespaces)
            {
                if (@namespace.Key == "System")
                {
                    continue;
                }

                var tableDefinitions = new List<TableDefinition>();

                foreach (var table in @namespace)
                {
                    var topic = "0x" + Convert.ToHexStringLower(table.Value.Topic);

                    var columns = new List<TableColumn>();
                    foreach (var column in table.Value.Columns)
                    {
                        var columnDto = new TableColumn(column.Column, column.Type.ToString());
                        columns.Add(columnDto);
                    }

                    var tableDto = new TableDefinition(table.Key.Table, topic, [.. columns]);
                    tableDefinitions.Add(tableDto);
                }

                var namespaceDto = new TableNamespace(@namespace.Key, [.. tableDefinitions]);
                namespaces.Add(namespaceDto);
            }
        }

        return Task.FromResult(namespaces.ToArray());
    }
}
