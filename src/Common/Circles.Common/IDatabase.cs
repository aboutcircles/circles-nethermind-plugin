using System.Data;
using System.Text.RegularExpressions;
using Nethermind.Core.Crypto;

namespace Circles.Common;

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

    /// <summary>
    /// Deletes all data from all tables from the specified block onwards.
    /// Used by REINDEX_FROM_BLOCK and reorg handling.
    /// </summary>
    Task DeleteAllGreaterOrEqualBlock(long reorgAt);

    Task WriteBatch(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);

    /// <summary>
    /// Writes a batch using INSERT ... ON CONFLICT DO NOTHING.
    /// Slower than WriteBatch (COPY) but handles duplicate keys gracefully.
    /// </summary>
    Task WriteBatchWithUpsert(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);

    /// <summary>
    /// Writes multiple batches to different tables within a single transaction.
    /// All writes succeed or all fail together, preventing partial writes.
    /// </summary>
    /// <param name="batches">Dictionary of (namespace, table) -> data to write</param>
    /// <param name="propertyMap">Schema property map for column mapping</param>
    /// <param name="useUpsert">If true, uses INSERT ON CONFLICT DO NOTHING instead of COPY</param>
    Task WriteBatchesAtomic(
        IDictionary<(string Namespace, string Table), IEnumerable<object>> batches,
        ISchemaPropertyMap propertyMap,
        bool useUpsert = false);

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
