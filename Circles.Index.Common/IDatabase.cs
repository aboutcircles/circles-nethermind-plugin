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
    Task DeleteFromBlockOnwards(long reorgAt);
    Task WriteBatch(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);
    long? LatestBlock();
    long? FirstGap();
    IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count);

    /// <summary>
    /// Calls the 'Update_Crc_UITransferHistory' stored procedure if available in the db.
    /// </summary>
    /// <returns>True if the stored procedure exists and was called, false otherwise.</returns>
    bool TryUpdateCrcUiTransferHistory();
}