using Nethermind.Core;

namespace Circles.Index.Indexer;

public interface IIndexerVisitor
{
    void VisitBlock(Nethermind.Core.Block block);

    /// <returns>If the receipt has logs</returns>
    bool VisitReceipt(Nethermind.Core.Block block, TxReceipt receipt);

    /// <returns>If the log entry was used</returns>
    bool VisitLog(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex);

    void LeaveReceipt(Nethermind.Core.Block block, TxReceipt receipt, bool logIndexed);

    void LeaveBlock(Nethermind.Core.Block block, bool receiptIndexed);
}