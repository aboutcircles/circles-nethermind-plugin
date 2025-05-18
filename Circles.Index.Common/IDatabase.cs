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
    Task WriteBatch(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);
    long? LatestBlock();
    long? FirstGap();
    IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count);
}