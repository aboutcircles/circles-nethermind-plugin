using Nethermind.Core;

namespace Circles.Index.Common;

public interface IIndexerVisitor
{
    void VisitBlock(Block block);

    bool VisitReceipt(Block block, TxReceipt receipt);

    bool VisitLog(Block block, TxReceipt receipt, LogEntry log, int logIndex);

    void LeaveReceipt(Block block, TxReceipt receipt, bool logIndexed);

    void LeaveBlock(Block block, bool receiptIndexed);
}


public interface INewIndexerVisitor
{
    bool VisitReceipt(Block block, TxReceipt receipt);

    bool VisitLog(Block block, TxReceipt receipt, LogEntry log, int logIndex);

    void LeaveReceipt(Block block, TxReceipt receipt, bool logIndexed);
}