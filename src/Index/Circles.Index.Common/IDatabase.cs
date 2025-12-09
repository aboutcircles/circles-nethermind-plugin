using System.Data;
using System.Text.RegularExpressions;
using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public interface IDatabaseUtils
{
    public IDatabaseSchema Schema { get; }

    public string QuoteIdentifier(string identifier)
    {
        if (!Regex.IsMatch(identifier, @"^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException("Invalid identifier");
        }

        return $"\"{identifier}\"";
    }

    IDbDataParameter CreateParameter(string? name, object? value);
}

public interface IReadonlyDatabase : IDatabaseUtils
{
    DatabaseQueryResult Select(ParameterizedSql select);
}

public interface IDatabase : IReadonlyDatabase
{
    void Migrate();
    Task DeleteAllGreaterOrEqualBlock(long reorgAt);
    Task DeleteFromTablesGreaterOrEqualBlock(long reorgAt, IEnumerable<string> tableNames);
    Task WriteBatch(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);

    /// <summary>
    /// Writes a batch using INSERT ... ON CONFLICT DO NOTHING.
    /// Slower than WriteBatch (COPY) but handles duplicate keys gracefully.
    /// </summary>
    Task WriteBatchWithUpsert(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);

    long? LatestBlock();
    long? FirstGap();
    IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count);

    /// <summary>
    /// Gets the maximum block number across all event tables (excluding System tables).
    /// Returns a dictionary of table name -> max block number.
    /// Used to detect inconsistencies after partial writes.
    /// </summary>
    IDictionary<string, long> GetMaxBlockPerTable();

    /// <summary>
    /// Finds the minimum of the maximum blocks across all event tables.
    /// This represents the safe resume point after a crash - any block above this
    /// may have partial data and should be re-indexed.
    /// Returns null if no data exists.
    /// </summary>
    long? GetSafeResumeBlock();
}