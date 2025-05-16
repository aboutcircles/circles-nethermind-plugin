using System.Collections.Concurrent;
using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2;

public class LogParser(Address v2HubAddress, Address erc20LiftAddress) : ILogParser
{
    private readonly Hash256 _stoppedTopic = new(DatabaseSchema.Stopped.Topic);
    private readonly Hash256 _trustTopic = new(DatabaseSchema.Trust.Topic);
    private readonly Hash256 _personalMintTopic = new(DatabaseSchema.PersonalMint.Topic);
    private readonly Hash256 _registerHumanTopic = new(DatabaseSchema.RegisterHuman.Topic);
    private readonly Hash256 _registerGroupTopic = new(DatabaseSchema.RegisterGroup.Topic);
    private readonly Hash256 _registerOrganizationTopic = new(DatabaseSchema.RegisterOrganization.Topic);
    private readonly Hash256 _transferBatchTopic = new(DatabaseSchema.TransferBatch.Topic);
    private readonly Hash256 _transferSingleTopic = new(DatabaseSchema.TransferSingle.Topic);
    private readonly Hash256 _approvalForAllTopic = new(DatabaseSchema.ApprovalForAll.Topic);
    private readonly Hash256 _erc20WrapperDeployed = new(DatabaseSchema.ERC20WrapperDeployed.Topic);
    private readonly Hash256 _erc20WrapperTransfer = new(DatabaseSchema.Erc20WrapperTransfer.Topic);
    private readonly Hash256 _depositInflationary = new(DatabaseSchema.DepositInflationary.Topic);
    private readonly Hash256 _withdrawInflationary = new(DatabaseSchema.WithdrawInflationary.Topic);
    private readonly Hash256 _depositDemurraged = new(DatabaseSchema.DepositDemurraged.Topic);
    private readonly Hash256 _withdrawDemurraged = new(DatabaseSchema.WithdrawDemurraged.Topic);
    private readonly Hash256 _streamCompletedTopic = new(DatabaseSchema.StreamCompleted.Topic);
    private readonly Hash256 _discountCostTopic = new(DatabaseSchema.DiscountCost.Topic);
    private readonly Hash256 _groupMintTopic = new(DatabaseSchema.GroupMint.Topic);
    private readonly Hash256 _flowEdgesScopeSingleStarted = new(DatabaseSchema.FlowEdgesScopeSingleStarted.Topic);
    private readonly Hash256 _flowEdgesScopeLastEnded = new(DatabaseSchema.FlowEdgesScopeLastEnded.Topic);

    // Tracks whether a specific address is recognized as an ERC20Wrapper contract
    // Address -> CirclesType (demurraged = 0 or static = 1)
    public static readonly ConcurrentDictionary<Address, long> Erc20WrapperAddresses = new();
    
    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {        
        var selectErc20WrapperDeployed = new Select(
            "CrcV2",
            "ERC20WrapperDeployed",
            ["erc20Wrapper", "circlesType"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = selectErc20WrapperDeployed.ToSql(database);
        var result = database.Select(sql);
        var rows = result.Rows.ToArray();

        logger.Info($" * Found {rows.Length} erc20 wrapper addresses");

        foreach (var row in rows)
        {
            Erc20WrapperAddresses.TryAdd(new Address(row[0]!.ToString()!), (long)row[1]!);
        }

        logger.Info("Caching erc20 wrapper addresses done");
        
        return Task.CompletedTask;
    }

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
}