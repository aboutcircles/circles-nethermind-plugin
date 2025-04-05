using System.Collections.Concurrent;
using System.Text.Json;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.CirclesV1;

public class LogParser(Address v1HubAddress) : ILogParser
{
    public static readonly ConcurrentDictionary<Address, object?> CirclesTokenAddresses = new();

    private readonly Hash256 _transferTopic = new(DatabaseSchema.Transfer.Topic);
    private readonly Hash256 _signupTopic = new(DatabaseSchema.Signup.Topic);
    private readonly Hash256 _organizationSignupTopic = new(DatabaseSchema.OrganizationSignup.Topic);
    private readonly Hash256 _hubTransferTopic = new(DatabaseSchema.HubTransfer.Topic);
    private readonly Hash256 _trustTopic = new(DatabaseSchema.Trust.Topic);

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = false,
        Converters =
        {
            new UInt256AsStringConverter()
        }
    };

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
    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        // // 1) Gather all hub transfers + gather all Transfers
        // var hubTransfers = new List<HubTransfer>();
        // var allTransfers = new List<Transfer>(events.Count);
        //
        // for (int i = 0; i < events.Count; i++)
        // {
        //     switch (events[i])
        //     {
        //         case HubTransfer ht:
        //             hubTransfers.Add(ht);
        //             break;
        //
        //         case Transfer t:
        //             allTransfers.Add(t);
        //             break;
        //     }
        // }
        //
        // // 2) If no hub => each Transfer stands alone
        // if (hubTransfers.Count == 0)
        // {
        //     var allTransferJson =
        //         JsonSerializer.Serialize(allTransfers.Cast<IIndexedEventV1>(), _jsonSerializerOptions);
        //
        //     for (int i = 0; i < allTransfers.Count; i++)
        //     {
        //         var t = allTransfers[i];
        //
        //         yield return new TransferSummary(
        //             t.BlockNumber,
        //             t.Timestamp,
        //             t.TransactionIndex,
        //             t.LogIndex,
        //             t.TransactionHash,
        //             t.Emitter,
        //             t.From,
        //             t.To,
        //             t.Value,
        //             allTransferJson
        //         );
        //     }
        //
        //     yield break;
        // }
        //
        // // 3) We do have one or more hubs => build adjacency.
        // var adjacency = new Dictionary<string, List<Transfer>>();
        // for (int i = 0; i < allTransfers.Count; i++)
        // {
        //     var t = allTransfers[i];
        //     if (!adjacency.TryGetValue(t.From, out var list))
        //     {
        //         list = new List<Transfer>();
        //         adjacency[t.From] = list;
        //     }
        //
        //     list.Add(t);
        // }
        //
        // var usedEdgesGlobal = new HashSet<Transfer>();
        //
        // for (int h = 0; h < hubTransfers.Count; h++)
        // {
        //     var hubTransfer = hubTransfers[h];
        //     var hubFrom = hubTransfer.From.ToLowerInvariant();
        //     var hubTo = hubTransfer.To.ToLowerInvariant();
        //     
        //     var usedEdges = new HashSet<Transfer>();
        //     var pathStack = new List<Transfer>();
        //
        //     void Dfs(string current, HashSet<string> visited)
        //     {
        //         if (string.Equals(current, hubTo))
        //         {
        //             for (int p = 0; p < pathStack.Count; p++)
        //             {
        //                 usedEdges.Add(pathStack[p]);
        //             }
        //             return;
        //         }
        //         
        //         if (!adjacency.TryGetValue(current, out var edges))
        //         {
        //             return;
        //         }
        //         
        //         for (int e = 0; e < edges.Count; e++)
        //         {
        //             var edge = edges[e];
        //             var next = edge.To;
        //             if (visited.Contains(next))
        //             {
        //                 continue;
        //             }
        //             pathStack.Add(edge);
        //             visited.Add(next);
        //             
        //             Dfs(next, visited);
        //             
        //             visited.Remove(next);
        //             pathStack.RemoveAt(pathStack.Count - 1);
        //         }
        //     }
        //
        //     // DFS from hubTransfer.from
        //     var visitedSet = new HashSet<string>() { hubFrom };
        //     Dfs(hubFrom, visitedSet);
        //
        //     // Mark all edges from this hub in the global usedEdges
        //     foreach (var edge in usedEdges)
        //         usedEdgesGlobal.Add(edge);
        //
        //     // Produce the hub TransferSummary
        //     var hubTransferEdgesJson =
        //         JsonSerializer.Serialize(usedEdges.Cast<IIndexedEventV1>(), _jsonSerializerOptions);
        //
        //     yield return new TransferSummary(
        //         hubTransfer.BlockNumber,
        //         hubTransfer.Timestamp,
        //         hubTransfer.TransactionIndex,
        //         hubTransfer.LogIndex,
        //         hubTransfer.TransactionHash,
        //         hubTransfer.Emitter,
        //         hubTransfer.From,
        //         hubTransfer.To,
        //         hubTransfer.Amount,
        //         hubTransferEdgesJson
        //     );
        // }
        //
        // // 4) For each Transfer not used by any hub route, stand-alone
        // var standAloneTransfers = new List<Transfer>();
        // for (int i = 0; i < allTransfers.Count; i++)
        // {
        //     var t = allTransfers[i];
        //     if (!usedEdgesGlobal.Contains(t))
        //     {
        //         standAloneTransfers.Add(t);
        //     }
        // }
        //
        // var standAloneTransfersJson =
        //     JsonSerializer.Serialize(standAloneTransfers.Cast<IIndexedEventV1>(), _jsonSerializerOptions);
        //
        // for (int i = 0; i < standAloneTransfers.Count; i++)
        // {
        //     var standAloneTransfer = standAloneTransfers[i];
        //     yield return new TransferSummary(
        //         standAloneTransfer.BlockNumber,
        //         standAloneTransfer.Timestamp,
        //         standAloneTransfer.TransactionIndex,
        //         standAloneTransfer.LogIndex,
        //         standAloneTransfer.TransactionHash,
        //         standAloneTransfer.Emitter,
        //         standAloneTransfer.From,
        //         standAloneTransfer.To,
        //         standAloneTransfer.Value,
        //         standAloneTransfersJson
        //     );
        // }
        yield break;
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        List<IIndexEvent> events = new();
        if (log.Topics.Length == 0)
        {
            return events;
        }

        var topic = log.Topics[0];
        if (topic == _transferTopic &&
            CirclesTokenAddresses.ContainsKey(log.Address))
        {
            events.Add(Erc20Transfer(block, receipt, log, logIndex));
        }

        if (log.Address == v1HubAddress)
        {
            if (topic == _signupTopic)
            {
                var signupEvents = CrcSignup(block, receipt, log, logIndex);
                foreach (var signupEvent in signupEvents)
                {
                    events.Add(signupEvent);
                }
            }

            if (topic == _organizationSignupTopic)
            {
                events.Add(CrcOrgSignup(block, receipt, log, logIndex));
            }

            if (topic == _hubTransferTopic)
            {
                events.Add(CrcHubTransfer(block, receipt, log, logIndex));
            }

            if (topic == _trustTopic)
            {
                events.Add(CrcTrust(block, receipt, log, logIndex));
            }
        }

        return events;
    }

    /// <summary>
    /// event Transfer(address indexed from, address indexed to, uint256 value);
    /// </summary>
    private IIndexEvent Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // parse addresses from the 2 topics
        string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        // parse single 256-bit value from log.Data
        UInt256 value = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new Transfer(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
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
    private IIndexEvent CrcOrgSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string org = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new OrganizationSignup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            org
        );
    }

    /// <summary>
    /// event Trust(address indexed canSendTo, address indexed user, uint256 limit);
    /// </summary>
    private IIndexEvent CrcTrust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string canSendTo = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string user = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        int limit = (int)LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new Trust(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
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
    private IIndexEvent CrcHubTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        UInt256 amount = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        return new HubTransfer(
            receipt.BlockNumber,
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

    private IEnumerable<IIndexEvent> CrcSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        if (log.Data.Length < 32)
        {
            throw new ArgumentException("Not enough data to parse the 'address token' in Signup event.");
        }

        Address tokenAddress = new Address(log.Data.Slice(12));

        var signupEvent = new Signup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            user,
            tokenAddress.ToString(true, false)
        );

        // Attempt to register the token address
        bool isNewToken = CirclesTokenAddresses.TryAdd(tokenAddress, null);
        if (!isNewToken)
        {
            // Already known => only return the Signup event
            return new[] { signupEvent };
        }

        // If new, find the first matching Transfer event in this tx
        IIndexEvent? signupBonusEvent = null;
        for (int i = 0; i < receipt.Logs!.Length; i++)
        {
            var repeatedLogEntry = receipt.Logs[i];
            if (repeatedLogEntry.Address != tokenAddress)
                continue;

            if (repeatedLogEntry.Topics.Length > 0
                && repeatedLogEntry.Topics[0] == _transferTopic)
            {
                signupBonusEvent = Erc20Transfer(block, receipt, repeatedLogEntry, i);
                break;
            }
        }

        return signupBonusEvent == null
            ? new[] { signupEvent }
            : new[] { signupEvent, signupBonusEvent };
    }
}