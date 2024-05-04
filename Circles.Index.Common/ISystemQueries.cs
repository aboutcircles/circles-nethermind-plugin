using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public interface ISystemQueries
{
    long? LatestBlock();
    long? FirstGap();
    IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count);
}