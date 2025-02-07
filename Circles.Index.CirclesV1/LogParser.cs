using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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
        IReadOnlyList<IIndexEvent> events)
    {
        // 1) Gather all hub transfers + gather all Transfers
        var hubTransfers = new List<HubTransfer>();
        var allTransfers = new List<Transfer>(events.Count);

        for (int i = 0; i < events.Count; i++)
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
            for (int i = 0; i < allTransfers.Count; i++)
            {
                var t = allTransfers[i];
                string json = BuildTransferJson(t.From, t.To, t.Value, t.TokenAddress);

                yield return new TransferSummary(
                    t.BlockNumber,
                    t.Timestamp,
                    t.TransactionIndex,
                    t.LogIndex,
                    t.TransactionHash,
                    t.TokenAddress,
                    t.From,
                    t.To,
                    t.Value,
                    1,
                    json
                );
            }

            yield break;
        }

        // 3) We do have one or more hubs => build adjacency ignoring amounts (normalize addresses).
        var adjacency = new Dictionary<string, List<Transfer>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allTransfers.Count; i++)
        {
            var t = allTransfers[i];

            // convert from and to to lower
            var fromLower = t.From.ToLowerInvariant();
            var toLower = t.To.ToLowerInvariant();

            // Rebuild the Transfer with normalized addresses
            t = new Transfer(
                t.BlockNumber,
                t.Timestamp,
                t.TransactionIndex,
                t.LogIndex,
                t.TransactionHash,
                t.TokenAddress,
                fromLower,
                toLower,
                t.Value
            );

            if (!adjacency.TryGetValue(t.From, out var list))
            {
                list = new List<Transfer>();
                adjacency[t.From] = list;
            }

            list.Add(t);

            // Replace the entry in allTransfers with the normalized version
            allTransfers[i] = t;
        }

        // We'll collect globally used edges so we can exclude them from stand-alone
        var usedEdgesGlobal = new HashSet<Transfer>();

        // For each hub transfer, do a DFS to find all routes
        foreach (var hubTransfer in hubTransfers)
        {
            var hubFrom = hubTransfer.From.ToLowerInvariant();
            var hubTo = hubTransfer.To.ToLowerInvariant();

            var usedEdges = new HashSet<Transfer>();
            var pathStack = new List<Transfer>();

            void Dfs(string current, HashSet<string> visited)
            {
                if (string.Equals(current, hubTo, StringComparison.OrdinalIgnoreCase))
                {
                    // Mark everything in pathStack as used
                    for (int p = 0; p < pathStack.Count; p++)
                    {
                        usedEdges.Add(pathStack[p]);
                    }

                    return;
                }

                if (!adjacency.TryGetValue(current, out var edges))
                    return;

                for (int e = 0; e < edges.Count; e++)
                {
                    var edge = edges[e];
                    var next = edge.To;
                    if (visited.Contains(next))
                        continue;

                    pathStack.Add(edge);
                    visited.Add(next);

                    Dfs(next, visited);

                    // backtrack
                    visited.Remove(next);
                    pathStack.RemoveAt(pathStack.Count - 1);
                }
            }

            // DFS from hubTransfer.from
            {
                var visitedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hubFrom };
                Dfs(hubFrom, visitedSet);
            }

            // Build a JSON for this hub
            string hubJson = BuildHubJson(hubFrom, hubTo, hubTransfer.Amount, usedEdges);

            // Mark all edges from this hub in the global usedEdges
            foreach (var edge in usedEdges)
                usedEdgesGlobal.Add(edge);

            // Produce the hub TransferSummary
            yield return new TransferSummary(
                hubTransfer.BlockNumber,
                hubTransfer.Timestamp,
                hubTransfer.TransactionIndex,
                hubTransfer.LogIndex,
                hubTransfer.TransactionHash,
                Address.Zero.ToString(true, false),
                hubTransfer.From,
                hubTransfer.To,
                hubTransfer.Amount,
                usedEdges.Count,
                hubJson
            );
        }

        // 4) For each Transfer not used by any hub route, stand-alone
        for (int i = 0; i < allTransfers.Count; i++)
        {
            var t = allTransfers[i];
            if (!usedEdgesGlobal.Contains(t))
            {
                string json = BuildTransferJson(t.From, t.To, t.Value, t.TokenAddress);

                yield return new TransferSummary(
                    t.BlockNumber,
                    t.Timestamp,
                    t.TransactionIndex,
                    t.LogIndex,
                    t.TransactionHash,
                    t.TokenAddress,
                    t.From,
                    t.To,
                    t.Value,
                    1,
                    json
                );
            }
        }
    }

    /// <summary>
    /// Stand-alone JSON
    /// {
    ///   "from":"...",
    ///   "to":"...",
    ///   "amount":"...",
    ///   "token":"..."
    /// }
    /// </summary>
    private static string BuildTransferJson(string from, string to, UInt256 amount, string token)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("from", from);
            writer.WriteString("to", to);
            writer.WriteString("amount", amount.ToString());
            writer.WriteString("token", token);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Hub JSON
    /// {
    ///   "from":"...",
    ///   "to":"...",
    ///   "amount":"...",
    ///   "edges":[
    ///     { "from":"...", "to":"...", "amount":"...", "token":"..." },
    ///     ...
    ///   ]
    /// }
    /// </summary>
    private static string BuildHubJson(
        string fromAddr,
        string toAddr,
        UInt256 amount,
        HashSet<Transfer> usedEdges)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            writer.WriteString("from", fromAddr);
            writer.WriteString("to", toAddr);
            writer.WriteString("amount", amount.ToString());

            writer.WriteStartArray("edges");
            foreach (var edge in usedEdges)
            {
                writer.WriteStartObject();
                writer.WriteString("from", edge.From);
                writer.WriteString("to", edge.To);
                writer.WriteString("amount", edge.Value.ToString());
                writer.WriteString("token", edge.TokenAddress);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
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