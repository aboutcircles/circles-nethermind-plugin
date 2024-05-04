using Nethermind.Core;

namespace Circles.Index.Common;

public abstract class SinkVisitor : INewIndexerVisitor
{
    public abstract IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex);
}