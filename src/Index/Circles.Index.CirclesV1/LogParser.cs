using System.Collections.Immutable;
using System.Numerics;
using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Npgsql;

namespace Circles.Index.CirclesV1;

public class LogParser(Address v1HubAddress) : ILogParser
{
    // Used internally by V1 LogParser to map tokens to their owners
    // Maintained for fast lookup during log parsing
    public static readonly RollbackCache<Address, Address> CirclesV1TokenOwnersByToken =
        new("CirclesV1TokenOwnersByToken");

    // Used internally by V1 LogParser to map owners to their tokens
    // Maintained for fast lookup during log parsing
    public static readonly RollbackCache<Address, Address> CirclesV1TokensByTokenOwner =
        new("CirclesV1TokensByTokenOwner");

    // Used to enrich V1 avatar events with avatar metadata
    // Maintained for fast lookup during log parsing
    public static readonly RollbackCache<string, (string Type, string? TokenAddress)>
        V1Avatars = new("V1Avatars");

    public IRollbackCache[] Caches { get; } =
    [
        CirclesV1TokenOwnersByToken, CirclesV1TokensByTokenOwner, V1Avatars
    ];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        InitV1TokenAddresses(logger, database);
        InitAvatarsCache(logger, database);

        return Task.CompletedTask;
    }

    private void InitAvatarsCache(InterfaceLogger logger, IDatabase database)
    {
        var registerGroupEvents = new Select(
            "V_CrcV1",
            "Avatars",
            ["user", "type", "token"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = registerGroupEvents.ToSql(database);
        var result = database.Select(sql);
        var rows = result.Rows.ToArray();

        var seed = new Dictionary<string, (string Type, string? Token)>(rows.Length + 10_000);
        foreach (var row in rows)
        {
            string user = row[0]?.ToString() ?? throw new InvalidOperationException("User is null");
            string type = row[1]?.ToString() ?? throw new InvalidOperationException("Type is null");
            string? token = row[2] is not DBNull and not null
                ? row[2]!.ToString()
                : null;

            seed.Add(user, (type, token));
        }

        V1Avatars.Seed(seed);

        logger.Info($" * Cached {seed.Count} V1 avatars");
    }


    private static void InitV1TokenAddresses(InterfaceLogger logger, IDatabase database)
    {
        var selectSignups = new Select(
            "CrcV1",
            "Signup",
            ["user", "token"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = selectSignups.ToSql(database);
        var result = database.Select(sql);
        var rows = result.Rows.ToArray();

        var seed = new Dictionary<Address, Address>(rows.Length + 25_000);
        var seed2 = new Dictionary<Address, Address>(rows.Length + 25_000);
        foreach (var row in rows)
        {
            var userAddress = new Address(row[0]?.ToString() ?? throw new InvalidOperationException("User address is null"));
            var tokenAddress = new Address(row[1]?.ToString() ?? throw new InvalidOperationException("Token address is null"));
            seed[tokenAddress] = userAddress;
            seed2[userAddress] = tokenAddress;
        }

        CirclesV1TokenOwnersByToken.Seed(seed);
        CirclesV1TokensByTokenOwner.Seed(seed2);

        logger.Info($" * Cached {seed.Count} Circles token addresses");
    }

    private readonly Hash256 _transferTopic = new(DatabaseSchema.Transfer.Topic);
    private readonly Hash256 _signupTopic = new(DatabaseSchema.Signup.Topic);
    private readonly Hash256 _organizationSignupTopic = new(DatabaseSchema.OrganizationSignup.Topic);
    private readonly Hash256 _hubTransferTopic = new(DatabaseSchema.HubTransfer.Topic);
    private readonly Hash256 _trustTopic = new(DatabaseSchema.Trust.Topic);

    // private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    // {
    //     WriteIndented = false,
    //     Converters =
    //     {
    //         new UInt256AsStringConverter()
    //     }
    // };

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
            CirclesV1TokenOwnersByToken.ContainsKey(log.Address))
        {
            var transferEvent = Erc20Transfer(block, receipt, log, logIndex);
            if (transferEvent != null)
            {
                events.Add(transferEvent);
            }
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
    private IIndexEvent? Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        try
        {
            // parse addresses from the 2 topics
            string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

            // parse single 256-bit value from log.Data
            UInt256 value = LogDataParsingHelper.ParseSingleUInt256(log.Data);

            // Validate data
            if (string.IsNullOrEmpty(from) || !from.StartsWith("0x") || from.Length != 42)
            {
                Console.WriteLine($"Invalid 'from' address in Transfer event at block {receipt.BlockNumber}, tx {receipt.TxHash}: {from}");
                return null; // Skip invalid event
            }
            if (string.IsNullOrEmpty(to) || !to.StartsWith("0x") || to.Length != 42)
            {
                Console.WriteLine($"Invalid 'to' address in Transfer event at block {receipt.BlockNumber}, tx {receipt.TxHash}: {to}");
                return null; // Skip invalid event
            }

            // Balance tracking removed - RPC fetches live balances from Nethermind
            // and Pathfinder loads from database views

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
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Transfer event at block {receipt.BlockNumber}, tx {receipt.TxHash}: {ex.Message}");
            return null; // Skip on error
        }
    }

    /// <summary>
    /// event OrganizationSignup(address indexed organization);
    /// </summary>
    private IIndexEvent CrcOrgSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string org = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        V1Avatars.Add(block.Number, org, ("CrcV1_OrganizationSignup", null));

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
        string tokenAddressString = tokenAddress.ToString(true, false);

        Address userAddress = new Address(user);

        var signupEvent = new Signup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            user,
            tokenAddressString
        );

        // Attempt to register the token address
        CirclesV1TokensByTokenOwner.Add(block.Number, userAddress, tokenAddress);
        V1Avatars.Add(block.Number, user, ("CrcV1_Signup", tokenAddressString));

        bool isNewToken = CirclesV1TokenOwnersByToken.Add(block.Number, tokenAddress, userAddress);
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