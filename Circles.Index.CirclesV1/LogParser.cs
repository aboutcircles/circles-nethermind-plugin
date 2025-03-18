using System.Collections.Immutable;
using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Utils;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV1;

public class LogParser : ILogParser
{
    private readonly UInt256 _signupBonus = UInt256.Parse("50000000000000000000");
    private readonly string _zeroAddress = "0x0000000000000000000000000000000000000000";

    private readonly ImmutableArray<KnownContract> _knownContracts = ImmutableArray<KnownContract>.Empty;

    public readonly KnownContract HubContract;
    public readonly KnownContract TokenContract;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = false,
        Converters =
        {
            new UInt256AsStringConverter()
        }
    };

    public LogParser(Address v1HubAddress)
    {
        HubContract = new KnownContract("CrcV1", "Hub", [v1HubAddress], [
            (DatabaseSchema.Signup.Topic, CrcSignup),
            (DatabaseSchema.OrganizationSignup.Topic, CrcOrgSignup),
            (DatabaseSchema.HubTransfer.Topic, CrcHubTransfer),
            (DatabaseSchema.Trust.Topic, CrcTrust),
        ]);
        _knownContracts = _knownContracts.Add(HubContract);

        TokenContract = new KnownContract("CrcV1", "Token", [], [
            (DatabaseSchema.Transfer.Topic, Erc20Transfer)
        ]);
        _knownContracts = _knownContracts.Add(TokenContract);
    }

    public Task Init(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        logger.Info("Caching Circles token addresses");

        var selectSignups = new Select(
            "CrcV1",
            "Signup",
            ["token"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = selectSignups.ToSql(database);
        var result = database.Select(sql);
        var rows = result.Rows.ToArray();

        logger.Info($" * Found {rows.Length} Circles token addresses");

        TokenContract.AddInstances(rows.Select(row => new Address(row[0]!.ToString()!)));

        logger.Info("Caching Circles token addresses done");

        return Task.CompletedTask;
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        for (int i = 0; i < _knownContracts.Length; i++)
        {
            var knownContract = _knownContracts[i];

            // Check if the contract has a parser for the topic
            if (!knownContract.TryGetParser(topic, out var parseLogDelegate) || parseLogDelegate == null)
            {
                continue;
            }

            // Check if we know the address
            if (!knownContract.IsKnownAddress(log.Address))
            {
                continue;
            }

            // Parse the log using the appropriate parser
            foreach (var ev in parseLogDelegate(block, receipt, log, logIndex))
            {
                switch (ev)
                {
                    case Signup signup:
                        // The Signup event announces a new Circles token. However, the initial bonus Transfer (50 CRC) 
                        // occurs before this announcement, causing it to be missed. To address this, explicitly search 
                        // the current transaction logs for this bonus Transfer. Also handle cases where the token might 
                        // already be known due to blockchain reorgs, ensuring no duplicate processing.
                        var signupBonusTransfer = receipt.Logs?
                            .Where(logEntry => logEntry.Topics.Length > 0
                                               && logEntry.Topics[0] == DatabaseSchema.Transfer.Topic
                                               && !TokenContract.IsKnownAddress(logEntry.Address))
                            .SelectMany(logEntry => Erc20Transfer(block, receipt, logEntry, logIndex))
                            .Cast<Transfer>()
                            .FirstOrDefault(transfer => transfer.From == _zeroAddress
                                                        && transfer.To == signup.User
                                                        && transfer.Value == _signupBonus);
                        // First return the Signup event
                        yield return ev;

                        // Then return the bonus Transfer event if found
                        if (signupBonusTransfer != null)
                        {
                            yield return signupBonusTransfer;
                        }

                        break;
                    default:
                        yield return ev;
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 1) Identify all HubTransfer events + gather all Transfers.
    /// 2) If there are no hub transfers => each Transfer is a stand-alone summary (hops=1).
    /// 3) If there are one or more hub transfers => 
    ///    - build an adjacency ignoring amounts, 
    ///    - for each hub, DFS to find *all* routes from hub.from->hub.to, 
    ///    - collect all edges in usedEdges, 
    ///    - produce one TransferSummary per hub with a JSON that has { from, to, amount, edges:[...] }, 
    ///    - produce stand-alone summaries for leftover edges.
    /// </summary>
    public IEnumerable<Either<IIndexEvent, IParsedCallData>> ParseTransaction(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        IIndexEvent[] events)
    {
        // 1) Gather all hub transfers + gather all Transfers
        var hubTransfers = new List<HubTransfer>();
        var allTransfers = new List<Transfer>(events.Length);

        for (int i = 0; i < events.Length; i++)
        {
            switch (events[i])
            {
                case HubTransfer ht:
                    hubTransfers.Add(ht);
                    break;

                case Transfer t:
                    allTransfers.Add(t);
                    break;
            }
        }

        // 2) If no hub => each Transfer stands alone
        if (hubTransfers.Count == 0)
        {
            var allTransferJson =
                JsonSerializer.Serialize(allTransfers.Cast<IIndexedEventV1>(), _jsonSerializerOptions);

            for (int i = 0; i < allTransfers.Count; i++)
            {
                var t = allTransfers[i];

                yield return Either<IIndexEvent, IParsedCallData>.FromLeft(new TransferSummary(
                    t.BlockNumber,
                    t.Timestamp,
                    t.TransactionIndex,
                    t.LogIndex,
                    0,
                    t.TransactionHash,
                    t.Emitter,
                    t.From,
                    t.To,
                    ConversionUtils.CirclesToAttoCircles(
                        ConversionUtils.CrcToCircles(
                            ConversionUtils.AttoCirclesToCircles(t.Value),
                            t.Timestamp)),
                    allTransferJson
                ));
            }

            yield break;
        }

        // 3) We do have one or more hubs => build adjacency.
        var adjacency = new Dictionary<string, List<Transfer>>();
        for (int i = 0; i < allTransfers.Count; i++)
        {
            var t = allTransfers[i];
            if (!adjacency.TryGetValue(t.From, out var list))
            {
                list = new List<Transfer>();
                adjacency[t.From] = list;
            }

            list.Add(t);
        }

        var usedEdgesGlobal = new HashSet<Transfer>();

        for (int h = 0; h < hubTransfers.Count; h++)
        {
            var hubTransfer = hubTransfers[h];
            var hubFrom = hubTransfer.From.ToLowerInvariant();
            var hubTo = hubTransfer.To.ToLowerInvariant();

            var usedEdges = new HashSet<Transfer>();
            var pathStack = new List<Transfer>();

            void Dfs(string current, HashSet<string> visited)
            {
                if (string.Equals(current, hubTo))
                {
                    for (int p = 0; p < pathStack.Count; p++)
                    {
                        usedEdges.Add(pathStack[p]);
                    }

                    return;
                }

                if (!adjacency.TryGetValue(current, out var edges))
                {
                    return;
                }

                for (int e = 0; e < edges.Count; e++)
                {
                    var edge = edges[e];
                    var next = edge.To;
                    if (visited.Contains(next))
                    {
                        continue;
                    }

                    pathStack.Add(edge);
                    visited.Add(next);

                    Dfs(next, visited);

                    visited.Remove(next);
                    pathStack.RemoveAt(pathStack.Count - 1);
                }
            }

            // DFS from hubTransfer.from
            var visitedSet = new HashSet<string>() { hubFrom };
            Dfs(hubFrom, visitedSet);

            // Mark all edges from this hub in the global usedEdges
            foreach (var edge in usedEdges)
                usedEdgesGlobal.Add(edge);

            // Produce the hub TransferSummary
            var hubTransferEdgesJson =
                JsonSerializer.Serialize(usedEdges.Cast<IIndexedEventV1>(), _jsonSerializerOptions);

            yield return Either<IIndexEvent, IParsedCallData>.FromLeft(new TransferSummary(
                hubTransfer.BlockNumber,
                hubTransfer.Timestamp,
                hubTransfer.TransactionIndex,
                hubTransfer.LogIndex,
                0,
                hubTransfer.TransactionHash,
                hubTransfer.Emitter,
                hubTransfer.From,
                hubTransfer.To,
                ConversionUtils.CirclesToAttoCircles(
                    ConversionUtils.CrcToCircles(
                        ConversionUtils.AttoCirclesToCircles(hubTransfer.Amount),
                        hubTransfer.Timestamp)),
                hubTransferEdgesJson
            ));
        }

        // 4) For each Transfer not used by any hub route, stand-alone
        var standAloneTransfers = new List<Transfer>();
        for (int i = 0; i < allTransfers.Count; i++)
        {
            var t = allTransfers[i];
            if (!usedEdgesGlobal.Contains(t))
            {
                standAloneTransfers.Add(t);
            }
        }

        var standAloneTransfersJson =
            JsonSerializer.Serialize(standAloneTransfers.Cast<IIndexedEventV1>(), _jsonSerializerOptions);

        for (int i = 0; i < standAloneTransfers.Count; i++)
        {
            var standAloneTransfer = standAloneTransfers[i];
            
            yield return Either<IIndexEvent, IParsedCallData>.FromLeft(new TransferSummary(
                standAloneTransfer.BlockNumber,
                standAloneTransfer.Timestamp,
                standAloneTransfer.TransactionIndex,
                standAloneTransfer.LogIndex,
                0,
                standAloneTransfer.TransactionHash,
                standAloneTransfer.Emitter,
                standAloneTransfer.From,
                standAloneTransfer.To,
                ConversionUtils.CirclesToAttoCircles(
                    ConversionUtils.CrcToCircles(
                        ConversionUtils.AttoCirclesToCircles(standAloneTransfer.Value),
                        standAloneTransfer.Timestamp)),
                standAloneTransfersJson
            ));
        }
    }

    /// <summary>
    /// event Transfer(address indexed from, address indexed to, uint256 value);
    /// </summary>
    private static IEnumerable<IIndexEvent> Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // parse addresses from the 2 topics
        string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        // parse single 256-bit value from log.Data
        UInt256 value = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new Transfer(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            log.Address.ToString(true, false),
            from,
            to,
            value
        );
    }

    /// <summary>
    /// event OrganizationSignup(address indexed organization);
    /// </summary>
    private static IEnumerable<IIndexEvent> CrcOrgSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string org = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        yield return new OrganizationSignup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            org
        );
    }

    /// <summary>
    /// event Trust(address indexed canSendTo, address indexed user, uint256 limit);
    /// </summary>
    private static IEnumerable<IIndexEvent> CrcTrust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string canSendTo = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string user = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        int limit = (int)LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new Trust(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            user,
            canSendTo,
            limit
        );
    }

    /// <summary>
    /// event HubTransfer(address indexed from, address indexed to, uint256 amount);
    /// </summary>
    private static IEnumerable<IIndexEvent> CrcHubTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        UInt256 amount = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new HubTransfer(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            from,
            to,
            amount
        );
    }

    private static IEnumerable<IIndexEvent> CrcSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        if (log.Data.Length < 32)
        {
            throw new ArgumentException("Not enough data to parse the 'address token' in Signup event.");
        }

        Address tokenAddress = new Address(log.Data.Slice(12));

        yield return new Signup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            user,
            tokenAddress.ToString(true, false)
        );
    }
}