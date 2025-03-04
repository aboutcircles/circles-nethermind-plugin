using Nethermind.Core;

namespace Circles.Index.Common;

public interface ILogParser
{
    /// <summary>
    /// Parses a log entry into a list of index events.
    /// </summary>
    IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex);

    /// <summary>
    /// Parses a transaction into a list of index events.
    /// This method is called after all logs have been parsed, so it can be used to aggregate events.
    /// </summary>
    IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events);
}