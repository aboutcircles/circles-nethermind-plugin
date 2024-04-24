using Nethermind.Core;

namespace Circles.Index.Common;

public abstract class SinkVisitor : INewIndexerVisitor
{
    public abstract bool VisitReceipt(Block block, TxReceipt receipt);
    public abstract bool VisitLog(Block block, TxReceipt receipt, LogEntry log, int logIndex);
    public abstract void LeaveReceipt(Block block, TxReceipt receipt, bool logIndexed);
}