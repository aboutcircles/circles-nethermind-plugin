using Nethermind.Core;

namespace Circles.Index.Common;

public interface ILogParser
{
    IEnumerable<IIndexEvent> ParseTransaction(Block block, int transactionIndex, Transaction transaction);
    
    IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex);
}