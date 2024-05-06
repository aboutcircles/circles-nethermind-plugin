using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public interface IDatabase
{
    void Migrate(IDatabaseSchema databaseSchema);
    Task DeleteFromBlockOnwards(long reorgAt);
    Task WriteBatch(TableSchema table, IEnumerable<IIndexEvent> data, ISchemaPropertyMap propertyMap);
    long? LatestBlock();
    long? FirstGap();
    IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count);
}