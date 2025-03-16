using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Circles.Index.CirclesV2.CMGroupDeployer;
using Circles.Index.CirclesV2.Hub;
using Circles.Index.CirclesV2.LBP;
using Circles.Index.CirclesV2.NameRegistry;
using Circles.Index.CirclesV2.StandardTreasury;
using Circles.Index.CirclesV2.Decoders;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2;

public class LogParser(
    Address v2HubAddress,
    Address erc20LiftAddress,
    Address nameRegistryAddress,
    Address standardTreasuryAddress,
    ImmutableHashSet<Address>? cmGroupDeployerAddresses,
    Address? lbpFactoryAddress) : ILogParser
{
    private readonly Hash256 _stoppedTopic = new(Hub.DatabaseSchema.Stopped.Topic);
    private readonly Hash256 _trustTopic = new(Hub.DatabaseSchema.Trust.Topic);
    private readonly Hash256 _personalMintTopic = new(Hub.DatabaseSchema.PersonalMint.Topic);
    private readonly Hash256 _registerHumanTopic = new(Hub.DatabaseSchema.RegisterHuman.Topic);
    private readonly Hash256 _registerGroupTopic = new(Hub.DatabaseSchema.RegisterGroup.Topic);
    private readonly Hash256 _registerOrganizationTopic = new(Hub.DatabaseSchema.RegisterOrganization.Topic);
    private readonly Hash256 _transferBatchTopic = new(Hub.DatabaseSchema.TransferBatch.Topic);
    private readonly Hash256 _transferSingleTopic = new(Hub.DatabaseSchema.TransferSingle.Topic);
    private readonly Hash256 _approvalForAllTopic = new(Hub.DatabaseSchema.ApprovalForAll.Topic);
    private readonly Hash256 _erc20WrapperDeployed = new(Hub.DatabaseSchema.ERC20WrapperDeployed.Topic);
    private readonly Hash256 _erc20WrapperTransfer = new(Hub.DatabaseSchema.Erc20WrapperTransfer.Topic);
    private readonly Hash256 _depositInflationary = new(Hub.DatabaseSchema.DepositInflationary.Topic);
    private readonly Hash256 _withdrawInflationary = new(Hub.DatabaseSchema.WithdrawInflationary.Topic);
    private readonly Hash256 _depositDemurraged = new(Hub.DatabaseSchema.DepositDemurraged.Topic);
    private readonly Hash256 _withdrawDemurraged = new(Hub.DatabaseSchema.WithdrawDemurraged.Topic);
    private readonly Hash256 _streamCompletedTopic = new(Hub.DatabaseSchema.StreamCompleted.Topic);
    private readonly Hash256 _discountCostTopic = new(Hub.DatabaseSchema.DiscountCost.Topic);
    private readonly Hash256 _groupMintTopic = new(Hub.DatabaseSchema.GroupMint.Topic);
    private readonly Hash256 _flowEdgesScopeSingleStarted = new(Hub.DatabaseSchema.FlowEdgesScopeSingleStarted.Topic);
    private readonly Hash256 _flowEdgesScopeLastEnded = new(Hub.DatabaseSchema.FlowEdgesScopeLastEnded.Topic);

    private readonly Hash256 _cmGroupCreatedTopic = new(CMGroupDeployer.DatabaseSchema.CMGroupCreated.Topic);

    private readonly Hash256 _circlesBackingDeployedTopic = new(LBP.DatabaseSchema.CirclesBackingDeployed.Topic);
    private readonly Hash256 _lbpDeployedTopic = new(LBP.DatabaseSchema.LBPDeployed.Topic);
    private readonly Hash256 _circlesBackingInitiatedTopic = new(LBP.DatabaseSchema.CirclesBackingInitiated.Topic);
    private readonly Hash256 _circlesBackingCompletedTopic = new(LBP.DatabaseSchema.CirclesBackingCompleted.Topic);
    private readonly Hash256 _releasedTopic = new(LBP.DatabaseSchema.Released.Topic);

    private readonly Hash256 _registerShortNameTopic = new(NameRegistry.DatabaseSchema.RegisterShortName.Topic);
    private readonly Hash256 _updateMetadataDigestTopic = new(NameRegistry.DatabaseSchema.UpdateMetadataDigest.Topic);
    private readonly Hash256 _cidV0Topic = new(NameRegistry.DatabaseSchema.CidV0.Topic);

    private readonly Hash256 _createVaultTopic = new(StandardTreasury.DatabaseSchema.CreateVault.Topic);
    private readonly Hash256 _groupMintSingleTopic = new(StandardTreasury.DatabaseSchema.CollateralLockedSingle.Topic);
    private readonly Hash256 _groupMintBatchTopic = new(StandardTreasury.DatabaseSchema.CollateralLockedBatch.Topic);
    private readonly Hash256 _groupRedeemTopic = new(StandardTreasury.DatabaseSchema.GroupRedeem.Topic);

    private readonly Hash256 _groupRedeemCollateralReturnTopic =
        new(StandardTreasury.DatabaseSchema.GroupRedeemCollateralReturn.Topic);

    private readonly Hash256 _groupRedeemCollateralBurnTopic =
        new(StandardTreasury.DatabaseSchema.GroupRedeemCollateralBurn.Topic);

    // Tracks whether a specific address is recognized as an ERC20Wrapper contract
    // Address -> CirclesType (demurraged = 0 or static = 1)
    public static readonly ConcurrentDictionary<Address, long> Erc20WrapperAddresses = new();

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = false,
        Converters =
        {
            new UInt256AsStringConverter()
        }
    };

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        if (events.Count == 0)
            yield break;

        var eventsv2 = new List<IIndexedEventV2>(events.Count);
        foreach (var e in events)
        {
            if (e is IIndexedEventV2 v2) eventsv2.Add(v2);
        }

        if (eventsv2.Count == 0)
            yield break;

        var result = TransferSummaryAggregator.AggregateAll(eventsv2, Erc20WrapperAddresses);
        int syntheticLogIndex = -(result.StreamTransfers.Totals.Count() + result.NonStreamTransfers.Totals.Count());

        if (result.StreamTransfers.Totals.Any())
        {
            var streamEventsJson = JsonSerializer.Serialize(result.StreamEvents, _jsonSerializerOptions);
            foreach (var summary in result.StreamTransfers.Totals)
            {
                yield return new TransferSummary(
                    block.Number,
                    (long)block.Timestamp,
                    transactionIndex,
                    syntheticLogIndex++,
                    transaction.Hash!.ToString(true),
                    "",
                    summary.Key.From,
                    summary.Key.To,
                    (UInt256)summary.Value,
                    streamEventsJson
                );
            }
        }

        if (result.NonStreamTransfers.Totals.Any())
        {
            var nonStreamEventsJson = JsonSerializer.Serialize(result.NonStreamEvents, _jsonSerializerOptions);
            foreach (var transfer in result.NonStreamTransfers.Totals)
            {
                yield return new TransferSummary(
                    block.Number,
                    (long)block.Timestamp,
                    transactionIndex,
                    syntheticLogIndex++,
                    transaction.Hash!.ToString(true),
                    "",
                    transfer.Key.From,
                    transfer.Key.To,
                    (UInt256)transfer.Value,
                    nonStreamEventsJson
                );
            }
        }

        if (result.StreamTransfers.Totals.Any() || result.NonStreamTransfers.Totals.Any())
        {
            var additionalTxData = AdditionalDataExtractor.ExtractAdditionalData(transaction);
            if (additionalTxData.Length > 0)
            {
                Console.WriteLine($"Additional data for tx {transaction.Hash}: {additionalTxData.ToHexString()}");
            }
        }
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

        var topic = log.Topics[0];

        // Events from the V2Hub
        if (log.Address == v2HubAddress)
        {
            if (topic == _stoppedTopic)
            {
                yield return CrcV2Stopped(block, receipt, log, logIndex);
            }

            if (topic == _trustTopic)
            {
                yield return CrcV2Trust(block, receipt, log, logIndex);
            }

            if (topic == _personalMintTopic)
            {
                yield return CrcV2PersonalMint(block, receipt, log, logIndex);
            }

            if (topic == _registerHumanTopic)
            {
                yield return CrcV2RegisterHuman(block, receipt, log, logIndex);
            }

            if (topic == _registerGroupTopic)
            {
                yield return CrcV2RegisterGroup(block, receipt, log, logIndex);
            }

            if (topic == _registerOrganizationTopic)
            {
                yield return CrcV2RegisterOrganization(block, receipt, log, logIndex);
            }

            if (topic == _transferBatchTopic)
            {
                foreach (var batchEvent in Erc1155TransferBatch(block, receipt, log, logIndex))
                {
                    yield return batchEvent;
                }
            }

            if (topic == _transferSingleTopic)
            {
                yield return Erc1155TransferSingle(block, receipt, log, logIndex);
            }

            if (topic == _approvalForAllTopic)
            {
                yield return Erc1155ApprovalForAll(block, receipt, log, logIndex);
            }

            if (topic == _streamCompletedTopic)
            {
                foreach (var streamCompleted in StreamCompleted(block, receipt, log, logIndex))
                {
                    yield return streamCompleted;
                }
            }

            if (topic == _discountCostTopic)
            {
                yield return DiscountCost(block, receipt, log, logIndex);
            }

            if (topic == _groupMintTopic)
            {
                foreach (var groupMint in GroupMint(block, receipt, log, logIndex))
                {
                    yield return groupMint;
                }
            }

            if (topic == _flowEdgesScopeSingleStarted)
            {
                yield return FlowEdgesScopeSingleStarted(block, receipt, log, logIndex);
            }

            if (topic == _flowEdgesScopeLastEnded)
            {
                yield return FlowEdgesScopeLastEnded(block, receipt, log, logIndex);
            }
        }

        // Events from the Erc20Lift contract
        if (log.Address == erc20LiftAddress)
        {
            if (topic == _erc20WrapperDeployed)
            {
                yield return Erc20WrapperDeployed(block, receipt, log, logIndex);
            }
        }

        // Events from the NameRegistry contract
        if (log.Address == nameRegistryAddress)
        {
            if (topic == _registerShortNameTopic)
            {
                yield return RegisterShortName(block, receipt, log, logIndex);
            }

            if (topic == _updateMetadataDigestTopic)
            {
                yield return UpdateMetadataDigest(block, receipt, log, logIndex);
            }

            if (topic == _cidV0Topic)
            {
                yield return CidV0(block, receipt, log, logIndex);
            }
        }

        // events from the StandardTreasuryAddress
        if (log.Address == standardTreasuryAddress)
        {
            if (topic == _createVaultTopic)
            {
                yield return CreateVault(block, receipt, log, logIndex);
            }
            else if (topic == _groupMintSingleTopic)
            {
                yield return CollateralLockedSingle(block, receipt, log, logIndex);
            }
            else if (topic == _groupMintBatchTopic)
            {
                foreach (var batchEvent in CollateralLockedBatch(block, receipt, log, logIndex))
                {
                    yield return batchEvent;
                }
            }
            else if (topic == _groupRedeemTopic)
            {
                yield return GroupRedeem(block, receipt, log, logIndex);
            }
            else if (topic == _groupRedeemCollateralReturnTopic)
            {
                foreach (var returnEvent in GroupRedeemCollateralReturn(block, receipt, log, logIndex))
                {
                    yield return returnEvent;
                }
            }
            else if (topic == _groupRedeemCollateralBurnTopic)
            {
                foreach (var burnEvent in GroupRedeemCollateralBurn(block, receipt, log, logIndex))
                {
                    yield return burnEvent;
                }
            }
        }

        // Events from known ERC20Wrapper addresses
        if (Erc20WrapperAddresses.ContainsKey(log.Address))
        {
            if (topic == _erc20WrapperTransfer)
            {
                yield return Erc20WrapperTransfer(block, receipt, log, logIndex);
            }

            if (topic == _depositDemurraged)
            {
                yield return DepositDemurraged(block, receipt, log, logIndex);
            }

            if (topic == _withdrawDemurraged)
            {
                yield return WithdrawDemurraged(block, receipt, log, logIndex);
            }

            if (topic == _depositInflationary)
            {
                yield return DepositInflationary(block, receipt, log, logIndex);
            }

            if (topic == _withdrawInflationary)
            {
                yield return WithdrawInflationary(block, receipt, log, logIndex);
            }
        }

        if (cmGroupDeployerAddresses != null && cmGroupDeployerAddresses.Contains(log.Address))
        {
            if (topic == _cmGroupCreatedTopic)
            {
                yield return CMGroupCreated(block, receipt, log, logIndex);
            }
        }

        if (lbpFactoryAddress != null && log.Address == lbpFactoryAddress)
        {
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
    }

    private IIndexEvent Erc20WrapperDeployed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event ERC20WrapperDeployed(address indexed avatar, address indexed erc20Wrapper, uint8 circlesType)

        // Parse addresses from topics:
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string erc20Wrapper = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        // Parse the single uint256 from log.Data => circlesType
        UInt256 circlesType = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        // Mark that we know about this wrapper
        Erc20WrapperAddresses.TryAdd(new Address(erc20Wrapper), (long)circlesType);

        return new ERC20WrapperDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            erc20Wrapper,
            (long)circlesType
        );
    }

    private ApprovalForAll Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event ApprovalForAll(address indexed account, address indexed operator, bool approved)

        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        // We'll interpret the 32 bytes of log.Data as bool => nonzero => true
        UInt256 raw = LogDataParsingHelper.ParseSingleUInt256(log.Data);
        bool approved = !raw.IsZero;

        return new ApprovalForAll(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            operatorAddress,
            approved
        );
    }

    private TransferSingle Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)

        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string fromAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for TransferSingle (needs 64 bytes).");
        }

        var id = new UInt256(dataSpan.Slice(0, 32), true);
        var value = new UInt256(dataSpan.Slice(32, 32), true);

        return new TransferSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            operatorAddress,
            fromAddress,
            toAddress,
            id,
            value
        );
    }

    private IEnumerable<TransferBatch> Erc1155TransferBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values)
        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string fromAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for TransferBatch (needs at least 64 bytes).");
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(dataSpan, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(dataSpan, valuesOffset);

        if (ids.Length != values.Length)
        {
            throw new InvalidOperationException("The number of ids and values must match in TransferBatch.");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            yield return new TransferBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                operatorAddress,
                fromAddress,
                toAddress,
                ids[i],
                values[i]
            );
        }
    }

    private RegisterOrganization CrcV2RegisterOrganization(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterOrganization(address indexed organization, string name)
        string orgAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string orgName = LogDataStringDecoder.ReadStrings(log.Data)[0];

        return new RegisterOrganization(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            orgAddress,
            orgName
        );
    }

    private RegisterGroup CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterGroup(address indexed group, address indexed mintPolicy, address indexed treasury, string name, string symbol)

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string mintPolicy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string treasury = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        string[] stringData = LogDataStringDecoder.ReadStrings(log.Data);
        string groupName = stringData[0];
        string groupSymbol = stringData[1];

        return new RegisterGroup(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            mintPolicy,
            treasury,
            groupName,
            groupSymbol
        );
    }

    private RegisterHuman CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterHuman(address indexed human, address indexed inviter)
        string humanAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string inviterAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            humanAddress,
            inviterAddress
        );
    }

    private PersonalMint CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event PersonalMint(address indexed to, uint256 amount, uint256 startPeriod, uint256 endPeriod)
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 96)
        {
            throw new ArgumentException("Insufficient data for PersonalMint (needs 96 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var startPeriod = new UInt256(dataSpan.Slice(32, 32), true);
        var endPeriod = new UInt256(dataSpan.Slice(64, 32), true);

        return new PersonalMint(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            toAddress,
            amount,
            startPeriod,
            endPeriod
        );
    }

    private Trust CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Trust(address indexed user, address indexed canSendTo, uint256 trustLimit)
        string userAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string canSendToAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        UInt256 limit = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new Trust(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            userAddress,
            canSendToAddress,
            limit
        );
    }

    private Stopped CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Stopped(address indexed who)
        string address = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Stopped(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            address
        );
    }

    private Erc20WrapperTransfer Erc20WrapperTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Erc20WrapperTransfer(address indexed from, address indexed to, uint256 value)

        string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        UInt256 amount = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new Erc20WrapperTransfer(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            log.Address.ToString(true, false),
            from,
            to,
            amount
        );
    }

    private DepositInflationary DepositInflationary(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DepositInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for DepositInflationary (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var demurraged = new UInt256(dataSpan.Slice(32, 32), true);

        return new DepositInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            demurraged
        );
    }

    private WithdrawInflationary WithdrawInflationary(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event WithdrawInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for WithdrawInflationary (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var demurraged = new UInt256(dataSpan.Slice(32, 32), true);

        return new WithdrawInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            demurraged
        );
    }

    private DepositDemurraged DepositDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DepositDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for DepositDemurraged (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var inflation = new UInt256(dataSpan.Slice(32, 32), true);

        return new DepositDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            inflation
        );
    }

    private WithdrawDemurraged WithdrawDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event WithdrawDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for WithdrawDemurraged (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var inflation = new UInt256(dataSpan.Slice(32, 32), true);

        return new WithdrawDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            inflation
        );
    }

    private DiscountCost DiscountCost(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DiscountCost(address indexed account, uint256 indexed id, uint256 discountCost)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var id = new UInt256(log.Topics[2].Bytes, true);

        UInt256 cost = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new DiscountCost(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            id,
            cost
        );
    }

    private IEnumerable<StreamCompleted> StreamCompleted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event StreamCompleted(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] amounts)

        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string fromAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for StreamCompleted (needs at least 64 bytes).");
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int amountsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(dataSpan, idsOffset);
        UInt256[] amounts = LogDataParsingHelper.ParseUInt256Array(dataSpan, amountsOffset);

        if (ids.Length != amounts.Length)
        {
            throw new InvalidOperationException("The number of ids and amounts must match in StreamCompleted.");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            yield return new StreamCompleted(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                i,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                operatorAddress,
                fromAddress,
                toAddress,
                ids[i],
                amounts[i]
            );
        }
    }

    private IEnumerable<GroupMint> GroupMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event GroupMint(address indexed sender, address indexed receiver, address indexed group, uint256[] collateral, uint256[] amounts);

        string sender = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string receiver = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for GroupMint (needs at least 64 bytes).");
        }

        int collateralOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int amountsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        UInt256[] collateral = LogDataParsingHelper.ParseUInt256Array(dataSpan, collateralOffset);
        UInt256[] amounts = LogDataParsingHelper.ParseUInt256Array(dataSpan, amountsOffset);

        if (collateral.Length != amounts.Length)
        {
            throw new InvalidOperationException("The number of collateral and amounts must match in GroupMint.");
        }

        for (int i = 0; i < collateral.Length; i++)
        {
            yield return new GroupMint(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                i,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                sender,
                receiver,
                group,
                collateral[i],
                amounts[i]
            );
        }
    }

    private FlowEdgesScopeSingleStarted FlowEdgesScopeSingleStarted(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId);
        UInt256 flowEdgeId = new UInt256(log.Topics[1].Bytes, true);
        UInt256 streamId = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new FlowEdgesScopeSingleStarted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            flowEdgeId,
            (ushort)streamId
        );
    }

    private FlowEdgesScopeLastEnded FlowEdgesScopeLastEnded(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event FlowEdgesScopeLastEnded();
        return new FlowEdgesScopeLastEnded(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false)
        );
    }

    // event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler);
    private CMGroupCreated CMGroupCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string proxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string mintHandler = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        string redemptionHandler = new Address(log.Data.Slice(12)).ToString(true, false);

        return new CMGroupCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            proxy,
            owner,
            mintHandler,
            redemptionHandler
        );
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
        var lbp = log.Topics.Length > 3
            ? "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength)
            : new Address(log.Data.Slice(12)).ToString(true, false);

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

    private UpdateMetadataDigest UpdateMetadataDigest(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        byte[] metadataDigest = log.Data;

        return new UpdateMetadataDigest(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            metadataDigest);
    }

    private RegisterShortName RegisterShortName(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce)
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        UInt256 shortName = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 nonce = new UInt256(log.Data.Slice(32, 32), true);

        return new RegisterShortName(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            shortName,
            nonce);
    }

    private CidV0 CidV0(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new CidV0(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            log.Data);
    }

    private CreateVault CreateVault(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event CreateVault(address indexed group, address indexed vault);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string vaultAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new CreateVault(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            vaultAddress
        );
    }

    private CollateralLockedSingle CollateralLockedSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event CollateralLockedSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);

        // Non-indexed params are in log.Data
        //  - first 32 bytes => value
        //  - remainder => userData
        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] userData = log.Data.Slice(32).ToArray();

        return new CollateralLockedSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            id,
            value,
            userData
        );
    }

    private IEnumerable<CollateralLockedBatch> CollateralLockedBatch(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event CollateralLockedBatch(address indexed group, uint256[] ids, uint256[] values, bytes userData);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data;

        // We expect at least 3 offsets (one each for ids, values, userData):
        //  - [0..31] => offset to ids
        //  - [32..63] => offset to values
        //  - [64..95] => offset to userData
        if (data.Length < 96)
        {
            throw new ArgumentException("Log data is too short to contain offsets");
        }

        // Parse each offset
        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);
        int userDataOffset = LogDataParsingHelper.ParseOffset(data, 64);

        if (idsOffset < 0 || valuesOffset < 0 || userDataOffset < 0)
        {
            throw new ArgumentException("Failed to parse offsets");
        }

        // Parse arrays/bytes from each offset
        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);
        byte[] userDataBytes = LogDataParsingHelper.ParseBytes(data, userDataOffset);

        // Typically, you can yield a single event that has all arrays,
        // but your existing pattern yields one event per (id, value).
        for (int i = 0; i < ids.Length; i++)
        {
            // If there are fewer values than ids, default to zero
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new CollateralLockedBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                groupAddress,
                ids[i],
                val,
                userDataBytes
            );
        }
    }

    private GroupRedeem GroupRedeem(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event GroupRedeem(address indexed group, uint256 indexed id, uint256 value, bytes data);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);

        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] dataBytes = log.Data.Slice(32).ToArray();

        return new GroupRedeem(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            id,
            value,
            dataBytes
        );
    }

    private IEnumerable<GroupRedeemCollateralReturn> GroupRedeemCollateralReturn(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event GroupRedeemCollateralReturn(address indexed group, address indexed to, uint256[] ids, uint256[] values);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        var data = log.Data;
        if (data.Length < 64)
        {
            throw new ArgumentException("Log data is too short to contain offsets");
        }

        // offsets to ids and values
        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        if (idsOffset < 0 || valuesOffset < 0)
        {
            throw new ArgumentException("Failed to parse offsets");
        }

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

        for (int i = 0; i < ids.Length; i++)
        {
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new GroupRedeemCollateralReturn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                groupAddress,
                toAddress,
                ids[i],
                val
            );
        }
    }

    private IEnumerable<GroupRedeemCollateralBurn> GroupRedeemCollateralBurn(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event GroupRedeemCollateralBurn(address indexed group, uint256[] ids, uint256[] values);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var data = log.Data;
        if (data.Length < 64)
        {
            yield break;
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        if (idsOffset < 0 || valuesOffset < 0)
        {
            yield break;
        }

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

        for (int i = 0; i < ids.Length; i++)
        {
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new GroupRedeemCollateralBurn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                groupAddress,
                ids[i],
                val
            );
        }
    }
}