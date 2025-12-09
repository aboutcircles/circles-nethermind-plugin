using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.OIC;

public class LogParser(Address oicContractAddress) : ILogParser
{
    private readonly Hash256 _openMiddlewareTransferTopic = new(DatabaseSchema.OpenMiddlewareTransfer.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
    }

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        return Task.CompletedTask;
    }

    public IRollbackCache[] Caches { get; } = [];

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        if (log.Address != oicContractAddress)
        {
            yield break;
        }

        var topic0 = log.Topics[0];

        if (topic0 == _openMiddlewareTransferTopic)
        {
            yield return ParseOpenMiddlewareTransfer(block, receipt, log, logIndex);
        }
    }

    private OpenMiddlewareTransfer ParseOpenMiddlewareTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event OpenMiddlewareTransfer(
        //     address indexed onBehalf,
        //     address indexed sender,
        //     address indexed recipient,
        //     uint256 amount,
        //     uint256 inflationaryAmount,
        //     bytes data
        // );

        string onBehalf = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string sender = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string recipient = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        // Non-indexed params are encoded in log.Data
        // [0..32) amount, [32..64) inflationaryAmount, [64..96) data offset -> dynamic bytes
        ReadOnlySpan<byte> data = log.Data;
        UInt256 amount = new UInt256(data.Slice(0, 32), true);
        UInt256 inflationary = new UInt256(data.Slice(32, 32), true);
        int dataOffset = LogDataParsingHelper.ParseOffset(data, 64);
        byte[] payload = LogDataParsingHelper.ParseBytes(data, dataOffset);

        return new OpenMiddlewareTransfer(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            onBehalf,
            sender,
            recipient,
            amount,
            inflationary,
            payload
        );
    }
}
