using System.Collections.Immutable;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.CirclesV2.LBP;

/*
   // Events
   /// @notice Emitted when a CirclesBacking is created.
   event CirclesBackingDeployed(address indexed backer, address indexed circlesBackingInstance);
   /// @notice Emitted when a LBP is created.
   event LBPDeployed(address indexed circlesBackingInstance, address indexed lbp);
   /// @notice Emitted when a Circles backing process is initiated.
   event CirclesBackingInitiated(
       address indexed backer,
       address indexed circlesBackingInstance,
       address backingAsset,
       address personalCirclesAddress
   );
   /// @notice Emitted when a Circles backing process is completed.
   event CirclesBackingCompleted(address indexed backer, address indexed circlesBackingInstance, address indexed lbp);
   /// @notice Emitted when a Circles backing is ended by user due to release of LP tokens.
   event Released(address indexed backer, address indexed circlesBackingInstance, address indexed lbp);

 */

public class LogParser(ImmutableHashSet<Address> factoryAddresses) : ILogParser
{
    private readonly Hash256 _circlesBackingDeployedTopic = new(DatabaseSchema.CirclesBackingDeployed.Topic);
    private readonly Hash256 _lbpDeployedTopic = new(DatabaseSchema.LBPDeployed.Topic);
    private readonly Hash256 _circlesBackingInitiatedTopic = new(DatabaseSchema.CirclesBackingInitiated.Topic);
    private readonly Hash256 _circlesBackingCompletedTopic = new(DatabaseSchema.CirclesBackingCompleted.Topic);
    private readonly Hash256 _releasedTopic = new(DatabaseSchema.Released.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
    }

    public IEnumerable<IIndexEvent> ParseLog(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        if (!factoryAddresses.Contains(log.Address))
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (topic == _circlesBackingDeployedTopic)
        {
            yield return CirclesBackingDeployed(block, receipt, log, logIndex);
        }

        if (topic == _lbpDeployedTopic)
        {
            yield return LbpDeployed(block, receipt, log, logIndex);
        }

        if (topic == _circlesBackingInitiatedTopic)
        {
            yield return CirclesBackingInitiated(block, receipt, log, logIndex);
        }

        if (topic == _circlesBackingCompletedTopic)
        {
            yield return CirclesBackingCompleted(block, receipt, log, logIndex);
        }

        if (topic == _releasedTopic)
        {
            yield return Released(block, receipt, log, logIndex);
        }
    }

    // event CirclesBackingDeployed(address indexed backer, address indexed circlesBackingInstance);
    private CirclesBackingDeployed CirclesBackingDeployed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new CirclesBackingDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance
        );
    }

    // event LBPDeployed(address indexed circlesBackingInstance, address indexed lbp);
    private LbpDeployed LbpDeployed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var circlesBackingInstance = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var lbp = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new LbpDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            circlesBackingInstance,
            lbp
        );
    }

    // event CirclesBackingInitiated(address indexed backer, address indexed circlesBackingInstance, address backingAsset, address personalCirclesAddress);
    private CirclesBackingInitiated CirclesBackingInitiated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        var data = log.Data;
        var backingAssetBytes = data[0..32];
        var personalCirclesAddressBytes = data[32..64];

        var backingAsset = "0x" + BitConverter
            .ToString(backingAssetBytes[12..32])
            .Replace("-", "")
            .ToLowerInvariant();

        var personalCirclesAddress = "0x" + BitConverter
            .ToString(personalCirclesAddressBytes[12..32])
            .Replace("-", "")
            .ToLowerInvariant();

        return new CirclesBackingInitiated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance,
            backingAsset,
            personalCirclesAddress
        );
    }

    // event CirclesBackingCompleted(address indexed backer, address indexed circlesBackingInstance, address lbp);
    private CirclesBackingCompleted CirclesBackingCompleted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var lbp = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new CirclesBackingCompleted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance,
            lbp
        );
    }

    private Released Released(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var lbp = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new Released(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance,
            lbp
        );
    }
}