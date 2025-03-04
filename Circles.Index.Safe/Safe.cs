using System.Collections.Concurrent;
using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Safe;

public record ProxyCreation(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Proxy,
    string? Singleton
) : IIndexEvent;

public record SafeSetup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string SafeAddress,
    string Initiator,
    string Owner,
    UInt256 Threshold,
    string Initializer,
    string FallbackHandler
) : IIndexEvent;

public record AddedOwner(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string SafeAddress,
    string Owner
) : IIndexEvent;

public record RemovedOwner(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string SafeAddress,
    string Owner
) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema ProxyCreation = EventSchema.FromSolidity(
        "Safe",
        "event ProxyCreation(address indexed proxy, address singleton)"
    );

    public static readonly EventSchema SafeSetup = new(
        "Safe",
        "SafeSetup",
        Keccak.Compute("SafeSetup(address,address[],uint256,address,address)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("safeAddress", ValueTypes.Address, true),
            new("initiator", ValueTypes.Address, true),
            new("owner", ValueTypes.Address, true),
            new("threshold", ValueTypes.BigInt, false),
            new("initializer", ValueTypes.Address, true),
            new("fallbackHandler", ValueTypes.Address, true)
        ]
    );

    public static readonly EventSchema AddedOwner = new(
        "Safe",
        "AddedOwner",
        Keccak.Compute("AddedOwner(address)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("safeAddress", ValueTypes.Address, true),
            new("owner", ValueTypes.Address, true),
        ]
    );

    public static readonly EventSchema RemovedOwner = new(
        "Safe",
        "RemovedOwner",
        Keccak.Compute("RemovedOwner(address)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("safeAddress", ValueTypes.Address, true),
            new("owner", ValueTypes.Address, true),
        ]
    );

    public static readonly EventSchema V_Safe_Owners = new("V_Safe", "Owners", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("safeAddress", ValueTypes.Address, true),
        new("owner", ValueTypes.Address, true)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view ""V_Safe_Owners"" AS
                WITH events AS (
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex"",
                        1 AS init_count,
                        0 AS add_count,
                        0 AS remove_count
                    FROM ""Safe_SafeSetup""
                
                    UNION ALL
                
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex"",
                        0 AS init_count,
                        1 AS add_count,
                        0 AS remove_count
                    FROM ""Safe_AddedOwner""
                
                    UNION ALL
                
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex"",
                        0 AS init_count,
                        0 AS add_count,
                        1 AS remove_count
                    FROM ""Safe_RemovedOwner""
                ),
                final_owners AS (
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        SUM(init_count) + SUM(add_count) - SUM(remove_count) AS final_count
                    FROM events
                    GROUP BY 1, 2
                    HAVING SUM(init_count) + SUM(add_count) - SUM(remove_count) > 0
                ),
                
                -- For each (safeAddress, owner) pair, select the event with the highest block/tx/log
                -- i.e., the most recent event that changed that pair.
                last_change AS (
                    SELECT DISTINCT ON (""safeAddress"", ""owner"")
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex""
                    FROM events
                    ORDER BY
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"" DESC,
                        ""timestamp"" DESC,
                        ""transactionIndex"" DESC,
                        ""logIndex"" DESC
                )
                SELECT
                    l.""blockNumber"",     
                    l.""timestamp"",       
                    l.""transactionIndex"",
                    l.""logIndex"",        
                    f.""safeAddress"",
                    f.""owner""
                FROM final_owners f
                         JOIN last_change l
                              ON f.""safeAddress"" = l.""safeAddress""
                                  AND f.""owner""       = l.""owner""
                ORDER BY f.""safeAddress"", f.""owner"";
         ")
    };

    public DatabaseSchema()
    {
        AddMappings<ProxyCreation>(
            ns: "Safe",
            table: "ProxyCreation",
            eventSchema: ProxyCreation,
            databaseFieldMap:
            [
                ("proxy", e => e.Proxy),
                ("singleton", e => e.Singleton)
            ]
        );

        AddMappings<SafeSetup>(
            ns: "Safe",
            table: "SafeSetup",
            eventSchema: SafeSetup,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("safeAddress", e => e.SafeAddress),
                ("initiator", e => e.Initiator),
                ("owner", e => e.Owner),
                ("threshold", e => (BigInteger)e.Threshold),
                ("initializer", e => e.Initializer),
                ("fallbackHandler", e => e.FallbackHandler)
            ]
        );

        AddMappings<AddedOwner>(
            ns: "Safe",
            table: "AddedOwner",
            eventSchema: AddedOwner,
            databaseFieldMap:
            [
                ("safeAddress", e => e.SafeAddress),
                ("owner", e => e.Owner)
            ]
        );

        AddMappings<RemovedOwner>(
            ns: "Safe",
            table: "RemovedOwner",
            eventSchema: RemovedOwner,
            databaseFieldMap:
            [
                ("safeAddress", e => e.SafeAddress),
                ("owner", e => e.Owner)
            ]
        );

        Tables.Add(("V_Safe", "Owners"), V_Safe_Owners);
    }
}

public class LogParser(Address[] factoryAddresses) : ILogParser
{
    public static readonly ConcurrentDictionary<Address, object?> KnownSafeProxies = new();

    private readonly HashSet<Address> _factoryAddresses = new(factoryAddresses);

    private readonly Hash256 _proxyCreationTopic = new(DatabaseSchema.ProxyCreation.Topic);
    private readonly Hash256 _legacyCrcProxyCreationTopic = Keccak.Compute("ProxyCreation(address)");
    private readonly Hash256 _safeSetupTopic = new(DatabaseSchema.SafeSetup.Topic);
    private readonly Hash256 _addedOwnerTopic = new(DatabaseSchema.AddedOwner.Topic);
    private readonly Hash256 _removedOwnerTopic = new(DatabaseSchema.RemovedOwner.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        // Depending on the log index, the SafeSetup or Added/RemovedOwner event might be emitted before the ProxyCreation event.
        // In that case, we need to wait for the ProxyCreation event to be parsed before we can parse the SafeSetup event.
        if (!events.Any(e => e is ProxyCreation))
        {
            yield break;
        }

        var eventsHashSet =
            events.ToHashSet(
                new DelegateEqualityComparer<IIndexEvent>(
                    (a, b) => a.TransactionHash == b.TransactionHash && a.LogIndex == b.LogIndex,
                    obj => obj.TransactionHash.GetHashCode() ^ obj.LogIndex.GetHashCode()));

        for (int i = 0; i < receipt.Logs?.Length; i++)
        {
            var log = receipt.Logs[i];
            var topic = log.Topics[0];

            if (KnownSafeProxies.ContainsKey(log.Address))
            {
                if (topic == _safeSetupTopic)
                {
                    foreach (var safeSetupEvent in SafeSetup(block, transaction, receipt, log, i))
                    {
                        if (!eventsHashSet.Contains(safeSetupEvent))
                        {
                            yield return safeSetupEvent;
                        }
                    }
                }
                else if (topic == _addedOwnerTopic)
                {
                    var addedOwner = AddedOwner(block, transaction, receipt, log, i);
                    if (!eventsHashSet.Contains(addedOwner))
                    {
                        yield return addedOwner;
                    }
                }
                else if (topic == _removedOwnerTopic)
                {
                    var removedOwner = RemovedOwner(block, transaction, receipt, log, i);
                    if (!eventsHashSet.Contains(removedOwner))
                    {
                        yield return removedOwner;
                    }
                }
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
            yield break;

        var topic = log.Topics[0];

        if (_factoryAddresses.Contains(log.Address))
        {
            if (topic == _proxyCreationTopic || topic == _legacyCrcProxyCreationTopic)
            {
                var evt = ProxyCreation(block, receipt, log, logIndex);
                KnownSafeProxies.TryAdd(new Address(evt.Proxy), null);
                yield return evt;
            }
        }

        if (KnownSafeProxies.ContainsKey(log.Address))
        {
            if (topic == _safeSetupTopic)
            {
                foreach (var safeSetupEvent in SafeSetup(block, transaction, receipt, log, logIndex))
                {
                    yield return safeSetupEvent;
                }
            }
            else if (topic == _addedOwnerTopic)
            {
                yield return AddedOwner(block, transaction, receipt, log, logIndex);
            }
            else if (topic == _removedOwnerTopic)
            {
                yield return RemovedOwner(block, transaction, receipt, log, logIndex);
            }
        }
    }

    private ProxyCreation ProxyCreation(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        string proxy = "";
        string? singleton = null;

        if (log.Topics.Length == 1)
        {
            // event ProxyCreation(address proxy);
            // event ProxyCreation(address proxy, address singleton);
            proxy = new Address(log.Data.Slice(12, 20)).ToString(true, false);
            if (log.Data.Length > 32)
            {
                singleton = new Address(log.Data.Slice(44)).ToString(true, false);
            }
        }
        else if (log.Topics.Length == 2)
        {
            // event ProxyCreation(address indexed proxy, address singleton);
            proxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            singleton = new Address(log.Data.Slice(12)).ToString(true, false);
        }
        else
        {
            throw new Exception("Unsupported ProxyCreation event format");
        }

        return new ProxyCreation(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            proxy,
            singleton
        );
    }

    private IEnumerable<SafeSetup> SafeSetup(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event SafeSetup(address indexed initiator, address[] owners, uint256 threshold, address indexed initializer, address fallbackHandler)
        string initiator = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();

        int ownersOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        string[] owners = LogDataParsingHelper.ParseAddressArray(dataSpan, ownersOffset);

        var thresholdBytes = dataSpan.Slice(32, 32);
        UInt256 threshold = new UInt256(thresholdBytes, true);

        var initializerBytes = dataSpan.Slice(64, 32);
        string initializer = new Address(initializerBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var fallbackHandlerBytes = dataSpan.Slice(96, 32);
        string fallbackHandler = new Address(fallbackHandlerBytes.Slice(12, 20).ToArray()).ToString(true, false);

        string safeAddress = log.Address.ToString(true, false);

        for (int i = 0; i < owners.Length; i++)
        {
            yield return new SafeSetup(
                BlockNumber: block.Number,
                Timestamp: (long)block.Timestamp,
                TransactionIndex: receipt.Index,
                LogIndex: logIndex,
                BatchIndex: i,
                TransactionHash: receipt.TxHash!.ToString(),
                Emitter: safeAddress,
                SafeAddress: safeAddress,
                Initiator: initiator,
                Owner: owners[i],
                Threshold: threshold,
                Initializer: initializer,
                FallbackHandler: fallbackHandler
            );
        }
    }

    private AddedOwner AddedOwner(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length == 32)
        {
            // event AddedOwner(address owner) => no indexed fields => single address is in log.Data
            var ownerBytes = dataSpan.Slice(12, 20);
            string owner = new Address(ownerBytes.ToArray()).ToString(true, false);

            string safeAddr = log.Address.ToString(true, false);

            return new AddedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                owner
            );
        }
        else
        {
            // event AddedOwner(address indexed owner)
            var ownerAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            string safeAddr = log.Address.ToString(true, false);

            return new AddedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                ownerAddress
            );
        }
    }

    private RemovedOwner RemovedOwner(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length == 32)
        {
            // event RemovedOwner(address owner) => no indexed fields => single address is in log.Data
            var ownerBytes = dataSpan.Slice(12, 20);
            string owner = new Address(ownerBytes.ToArray()).ToString(true, false);

            string safeAddr = log.Address.ToString(true, false);

            return new RemovedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                owner
            );
        }
        else
        {
            // event RemovedOwner(address indexed owner)
            var ownerAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            string safeAddr = log.Address.ToString(true, false);

            return new RemovedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                ownerAddress
            );
        }
    }
}