using System.Collections.Immutable;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Circles.Common;

public interface ILogParser
{
    Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings);

    IRollbackCache[] Caches { get; }

    /// <summary>
    /// Topic hashes of events that write to caches during ParseLog/ParseTransaction.
    /// Used by the pipeline to partition receipts: receipts with only non-cache-writing
    /// topics can be parsed in parallel, while cache-writing receipts must be sequential.
    /// Return empty to indicate no cache writes (safe for parallel parsing).
    /// </summary>
    IReadOnlySet<Hash256> CacheWritingTopics => ImmutableHashSet<Hash256>.Empty;

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
