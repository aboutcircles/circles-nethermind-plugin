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
    /// 1) Identify at most one HubTransfer + gather all Transfers.
    /// 2) If no hub => each Transfer is a stand-alone summary (hops=1).
    /// 3) If a hub => 
    ///    - build an adjacency ignoring amounts, 
    ///    - DFS to find *all* routes from hub.from->hub.to, 
    ///    - collect all edges in usedEdges, 
    ///    - produce one TransferSummary with a JSON that has { from, to, amount, edges:[...] }, 
    ///    - produce stand-alone summaries for leftover edges.
    /// </summary>
    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        IReadOnlyList<IIndexEvent> events)
    {
        // 1) Identify hubTransfer and gather Transfers
        HubTransfer? hubTransfer = null;
        var allTransfers = new List<Transfer>(events.Count);

        for (int i = 0; i < events.Count; i++)
        {
            switch (events[i])
            {
                case HubTransfer ht:
                    if (hubTransfer == null)
                        hubTransfer = ht; // keep only the first
                    break;

                case Transfer t:
                    allTransfers.Add(t);
                    break;
            }
        }

        // 2) If no hub => each Transfer stands alone
        if (hubTransfer == null)
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

        // 3) We do have a hub => build adjacency ignoring amounts.
        //    Normalize addresses to ensure consistent matching (lowercase).
        //    Then do a DFS collecting all edges that appear on any route from hub.from -> hub.to.
        var adjacency = new Dictionary<string, List<Transfer>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allTransfers.Count; i++)
        {
            var t = allTransfers[i];

            // convert from and to to lower
            var fromLower = t.From.ToLowerInvariant();
            var toLower = t.To.ToLowerInvariant();

            // Rebuild the Transfer with normalized addresses (optional, but helpful if you suspect case mismatches)
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

        // Also normalize hub addresses
        var hubFrom = hubTransfer.From.ToLowerInvariant();
        var hubTo = hubTransfer.To.ToLowerInvariant();

        // We'll do a DFS enumerating *all* possible routes from hubFrom -> hubTo.
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

        // Start DFS from the hub's "from" address
        {
            var visitedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hubFrom };
            Dfs(hubFrom, visitedSet);
        }

        // 4) Build a JSON for the hub with { from, to, amount, edges: [...] }
        string hubJson = BuildHubJson(hubFrom, hubTo, hubTransfer.Amount, usedEdges);

        yield return new TransferSummary(
            hubTransfer.BlockNumber,
            hubTransfer.Timestamp,
            hubTransfer.TransactionIndex,
            hubTransfer.LogIndex,
            hubTransfer.TransactionHash,
            Address.Zero.ToString(true, false),
            // Use the un-lowercased or the normalized hubFrom/hubTo as you like. 
            // Typically we keep the original for display:
            hubTransfer.From,
            hubTransfer.To,
            hubTransfer.Amount,
            usedEdges.Count,
            hubJson
        );

        // 5) For each Transfer not used, stand-alone
        for (int i = 0; i < allTransfers.Count; i++)
        {
            var t = allTransfers[i];
            if (!usedEdges.Contains(t))
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

    private IIndexEvent Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 value = new(log.Data, true);

        return new Transfer(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , log.Address.ToString(true, false)
            , from
            , to
            , value);
    }

    private IIndexEvent CrcOrgSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string org = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new OrganizationSignup(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , org);
    }

    private IIndexEvent CrcTrust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string canSendTo = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string user = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        int limit = new UInt256(log.Data, true).ToInt32(CultureInfo.InvariantCulture);

        return new Trust(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , canSendTo
            , limit);
    }

    private IIndexEvent CrcHubTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new(log.Data, true);

        return new HubTransfer(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , from
            , to
            , amount);
    }

    private IEnumerable<IIndexEvent> CrcSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // Extract user address and token address from the log entry.
        string user = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        Address tokenAddress = new Address(log.Data.Slice(12));

        // Generate the Signup event.
        IIndexEvent signupEvent = new Signup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            user,
            tokenAddress.ToString(true, false));

        // Attempt to register the token address. If the token is already known, skip
        // generating the transfer event to avoid duplicates (e.g., during reorgs).
        bool isNewToken = CirclesTokenAddresses.TryAdd(tokenAddress, null);

        if (!isNewToken)
        {
            // If the token was already known, return only the Signup event.
            return new[] { signupEvent };
        }

        // If the token is new, attempt to locate and generate the corresponding transfer event.
        IIndexEvent? signupBonusEvent = null;
        for (int i = 0; i < receipt.Logs!.Length; i++)
        {
            var repeatedLogEntry = receipt.Logs[i];
            if (repeatedLogEntry.Address != tokenAddress)
            {
                continue; // Skip logs unrelated to the token address.
            }

            if (repeatedLogEntry.Topics[0] == _transferTopic)
            {
                signupBonusEvent = Erc20Transfer(block, receipt, repeatedLogEntry, i);
                break; // Only one matching transfer event is expected.
            }
        }

        // Return the Signup event, along with the Transfer event if it was found.
        return signupBonusEvent == null
            ? new[] { signupEvent }
            : new[] { signupEvent, signupBonusEvent };
    }
}