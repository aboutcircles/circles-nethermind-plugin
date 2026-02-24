using System.Collections.Immutable;
using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.PaymentGateway;

/// <summary>
/// Parses logs for PaymentGatewayFactory and PaymentGateway instances.
/// </summary>
public class LogParser(ImmutableHashSet<Address> factoryAddresses) : ILogParser
{
    // Topics
    private static readonly Hash256 _gatewayCreated = new(DatabaseSchema.GatewayCreated.Topic);
    private static readonly Hash256 _paymentReceived = new(DatabaseSchema.PaymentReceived.Topic);
    private static readonly Hash256 _trustUpdated = new(DatabaseSchema.TrustUpdated.Topic);

    // Cache discovered gateways (from factory events or DB seed)
    public static readonly RollbackCache<Address, object?> Gateways = new("PaymentGateways");

    public IRollbackCache[] Caches { get; } = [Gateways];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        var seeds = new Dictionary<Address, object?>();

        // Seed from previously indexed GatewayCreated events
        var select = new Query.Select(DatabaseSchema.Namespace, nameof(Events.GatewayCreated),
            ["gateway"], [], [], int.MaxValue, false, int.MaxValue);
        foreach (var row in database.Select(select.ToSql(database)).Rows)
        {
            seeds[new Address(row[0]!.ToString()!)] = null;
        }

        Gateways.Seed(seeds);
        logger.Info($" * Cached {seeds.Count} PaymentGateways");
        return Task.CompletedTask;
    }

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        // Factory-originated events
        if (factoryAddresses.Contains(log.Address))
        {
            // GatewayCreated
            if (topic == _gatewayCreated)
            {
                var evt = ParseGatewayCreated(block, receipt, log, logIndex);
                // remember gateway address
                Gateways.Add(block.Number, new Address(evt.Gateway), null);
                yield return evt;
                yield break;
            }

            // PaymentReceived
            if (topic == _paymentReceived)
            {
                yield return ParsePaymentReceived(block, receipt, log, logIndex);
                yield break;
            }

            // TrustUpdated
            if (topic == _trustUpdated)
            {
                yield return ParseTrustUpdated(block, receipt, log, logIndex);
            }
        }
    }

    private static Events.GatewayCreated ParseGatewayCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event GatewayCreated(address indexed owner, address indexed gateway)
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string gateway = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new Events.GatewayCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            owner,
            gateway
        );
    }

    private static Events.PaymentReceived ParsePaymentReceived(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event PaymentReceived(address indexed payer, address indexed payee, address indexed gateway, uint256 tokenId, uint256 amount, bytes data)
        string payer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string payee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string gateway = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        // Non-indexed data is ABI-encoded (head/tail)
        ReadOnlySpan<byte> data = log.Data;
        UInt256 tokenId = new UInt256(data.Slice(0, 32), true);
        UInt256 amount = new UInt256(data.Slice(32, 32), true);
        // third word in head is the offset to dynamic bytes
        int bytesOffset;
        byte[] dataBytes;
        try
        {
            bytesOffset = LogDataParsingHelper.ParseOffset(data, 64);
            dataBytes = LogDataParsingHelper.ParseBytes(data, bytesOffset);
        }
        catch
        {
            // Be defensive: if decoding fails, store empty bytes instead of crashing the indexer
            dataBytes = Array.Empty<byte>();
        }

        return new Events.PaymentReceived(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            payer,
            payee,
            gateway,
            tokenId,
            amount,
            dataBytes
        );
    }

    private static Events.TrustUpdated ParseTrustUpdated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event TrustUpdated(address indexed gateway, address indexed trustReceiver, uint96 expiry)
        string gateway = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string trustReceiver = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        UInt256 expiry = new UInt256(log.Data.Slice(0, 32), true);

        return new Events.TrustUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            gateway,
            trustReceiver,
            expiry
        );
    }
}
