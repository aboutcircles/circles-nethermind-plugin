using System.Data;
using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public interface IDatabase
{
    public IDatabaseSchema Schema { get; }
    
    void Migrate();
    Task DeleteFromBlockOnwards(long reorgAt);
    Task WriteBatch(string @namespace, string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap);
    long? LatestBlock();
    long? FirstGap();
    IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count);
    IEnumerable<object[]> Select(Select select);
    IDataParameter CreateParameter();
    public object? Convert(object? input, ValueTypes target);
}