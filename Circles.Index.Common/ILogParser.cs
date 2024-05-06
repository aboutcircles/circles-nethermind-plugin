using Nethermind.Core;

namespace Circles.Index.Common;

public interface ILogParser
{
    IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex);
}