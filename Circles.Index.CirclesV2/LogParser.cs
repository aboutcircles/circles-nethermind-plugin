using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

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
    public static readonly ConcurrentDictionary<Address, object?> Erc20WrapperAddresses = new();

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        IReadOnlyList<IIndexEvent> events)
    {
        // Sort events by LogIndex to ensure we process them in chronological order
        var e = events.OrderBy(ev => ev.LogIndex).ToArray();

        // This stores all "active flows" so that we can produce one summary upon StreamCompleted
        var flows = new List<FlowRecord>();
        FlowRecord? currentFlow = null;

        var lastLogIndex = -1;

        // We'll iterate through all events. Whenever we see TransferSingle/TransferBatch, we either
        // add it to the current flow (if one is active) OR emit a stand-alone summary.
        for (int i = 0; i < e.Length; i++)
        {
            var evt = e[i];

            switch (evt)
            {
                case TransferSingle ts:
                {
                    // If there's an active flow, we add this TransferSingle to that flow's Edges
                    // Otherwise, produce a stand-alone TransferSummary
                    if (currentFlow != null)
                    {
                        currentFlow.Edges.Add(ts);
                    }
                    else
                    {
                        yield return BuildStandaloneSummary(ts);
                    }

                    break;
                }

                case TransferBatch tb:
                {
                    // Typically each TransferBatch record is already one "transfer" in your system
                    // If you want to treat each "id/value" inside TransferBatch as a separate edge,
                    // you'd parse those out. But for simplicity, we assume TransferBatch is one event.
                    if (currentFlow != null)
                    {
                        currentFlow.Edges.Add(tb);
                    }
                    else
                    {
                        yield return BuildStandaloneSummary(tb);
                    }

                    break;
                }

                case FlowEdgesScopeSingleStarted started:
                {
                    // Start a new flow.
                    var flow = new FlowRecord(started.FlowEdgeId, started.StreamId);
                    flows.Add(flow);
                    currentFlow = flow;
                    break;
                }

                case FlowEdgesScopeLastEnded ended:
                {
                    // End the current flow
                    currentFlow = null;
                    break;
                }

                case StreamCompleted sc:
                {
                    if (evt.LogIndex == lastLogIndex)
                    {
                        // This is the same StreamCompleted event (different batchIndex). We ignore it.
                        break;
                    }

                    // Summarize all flows in a big JSON
                    string graphJson = BuildGraphJson(flows);

                    // Clear flows so we can handle multiple StreamCompleted in one tx if necessary
                    flows.Clear();
                    currentFlow = null;

                    // Produce a single TransferSummary for the entire set of flows
                    yield return new TransferSummary(
                        block.Number,
                        (long)block.Timestamp,
                        transactionIndex,
                        sc.LogIndex,
                        sc.TransactionHash,
                        sc.From,
                        sc.To,
                        graphJson
                    );
                    break;
                }
            }

            lastLogIndex = evt.LogIndex;
        }
    }

    /// <summary>
    /// Produces a stand-alone summary for a single TransferSingle or TransferBatch
    /// that isn't part of a flow.
    /// We mimic the shape of TransferSummary from V1, but use whichever fields you like.
    /// </summary>
    private TransferSummary BuildStandaloneSummary(IIndexEvent ev)
    {
        // Because TransferBatch and TransferSingle differ in fields, you can handle them separately:
        // For demonstration, we only store minimal data in graphJson.

        string graphJson;
        long blockNumber, timestamp;
        int transactionIndex, logIndex, batchIndex;
        string transactionHash;

        if (ev is TransferSingle ts)
        {
            blockNumber = ts.BlockNumber;
            timestamp = ts.Timestamp;
            transactionIndex = ts.TransactionIndex;
            logIndex = ts.LogIndex;
            transactionHash = ts.TransactionHash;

            // Example JSON with from -> to, id/value
            graphJson = JsonSerializer.Serialize(new
            {
                single = new
                {
                    from = ts.From,
                    to = ts.To,
                    id = ts.Id.ToString(),
                    value = ts.Value.ToString()
                }
            });
        }
        else if (ev is TransferBatch tb)
        {
            blockNumber = tb.BlockNumber;
            timestamp = tb.Timestamp;
            transactionIndex = tb.TransactionIndex;
            logIndex = tb.LogIndex;
            transactionHash = tb.TransactionHash;
            batchIndex = tb.BatchIndex;

            graphJson = JsonSerializer.Serialize(new
            {
                batch = new
                {
                    from = tb.From,
                    to = tb.To,
                    id = tb.Id.ToString(),
                    value = tb.Value.ToString()
                }
            });
        }
        else
        {
            // Shouldn't happen if we only call this method for Single/Batch
            throw new InvalidOperationException("BuildStandaloneSummary called with unknown event type.");
        }

        return new TransferSummary(
            blockNumber,
            timestamp,
            transactionIndex,
            logIndex,
            transactionHash,
            // ev.
            graphJson
        );
    }

    /// <summary>
    /// Builds a single JSON for the entire "Stream" that contains all flows and their edges.
    /// Each flow has FlowEdgeId, StreamId, plus a list of "events" that were captured.
    /// </summary>
    private string BuildGraphJson(List<FlowRecord> flows)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("flows");
            foreach (var flow in flows)
            {
                writer.WriteStartObject();
                writer.WriteString("flowEdgeId", flow.FlowEdgeId.ToString());
                writer.WriteNumber("streamId", flow.StreamId);
                writer.WriteStartArray("events");
                foreach (var edge in flow.Edges)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", edge.GetType().Name);

                    // You could add the "from","to","id","value" here if you like
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Represents one "flow" started by FlowEdgesScopeSingleStarted.
    /// We store any TransferSingle/TransferBatch in Edges until we see FlowEdgesScopeLastEnded or StreamCompleted.
    /// </summary>
    private class FlowRecord(UInt256 flowEdgeId, ushort streamId)
    {
        public UInt256 FlowEdgeId { get; } = flowEdgeId;
        public ushort StreamId { get; } = streamId;
        public List<IIndexEvent> Edges { get; } = new();
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
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string erc20Wrapper = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 circlesType = new UInt256(log.Data, true);

        Erc20WrapperAddresses.TryAdd(new Address(erc20Wrapper), null);

        return new ERC20WrapperDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            avatar,
            erc20Wrapper,
            (long)(BigInteger)circlesType
        );
    }

    private ApprovalForAll Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event ApprovalForAll(address indexed account, address indexed operator, bool approved)
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string operatorAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        bool approved = new BigInteger(log.Data) == 1;

        return new ApprovalForAll(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            operatorAddress,
            approved
        );
    }

    private TransferSingle Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)
        string operatorAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string fromAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        // Non-indexed: id (32 bytes) + value (32 bytes)
        UInt256 id = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 value = new UInt256(log.Data.Slice(32), true);

        return new TransferSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
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
        string operatorAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string fromAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        // The first 2 * 32 bytes in log.Data are offsets for the two arrays: ids, values
        var data = log.Data;
        if (data.Length < 64)
        {
            yield break;
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        // Now parse each array
        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

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
        string orgAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string orgName = LogDataStringDecoder.ReadStrings(log.Data)[0];

        return new RegisterOrganization(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            orgAddress,
            orgName
        );
    }

    private RegisterGroup CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterGroup(
        //     address indexed group,
        //     address indexed mintPolicy,
        //     address indexed treasury,
        //     string name,
        //     string symbol
        // )
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string mintPolicy = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string treasury = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        string[] stringData = LogDataStringDecoder.ReadStrings(log.Data);
        string groupName = stringData[0];
        string groupSymbol = stringData[1];

        return new RegisterGroup(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
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
        string humanAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string inviterAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            humanAddress,
            inviterAddress
        );
    }

    private PersonalMint CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event PersonalMint(address indexed to, uint256 amount, uint256 startPeriod, uint256 endPeriod)
        string toAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
        UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);

        return new PersonalMint(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            toAddress,
            amount,
            startPeriod,
            endPeriod
        );
    }

    private Trust CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Trust(address indexed user, address indexed canSendTo, uint256 trustLimit)
        string userAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string canSendToAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 limit = new UInt256(log.Data, true);

        return new Trust(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            userAddress,
            canSendToAddress,
            limit
        );
    }

    private Stopped CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Stopped(address indexed who)
        string address = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new Stopped(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            address
        );
    }

    private Erc20WrapperTransfer Erc20WrapperTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Erc20WrapperTransfer(address indexed from, address indexed to, uint256 value)
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data, true);

        return new Erc20WrapperTransfer(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            from,
            to,
            amount
        );
    }

    private DepositInflationary DepositInflationary(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DepositInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurragedAmount = new UInt256(log.Data.Slice(32), true);

        return new DepositInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            demurragedAmount
        );
    }

    private WithdrawInflationary WithdrawInflationary(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event WithdrawInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurragedAmount = new UInt256(log.Data.Slice(32), true);

        return new WithdrawInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            demurragedAmount
        );
    }

    private DepositDemurraged DepositDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DepositDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 inflationaryAmount = new UInt256(log.Data.Slice(32), true);

        return new DepositDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            inflationaryAmount
        );
    }

    private WithdrawDemurraged WithdrawDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event WithdrawDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 inflationaryAmount = new UInt256(log.Data.Slice(32), true);

        return new WithdrawDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            inflationaryAmount
        );
    }

    private DiscountCost DiscountCost(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DiscountCost(address indexed account, uint256 indexed id, uint256 discountCost)
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);
        UInt256 cost = new UInt256(log.Data, true);

        return new DiscountCost(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            id,
            cost
        );
    }

    private IEnumerable<StreamCompleted> StreamCompleted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event StreamCompleted(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] amounts)
        string operatorAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string fromAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        // log.Data has two offsets (ids, amounts)
        var data = log.Data;
        if (data.Length < 64)
        {
            yield break;
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int amountsOffset = LogDataParsingHelper.ParseOffset(data, 32);

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] amounts = LogDataParsingHelper.ParseUInt256Array(data, amountsOffset);

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
        // event GroupMint(
        //     address indexed sender,
        //     address indexed receiver,
        //     address indexed group,
        //     uint256[] collateral,
        //     uint256[] amounts
        // );
        string sender = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string receiver = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string group = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        var data = log.Data;
        if (data.Length < 64)
        {
            yield break;
        }

        int collateralOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int amountsOffset = LogDataParsingHelper.ParseOffset(data, 32);

        UInt256[] collateral = LogDataParsingHelper.ParseUInt256Array(data, collateralOffset);
        UInt256[] amounts = LogDataParsingHelper.ParseUInt256Array(data, amountsOffset);

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
                sender,
                receiver,
                group,
                collateral[i],
                amounts[i]
            );
        }
    }

    private FlowEdgesScopeSingleStarted FlowEdgesScopeSingleStarted(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId);
        UInt256 flowEdgeId = new UInt256(log.Topics[1].Bytes, true);
        UInt256 streamId = new UInt256(log.Data, true);

        return new FlowEdgesScopeSingleStarted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            flowEdgeId,
            (UInt16)streamId
        );
    }

    private FlowEdgesScopeLastEnded FlowEdgesScopeLastEnded(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event FlowEdgesScopeLastEnded();
        return new FlowEdgesScopeLastEnded(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString()
        );
    }
}